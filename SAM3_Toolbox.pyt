# -*- coding: utf-8 -*-
# SAM3 Segmentation Toolbox for ArcGIS Pro (3.x)
#
# Python Toolbox exposing Segment Anything Model 3 (SAM 3):
#   1. Segment With Text Prompt   - open-vocabulary concept segmentation
#   2. Segment With Point Prompts - point features as clicks
#   3. Segment With Box Prompts   - polygon envelopes as boxes
#   4. Segment Everything         - automatic masks inside an extent
#   5. Interactive Edit           - TagLab-style positive/negative
#                                   clicks on the current map view
#
# Requires the 'sam3_env' conda environment (see docs/User_Manual.html).

import importlib
import json
import os
import sys
import traceback

import arcpy

TOOLBOX_DIR = os.path.dirname(os.path.abspath(__file__))
if TOOLBOX_DIR not in sys.path:
    sys.path.insert(0, TOOLBOX_DIR)

import sam3_tools.config as config
import sam3_tools.geoutils as geoutils
import sam3_tools.engine as engine

# Reload on toolbox refresh so code edits are picked up without
# restarting ArcGIS Pro (model cache in engine survives via globals).
importlib.reload(config)
importlib.reload(geoutils)


def _report_device(messages):
    try:
        messages.addMessage("Inference device: " + engine.describe_device())
    except Exception as exc:
        messages.addWarningMessage(str(exc))


def _fail(messages, exc):
    messages.addErrorMessage(str(exc))
    messages.addErrorMessage(traceback.format_exc())
    raise arcpy.ExecuteError(str(exc))


def _common_output_param():
    p = arcpy.Parameter(
        displayName="Output Polygon Feature Class",
        name="out_features",
        datatype="DEFeatureClass",
        parameterType="Required",
        direction="Output")
    return p


def _raster_param():
    return arcpy.Parameter(
        displayName="Input Raster (imagery)",
        name="in_raster",
        datatype=["GPRasterLayer", "DERasterDataset"],
        parameterType="Required",
        direction="Input")


def _aoi_param():
    p = arcpy.Parameter(
        displayName="Area of Interest (optional polygon layer)",
        name="aoi",
        datatype="GPFeatureLayer",
        parameterType="Optional",
        direction="Input")
    p.filter.list = ["Polygon"]
    return p


def _max_size_param():
    p = arcpy.Parameter(
        displayName="Max Image Size (pixels, longer side)",
        name="max_size",
        datatype="GPLong",
        parameterType="Optional",
        direction="Input")
    p.value = config.DEFAULT_MAX_IMAGE_SIZE
    return p


def _model_id_param():
    p = arcpy.Parameter(
        displayName="Model ID (Hugging Face)",
        name="model_id",
        datatype="GPString",
        parameterType="Optional",
        direction="Input",
        category="Advanced")
    p.value = config.DEFAULT_MODEL_ID
    return p


def _click_schema_spatial_ref():
    """Spatial reference for the click schema: prefer the active map's
    SR so clicks need no datum transformation; fall back to WGS84."""
    try:
        aprx = arcpy.mp.ArcGISProject("CURRENT")
        active_map = aprx.activeMap
        if active_map is not None:
            sr = active_map.spatialReference
            if sr is not None and sr.factoryCode:
                return sr
    except Exception:
        pass
    return arcpy.SpatialReference(4326)


def _click_schema_value():
    """Empty point FeatureSet so the user can click points directly on
    the map from the Geoprocessing pane (pencil button)."""
    sr = _click_schema_spatial_ref()
    schema = {
        "geometryType": "esriGeometryPoint",
        "spatialReference": {"wkid": sr.factoryCode},
        "fields": [{"name": "OBJECTID", "type": "esriFieldTypeOID",
                    "alias": "OBJECTID"}],
        "features": [],
    }
    try:
        return arcpy.FeatureSet(json.dumps(schema))
    except Exception:
        pass
    # Fallback: template feature class in the scratch geodatabase.
    template = os.path.join(arcpy.env.scratchGDB, "sam3_click_template")
    if not arcpy.Exists(template):
        arcpy.management.CreateFeatureclass(
            arcpy.env.scratchGDB, "sam3_click_template", "POINT",
            spatial_reference=sr)
    return template


def _click_param(name, display_name, required):
    p = arcpy.Parameter(
        displayName=display_name,
        name=name,
        datatype="GPFeatureRecordSetLayer",
        parameterType="Required" if required else "Optional",
        direction="Input")
    try:
        p.value = _click_schema_value()
    except Exception:
        pass  # schema is a convenience; the tool still validates at run
    return p


