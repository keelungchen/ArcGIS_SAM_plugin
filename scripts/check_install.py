# Installation check for the SAM3 Toolbox environment.
# Run inside the sam3_env environment:
#   %LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe check_install.py
#
# Add --download to also download the SAM 3 checkpoint (several GB).

import sys


def ok(msg):
    print("[ OK ] " + msg)


def fail(msg):
    print("[FAIL] " + msg)


def main():
    errors = 0

    print("Python: " + sys.version.split()[0])

    try:
        import numpy
        ok("numpy " + numpy.__version__)
    except ImportError as e:
        fail("numpy: " + str(e))
        errors += 1

    try:
        import PIL
        ok("pillow " + PIL.__version__)
    except ImportError as e:
        fail("pillow: " + str(e))
        errors += 1

    try:
        import skimage
        ok("scikit-image " + skimage.__version__)
    except ImportError as e:
        fail("scikit-image (needed by the interactive add-in): " + str(e)
             + "  -> pip install scikit-image")
        errors += 1

    try:
        import torch
        ok("torch " + torch.__version__)
        if torch.cuda.is_available():
            ok("CUDA available: " + torch.cuda.get_device_name(0))
        else:
            print("[INFO] No CUDA GPU detected - SAM 3 will run on CPU "
                  "(slow but functional).")
    except ImportError as e:
        fail("torch: " + str(e) + "  -> run setup_env.bat step 2")
        errors += 1

    try:
        import transformers
        ok("transformers " + transformers.__version__)
        missing = []
        for cls in ("Sam3Model", "Sam3Processor",
                    "Sam3TrackerModel", "Sam3TrackerProcessor"):
            if not hasattr(transformers, cls):
                missing.append(cls)
        if missing:
            fail("transformers is too old, missing: " + ", ".join(missing)
                 + "  -> pip install --upgrade transformers")
            errors += 1
        else:
            ok("SAM 3 classes found in transformers")
        if hasattr(transformers, "Sam2Model"):
            ok("SAM 2 classes found (interactive add-in default: "
               "facebook/sam2.1-hiera-tiny)")
        else:
            print("[INFO] transformers has no Sam2Model - the interactive "
                  "add-in default model needs it; upgrade transformers.")
    except ImportError as e:
        fail("transformers: " + str(e))
        errors += 1

    # Optional RITM engine (TagLab's positive/negative clicks network).
    import os
    server_dir = os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "python_server")
    if server_dir not in sys.path:
        sys.path.insert(0, server_dir)
    try:
        import isegm  # noqa: F401
        ok("isegm found - RITM engine available")
        ckpt = os.path.join(os.path.dirname(server_dir), "models",
                            "ritm_corals.pth")
        if os.path.exists(ckpt):
            ok("RITM checkpoint present (ritm_corals.pth)")
        else:
            print("[INFO] RITM checkpoint missing - run "
                  "scripts\\get_ritm.bat if you want the RITM engine.")
    except ImportError:
        print("[INFO] isegm not installed - RITM engine disabled "
              "(optional; run scripts\\get_ritm.bat to enable).")

    try:
        import arcpy  # noqa: F401
        ok("arcpy importable (environment cloned from arcgispro-py3)")
    except Exception:
        print("[INFO] arcpy not importable from this shell - normal when "
              "run outside ArcGIS Pro on some setups.")

    try:
        from huggingface_hub import HfApi
        api = HfApi()
        try:
            api.model_info("facebook/sam3")
            ok("Hugging Face access to facebook/sam3 confirmed")
        except Exception as e:
            fail("Cannot access facebook/sam3: " + str(e))
            print("       -> Accept the license at "
                  "https://huggingface.co/facebook/sam3")
            print("       -> Then run: hf auth login")
            errors += 1
    except ImportError as e:
        fail("huggingface_hub: " + str(e))
        errors += 1

    if "--download" in sys.argv and errors == 0:
        print("\nDownloading SAM 3 checkpoint (several GB, one time)...")
        from transformers import Sam3Model, Sam3Processor
        Sam3Processor.from_pretrained("facebook/sam3")
        Sam3Model.from_pretrained("facebook/sam3")
        ok("Checkpoint downloaded and cached.")

    print()
    if errors:
        print("RESULT: {0} problem(s) found - see messages above and the "
              "user manual troubleshooting section.".format(errors))
        sys.exit(1)
    print("RESULT: environment looks good. Restart ArcGIS Pro and add "
          "SAM3_Toolbox.pyt.")


if __name__ == "__main__":
    main()
