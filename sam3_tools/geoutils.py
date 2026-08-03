# Geospatial helpers: raster export, coordinate transforms and
# mask -> polygon conversion. All arcpy-dependent code lives here.

import math
import os
import uuid

import arcpy
import numpy as np

from . import config

# Largest native-resolution read (pixels) done in memory. Bigger
# extents fall back to the slower Clip/Resample geoprocessing path.
MAX_NATIVE_READ_PX = 64_000_000

# arcpy.Raster construction, Describe() and GetRasterProperties() are
# geoprocessing calls costing seconds each; the Raster object itself
# answers extent/cell size/spatial reference instantly once created.
_RASTER_CACHE = {}


def _get_raster(raster):
    """Return a cached arcpy.Raster for a dataset path (server use)."""
    key = str(raster)
    ras = _RASTER_CACHE.get(key)
    if ras is None:
        if len(_RASTER_CACHE) > 8:   # long sessions: keep the cache small
            _RASTER_CACHE.clear()
        ras = arcpy.Raster(raster)
        _RASTER_CACHE[key] = ras
    return ras


def _remove_scratch_raster(path):
    """Best-effort removal of a scratch GeoTIFF and its sidecars so
    long sessions do not fill the scratch folder."""
    base = os.path.splitext(path)[0]
    for f in (path, path + ".aux.xml", path + ".ovr", path + ".xml",
              base + ".tfw"):
        try:
            os.remove(f)
        except OSError:
            pass


class GeoInfo(object):
    """Georeferencing info for the image array sent to the model."""

    def __init__(self, xmin, ymin, xmax, ymax, cell_w, cell_h,
                 n_cols, n_rows, spatial_ref):
        self.xmin = xmin
        self.ymin = ymin
        self.xmax = xmax
        self.ymax = ymax
        self.cell_w = cell_w
        self.cell_h = cell_h
        self.n_cols = n_cols
        self.n_rows = n_rows
        self.spatial_ref = spatial_ref

    def map_to_pixel(self, x, y):
        """Map coordinates -> (col, row) pixel coordinates (float)."""
        col = (x - self.xmin) / self.cell_w
        row = (self.ymax - y) / self.cell_h
        return col, row

    def clamp_pixel(self, col, row):
        col = min(max(col, 0.0), self.n_cols - 1.0)
        row = min(max(row, 0.0), self.n_rows - 1.0)
        return col, row


def _scratch_path(ext=".tif"):
    folder = arcpy.env.scratchFolder
    return os.path.join(folder, "sam3_{0}{1}".format(uuid.uuid4().hex[:12], ext))


def get_active_view_extent():
    """Return the current map view extent, or None when unavailable."""
    try:
        aprx = arcpy.mp.ArcGISProject("CURRENT")
        view = aprx.activeView
        if view is not None and hasattr(view, "camera"):
            return view.camera.getExtent()
    except Exception:
        pass
    return None


