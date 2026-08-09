# ArcGIS SAM Plugin — SAM 3 Segmentation Toolbox + Interactive Add-in

**[繁體中文說明請看這裡 / Traditional Chinese README](README.zh-TW.md)**

Brings Meta's **Segment Anything Model (SAM 2.1 / SAM 3)** and TagLab's
**RITM** click network into **ArcGIS Pro 3.x** (developed and tested
against 3.6.1, Windows 10/11).

The project ships two independent components that share the same Python
core:

| Component | What it is | Best for |
|---|---|---|
| **Python Toolbox** (`SAM3_Toolbox.pyt`) | Standard ArcGIS geoprocessing tools | Batch / unattended segmentation over an extent |
| **SAM Interactive C# Add-in** (`csharp_addin/`) | ArcGIS Pro ribbon add-in with a live map tool | TagLab-style click-by-click digitising with real-time mask preview |

The add-in talks to a local Python inference server
(`python_server/sam_server.py`) over `http://127.0.0.1:<port>`. The
server freezes a **work area** from the current map view and caches the
image embedding for it, so repeated clicks stay fast even on very large
rasters.

---

## Table of contents

1. [Features](#1-features)
2. [Requirements](#2-requirements)
3. [Credentials you must supply yourself](#3-credentials-you-must-supply-yourself)
4. [Assets not included in this repository](#4-assets-not-included-in-this-repository)
5. [Installation — one-click](#5-installation--one-click)
6. [Installation — manual](#6-installation--manual)
7. [Using the Python Toolbox](#7-using-the-python-toolbox)
8. [Using the Interactive Add-in](#8-using-the-interactive-add-in)
9. [Configuration reference](#9-configuration-reference)
10. [Building from source](#10-building-from-source)
11. [Repository layout](#11-repository-layout)
12. [Troubleshooting](#12-troubleshooting)
13. [Updating](#13-updating)
14. [Changelog](#14-changelog)
15. [Licensing and attribution](#15-licensing-and-attribution)

---

## 1. Features

### Python Toolbox — 5 geoprocessing tools

| # | Tool | Prompt input | Output |
|---|---|---|---|
| 1 | **Segment With Text Prompt** | A short English noun phrase (e.g. `building`, `coral`, `car`) | Every matching instance in the extent |
| 2 | **Segment With Point Prompts** | A point feature layer; an optional integer field marks 1 = foreground, 0 = background | One object per point, or a single object from all points |
| 3 | **Segment With Box Prompts** | A polygon feature layer (envelopes are used as boxes) | One object per polygon |
| 4 | **Segment Everything** | None — an automatic grid of points | All detected objects in the extent |
| 5 | **Interactive Edit (Positive/Negative Clicks)** | Point layer with positive/negative clicks | One object per run, appended to an existing polygon layer; restricted to the current map view extent |

All tools write to a polygon feature class, honour the map's spatial
reference, and resample the exported raster to
`DEFAULT_MAX_IMAGE_SIZE` (2048 px on the longer side) before inference.

### Interactive Add-in

- **Live click segmentation** — left-click = positive point,
  right-click = negative point; the mask preview redraws after every click.
- **Model drop-down in the ribbon** — switch between SAM 2.1 Tiny,
  SAM 2.1 Small, SAM 3 and RITM without editing any config file. The
  choice is persisted automatically.
- **Attribute label drop-downs** — pick a field of the target layer and
  one of its **domain values** (or a subtype); every polygon you save is
  tagged with it, so you label while you segment instead of filling the
  attribute table afterwards. Fields without a domain are flagged and
  accept a typed value.
- **Frozen work area** — the extent is captured at the first click and
  its embedding is cached, so subsequent clicks are near-instant.
- **On-map overlay panel** — shows the active model, the active label,
  click counts, confidence score and status, with Save / Clear clicks /
  Reset area buttons.
- **Keyboard**: `Space` = commit polygon, `Ctrl+Z` = undo last click,
  `Esc` = clear all clicks (work area stays).
- **Background warm-up** — arcpy and the model preload while you set up
  your layers.

### Engines

| Engine | Model | Size | Gated? | Notes |
|---|---|---|---|---|
| `sam` (default) | `facebook/sam2.1-hiera-tiny` | ~155 MB | **No** | Loads in seconds, no warm-up, SAM-grade click quality |
| `sam` | `facebook/sam2.1-hiera-small` | ~185 MB | **No** | Slightly better quality |
| `sam` | `facebook/sam3` | several GB | **Yes** | Best quality + text prompts; requires accepting Meta's licence |
| `ritm` | `ritm_corals.pth` | ~39 MB | No | TagLab's coral-finetuned click network; CPU-friendly, near-zero VRAM |

---

## 2. Requirements

- **ArcGIS Pro 3.x** on Windows 10 or 11 (target: 3.6.x)
- **~15 GB free disk** — cloned conda environment + PyTorch + model weights
- **NVIDIA GPU with ≥ 8 GB VRAM** strongly recommended
  (CPU works but is slow; the RITM engine is the CPU-friendly option)
- **`transformers >= 4.57`** — provides `Sam3Model` / `Sam3TrackerModel`
- **`scikit-image`** in `sam3_env` — required by the interactive add-in
- **A free Hugging Face account** — only if you want to use the gated
  `facebook/sam3` model (see next section)
- **.NET 8 SDK** — only if you rebuild the C# add-in from source

---

## 3. Credentials you must supply yourself

> **This repository contains no API keys, tokens or account credentials.**
> Nothing is hardcoded — every credential below is read from your own
> machine at runtime. You must obtain and configure your own.

### 3.1 Hugging Face access token (only needed for `facebook/sam3`)

The default engine (`facebook/sam2.1-hiera-tiny`) is **not gated** and
needs no login at all. You only need a token if you want SAM 3 — which
is what powers text-prompt segmentation (Tool 1) and the highest-quality
click results.

**How to get one:**

1. Create a free account at <https://huggingface.co/join>.
2. Go to <https://huggingface.co/facebook/sam3> and click
   **Agree and access repository** to accept Meta's model licence.
   Access is granted immediately in most cases.
3. Go to **Settings → Access Tokens**
   (<https://huggingface.co/settings/tokens>) and click
   **Create new token**. A **Read** token is sufficient — do not create
   a Write token for this.
4. Copy the token. It looks like `hf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`.
   **Treat it like a password.**

**How to use it (pick one):**

```bat
:: Recommended - interactive login, stores the token in your user profile
"%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe" -m huggingface_hub.commands.huggingface_cli login

:: Or, if the `hf` CLI is on your PATH inside sam3_env:
hf auth login
```

Paste the token when prompted. It is saved to
`%USERPROFILE%\.cache\huggingface\token` — **outside this repository**,
so it can never be committed by accident.

Alternatively, set an environment variable for the current session only:

```powershell
$env:HF_TOKEN = "hf_your_token_here"
```

**Never** paste your token into `sam3_tools/config.py`, `config.json`,
or any file inside this repository.

**Verify it worked:**

```bat
"%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe" scripts\check_install.py
```

You should see `[ OK ] Hugging Face access to facebook/sam3 confirmed`.

### 3.2 Local server port

The add-in and the Python server talk over `127.0.0.1` on a port stored
in your local `config.json` (default `8765`). This is **loopback only** —
it is not exposed to your network — but if port 8765 is already taken on
your machine, change it. The one-click installer picks a free port
automatically.

### 3.3 Machine-specific paths

`%LOCALAPPDATA%\SAM3Interactive\config.json` contains absolute paths to
*your* Python executable and *your* clone of this repository. It is
generated on your machine by `scripts\install_addin_config.bat` and is
listed in `.gitignore` — do not commit it, and do not copy someone
else's copy of it.

---

## 4. Assets not included in this repository

To keep the repo small and to respect third-party licences, the
following are **not committed** and are downloaded or built on demand:

| Missing item | Size | How to get it |
|---|---|---|
| `models/ritm_corals.pth` | ~39 MB | Run `scripts\get_ritm.bat`, or download directly from <http://taglab.isti.cnr.it/models/ritm_corals.pth> into `models\` |
| `python_server/isegm/` | ~1 MB | Downloaded by `scripts\get_ritm.bat` (fetches TagLab's vendored copy of the RITM inference code) |
| SAM 2.1 / SAM 3 weights | 155 MB – several GB | Downloaded automatically by `transformers` on first use into `%USERPROFILE%\.cache\huggingface` |
| `dist_package\ArcGIS_SAM_plugin_Setup.zip` | ~36 MB | Build it yourself: `powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1` |
| `csharp_addin\**\obj`, `bin` | — | Regenerated by `scripts\build_addin.ps1` |

The prebuilt add-in `csharp_addin\dist\SAM3Interactive.esriAddinX`
**is** included, so you do not need Visual Studio or the .NET SDK for a
normal install.

---

## 5. Installation — one-click

Best for deploying to a fresh computer.

1. **Build the portable package** on a machine that already has the repo:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1
   ```

   This produces `dist_package\ArcGIS_SAM_plugin_Setup.zip`.

2. **Copy the zip** to the target computer and extract it anywhere.

3. **Close ArcGIS Pro**, then double-click **`INSTALL.bat`**.

The installer performs, in order:

1. Locates ArcGIS Pro and checks its version
2. Checks free disk space
3. Creates the `sam3_env` conda environment (a clone of `arcgispro-py3`)
4. Installs PyTorch (GPU auto-detected)
5. Installs the remaining Python packages
6. Copies the runtime to `%LOCALAPPDATA%\SAM3Interactive\app`
7. Writes `config.json`, auto-picking a free port
8. Installs the add-in (`.esriAddinX`)
9. Validates the Python environment

Every failure prints a `PROBLEM:` line and a matching `FIX:` line, and
everything is logged to `install.log` next to `INSTALL.bat`. Re-running
the installer skips steps that already completed.

**Options:**

```bat
INSTALL.bat -Recreate   :: delete and rebuild sam3_env from scratch
INSTALL.bat -CpuOnly    :: force CPU-only PyTorch
```

After installing, if you want SAM 3, do the Hugging Face login from
[section 3.1](#31-hugging-face-access-token-only-needed-for-facebooksam3).

---

## 6. Installation — manual

1. **Close ArcGIS Pro** and run:

   ```bat
   scripts\setup_env.bat
   ```

   This clones `arcgispro-py3` → `sam3_env`, installs PyTorch,
   `transformers`, `scikit-image` and friends, and switches Pro's active
   environment.

2. **(Optional, for SAM 3)** Accept the licence at
   <https://huggingface.co/facebook/sam3> and log in — see
   [section 3.1](#31-hugging-face-access-token-only-needed-for-facebooksam3).

3. **Verify the environment:**

   ```bat
   "%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe" scripts\check_install.py
   ```

   Add `--download` to also pull the SAM 3 checkpoint now (several GB).

4. **Load the toolbox:** start ArcGIS Pro, confirm the active
   environment is `sam3_env` (Project → Package Manager), then
   **Catalog → Toolboxes → right-click → Add Toolbox →
   `SAM3_Toolbox.pyt`**.

5. **Install the add-in:** double-click
   `csharp_addin\dist\SAM3Interactive.esriAddinX`.

6. **Write the add-in config** (once):

   ```bat
   scripts\install_addin_config.bat
   ```

7. **(Optional) Enable the RITM engine:**

   ```bat
   scripts\get_ritm.bat
   ```

   Downloads the isegm code + `ritm_corals.pth`, installs
   `opencv-python` and `easydict`, and flips the config to
   `"engine": "ritm"`.

---

## 7. Using the Python Toolbox

1. Add `SAM3_Toolbox.pyt` to your project (see above).
2. Set the map to the area you want to process — several tools use the
   **current view extent**, so zoom in first. A smaller extent is both
   faster and more accurate.
3. Open a tool, fill in:
   - **Input raster layer** — the imagery to segment
   - **Prompt input** — text / points / polygons, depending on the tool
   - **Output feature class** — where polygons are written
   - **Advanced → Model ID** — override the Hugging Face model id
     (default from `sam3_tools/config.py`)
   - **Advanced → thresholds** — score threshold (default `0.5`) and
     mask binarisation threshold (default `0.5`)
4. Run. The first run of a given model downloads its weights, which can
   take a while; later runs use the cache.

**Tips**

- Text prompts work best as **short English noun phrases**
  (`building`, not `the large red building on the left`).
- Text prompts require SAM 3, which requires the Hugging Face login.
- For point prompts, add an integer field with `1` = foreground and
  `0` = background to steer the model away from surrounding objects.
- If you get too many small false positives, raise the score threshold
  or increase `DEFAULT_MIN_MASK_AREA_PX` in `sam3_tools/config.py`.

---

## 8. Using the Interactive Add-in

1. Start ArcGIS Pro and open the **SAM Segmentation** ribbon tab.
2. Pick your **Imagery** layer and your **Target** polygon layer from
   the ribbon drop-downs, and choose a **Model**.
3. *(Optional)* In the **Attribute Label** group, pick a **Label field**
   of the target layer and then a **Label** value — every polygon you
   save afterwards is tagged with it. See 8.1 below.
4. Click **Start Server** (a progress dialog appears while arcpy and the
   model preload). Wait for the status to go green.
5. Click the **Segment** tool, then click on the map:

   | Action | Effect |
   |---|---|
   | **Left-click** | Add a **positive** point (inside the object) |
   | **Right-click** | Add a **negative** point (outside the object) |
   | **Space** | Commit the current mask as a polygon in the target layer |
   | **Ctrl+Z** | Undo the last click |
   | **Esc** | Clear all clicks; the work area stays |

6. The **work area** is frozen from the map view at your first click.
   Use the ribbon's **New Work Area** / **Cancel Work Area** buttons to
   move it or drop it.
7. The on-map overlay shows the model, the active label, click counts,
   confidence score and status, plus Save / Clear clicks / Reset area
   buttons.

**Tips**

- Start with **one** positive click in the middle of the object, then
  add negative clicks only where the mask leaks.
- Zoom to a sensible scale before the first click. The server refuses
  work areas covering more than `MAX_WORKAREA_NATIVE_PX`
  (512 million native raster pixels) and asks you to zoom in — this
  prevents a multi-minute export that looks like a freeze.
- Switching models restarts the server; the work area is rebuilt on the
  next click.
- For coral / benthic imagery, the **RITM** engine often beats SAM out
  of the box, and runs comfortably on CPU.
- Segment one class at a time: set the **Label** once, work through all
  objects of that class, then switch the label — much faster than
  editing attributes afterwards.

### 8.1 Labelling polygons while you segment

The **Attribute Label** ribbon group writes an attribute value into every
polygon as it is created, so you never have to open the Attributes pane
between objects:

| Drop-down | What it does |
|---|---|
| **Label field** | Field of the **Target** layer that carries the label. Opening the list reads the fields of the current target layer. |
| **Label** | Value written into that field for every polygon saved from now on. Switch it any time to tag the next objects differently. |

Each field is marked with where its values come from:

| Marker | Meaning | How you pick a value |
|---|---|---|
| `[domain: n values]` | Coded value domain on the field | Pick a description from the **Label** list; the **code** is stored |
| `[subtypes: n]` | The layer's subtype field | Pick a subtype name; its code is stored |
| `[range domain]` | Range domain (min/max only) | Type the value into **Label** and press Enter |
| `[no domain]` | No domain at all | Type the value into **Label** and press Enter |

Picking a field without a coded value domain shows a notification saying
so (bell icon, top right) — attach a coded value domain in the
geodatabase (*Fields view → Domain*) if you want a fixed list to choose
from.

Choose `(no value)` to stop tagging, or `(no label)` in the field
drop-down to switch labelling off entirely. Changing the **Target**
layer clears both drop-downs, since fields and domains belong to the
layer. The active label is shown in green in the on-map panel, and each
save is confirmed with e.g. `Polygon saved and selected (score 0.94,
Class = Acropora).`

---

## 9. Configuration reference

### 9.1 `sam3_tools/config.py` — toolbox defaults (committed)

| Constant | Default | Meaning |
|---|---|---|
| `DEFAULT_MODEL_ID` | `facebook/sam3` | Model for the geoprocessing tools |
| `DEFAULT_INTERACTIVE_ENGINE` | `sam` | `sam` or `ritm` |
| `DEFAULT_INTERACTIVE_MODEL_ID` | `facebook/sam2.1-hiera-tiny` | Model for the add-in |
| `RITM_CHECKPOINT_FILENAME` | `ritm_corals.pth` | Looked up in `models\` |
| `DEFAULT_MAX_IMAGE_SIZE` | `2048` | Longer image side sent to the model |
| `ABSOLUTE_MAX_IMAGE_SIZE` | `8192` | Hard cap against accidental huge exports |
| `MAX_WORKAREA_NATIVE_PX` | `512_000_000` | Refuse work areas larger than this |
| `DEFAULT_SCORE_THRESHOLD` | `0.5` | Confidence threshold for text prompts |
| `DEFAULT_MASK_THRESHOLD` | `0.5` | Mask binarisation threshold |
| `STRETCH_PERCENTILES` | `(2.0, 98.0)` | Percentile stretch for non-8-bit rasters |
| `DEFAULT_GRID_POINTS_PER_SIDE` | `32` | "Segment everything" grid density |
| `DEFAULT_IOU_DEDUP_THRESHOLD` | `0.75` | Deduplication IoU |
| `DEFAULT_MIN_MASK_AREA_PX` | `64` | Discard masks smaller than this |

### 9.2 `%LOCALAPPDATA%\SAM3Interactive\config.json` — add-in config (**never commit**)

Generated by `scripts\install_addin_config.bat`. Machine-specific:

```jsonc
{
  "python_exe":      "C:\\Users\\<YOUR-USERNAME>\\AppData\\Local\\ESRI\\conda\\envs\\sam3_env\\python.exe",
  "server_script":   "<PATH-TO-YOUR-CLONE>\\python_server\\sam_server.py",
  "port":            8765,
  "engine":          "sam",
  "model_id":        "facebook/sam2.1-hiera-tiny",
  "ritm_checkpoint": "<PATH-TO-YOUR-CLONE>\\models\\ritm_corals.pth",
  "max_image_size":  2048
}
```

Replace `<YOUR-USERNAME>` and `<PATH-TO-YOUR-CLONE>` with your own
values — or just run `install_addin_config.bat`, which fills them in
correctly for your machine. The server log lands next to it at
`%LOCALAPPDATA%\SAM3Interactive\server.log`.

---

## 10. Building from source

Rebuilding the C# add-in needs only the **.NET 8 SDK** — no Visual Studio:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_addin.ps1
```

Output: `csharp_addin\dist\SAM3Interactive.esriAddinX`.

Building the portable installer package:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1
```

Output: `dist_package\ArcGIS_SAM_plugin_Setup.zip`.

---

## 11. Repository layout

```
SAM3_Toolbox.pyt              Toolbox entry point (add this to ArcGIS Pro)
INSTALL.bat                   One-click installer entry point
sam3_tools/                   Core Python package (must stay next to the .pyt)
  config.py                   Defaults: model ids, thresholds, image size
  engine.py                   SAM 2/3 inference + InteractiveSession (embedding cache)
  ritm_engine.py              RITM interactive inference (TagLab's clicks network)
  geoutils.py                 Raster export, coordinate transforms, mask -> polygon
  masktools.py                Mask post-processing (contours, smoothing; arcpy-free)
python_server/
  sam_server.py               Local HTTP inference server used by the add-in
  isegm/                      RITM source        [not committed - get_ritm.bat]
models/
  ritm_corals.pth             TagLab coral checkpoint  [not committed - get_ritm.bat]
csharp_addin/
  SAM3Interactive/            ArcGIS Pro SDK add-in source (net8.0-windows)
    Config.daml               Ribbon / tab / button definitions
    InteractiveSegmentTool.cs Map tool: click handling, live preview
    SamServerClient.cs        HTTP client for the Python server
    SamServerManager.cs       Server lifecycle (start / stop / health)
    ServerConfig.cs           config.json schema + validation
    ModelComboBox.cs          Model drop-down
    SegmentOverlayView*.*     On-map overlay panel (WPF)
  dist/                       Built SAM3Interactive.esriAddinX (double-click to install)
installer/
  install.ps1                 The one-click installer logic
scripts/
  setup_env.bat               Conda environment setup
  check_install.py            Environment / model-access verification
  install_addin_config.bat    Writes the add-in config.json
  get_ritm.bat                Enable the RITM engine (code + weights download)
  fetch_isegm.ps1             Downloads TagLab's vendored isegm code
  build_addin.ps1             Build + package the add-in without Visual Studio
  make_package.ps1            Build the portable installer zip
docs/
  User_Manual.html            Full manual in Traditional Chinese
```

---

## 12. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Cannot access facebook/sam3` | Licence not accepted, or not logged in | Accept at <https://huggingface.co/facebook/sam3>, then run the login from [3.1](#31-hugging-face-access-token-only-needed-for-facebooksam3) |
| `401 Unauthorized` from Hugging Face | Expired or revoked token | Create a new **Read** token and log in again |
| `transformers is too old, missing: Sam3Model` | `transformers < 4.57` | `pip install --upgrade "transformers>=4.57"` inside `sam3_env` |
| `scikit-image` missing | Environment predates v2.0.0 | `pip install scikit-image` inside `sam3_env` |
| **OMP Error #15** / crash on startup | Duplicate OpenMP runtime | Fixed in 2.2.0 — update, or set `KMP_DUPLICATE_LIB_OK=TRUE` |
| Server will not start | Wrong paths in `config.json` | Re-run `scripts\install_addin_config.bat`; check `%LOCALAPPDATA%\SAM3Interactive\server.log` |
| Port already in use | Something else owns 8765 | Edit `"port"` in `config.json`, or re-run the one-click installer to auto-pick a free port |
| "Zoom in" error at first click | Work area exceeds `MAX_WORKAREA_NATIVE_PX` | Zoom in, or raise the limit in `sam3_tools/config.py` |
| SAM ribbon present but **every control greyed out / drop-downs will not open** | The add-in was re-installed **while Pro was running**, so Pro's `AssemblyCache` kept the old locked DLL next to the new `Config.daml` and the assembly fails to load | Close Pro, then `Remove-Item "$env:LOCALAPPDATA\ESRI\ArcGISPro\AssemblyCache\{8f3b0f0a-9c1e-4f5d-b1a2-6d9d1e7c4a55}" -Recurse -Force` and restart Pro. Since 2.6.1 `build_addin.ps1 -Install` clears this cache automatically and warns when Pro is running |
| **"The requested extent does not overlap the input raster"** | The **Imagery** layer is not the raster under the current view (common with several sites in one project) | Open the **Imagery** drop-down and pick the layer marked `[in view]`; since 2.6.1 the add-in names the right layer in the message instead of failing in the server |
| RITM checkpoint not found | `models\ritm_corals.pth` missing | Run `scripts\get_ritm.bat` |
| Toolbox tools greyed out | Pro is not using `sam3_env` | Project → Package Manager → switch the active environment, restart Pro |
| Very slow inference | Running on CPU | Check `check_install.py` for `CUDA available`; or switch to the RITM engine |

The full troubleshooting chapter (Traditional Chinese) lives in
[`docs/User_Manual.html`](docs/User_Manual.html) — open it in any browser.

---

## 13. Updating

- **Plugin code** — overwrite `SAM3_Toolbox.pyt` and `sam3_tools\` in
  place, then right-click the toolbox in Catalog → **Refresh**.
- **Packages** — activate `sam3_env`, then
  `pip install --upgrade transformers accelerate`, and re-run
  `check_install.py`.
- **Model** — set a new Hugging Face repo id in each tool's
  *Advanced → Model ID*, or change `DEFAULT_MODEL_ID` in
  `sam3_tools/config.py`.
- **After an ArcGIS Pro major upgrade** — delete `sam3_env` and re-run
  `setup_env.bat` (manual §8.4); bump the `Esri.ArcGISPro.Extensions30`
  package version and the DAML `desktopVersion`, then rebuild the add-in
  (manual §5.4).

---

## 14. Changelog

### 2.6.1 — 2026-08-02
Imagery selection fixes for projects holding several sites. The add-in
now verifies that the map view overlaps the chosen **Imagery** layer
*before* calling the server, and names the layer the view is actually
over instead of failing with the server's
`The requested extent does not overlap the input raster`. Rasters
covering the current view are marked `[in view]` in the drop-down, and
an imagery pick that cannot be resolved in the active map (picked in
another map, or the layer was removed) no longer silently falls back to
the first raster in the map — the raster under the view is used instead.
The **Imagery** / **Target** drop-downs also keep showing their
selection after the list refreshes.

### 2.6.0 — 2026-08-02
**Attribute Label** ribbon group for the add-in: pick a **Label field**
of the target polygon layer and a **Label** value, and every polygon
saved with `Space` is created with that attribute already set. Values
come from the field's coded value domain or from the layer subtypes;
fields are marked `[domain: n values]` / `[subtypes: n]` /
`[range domain]` / `[no domain]` in the list, picking a field without a
domain raises a notification and falls back to a typed value (validated
against the field type). The label is shown in the on-map panel and
cleared when the target layer changes. See [8.1](#81-labelling-polygons-while-you-segment).

### 2.2.0 — 2026-07-05
UI overhaul: Chinese ribbon (`SAM 分割` tab), **model drop-down**
(SAM 2.1 Tiny/Small, SAM 3, RITM — switchable in the UI, persisted
automatically, no config editing), on-map overlay panel (model / click
counts / score / status + Save, Clear clicks, Reset-area buttons),
progress dialogs for server start and work-area preparation, background
server warm-up (arcpy + model preload).
Fixes: RITM checkpoint `models.isegm` namespace loading; OpenMP runtime
clash crash (OMP Error #15); silent isegm download failure in
`get_ritm.bat` (the original RITM repo is gone — TagLab's vendored copy
is fetched instead).

### 2.1.0 — 2026-07-04
Dual-engine interactive backend, switchable via the add-in config
(`engine`). Default is now `facebook/sam2.1-hiera-tiny` (~155 MB, **not**
gated, loads in seconds, no warm-up). New **RITM engine** — the exact
network and coral-finetuned checkpoint (`ritm_corals.pth`) TagLab uses
for its Positive/Negative Clicks tool (CPU-friendly, near-zero VRAM),
enabled with `scripts\get_ritm.bat`. Added `sam3_tools/ritm_engine.py`;
the SAM engine now auto-selects SAM 2 / SAM 3 classes from the model id.

### 2.0.0 — 2026-07-04
New **SAM3 Interactive C# Add-in**: real-time TagLab-style click
segmentation directly on the map (left/right click = positive/negative,
live mask preview, Space to commit, Ctrl+Z undo, Esc reset). Work area
frozen from the current view at the first click; image embedding cached
per work area. Added `python_server/sam_server.py` (localhost inference
server), `engine.InteractiveSession`, `sam3_tools/masktools.py`,
`scripts/build_addin.ps1`, `scripts/install_addin_config.bat`;
`scikit-image` added to the environment setup.

### 1.1.0 — 2026-07-04
New tool *5 - Interactive Edit (Positive/Negative Clicks)* (GP-tool
version, non-realtime, kept as a fallback): TagLab-style interactive
segmentation. Click positive/negative points directly on the map;
analysis is restricted to the current map view extent (large-image
friendly); result polygons are appended to an existing target layer;
optional boundary smoothing.

### 1.0.0 — 2026-07-03
Initial release: text / point / box prompts, automatic segmentation,
HTML user manual.

---

## 15. Licensing and attribution

This repository contains the ArcGIS integration code. The models and
third-party components it uses carry **their own licences**, which you
must review and accept independently:

- **SAM 2.1 / SAM 3** — model weights are distributed by **Meta** via
  Hugging Face (`facebook/sam2.1-hiera-tiny`, `facebook/sam3`) under
  Meta's own licence. `facebook/sam3` is a **gated** repository: you must
  accept the terms at <https://huggingface.co/facebook/sam3> before use.
- **RITM** — the `isegm` inference code originates from
  [SamsungLabs/ritm_interactive_segmentation](https://github.com/SamsungLabs/ritm_interactive_segmentation)
  (MIT). The original repository is no longer available, so
  `scripts\get_ritm.bat` fetches TagLab's vendored copy.
- **`ritm_corals.pth`** — coral-finetuned checkpoint from
  [TagLab](https://taglab.isti.cnr.it/) (ISTI-CNR). Please cite TagLab
  if you use it in published work.
- **ArcGIS Pro SDK** — the add-in references Esri's
  `Esri.ArcGISPro.Extensions30` assemblies, which require a licensed
  ArcGIS Pro installation.

---

### A note on privacy

If you fork or redistribute this project, keep these out of version
control (they are already in `.gitignore`):

- `.claude/` and any local editor/agent settings — they embed absolute
  paths containing your Windows username
- `config.json` — machine-specific paths and port
- `install.log` and `*.log` — contain your username and full local paths
- `csharp_addin/**/obj/` and `bin/` — MSBuild writes your absolute paths
  into `project.assets.json` and friends
- Anything holding a Hugging Face token
