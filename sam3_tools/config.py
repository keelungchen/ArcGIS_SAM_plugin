# Configuration constants for the SAM3 toolbox.

# Hugging Face model id for SAM 3 (gated repo - you must accept the license
# on https://huggingface.co/facebook/sam3 and login with `hf auth login`).
DEFAULT_MODEL_ID = "facebook/sam3"

# Interactive (click) segmentation defaults used by the C# add-in server.
# engine: "ritm" (TagLab's RITM network, shipped with the installer) or
# "sam" (Hugging Face SAM2/SAM3 point prompts).
# RITM is the default: it is small (~39 MB), CPU-friendly and needs no
# image embedding, so the work area is ready almost instantly. The SAM
# weights are only downloaded / loaded once SAM is picked in the ribbon.
DEFAULT_INTERACTIVE_ENGINE = "ritm"
# sam2.1-hiera-tiny: ~155 MB, NOT gated, loads in seconds - much lighter
# than SAM3 while click quality stays SAM-grade.
DEFAULT_INTERACTIVE_MODEL_ID = "facebook/sam2.1-hiera-tiny"
# Default RITM checkpoint file name (TagLab's coral-finetuned weights),
# looked up in the repository's models\ folder unless overridden.
RITM_CHECKPOINT_FILENAME = "ritm_corals.pth"

# Maximum size (pixels) of the longer image side sent to the model.
# Larger extents are resampled down to this size before inference.
DEFAULT_MAX_IMAGE_SIZE = 2048

# Hard upper bound to protect against accidental huge exports.
ABSOLUTE_MAX_IMAGE_SIZE = 8192

# Refuse to build an interactive work area whose view extent covers
# more native raster pixels than this. Exporting such an extent takes
# minutes and looks like a freeze - the server answers immediately
# with an error telling the user to zoom in instead.
MAX_WORKAREA_NATIVE_PX = 512_000_000

# Default confidence threshold for text-prompt (concept) segmentation.
DEFAULT_SCORE_THRESHOLD = 0.5

# Default mask binarization threshold.
DEFAULT_MASK_THRESHOLD = 0.5

# Percentile stretch applied when converting non-8-bit rasters to RGB.
STRETCH_PERCENTILES = (2.0, 98.0)

# "Segment everything" defaults.
DEFAULT_GRID_POINTS_PER_SIDE = 32
DEFAULT_IOU_DEDUP_THRESHOLD = 0.75
DEFAULT_MIN_MASK_AREA_PX = 64

# Extent expansion factor used when the processing extent is derived from
# prompt features (points / boxes) so the model sees enough context.
PROMPT_EXTENT_EXPAND_RATIO = 0.25
PROMPT_EXTENT_MIN_CELLS = 256  # minimum half-width in raster cells
