# SAM 3 inference engine.
#
# Uses the Hugging Face `transformers` implementation of SAM 3:
#   - Text (concept) prompts  : Sam3Processor + Sam3Model
#   - Point / box prompts     : Sam3TrackerProcessor + Sam3TrackerModel
#
# Models are cached in module-level globals so repeated tool runs inside
# the same ArcGIS Pro session do not reload weights from disk.

import gc

import numpy as np

from . import config

_CACHE = {
    "text": None,          # (processor, model)
    "interactive": None,   # (processor, model)
    "model_id": None,
    "device": None,
}

_INSTALL_HINT = (
    "SAM 3 python dependencies are missing or too old.\n"
    "Inside the 'sam3_env' conda environment run:\n"
    "    pip install --upgrade \"transformers>=4.57\" accelerate "
    "huggingface_hub pillow\n"
    "and make sure PyTorch is installed. See the user manual "
    "(docs/User_Manual.html), section 'Installation'."
)

_GATED_HINT = (
    "Could not download the SAM 3 checkpoint. The repository "
    "'facebook/sam3' on Hugging Face is gated:\n"
    "  1. Visit https://huggingface.co/facebook/sam3 and accept the "
    "license.\n"
    "  2. Run 'hf auth login' (or 'huggingface-cli login') in the "
    "sam3_env environment with your HF token.\n"
    "See the user manual, section 'Model download'."
)


def _get_torch():
    try:
        import torch
        return torch
    except ImportError:
        raise RuntimeError("PyTorch is not installed.\n" + _INSTALL_HINT)


def get_device():
    torch = _get_torch()
    if _CACHE["device"] is None:
        _CACHE["device"] = "cuda" if torch.cuda.is_available() else "cpu"
    return _CACHE["device"]


def describe_device():
    torch = _get_torch()
    dev = get_device()
    if dev == "cuda":
        return "GPU: {0}".format(torch.cuda.get_device_name(0))
    return "CPU (no CUDA GPU detected - inference will be slower)"


def clear_cache():
    """Free model memory (exposed for troubleshooting)."""
    _CACHE["text"] = None
    _CACHE["interactive"] = None
    gc.collect()
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


def _invalidate_if_model_changed(model_id):
    if _CACHE["model_id"] not in (None, model_id):
        clear_cache()
    _CACHE["model_id"] = model_id


def _load_text_model(model_id):
    _invalidate_if_model_changed(model_id)
    if _CACHE["text"] is not None:
        return _CACHE["text"]
    torch = _get_torch()
    try:
        from transformers import Sam3Processor, Sam3Model
    except ImportError:
        raise RuntimeError(
            "transformers does not provide Sam3Model / Sam3Processor.\n"
            + _INSTALL_HINT)
    try:
        processor = Sam3Processor.from_pretrained(model_id)
        model = Sam3Model.from_pretrained(
            model_id,
            torch_dtype=(torch.float16 if get_device() == "cuda"
                         else torch.float32))
    except Exception as exc:  # gated repo / network issues
        raise RuntimeError("{0}\n\nOriginal error: {1}".format(
            _GATED_HINT, exc))
    model.to(get_device())
    model.eval()
    _CACHE["text"] = (processor, model)
    return _CACHE["text"]


def _interactive_classes(model_id):
    """Pick processor/model classes from the model id (sam2 vs sam3)."""
    mid = (model_id or "").lower()
    if "sam3" in mid:
        try:
            from transformers import (Sam3TrackerProcessor,
                                      Sam3TrackerModel)
            return Sam3TrackerProcessor, Sam3TrackerModel
        except ImportError:
            raise RuntimeError(
                "transformers does not provide Sam3TrackerModel / "
                "Sam3TrackerProcessor (needed for point/box prompts).\n"
                + _INSTALL_HINT)
    try:
        from transformers import Sam2Processor, Sam2Model
        return Sam2Processor, Sam2Model
    except ImportError:
        raise RuntimeError(
            "transformers does not provide Sam2Model / Sam2Processor "
            "(needed for '{0}').\n".format(model_id) + _INSTALL_HINT)


def _load_interactive_model(model_id):
    _invalidate_if_model_changed(model_id)
    if _CACHE["interactive"] is not None:
        return _CACHE["interactive"]
    torch = _get_torch()
    proc_cls, model_cls = _interactive_classes(model_id)
    try:
        processor = proc_cls.from_pretrained(model_id)
        model = model_cls.from_pretrained(
            model_id,
            torch_dtype=(torch.float16 if get_device() == "cuda"
                         else torch.float32))
    except Exception as exc:
        raise RuntimeError("{0}\n\nOriginal error: {1}".format(
            _GATED_HINT, exc))
    model.to(get_device())
    model.eval()
    _CACHE["interactive"] = (processor, model)
    return _CACHE["interactive"]