class Toolbox(object):
    def __init__(self):
        self.label = "SAM3 Segmentation"
        self.alias = "sam3"
        self.description = ("Segment Anything Model 3 tools for ArcGIS "
                            "Pro: text, point and box prompts, automatic "
                            "segmentation, and TagLab-style interactive "
                            "click editing.")
        self.tools = [SegmentWithText, SegmentWithPoints,
                      SegmentWithBoxes, SegmentEverything,
                      InteractiveEditClicks]


class SegmentWithText(object):
    def __init__(self):
        self.label = "1 - Segment With Text Prompt"
        self.description = (
            "Find and segment every instance of a concept described by a "
            "short noun phrase (e.g. 'building', 'red car', 'pond') "
            "inside the processing extent.")
        self.canRunInBackground = False

    def getParameterInfo(self):
        raster = _raster_param()
        text = arcpy.Parameter(
            displayName="Text Prompt (short noun phrase, English)",
            name="text_prompt",
            datatype="GPString",
            parameterType="Required",
            direction="Input")
        aoi = _aoi_param()
        thresh = arcpy.Parameter(
            displayName="Confidence Threshold (0-1)",
            name="score_threshold",
            datatype="GPDouble",
            parameterType="Optional",
            direction="Input")
        thresh.value = config.DEFAULT_SCORE_THRESHOLD
        max_size = _max_size_param()
        model_id = _model_id_param()
        out_fc = _common_output_param()
        return [raster, text, aoi, thresh, max_size, model_id, out_fc]

    def execute(self, parameters, messages):
        importlib.reload(geoutils)
        try:
            raster = parameters[0].value
            text_prompt = parameters[1].valueAsText
            aoi = parameters[2].value
            score_threshold = parameters[3].value or \
                config.DEFAULT_SCORE_THRESHOLD
            max_size = parameters[4].value or config.DEFAULT_MAX_IMAGE_SIZE
            model_id = parameters[5].valueAsText or config.DEFAULT_MODEL_ID
            out_fc = parameters[6].valueAsText

            _report_device(messages)
            extent, sr, cw, ch = geoutils.resolve_extent(
                raster, aoi_layer=aoi, messages=messages)
            rgb, geo = geoutils.export_rgb_array(
                raster, extent, sr, cw, ch, max_size, messages)

            messages.addMessage(
                "Running SAM 3 with text prompt: '{0}' ...".format(
                    text_prompt))
            masks, scores = engine.segment_with_text(
                rgb, text_prompt, score_threshold=score_threshold,
                model_id=model_id)
            messages.addMessage("Model returned {0} mask(s)."
                                .format(len(masks)))

            geoutils.masks_to_feature_class(
                masks, scores, geo, out_fc,
                prompt_labels=[text_prompt] * len(masks),
                messages=messages)
        except arcpy.ExecuteError:
            raise
        except Exception as exc:
            _fail(messages, exc)


class SegmentWithPoints(object):
    def __init__(self):
        self.label = "2 - Segment With Point Prompts"
        self.description = (
            "Segment objects using point features as click prompts. "
            "Optionally use a field (1=foreground, 0=background) to mark "
            "exclusion points.")
        self.canRunInBackground = False

    def getParameterInfo(self):
        raster = _raster_param()
        points = arcpy.Parameter(
            displayName="Prompt Points (point feature layer)",
            name="prompt_points",
            datatype="GPFeatureLayer",
            parameterType="Required",
            direction="Input")
        points.filter.list = ["Point"]
        label_field = arcpy.Parameter(
            displayName="Label Field (1=foreground, 0=background; optional)",
            name="label_field",
            datatype="Field",
            parameterType="Optional",
            direction="Input")
        label_field.parameterDependencies = ["prompt_points"]
        label_field.filter.list = ["Short", "Long"]
        one_object = arcpy.Parameter(
            displayName="All points describe ONE object",
            name="one_object",
            datatype="GPBoolean",
            parameterType="Optional",
            direction="Input")
        one_object.value = False
        aoi = _aoi_param()
        max_size = _max_size_param()
        model_id = _model_id_param()
        out_fc = _common_output_param()
        return [raster, points, label_field, one_object, aoi,
                max_size, model_id, out_fc]

    def execute(self, parameters, messages):
        importlib.reload(geoutils)
        try:
            raster = parameters[0].value
            points = parameters[1].value
            label_field = parameters[2].valueAsText
            one_object = bool(parameters[3].value)
            aoi = parameters[4].value
            max_size = parameters[5].value or config.DEFAULT_MAX_IMAGE_SIZE
            model_id = parameters[6].valueAsText or config.DEFAULT_MODEL_ID
            out_fc = parameters[7].valueAsText

            _report_device(messages)
            extent, sr, cw, ch = geoutils.resolve_extent(
                raster, aoi_layer=aoi, prompt_layer=points,
                use_view_extent=False, messages=messages)
            rgb, geo = geoutils.export_rgb_array(
                raster, extent, sr, cw, ch, max_size, messages)

            prompts = geoutils.read_point_prompts(
                points, geo, label_field, messages)
            messages.addMessage(
                "Running SAM 3 with {0} point prompt(s) "
                "(one_object={1}) ...".format(len(prompts), one_object))
            masks, scores = engine.segment_with_points(
                rgb, prompts, one_object=one_object, model_id=model_id)

            geoutils.masks_to_feature_class(
                masks, scores, geo, out_fc,
                prompt_labels=["point"] * len(masks),
                messages=messages)
        except arcpy.ExecuteError:
            raise
        except Exception as exc:
            _fail(messages, exc)