def resolve_extent(raster, aoi_layer=None, prompt_layer=None,
                   use_view_extent=True, messages=None):
    """Decide the processing extent.

    Priority: AOI polygon layer > prompt features (expanded) >
    active map view extent > full raster extent.
    Result is always intersected with the raster extent.
    """
    desc = arcpy.Describe(raster)
    r_ext = desc.extent
    raster_sr = desc.spatialReference
    cell_w = float(arcpy.management.GetRasterProperties(
        raster, "CELLSIZEX").getOutput(0).replace(",", "."))
    cell_h = float(arcpy.management.GetRasterProperties(
        raster, "CELLSIZEY").getOutput(0).replace(",", "."))

    ext = None
    if aoi_layer:
        aoi_desc = arcpy.Describe(aoi_layer)
        ext = aoi_desc.extent.projectAs(raster_sr)
        if messages:
            messages.addMessage("Processing extent taken from AOI layer.")
    elif prompt_layer:
        p_desc = arcpy.Describe(prompt_layer)
        p_ext = p_desc.extent.projectAs(raster_sr)
        pad_x = max(p_ext.width * config.PROMPT_EXTENT_EXPAND_RATIO,
                    config.PROMPT_EXTENT_MIN_CELLS * cell_w)
        pad_y = max(p_ext.height * config.PROMPT_EXTENT_EXPAND_RATIO,
                    config.PROMPT_EXTENT_MIN_CELLS * cell_h)
        ext = arcpy.Extent(p_ext.XMin - pad_x, p_ext.YMin - pad_y,
                           p_ext.XMax + pad_x, p_ext.YMax + pad_y)
        if messages:
            messages.addMessage(
                "Processing extent derived from prompt features (padded).")
    elif use_view_extent:
        view_ext = get_active_view_extent()
        if view_ext is not None:
            ext = view_ext.projectAs(raster_sr)
            if messages:
                messages.addMessage(
                    "Processing extent taken from the active map view.")

    if ext is None:
        ext = r_ext
        if messages:
            messages.addMessage("Processing extent = full raster extent.")

    # Intersect with the raster extent.
    xmin = max(ext.XMin, r_ext.XMin)
    ymin = max(ext.YMin, r_ext.YMin)
    xmax = min(ext.XMax, r_ext.XMax)
    ymax = min(ext.YMax, r_ext.YMax)
    if xmin >= xmax or ymin >= ymax:
        raise arcpy.ExecuteError(
            "The processing extent does not overlap the input raster.")
    return arcpy.Extent(xmin, ymin, xmax, ymax), raster_sr, cell_w, cell_h


def resolve_extent_from_coords(raster, xmin, ymin, xmax, ymax,
                               extent_sr_wkt=None, messages=None):
    """Like resolve_extent(), but the processing extent is given as
    plain coordinates (e.g. the map view extent sent by the C# add-in).

    extent_sr_wkt: spatial reference of the coordinates (WKT / esri
    string). When omitted, coordinates are assumed to be in the
    raster's spatial reference. Result is intersected with the raster
    extent. Returns (extent, raster_sr, cell_w, cell_h).
    """
    ras = _get_raster(raster)
    r_ext = ras.extent
    raster_sr = ras.spatialReference
    cell_w = float(ras.meanCellWidth)
    cell_h = float(ras.meanCellHeight)

    if extent_sr_wkt:
        src_sr = arcpy.SpatialReference()
        src_sr.loadFromString(extent_sr_wkt)
        if src_sr.name != raster_sr.name:
            corners = arcpy.Array([
                arcpy.Point(xmin, ymin), arcpy.Point(xmin, ymax),
                arcpy.Point(xmax, ymax), arcpy.Point(xmax, ymin),
                arcpy.Point(xmin, ymin)])
            poly = arcpy.Polygon(corners, src_sr)
            ext = poly.projectAs(raster_sr).extent
        else:
            ext = arcpy.Extent(xmin, ymin, xmax, ymax)
    else:
        ext = arcpy.Extent(xmin, ymin, xmax, ymax)

    i_xmin = max(ext.XMin, r_ext.XMin)
    i_ymin = max(ext.YMin, r_ext.YMin)
    i_xmax = min(ext.XMax, r_ext.XMax)
    i_ymax = min(ext.YMax, r_ext.YMax)
    if i_xmin >= i_xmax or i_ymin >= i_ymax:
        raise RuntimeError(
            "The requested extent does not overlap the input raster.")
    return (arcpy.Extent(i_xmin, i_ymin, i_xmax, i_ymax),
            raster_sr, cell_w, cell_h)