def _to_pil(rgb_array):
    from PIL import Image
    return Image.fromarray(rgb_array, mode="RGB")


# ---------------------------------------------------------------------------
# Text (concept) prompts
# ---------------------------------------------------------------------------

def segment_with_text(rgb_array, text_prompt,
                      score_threshold=config.DEFAULT_SCORE_THRESHOLD,
                      mask_threshold=config.DEFAULT_MASK_THRESHOLD,
                      model_id=config.DEFAULT_MODEL_ID):
    """Segment every instance matching a short noun phrase.

    Returns (masks, scores): list of HxW bool arrays and list of floats.
    """
    torch = _get_torch()
    processor, model = _load_text_model(model_id)
    image = _to_pil(rgb_array)

    inputs = processor(images=image, text=text_prompt.strip(),
                       return_tensors="pt").to(model.device)
    with torch.no_grad():
        outputs = model(**inputs)

    target_size = [(rgb_array.shape[0], rgb_array.shape[1])]
    results = processor.post_process_instance_segmentation(
        outputs,
        threshold=float(score_threshold),
        mask_threshold=float(mask_threshold),
        target_sizes=target_size)[0]

    masks_t = results.get("masks")
    scores_t = results.get("scores")
    masks, scores = [], []
    if masks_t is not None and len(masks_t) > 0:
        masks_np = masks_t.cpu().numpy() > 0
        scores_np = (scores_t.cpu().numpy()
                     if scores_t is not None
                     else np.ones(len(masks_np)))
        for m, s in zip(masks_np, scores_np):
            if m.sum() >= config.DEFAULT_MIN_MASK_AREA_PX:
                masks.append(m.astype(bool))
                scores.append(float(s))
    return masks, scores


# ---------------------------------------------------------------------------
# Interactive prompts (points / boxes) via the SAM3 tracker head
# ---------------------------------------------------------------------------

def _run_interactive(rgb_array, input_points=None, input_labels=None,
                     input_boxes=None, model_id=config.DEFAULT_MODEL_ID,
                     multimask_output=False):
    """Low-level call. Shapes follow the HF SAM convention:

    input_points : [[[[x, y], ...per-object points...], ...objects...]]
    input_labels : [[[1, 0, ...], ...]]
    input_boxes  : [[[x1, y1, x2, y2], ...objects...]]

    Returns (masks, iou_scores) where masks is a list of HxW bool arrays,
    one per object (best mask when multimask_output).
    """
    torch = _get_torch()
    processor, model = _load_interactive_model(model_id)
    image = _to_pil(rgb_array)

    kwargs = {"images": image, "return_tensors": "pt"}
    if input_points is not None:
        kwargs["input_points"] = input_points
        if input_labels is not None:
            kwargs["input_labels"] = input_labels
    if input_boxes is not None:
        kwargs["input_boxes"] = input_boxes

    inputs = processor(**kwargs).to(model.device)
    with torch.no_grad():
        outputs = model(**inputs, multimask_output=multimask_output)

    original_sizes = inputs["original_sizes"]
    masks_batch = processor.post_process_masks(
        outputs.pred_masks.cpu(), original_sizes)[0]
    # masks_batch: (n_objects, n_masks_per_object, H, W)
    iou = outputs.iou_scores.cpu().numpy()[0]  # (n_objects, n_masks)

    masks, scores = [], []
    masks_np = masks_batch.numpy() if hasattr(masks_batch, "numpy") \
        else np.asarray(masks_batch)
    for obj_idx in range(masks_np.shape[0]):
        obj_iou = iou[obj_idx]
        best = int(np.argmax(obj_iou))
        masks.append(masks_np[obj_idx, best].astype(bool))
        scores.append(float(obj_iou[best]))
    return masks, scores


