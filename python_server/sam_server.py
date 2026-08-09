# -*- coding: utf-8 -*-
# Local HTTP inference server for the SAM3 Interactive ArcGIS Pro add-in.
#
# Runs inside the 'sam3_env' conda environment (has arcpy + PyTorch).
# The C# add-in starts this process and talks JSON over localhost:
#
#   GET  /ping       -> server / model status
#   POST /set_image  -> export the view extent from the raster and
#                       compute the image embedding (TagLab work area)
#   POST /predict    -> masks for the current positive/negative clicks
#   POST /warm       -> preload an engine/model in the background
#   POST /reset      -> drop the current work area
#   POST /shutdown   -> terminate the server
#
# Binds to 127.0.0.1 only. One session at a time (single Pro user).

import os
# Torch and OpenCV both bundle an OpenMP runtime inside the cloned
# arcgispro-py3 environment; without this the process dies with
# "OMP: Error #15" as soon as both are loaded. Must be set before
# numpy/torch/cv2 are imported.
os.environ.setdefault("KMP_DUPLICATE_LIB_OK", "TRUE")

import argparse
import json
import sys
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SERVER_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_DIR = os.path.dirname(SERVER_DIR)
for _p in (REPO_DIR, SERVER_DIR):   # SERVER_DIR holds isegm/ (RITM)
    if _p not in sys.path:
        sys.path.insert(0, _p)

import sam3_tools.config as config          # noqa: E402
import sam3_tools.engine as engine          # noqa: E402
import sam3_tools.masktools as masktools    # noqa: E402
# sam3_tools.geoutils (arcpy) and sam3_tools.ritm_engine are imported
# lazily so /ping responds immediately and a missing isegm package
# only matters when the RITM engine is actually requested.

STATE = {
    "engine": None,    # "sam" | "ritm"
    "session": None,   # engine.InteractiveSession | ritm_engine.RitmSession
    "geo": None,       # geoutils.GeoInfo of the exported image
    "status": "starting",   # starting | warming | ready
    "warm": None,      # (engine, model id / checkpoint) being preloaded
    "device": None,    # filled in by the warmup thread
}

# Serializes model loading / inference between the warmup thread and
# request handling.
INFER_LOCK = threading.Lock()
# Guards STATE["warm"] so the same model is never warmed twice.
WARM_LOCK = threading.Lock()


def _warm_target(engine_name, model_id, ritm_checkpoint):
    """Normalise a warm request into ("sam"|"ritm", model ref)."""
    engine_name = (engine_name or
                   config.DEFAULT_INTERACTIVE_ENGINE).lower()
    if engine_name == "ritm":
        return ("ritm", ritm_checkpoint or _default_ritm_checkpoint())
    return ("sam", model_id or config.DEFAULT_INTERACTIVE_MODEL_ID)


def start_warmup(engine_name, model_id, ritm_checkpoint):
    """Preload the heavy bits in the background so the first click in
    ArcGIS Pro is fast: arcpy (needed for the image export) and the
    requested model. Returns the target being warmed; a target that is
    already loaded (or loading) is not warmed twice.

    Called at start-up with the model the add-in has selected, and
    again via /warm when the user picks another model in the ribbon -
    that way SAM weights are only ever loaded when SAM is chosen."""
    target = _warm_target(engine_name, model_id, ritm_checkpoint)
    with WARM_LOCK:
        if STATE["warm"] == target:
            return target
        STATE["warm"] = target
        STATE["status"] = "warming"
    threading.Thread(target=_warmup, args=target, daemon=True).start()
    return target


def _warmup(kind, ref):
    # arcpy and the model are preloaded independently: right after
    # ArcGIS Pro starts arcpy may not be licensed yet, and that must
    # not cost us the model preload (handle_set_image imports arcpy
    # again anyway).
    try:
        _log("warmup: importing arcpy ...")
        import sam3_tools.geoutils  # noqa: F401  (imports arcpy)
        _log("warmup: arcpy ready")
    except Exception as exc:
        _log("warmup: arcpy not ready ({0}) - retried with the first "
             "work area".format(exc))
    try:
        with INFER_LOCK:
            if kind == "ritm":
                import sam3_tools.ritm_engine as ritm_engine
                if os.path.exists(ref):
                    _log("warmup: loading RITM model ...")
                    ritm_engine._load_model(ref)
                else:
                    _log("warmup: RITM checkpoint missing, skipped: "
                         "{0}".format(ref))
            else:
                _log("warmup: loading SAM model {0} ...".format(ref))
                engine._load_interactive_model(ref)
        STATE["device"] = engine.describe_device()
        _log("warmup: done ({0})".format(STATE["device"]))
    except Exception as exc:
        _log("warmup: model preload failed (non-fatal): {0}".format(exc))
        with WARM_LOCK:
            if STATE["warm"] == (kind, ref):
                STATE["warm"] = None      # let a later /warm retry
    finally:
        STATE["status"] = "ready"