def export_rgb_array(raster, extent, spatial_ref, cell_w, cell_h,
                     max_size=config.DEFAULT_MAX_IMAGE_SIZE, messages=None):
    """Clip (and optionally resample) the raster, return (rgb_uint8, GeoInfo).

    rgb_uint8 has shape (rows, cols, 3).

    Fast path: snap the extent to the raster grid and read the pixels
    straight into numpy (RasterToNumPyArray), downsampling in memory.
    This avoids the Clip/Resample geoprocessing tools, whose per-call
    overhead and temporary GeoTIFFs dominate work-area preparation.
    Very large native reads fall back to the geoprocessing path.
    """
    max_size = min(int(max_size), config.ABSOLUTE_MAX_IMAGE_SIZE)

    try:
        ras = _get_raster(raster)
        r_ext = ras.extent
        # Snap to the raster grid so the pixel georeference is exact.
        col0 = max(int(math.floor((extent.XMin - r_ext.XMin) / cell_w)), 0)
        row0 = max(int(math.floor((r_ext.YMax - extent.YMax) / cell_h)), 0)
        col1 = min(int(math.ceil((extent.XMax - r_ext.XMin) / cell_w)),
                   int(ras.width))
        row1 = min(int(math.ceil((r_ext.YMax - extent.YMin) / cell_h)),
                   int(ras.height))
        n_cols = col1 - col0
        n_rows = row1 - row0
        if n_cols < 8 or n_rows < 8:
            raise arcpy.ExecuteError(
                "Processing extent is too small ({0} x {1} px)."
                .format(n_cols, n_rows))
        if n_cols * n_rows <= MAX_NATIVE_READ_PX:
            return _export_rgb_in_memory(
                ras, r_ext, col0, row0, n_cols, n_rows,
                cell_w, cell_h, max_size, spatial_ref, messages)
    except arcpy.ExecuteError:
        raise
    except Exception as exc:
        if messages:
            messages.addWarningMessage(
                "In-memory export failed ({0}); falling back to "
                "geoprocessing export.".format(exc))

    return _export_rgb_geoprocessing(
        raster, extent, spatial_ref, cell_w, cell_h, max_size, messages)


def _export_rgb_in_memory(ras, r_ext, col0, row0, n_cols, n_rows,
                          cell_w, cell_h, max_size, spatial_ref,
                          messages=None):
    """Direct numpy read of a grid-aligned window + in-memory resize."""
    xmin = r_ext.XMin + col0 * cell_w
    ymax = r_ext.YMax - row0 * cell_h
    ymin = ymax - n_rows * cell_h
    xmax = xmin + n_cols * cell_w

    arr = arcpy.RasterToNumPyArray(
        ras, arcpy.Point(xmin, ymin), n_cols, n_rows, nodata_to_value=0)
    if arr.ndim == 2:
        arr = arr[np.newaxis, :, :]

    out_cols, out_rows = n_cols, n_rows
    longer = max(n_cols, n_rows)
    if longer > max_size:
        scale = float(longer) / float(max_size)
        out_cols = max(int(round(n_cols / scale)), 8)
        out_rows = max(int(round(n_rows / scale)), 8)
        arr = _resize_bands(arr, out_cols, out_rows)
        if messages:
            messages.addMessage(
                "Extent is large - downsampled to {0} x {1} px."
                .format(out_cols, out_rows))

    geo = GeoInfo(xmin, ymin, xmax, ymax,
                  (xmax - xmin) / out_cols, (ymax - ymin) / out_rows,
                  out_cols, out_rows, spatial_ref)
    rgb = _bands_to_rgb_uint8(arr)
    if messages:
        messages.addMessage(
            "Image exported (in-memory): {0} x {1} px, {2} band(s)."
            .format(rgb.shape[1], rgb.shape[0], arr.shape[0]))
    return rgb, geo


def _resize_bands(arr, out_cols, out_rows):
    """(bands, rows, cols) -> resized copy, best interpolation available."""
    try:
        import cv2
        return np.stack([
            cv2.resize(band.astype(np.float32), (out_cols, out_rows),
                       interpolation=cv2.INTER_AREA)
            for band in arr])
    except ImportError:
        pass
    try:
        from PIL import Image
        return np.stack([
            np.asarray(Image.fromarray(
                band.astype(np.float32), mode="F")
                .resize((out_cols, out_rows), Image.BILINEAR))
            for band in arr])
    except ImportError:
        # Nearest-neighbour striding as a last resort.
        rows = np.linspace(0, arr.shape[1] - 1, out_rows).astype(np.int64)
        cols = np.linspace(0, arr.shape[2] - 1, out_cols).astype(np.int64)
        return arr[:, rows[:, None], cols[None, :]]