class SegmentWithBoxes(object):
    def __init__(self):
        self.label = "3 - Segment With Box Prompts (polygons)"
        self.description = (
            "Segment one object per input polygon. The envelope "
            "(bounding box) of each polygon is used as a box prompt.")
        self.canRunInBackground = False

    def getParameterInfo(self):
        raster = _raster_param()
        boxes = arcpy.Parameter(
            displayName="Prompt Polygons (box prompts)",
            name="prompt_polygons",
            datatype="GPFeatureLayer",
            parameterType="Required",
            direction="Input")
        boxes.filter.list = ["Polygon"]
        aoi = _aoi_param()
        max_size = _max_size_param()
        model_id = _model_id_param()
        out_fc = _common_output_param()
        return [raster, boxes, aoi, max_size, model_id, out_fc]

    def execute(self, parameters, messages):
        importlib.reload(geoutils)
        try:
            raster = parameters[0].value
            boxes_layer = parameters[1].value
            aoi = parameters[2].value
            max_size = parameters[3].value or config.DEFAULT_MAX_IMAGE_SIZE
            model_id = parameters[4].valueAsText or config.DEFAULT_MODEL_ID
            out_fc = parameters[5].valueAsText

            _report_device(messages)
            extent, sr, cw, ch = geoutils.resolve_extent(
                raster, aoi_layer=aoi, prompt_layer=boxes_layer,
                use_view_extent=False, messages=messages)
            rgb, geo = geoutils.export_rgb_array(
                raster, extent, sr, cw, ch, max_size, messages)

            pixel_boxes = geoutils.read_box_prompts(
                boxes_layer, geo, messages)
            messages.addMessage(
                "Running SAM 3 with {0} box prompt(s) ..."
                .format(len(pixel_boxes)))
            masks, scores = engine.segment_with_boxes(
                rgb, pixel_boxes, model_id=model_id)

            geoutils.masks_to_feature_class(
                masks, scores, geo, out_fc,
                prompt_labels=["box"] * len(masks),
                messages=messages)
        except arcpy.ExecuteError:
            raise
        except Exception as exc:
            _fail(messages, exc)


class SegmentEverything(object):
    def __init__(self):
        self.label = "4 - Segment Everything (automatic)"
        self.description = (
            "Automatically segment all objects inside the processing "
            "extent using a regular grid of point prompts. Use an AOI "
            "polygon or zoom the map to the target area first.")
        self.canRunInBackground = False

    def getParameterInfo(self):
        raster = _raster_param()
        aoi = _aoi_param()
        grid = arcpy.Parameter(
            displayName="Grid Points Per Side",
            name="points_per_side",
            datatype="GPLong",
            parameterType="Optional",
            direction="Input")
        grid.value = config.DEFAULT_GRID_POINTS_PER_SIDE
        min_score = arcpy.Parameter(
            displayName="Min Mask Quality Score (0-1)",
            name="min_score",
            datatype="GPDouble",
            parameterType="Optional",
            direction="Input")
        min_score.value = 0.7
        max_size = _max_size_param()
        model_id = _model_id_param()
        out_fc = _common_output_param()
        return [raster, aoi, grid, min_score, max_size, model_id, out_fc]

    def execute(self, parameters, messages):
        importlib.reload(geoutils)
        try:
            raster = parameters[0].value
            aoi = parameters[1].value
            points_per_side = parameters[2].value or \
                config.DEFAULT_GRID_POINTS_PER_SIDE
            min_score = parameters[3].value or 0.7
            max_size = parameters[4].value or config.DEFAULT_MAX_IMAGE_SIZE
            model_id = parameters[5].valueAsText or config.DEFAULT_MODEL_ID
            out_fc = parameters[6].valueAsText

            _report_device(messages)
            extent, sr, cw, ch = geoutils.resolve_extent(
                raster, aoi_layer=aoi, messages=messages)
            rgb, geo = geoutils.export_rgb_array(
                raster, extent, sr, cw, ch, max_size, messages)

            messages.addMessage(
                "Running automatic segmentation "
                "({0} x {0} grid points) - this may take a while ..."
                .format(points_per_side))
            arcpy.SetProgressor("step", "SAM 3 automatic segmentation",
                                0, 100, 1)

            def progress(done, total):
                arcpy.SetProgressorPosition(int(100.0 * done / total))

            masks, scores = engine.segment_everything(
                rgb, points_per_side=int(points_per_side),
                min_iou_score=float(min_score), model_id=model_id,
                progress_callback=progress)
            arcpy.ResetProgressor()
            messages.addMessage("Kept {0} mask(s) after de-duplication."
                                .format(len(masks)))

            geoutils.masks_to_feature_class(
                masks, scores, geo, out_fc,
                prompt_labels=["auto"] * len(masks),
                messages=messages)
        except arcpy.ExecuteError:
            raise
        except Exception as exc:
            _fail(messages, exc)