def segment_with_points(rgb_array, point_prompts, one_object=False,
                        model_id=config.DEFAULT_MODEL_ID):
    """point_prompts: list of ((col, row), label) from geoutils.

    one_object=True  -> all points describe a single object
                        (background points supported).
    one_object=False -> each foreground point is a separate object;
                        background points are shared with every object.
    """
    fg = [(p, l) for p, l in point_prompts if l == 1]
    bg = [(p, l) for p, l in point_prompts if l == 0]
    if not fg:
        raise RuntimeError("At least one foreground point (label=1) "
                           "is required.")

    if one_object:
        pts = [[float(p[0]), float(p[1])] for p, _ in point_prompts]
        lbl = [int(l) for _, l in point_prompts]
        input_points = [[pts]]
        input_labels = [[lbl]]
    else:
        objects_pts, objects_lbl = [], []
        for (p, _l) in fg:
            pts = [[float(p[0]), float(p[1])]]
            lbl = [1]
            for (bp, _bl) in bg:
                pts.append([float(bp[0]), float(bp[1])])
                lbl.append(0)
            objects_pts.append(pts)
            objects_lbl.append(lbl)
        input_points = [objects_pts]
        input_labels = [objects_lbl]

    masks, scores = _run_interactive(
        rgb_array, input_points=input_points, input_labels=input_labels,
        model_id=model_id, multimask_output=(one_object or len(fg) == 1))

    keep_m, keep_s = [], []
    for m, s in zip(masks, scores):
        if m.sum() >= config.DEFAULT_MIN_MASK_AREA_PX:
            keep_m.append(m)
            keep_s.append(s)
    return keep_m, keep_s


def segment_with_boxes(rgb_array, pixel_boxes,
                       model_id=config.DEFAULT_MODEL_ID,
                       batch_size=16):
    """pixel_boxes: list of [x1, y1, x2, y2] in pixel coordinates."""
    all_masks, all_scores = [], []
    for i in range(0, len(pixel_boxes), batch_size):
        chunk = pixel_boxes[i:i + batch_size]
        boxes = [[[float(v) for v in box] for box in chunk]]
        masks, scores = _run_interactive(
            rgb_array, input_boxes=boxes, model_id=model_id,
            multimask_output=False)
        all_masks.extend(masks)
        all_scores.extend(scores)

    keep_m, keep_s = [], []
    for m, s in zip(all_masks, all_scores):
        if m.sum() >= config.DEFAULT_MIN_MASK_AREA_PX:
            keep_m.append(m)
            keep_s.append(s)
    return keep_m, keep_s


# ---------------------------------------------------------------------------
# Real-time interactive session (used by the C# add-in server)
# ---------------------------------------------------------------------------

class InteractiveSession(object):
    """One image + cached embeddings for real-time click prediction.

    TagLab-style usage: set_image() once when the work area is frozen
    (heavy, runs the image encoder), then predict() per click (light,
    only the prompt decoder runs when embeddings could be cached).
    """

    def __init__(self, model_id=config.DEFAULT_INTERACTIVE_MODEL_ID):
        self.model_id = model_id
        self._image = None
        self._embeddings = None
        self._image_size = None  # (rows, cols)
        self._orig_sizes = None  # [[rows, cols]] from the processor
        # Whether the processor accepts points without the image
        # (skips the per-click image preprocessing entirely).
        self._points_only = True

    @property
    def has_image(self):
        return self._image is not None

    def set_image(self, rgb_array):
        """Store the image and precompute embeddings when supported."""
        torch = _get_torch()
        processor, model = _load_interactive_model(self.model_id)
        self._image = _to_pil(rgb_array)
        self._image_size = (rgb_array.shape[0], rgb_array.shape[1])
        # Release the previous embedding BEFORE computing the new one;
        # otherwise long sessions fragment and slowly fill GPU memory
        # (old + new embedding alive at the same time on every work
        # area, and the CUDA allocator never returns the blocks).
        if self._embeddings is not None:
            self._embeddings = None
            if get_device() == "cuda":
                torch.cuda.empty_cache()
        self._orig_sizes = None
        self._points_only = True
        if hasattr(model, "get_image_embeddings"):
            try:
                raw = processor(
                    images=self._image, return_tensors="pt")
                sizes = raw.get("original_sizes")
                self._orig_sizes = (sizes.tolist()
                                    if hasattr(sizes, "tolist") else sizes)
                inputs = raw.to(model.device)
                with torch.inference_mode():
                    self._embeddings = model.get_image_embeddings(
                        inputs["pixel_values"])
            except Exception:
                # Fall back to full forward passes per click.
                self._embeddings = None
                self._orig_sizes = None

    def _process_click_inputs(self, processor, model,
                              input_points, input_labels):
        """Processor call for one click. When embeddings are cached we
        first try the points-only form (original_sizes instead of the
        image) - preprocessing the full image again on every click is
        the main per-click cost besides the decoder itself."""
        if (self._embeddings is not None and self._points_only and
                self._orig_sizes is not None):
            try:
                return processor(
                    input_points=input_points,
                    input_labels=input_labels,
                    original_sizes=self._orig_sizes,
                    return_tensors="pt").to(model.device)
            except Exception:
                # Processor needs the image - remember and fall through.
                self._points_only = False
        return processor(
            images=self._image, input_points=input_points,
            input_labels=input_labels,
            return_tensors="pt").to(model.device)

    def predict(self, points, labels):
        """points: [[col, row], ...] pixel coords; labels: [1|0, ...].

        All clicks describe ONE object. Returns (mask, score) where
        mask is a HxW bool array (best of the multimask proposals).
        """
        if self._image is None:
            raise RuntimeError("No image set. Call set_image() first.")
        torch = _get_torch()
        processor, model = _load_interactive_model(self.model_id)

        input_points = [[[[float(x), float(y)] for x, y in points]]]
        input_labels = [[[int(l) for l in labels]]]
        inputs = self._process_click_inputs(
            processor, model, input_points, input_labels)

        outputs = None
        if self._embeddings is not None:
            try:
                slim = {k: v for k, v in inputs.items()
                        if k != "pixel_values"}
                with torch.inference_mode():
                    outputs = model(image_embeddings=self._embeddings,
                                    multimask_output=True, **slim)
            except Exception:
                # This model's forward does not accept cached
                # embeddings - fall back to full passes from now on.
                self._embeddings = None
                outputs = None
        if outputs is None:
            if "pixel_values" not in inputs:
                # Points-only inputs cannot run a full forward pass.
                inputs = processor(
                    images=self._image, input_points=input_points,
                    input_labels=input_labels,
                    return_tensors="pt").to(model.device)
            with torch.inference_mode():
                outputs = model(**inputs, multimask_output=True)

        original_sizes = inputs.get("original_sizes")
        if original_sizes is None:
            original_sizes = self._orig_sizes
        if original_sizes is None:
            original_sizes = [list(self._image_size)]
        masks_batch = processor.post_process_masks(
            outputs.pred_masks.cpu(), original_sizes)[0]
        iou = outputs.iou_scores.cpu().numpy()[0]  # (n_objects, n_masks)

        masks_np = masks_batch.numpy() if hasattr(masks_batch, "numpy") \
            else np.asarray(masks_batch)
        obj_iou = iou[0]
        best = int(np.argmax(obj_iou))
        return masks_np[0, best].astype(bool), float(obj_iou[best])