def _export_rgb_geoprocessing(raster, extent, spatial_ref, cell_w, cell_h,
                              max_size, messages=None):
    """Original Clip/Resample geoprocessing export (slow but handles
    arbitrarily large extents and exotic raster formats)."""
    n_cols = int(round(extent.width / cell_w))
    n_rows = int(round(extent.height / cell_h))
    if n_cols < 8 or n_rows < 8:
        raise arcpy.ExecuteError(
            "Processing extent is too small ({0} x {1} px)."
            .format(n_cols, n_rows))

    out_cell_w, out_cell_h = cell_w, cell_h
    longer = max(n_cols, n_rows)
    if longer > max_size:
        scale = float(longer) / float(max_size)
        out_cell_w = cell_w * scale
        out_cell_h = cell_h * scale
        n_cols = int(round(extent.width / out_cell_w))
        n_rows = int(round(extent.height / out_cell_h))
        if messages:
            messages.addMessage(
                "Extent is large - resampling to {0} x {1} px "
                "(cell size {2:.4f}).".format(n_cols, n_rows, out_cell_w))

    # Clip to extent (writes a temporary GeoTIFF in the scratch folder).
    clip_path = _scratch_path(".tif")
    rect = "{0} {1} {2} {3}".format(extent.XMin, extent.YMin,
                                    extent.XMax, extent.YMax)
    old_extent = arcpy.env.extent
    old_snap = arcpy.env.snapRaster
    try:
        arcpy.env.extent = extent
        arcpy.env.snapRaster = raster
        arcpy.management.Clip(raster, rect, clip_path, "#", "#",
                              "NONE", "MAINTAIN_EXTENT")
        src_path = clip_path
        if (out_cell_w, out_cell_h) != (cell_w, cell_h):
            resamp_path = _scratch_path(".tif")
            arcpy.management.Resample(
                clip_path, resamp_path,
                "{0} {1}".format(out_cell_w, out_cell_h), "BILINEAR")
            src_path = resamp_path
    finally:
        arcpy.env.extent = old_extent
        arcpy.env.snapRaster = old_snap

    arr = arcpy.RasterToNumPyArray(src_path, nodata_to_value=0)
    if arr.ndim == 2:
        arr = arr[np.newaxis, :, :]

    # Pick up the *actual* georeference of the exported raster.
    src_ras = arcpy.Raster(src_path)
    g_ext = src_ras.extent
    geo = GeoInfo(g_ext.XMin, g_ext.YMin, g_ext.XMax, g_ext.YMax,
                  src_ras.meanCellWidth, src_ras.meanCellHeight,
                  arr.shape[2], arr.shape[1], spatial_ref)

    rgb = _bands_to_rgb_uint8(arr)
    del src_ras
    for tmp in {clip_path, src_path}:
        _remove_scratch_raster(tmp)
    if messages:
        messages.addMessage(
            "Image exported: {0} x {1} px, {2} band(s) -> RGB."
            .format(rgb.shape[1], rgb.shape[0], arr.shape[0]))
    return rgb, geo


def _bands_to_rgb_uint8(arr):
    """(bands, rows, cols) -> (rows, cols, 3) uint8 with percentile stretch."""
    n_bands = arr.shape[0]
    if n_bands >= 3:
        bands = [arr[0], arr[1], arr[2]]
    else:
        bands = [arr[0], arr[0], arr[0]]

    out = np.zeros((arr.shape[1], arr.shape[2], 3), dtype=np.uint8)
    for i, band in enumerate(bands):
        band = band.astype(np.float32)
        if band.dtype != np.uint8:
            valid = band[np.isfinite(band)]
            if valid.size == 0:
                continue
            lo, hi = np.percentile(valid, config.STRETCH_PERCENTILES)
            if hi <= lo:
                lo, hi = float(valid.min()), float(valid.max() or 1.0)
            if hi <= lo:
                hi = lo + 1.0
            band = np.clip((band - lo) / (hi - lo) * 255.0, 0, 255)
        out[:, :, i] = band.astype(np.uint8)
    return out


