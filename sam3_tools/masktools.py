# Mask post-processing helpers (arcpy-free, safe to use outside Pro).
#
# Mirrors TagLab's clean-up steps: keep the largest connected
# component, trace sub-pixel contours, simplify the vertices.

import numpy as np


def keep_largest_component(mask):
    """Keep only the largest connected region of a bool mask."""
    from scipy import ndimage
    labeled, n = ndimage.label(mask)
    if n <= 1:
        return mask
    sizes = ndimage.sum(mask, labeled, range(1, n + 1))
    keep = int(np.argmax(sizes)) + 1
    return labeled == keep


def _ring_area(ring):
    """Signed shoelace area of an Nx2 (col, row) ring."""
    x = ring[:, 0]
    y = ring[:, 1]
    return 0.5 * float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def mask_to_rings(mask, simplify_tolerance=1.0, min_ring_area_px=16.0):
    """Bool mask -> list of rings in pixel coordinates.

    Each ring is a list of [col, row] float pairs (closed: first point
    is repeated last). The first / largest rings are outer boundaries,
    smaller opposite-orientation rings are holes; consumers can rely on
    even-odd filling (ArcGIS SimplifyAsFeature fixes orientations).
    """
    try:
        from skimage import measure
    except ImportError:
        raise RuntimeError(
            "scikit-image is required for contour extraction. Run "
            "'pip install scikit-image' inside the sam3_env "
            "environment.")

    if not mask.any():
        return []

    # Pad so contours touching the border are closed.
    padded = np.pad(mask.astype(np.float32), 1, mode="constant")
    contours = measure.find_contours(padded, 0.5)

    rings = []
    for contour in contours:
        if simplify_tolerance and simplify_tolerance > 0:
            contour = measure.approximate_polygon(
                contour, tolerance=float(simplify_tolerance))
        if len(contour) < 4:
            continue
        # find_contours yields (row, col); shift back for the padding
        # and convert to (col, row).
        ring = np.column_stack((contour[:, 1] - 1.0, contour[:, 0] - 1.0))
        if abs(_ring_area(ring)) < float(min_ring_area_px):
            continue
        if not np.array_equal(ring[0], ring[-1]):
            ring = np.vstack([ring, ring[:1]])
        rings.append([[float(c), float(r)] for c, r in ring])
    return rings


def clean_mask_to_rings(mask, largest_only=True, simplify_tolerance=1.0,
                        min_ring_area_px=16.0):
    """Full pipeline: (optional) largest component + contour rings."""
    mask = mask.astype(bool)
    if largest_only:
        mask = keep_largest_component(mask)
    return mask_to_rings(mask, simplify_tolerance=simplify_tolerance,
                         min_ring_area_px=min_ring_area_px)