class InteractiveEditClicks(object):
    def __init__(self):
        self.label = "5 - Interactive Edit (Positive/Negative Clicks)"
        self.description = (
            "TagLab-style interactive segmentation: click a few points "
            "INSIDE the object (positive) and, if needed, a few points "
            "OUTSIDE it (negative) directly on the map. Only the imagery "
            "inside the CURRENT map view extent is analysed, so zoom to "
            "the object first. The resulting polygon is appended to the "
            "target layer; repeat the tool to digitise object after "
            "object.")
        self.canRunInBackground = False  # needs the active map view

    def getParameterInfo(self):
        raster = _raster_param()
        pos_clicks = _click_param(
            "positive_clicks",
            "Positive Clicks (points INSIDE the object - click on map)",
            required=True)
        neg_clicks = _click_param(
            "negative_clicks",
            "Negative Clicks (points OUTSIDE the object - optional)",
            required=False)
        target = arcpy.Parameter(
            displayName="Target Polygon Layer (result is appended)",
            name="target_layer",
            datatype="GPFeatureLayer",
            parameterType="Required",
            direction="Input")
        target.filter.list = ["Polygon"]
        simplify = arcpy.Parameter(
            displayName="Smooth polygon boundary",
            name="simplify",
            datatype="GPBoolean",
            parameterType="Optional",
            direction="Input")
        simplify.value = True
        max_size = _max_size_param()
        model_id = _model_id_param()
        return [raster, pos_clicks, neg_clicks, target, simplify,
                max_size, model_id]

    def execute(self, parameters, messages):
        importlib.reload(geoutils)
        try:
            raster = parameters[0].value
            pos_clicks = parameters[1].value
            neg_clicks = parameters[2].value
            target = parameters[3].value
            simplify = bool(parameters[4].value)
            max_size = parameters[5].value or config.DEFAULT_MAX_IMAGE_SIZE
            model_id = parameters[6].valueAsText or config.DEFAULT_MODEL_ID

            # This tool analyses ONLY the current map view extent so it
            # stays fast on very large imagery.
            if geoutils.get_active_view_extent() is None:
                raise arcpy.ExecuteError(
                    "No active map view found. Open the map, zoom to the "
                    "target object and run the tool from the "
                    "Geoprocessing pane (not from a background/batch "
                    "context).")

            _report_device(messages)
            extent, sr, cw, ch = geoutils.resolve_extent(
                raster, messages=messages)
            rgb, geo = geoutils.export_rgb_array(
                raster, extent, sr, cw, ch, max_size, messages)

            prompts = geoutils.read_click_prompts(
                pos_clicks, neg_clicks, geo, messages)
            n_pos = sum(1 for _, l in prompts if l == 1)
            n_neg = len(prompts) - n_pos
            messages.addMessage(
                "Running SAM 3 with {0} positive / {1} negative "
                "click(s) ...".format(n_pos, n_neg))

            # All clicks describe ONE object (TagLab behaviour).
            masks, scores = engine.segment_with_points(
                rgb, prompts, one_object=True, model_id=model_id)
            if not masks:
                raise arcpy.ExecuteError(
                    "The model returned no usable mask. Add more "
                    "positive clicks spread over the object, or zoom in "
                    "closer so the object appears larger.")

            geoutils.append_masks_to_target(
                masks, scores, geo, target,
                prompt_labels=["click"] * len(masks),
                simplify=simplify, messages=messages)
        except arcpy.ExecuteError:
            raise
        except Exception as exc:
            _fail(messages, exc)