def read_point_prompts(point_layer, geo, label_field=None, messages=None):
    """Read point features -> list of ((col, row), label) in pixel coords.

    label: 1 = foreground (include), 0 = background (exclude).
    Points outside the processing extent are skipped.
    """
    fields = ["SHAPE@XY"]
    if label_field:
        fields.append(label_field)

    prompts = []
    skipped = 0
    sr = geo.spatial_ref
    with arcpy.da.SearchCursor(point_layer, fields,
                               spatial_reference=sr) as cur:
        for row in cur:
            x, y = row[0]
            if not (geo.xmin <= x <= geo.xmax and geo.ymin <= y <= geo.ymax):
                skipped += 1
                continue
            label = 1
            if label_field and row[1] is not None:
                label = 1 if int(row[1]) != 0 else 0
            col, prow = geo.map_to_pixel(x, y)
            col, prow = geo.clamp_pixel(col, prow)
            prompts.append(((col, prow), label))

    if messages and skipped:
        messages.addWarningMessage(
            "{0} point(s) fall outside the processing extent and were "
            "skipped.".format(skipped))
    if not prompts:
        raise arcpy.ExecuteError(
            "No prompt points inside the processing extent.")
    return prompts


def read_click_prompts(positive_features, negative_features, geo,
                       messages=None):
    """Read interactive click feature sets -> point prompts.

    positive_features / negative_features come from
    GPFeatureRecordSetLayer parameters (points clicked directly on the
    map). Returns a list of ((col, row), label) in pixel coordinates,
    label 1 = positive (inside object), 0 = negative (outside).
    Clicks outside the processing extent are skipped with a warning.
    """
    prompts = []
    sr = geo.spatial_ref
    for features, label, tag in ((positive_features, 1, "positive"),
                                 (negative_features, 0, "negative")):
        if features is None:
            continue
        tmp = "memory\\sam3_clicks_{0}".format(uuid.uuid4().hex[:12])
        arcpy.management.CopyFeatures(features, tmp)
        try:
            skipped = 0
            with arcpy.da.SearchCursor(tmp, ["SHAPE@XY"],
                                       spatial_reference=sr) as cur:
                for (xy,) in cur:
                    if xy is None:
                        continue
                    x, y = xy
                    if not (geo.xmin <= x <= geo.xmax and
                            geo.ymin <= y <= geo.ymax):
                        skipped += 1
                        continue
                    col, prow = geo.map_to_pixel(x, y)
                    col, prow = geo.clamp_pixel(col, prow)
                    prompts.append(((col, prow), label))
            if messages and skipped:
                messages.addWarningMessage(
                    "{0} {1} click(s) fall outside the current view "
                    "extent and were skipped.".format(skipped, tag))
        finally:
            arcpy.management.Delete(tmp)

    if not any(l == 1 for _, l in prompts):
        raise arcpy.ExecuteError(
            "No positive clicks inside the current map view. Click at "
            "least one point INSIDE the target object (first clicks "
            "parameter) and keep the object visible in the view.")
    return prompts


def read_box_prompts(polygon_layer, geo, messages=None):
    """Read polygon features -> list of [x1, y1, x2, y2] pixel boxes.

    Each polygon's envelope is used as one box prompt.
    """
    boxes = []
    skipped = 0
    sr = geo.spatial_ref
    with arcpy.da.SearchCursor(polygon_layer, ["SHAPE@"],
                               spatial_reference=sr) as cur:
        for (shape,) in cur:
            if shape is None:
                continue
            ext = shape.extent
            if (ext.XMax < geo.xmin or ext.XMin > geo.xmax or
                    ext.YMax < geo.ymin or ext.YMin > geo.ymax):
                skipped += 1
                continue
            c1, r1 = geo.clamp_pixel(*geo.map_to_pixel(ext.XMin, ext.YMax))
            c2, r2 = geo.clamp_pixel(*geo.map_to_pixel(ext.XMax, ext.YMin))
            if abs(c2 - c1) < 2 or abs(r2 - r1) < 2:
                skipped += 1
                continue
            boxes.append([c1, r1, c2, r2])

    if messages and skipped:
        messages.addWarningMessage(
            "{0} polygon(s) outside the extent (or too small) were skipped."
            .format(skipped))
    if not boxes:
        raise arcpy.ExecuteError(
            "No usable box prompts inside the processing extent.")
    return boxes