def _default_ritm_checkpoint():
    return os.path.join(REPO_DIR, "models",
                        config.RITM_CHECKPOINT_FILENAME)


def _make_session(engine_name, model_id, ritm_checkpoint):
    """(Re)create the session when engine/model settings changed."""
    if engine_name == "ritm":
        import sam3_tools.ritm_engine as ritm_engine
        checkpoint = ritm_checkpoint or _default_ritm_checkpoint()
        if not os.path.exists(checkpoint):
            raise RuntimeError(
                "RITM checkpoint not found: {0}\nRun "
                "scripts\\get_ritm.bat once to download it (or fix "
                "'ritm_checkpoint' in the add-in config).".format(
                    checkpoint))
        session = STATE["session"]
        if (STATE["engine"] != "ritm" or session is None or
                session.checkpoint_path != checkpoint):
            session = ritm_engine.RitmSession(checkpoint)
    else:
        engine_name = "sam"
        model_id = model_id or config.DEFAULT_INTERACTIVE_MODEL_ID
        session = STATE["session"]
        if (STATE["engine"] != "sam" or session is None or
                session.model_id != model_id):
            session = engine.InteractiveSession(model_id=model_id)
    STATE["engine"] = engine_name
    STATE["session"] = session
    # The model this session uses is loaded now - remember it so a
    # later /warm for the same model is a no-op.
    with WARM_LOCK:
        STATE["warm"] = _warm_target(engine_name, model_id,
                                     ritm_checkpoint)
    return session


def _log(msg):
    print("[sam_server] {0}".format(msg))
    sys.stdout.flush()


def handle_ping(_payload):
    # Never touch torch here: /ping must answer instantly, also while
    # the warmup thread is still importing it.
    warm = STATE["warm"]
    return {
        "ok": True,
        "status": STATE["status"],
        "engine": STATE["engine"],
        "warm_engine": warm[0] if warm else None,
        "has_image": bool(STATE["session"] and STATE["session"].has_image),
        "device": STATE["device"],
    }


def handle_warm(payload):
    """Preload an engine/model without touching the work area, so
    picking another model in the ribbon does not cost the next click
    the load time."""
    kind, ref = start_warmup(payload.get("engine"),
                             payload.get("model_id"),
                             payload.get("ritm_checkpoint"))
    return {"ok": True, "status": STATE["status"], "engine": kind,
            "target": ref}


def handle_set_image(payload):
    import sam3_tools.geoutils as geoutils

    raster_path = payload["raster_path"]
    ext = payload["extent"]
    extent_sr_wkt = payload.get("extent_sr_wkt")
    max_size = int(payload.get("max_size") or config.DEFAULT_MAX_IMAGE_SIZE)
    engine_name = (payload.get("engine") or
                   config.DEFAULT_INTERACTIVE_ENGINE).lower()
    model_id = payload.get("model_id")
    ritm_checkpoint = payload.get("ritm_checkpoint")

    _log("set_image ({0}): {1}".format(engine_name, raster_path))

    t0 = time.perf_counter()
    extent, sr, cw, ch = geoutils.resolve_extent_from_coords(
        raster_path, float(ext["xmin"]), float(ext["ymin"]),
        float(ext["xmax"]), float(ext["ymax"]),
        extent_sr_wkt=extent_sr_wkt)

    # Fail fast on absurdly large view extents instead of grinding
    # through a multi-minute export that looks like a freeze.
    native_px = (extent.width / cw) * (extent.height / ch)
    if native_px > config.MAX_WORKAREA_NATIVE_PX:
        return {"ok": False, "error": (
            "The current view covers about {0:,.0f} megapixels of the "
            "raster at native resolution (limit: {1:,.0f} MP). Zoom in "
            "closer to the target object and try again.".format(
                native_px / 1e6,
                config.MAX_WORKAREA_NATIVE_PX / 1e6))}

    rgb, geo = geoutils.export_rgb_array(
        raster_path, extent, sr, cw, ch, max_size)
    t_export = time.perf_counter() - t0

    t0 = time.perf_counter()
    with INFER_LOCK:
        session = _make_session(engine_name, model_id, ritm_checkpoint)
        session.set_image(rgb)
    t_encode = time.perf_counter() - t0
    STATE["geo"] = geo
    STATE["device"] = engine.describe_device()
    _log("work area set: {0} x {1} px (export {2:.1f}s, "
         "encode {3:.1f}s)".format(geo.n_cols, geo.n_rows,
                                   t_export, t_encode))

    return {
        "ok": True,
        "device": STATE["device"],
        "image": {
            "cols": geo.n_cols,
            "rows": geo.n_rows,
            "xmin": geo.xmin,
            "ymin": geo.ymin,
            "xmax": geo.xmax,
            "ymax": geo.ymax,
            "cell_w": geo.cell_w,
            "cell_h": geo.cell_h,
            "sr_wkt": geo.spatial_ref.exportToString(),
        },
    }


