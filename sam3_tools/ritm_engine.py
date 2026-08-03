# RITM interactive segmentation backend - the SAME network TagLab uses
# for its Positive/Negative Clicks tool (checkpoint: ritm_corals.pth,
# finetuned on coral orthomosaics).
#
# Requires the 'isegm' package from the official RITM repository
# (SamsungLabs/ritm_interactive_segmentation) - run
# scripts\get_ritm.bat once to download the code and the checkpoint.
#
# Compared to SAM there is no image-embedding stage: every click runs
# one forward pass of a small (~130 MB) HRNet, which is fast even on
# CPU. Clicks are replayed iteratively with the previous mask, exactly
# like TagLab's Ritm.py.

import sys
import types

import numpy as np

from .engine import _get_torch, get_device

_INSTALL_HINT = (
    "RITM code (isegm) or its dependencies are missing.\n"
    "Run scripts\\get_ritm.bat once - it downloads the official RITM "
    "source, TagLab's ritm_corals.pth checkpoint and the required "
    "pip packages (opencv-python, easydict) into sam3_env.")

_CACHE = {
    "checkpoint": None,
    "model": None,
}


def _alias_taglab_namespace():
    """TagLab checkpoints store model class paths as
    'models.isegm.<...>'. Alias that namespace onto our plain 'isegm'
    package so isegm.utils.serialization can import the class."""
    import isegm
    parent = sys.modules.get("models")
    if parent is None:
        parent = types.ModuleType("models")
        parent.__path__ = []
        sys.modules["models"] = parent
    if getattr(parent, "isegm", None) is not isegm:
        parent.isegm = isegm
    sys.modules.setdefault("models.isegm", isegm)


def _import_isegm():
    try:
        from isegm.inference import clicker as clicker_mod
        from isegm.inference import utils as isegm_utils
        from isegm.inference.predictors import get_predictor
        _alias_taglab_namespace()
        return clicker_mod, isegm_utils, get_predictor
    except ImportError as exc:
        raise RuntimeError("{0}\n\nOriginal error: {1}".format(
            _INSTALL_HINT, exc))


def _load_model(checkpoint_path):
    if (_CACHE["model"] is not None and
            _CACHE["checkpoint"] == checkpoint_path):
        return _CACHE["model"]
    _get_torch()
    _clicker, isegm_utils, _get_pred = _import_isegm()
    device = get_device()
    model = isegm_utils.load_is_model(
        checkpoint_path, device, cpu_dist_maps=(device == "cpu"))
    _CACHE["model"] = model
    _CACHE["checkpoint"] = checkpoint_path
    return model


class RitmSession(object):
    """One work-area image + iterative click state (TagLab-style)."""

    def __init__(self, checkpoint_path):
        self.checkpoint_path = checkpoint_path
        self._rgb = None
        self._predictor = None
        self._clicker = None
        self._history = []     # [(row, col, label), ...] applied so far
        self._last_mask = None
        self._last_score = 0.0

    @property
    def has_image(self):
        return self._rgb is not None

    def set_image(self, rgb_array):
        clicker_mod, _utils, get_predictor = _import_isegm()
        model = _load_model(self.checkpoint_path)
        self._predictor = get_predictor(
            model, brs_mode="NoBRS", device=get_device())
        self._rgb = np.ascontiguousarray(rgb_array)
        self._predictor.set_input_image(self._rgb)
        self._clicker = clicker_mod.Clicker()
        self._history = []
        self._last_mask = None
        self._last_score = 0.0

    def _reset_clicks(self):
        clicker_mod, _utils, _get_pred = _import_isegm()
        self._predictor.set_input_image(self._rgb)
        self._clicker = clicker_mod.Clicker()
        self._history = []
        self._last_mask = None
        self._last_score = 0.0

    def predict(self, points, labels):
        """points: [[col, row], ...]; labels: [1|0, ...] -> (mask, score).

        The C# add-in resends ALL clicks each time. When the new list
        just appends to the previous one, only the new clicks are run
        (incremental, like TagLab); otherwise (undo / reset) the whole
        sequence is replayed from scratch.
        """
        if self._rgb is None:
            raise RuntimeError("No image set. Call set_image() first.")
        clicker_mod, _utils, _get_pred = _import_isegm()

        wanted = [(float(r), float(c), int(l))
                  for (c, r), l in zip(points, labels)]
        if wanted == self._history and self._last_mask is not None:
            return self._last_mask, self._last_score
        if wanted[:len(self._history)] != self._history:
            self._reset_clicks()

        pred = None
        for row, col, label in wanted[len(self._history):]:
            click = clicker_mod.Click(is_positive=(label == 1),
                                      coords=(row, col))
            self._clicker.add_click(click)
            pred = self._predictor.get_prediction(self._clicker)
            self._history.append((row, col, label))

        if pred is None:  # nothing new was applied
            return self._last_mask, self._last_score

        mask = pred > 0.5
        score = float(pred[mask].mean()) if mask.any() else 0.0
        self._last_mask = mask
        self._last_score = score
        return mask, score