def masks_to_feature_class(masks, scores, geo, out_fc,
                           prompt_labels=None, simplify=False,
                           messages=None):
    """Convert instance masks to a polygon feature class.

    masks  : list of (rows, cols) bool arrays in image space.
    scores : list of float confidence values (same length).
    prompt_labels : optional list of str describing the prompt per mask.
    simplify : True smooths the pixel stair-step boundaries.
    """
    if not masks:
        raise arcpy.ExecuteError(
            "The model returned no masks. Try a lower threshold, a "
            "different prompt or a smaller extent.")

    n_rows, n_cols = masks[0].shape
    label_arr = np.zeros((n_rows, n_cols), dtype=np.int32)

    # Paint large masks first so small masks survive overlaps.
    order = sorted(range(len(masks)),
                   key=lambda i: int(masks[i].sum()), reverse=True)
    id_score = {}
    id_label = {}
    for new_id, i in enumerate(order, start=1):
        m = masks[i].astype(bool)
        label_arr[m] = new_id
        id_score[new_id] = float(scores[i]) if scores is not None else 1.0
        if prompt_labels is not None:
            id_label[new_id] = str(prompt_labels[i])[:254]

    lower_left = arcpy.Point(geo.xmin, geo.ymin)
    tmp_raster = arcpy.NumPyArrayToRaster(
        label_arr, lower_left, geo.cell_w, geo.cell_h, value_to_nodata=0)
    ras_path = _scratch_path(".tif")
    tmp_raster.save(ras_path)
    arcpy.management.DefineProjection(ras_path, geo.spatial_ref)

    arcpy.conversion.RasterToPolygon(
        ras_path, out_fc, "SIMPLIFY" if simplify else "NO_SIMPLIFY",
        "Value", "MULTIPLE_OUTER_PART")

    # Attach score / prompt attributes.
    arcpy.management.AddField(out_fc, "Score", "DOUBLE")
    if prompt_labels is not None:
        arcpy.management.AddField(out_fc, "Prompt", "TEXT",
                                  field_length=255)
    upd_fields = ["gridcode", "Score"]
    if prompt_labels is not None:
        upd_fields.append("Prompt")
    with arcpy.da.UpdateCursor(out_fc, upd_fields) as cur:
        for row in cur:
            gid = row[0]
            row[1] = id_score.get(gid, 0.0)
            if prompt_labels is not None:
                row[2] = id_label.get(gid, "")
            cur.updateRow(row)

    if messages:
        result = int(arcpy.management.GetCount(out_fc).getOutput(0))
        messages.addMessage(
            "Created {0} polygon feature(s) from {1} mask(s)."
            .format(result, len(masks)))
    return out_fc


def append_masks_to_target(masks, scores, geo, target_layer,
                           prompt_labels=None, simplify=False,
                           messages=None):
    """Convert masks to polygons and append them to an existing
    polygon layer / feature class (edit-style workflow).

    Score / Prompt fields are added to the target if missing.
    Returns the number of features appended.
    """
    tmp_fc = os.path.join(
        arcpy.env.scratchGDB,
        "sam3_append_{0}".format(uuid.uuid4().hex[:12]))
    masks_to_feature_class(masks, scores, geo, tmp_fc,
                           prompt_labels=prompt_labels,
                           simplify=simplify, messages=None)
    try:
        existing = {f.name.lower() for f in arcpy.ListFields(target_layer)}
        if "score" not in existing:
            arcpy.management.AddField(target_layer, "Score", "DOUBLE")
        if prompt_labels is not None and "prompt" not in existing:
            arcpy.management.AddField(target_layer, "Prompt", "TEXT",
                                      field_length=255)
        before = int(arcpy.management.GetCount(target_layer).getOutput(0))
        arcpy.management.Append(tmp_fc, target_layer, "NO_TEST")
        after = int(arcpy.management.GetCount(target_layer).getOutput(0))
        added = after - before
        if messages:
            messages.addMessage(
                "Appended {0} polygon feature(s) to the target layer "
                "({1} feature(s) total).".format(added, after))
        return added
    finally:
        arcpy.management.Delete(tmp_fc)