def handle_predict(payload):
    points = payload["points"]   # [[col, row], ...] pixel coords
    labels = payload["labels"]   # [1|0, ...]
    if not points or not any(int(l) == 1 for l in labels):
        return {"ok": False,
                "error": "At least one positive click is required."}

    t0 = time.perf_counter()
    # Session and geo are read under the lock so a /set_image running
    # in another thread cannot swap the work area mid-prediction.
    with INFER_LOCK:
        session = STATE["session"]
        geo = STATE["geo"]
        if session is None or not session.has_image or geo is None:
            return {"ok": False,
                    "error": "No work area. Call set_image first."}
        mask, score = session.predict(points, labels)
    _log("predict: {0} click(s), {1:.2f}s".format(
        len(points), time.perf_counter() - t0))
    if not mask.any():
        return {"ok": True, "score": score, "rings": []}

    pixel_rings = masktools.clean_mask_to_rings(
        mask, largest_only=True,
        simplify_tolerance=float(payload.get("simplify_tolerance", 1.0)))

    # Pixel -> map coordinates (image spatial reference).
    map_rings = []
    for ring in pixel_rings:
        map_rings.append([
            [round(geo.xmin + c * geo.cell_w, 4),
             round(geo.ymax - r * geo.cell_h, 4)]
            for c, r in ring])
    return {"ok": True, "score": round(score, 4), "rings": map_rings}


def handle_reset(_payload):
    STATE["engine"] = None
    STATE["session"] = None
    STATE["geo"] = None
    return {"ok": True}


ROUTES = {
    "/ping": handle_ping,
    "/set_image": handle_set_image,
    "/predict": handle_predict,
    "/warm": handle_warm,
    "/reset": handle_reset,
}


class Server(ThreadingHTTPServer):
    """Threading server: /ping and /warm stay answerable while a long
    /set_image is running (INFER_LOCK still serializes inference)."""

    def handle_error(self, request, client_address):
        # A client dropping an idle keep-alive connection is normal;
        # do not dump a traceback into server.log for it.
        exc = sys.exc_info()[1]
        if isinstance(exc, (ConnectionResetError, ConnectionAbortedError,
                            BrokenPipeError)):
            return
        ThreadingHTTPServer.handle_error(self, request, client_address)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # quiet default request logging
        pass

    def _respond(self, obj, status=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/ping":
            self._respond(handle_ping(None))
        else:
            self._respond({"ok": False, "error": "Unknown route"}, 404)

    def do_POST(self):
        if self.path == "/shutdown":
            self._respond({"ok": True})
            _log("shutdown requested")

            def _stop(server):
                server.shutdown()
            import threading
            threading.Thread(target=_stop, args=(self.server,),
                             daemon=True).start()
            return

        route = ROUTES.get(self.path)
        if route is None:
            self._respond({"ok": False, "error": "Unknown route"}, 404)
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
            payload = json.loads(self.rfile.read(length) or b"{}")
            self._respond(route(payload))
        except Exception as exc:
            _log("ERROR: {0}\n{1}".format(exc, traceback.format_exc()))
            self._respond({"ok": False, "error": str(exc)}, 200)


def main():
    parser = argparse.ArgumentParser(
        description="SAM interactive segmentation server")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--warm-engine", default=None,
                        help="engine to preload at startup (sam|ritm)")
    parser.add_argument("--warm-model", default=None,
                        help="SAM model id to preload")
    parser.add_argument("--warm-ritm-checkpoint", default=None,
                        help="RITM checkpoint to preload")
    args = parser.parse_args()

    server = Server(("127.0.0.1", args.port), Handler)
    _log("listening on http://127.0.0.1:{0}".format(args.port))

    start_warmup(args.warm_engine, args.warm_model,
                 args.warm_ritm_checkpoint)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    _log("stopped")
    # Skip interpreter finalization: torch/cv2/arcpy teardown inside
    # the cloned conda env can crash with a fatal GIL error at exit.
    sys.stdout.flush()
    os._exit(0)


if __name__ == "__main__":
    main()