# ---------------------------------------------------------------------------
# Segment everything (automatic, grid of point prompts)
# ---------------------------------------------------------------------------

def _mask_iou(a, b):
    inter = np.logical_and(a, b).sum()
    if inter == 0:
        return 0.0
    union = np.logical_or(a, b).sum()
    return float(inter) / float(union)


def segment_everything(rgb_array,
                       points_per_side=config.DEFAULT_GRID_POINTS_PER_SIDE,
                       min_iou_score=0.7,
                       dedup_iou=config.DEFAULT_IOU_DEDUP_THRESHOLD,
                       min_area_px=config.DEFAULT_MIN_MASK_AREA_PX,
                       model_id=config.DEFAULT_MODEL_ID,
                       batch_size=32,
                       progress_callback=None):
    """Automatic segmentation: prompt the model with a regular grid of
    points, keep confident masks and de-duplicate them by IoU.
    """
    h, w = rgb_array.shape[0], rgb_array.shape[1]
    xs = np.linspace(w * 0.5 / points_per_side,
                     w - w * 0.5 / points_per_side, points_per_side)
    ys = np.linspace(h * 0.5 / points_per_side,
                     h - h * 0.5 / points_per_side, points_per_side)
    grid = [(float(x), float(y)) for y in ys for x in xs]

    candidates = []  # (mask, score)
    total = len(grid)
    for i in range(0, total, batch_size):
        chunk = grid[i:i + batch_size]
        input_points = [[[[x, y]] for (x, y) in chunk]]
        input_labels = [[[1] for _ in chunk]]
        masks, scores = _run_interactive(
            rgb_array, input_points=input_points,
            input_labels=input_labels, model_id=model_id,
            multimask_output=True)
        for m, s in zip(masks, scores):
            area = int(m.sum())
            if s >= min_iou_score and min_area_px <= area < 0.9 * h * w:
                candidates.append((m, s))
        if progress_callback:
            progress_callback(min(i + batch_size, total), total)

    # Greedy non-maximum suppression by mask IoU.
    candidates.sort(key=lambda t: t[1], reverse=True)
    kept_masks, kept_scores = [], []
    for m, s in candidates:
        duplicate = False
        for km in kept_masks:
            if _mask_iou(m, km) > dedup_iou:
                duplicate = True
                break
        if not duplicate:
            kept_masks.append(m)
            kept_scores.append(s)
    return kept_masks, kept_scores
