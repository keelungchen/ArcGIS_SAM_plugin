# ArcGIS SAM Plugin — SAM 3 分割工具箱 + 即時互動增益集

**[English README](README.md)**

將 Meta 的 **Segment Anything Model（SAM 2.1 / SAM 3）** 以及 TagLab 的
**RITM** 點擊網路整合進 **ArcGIS Pro 3.x**（開發與測試版本為 3.6.1，
Windows 10/11）。

本專案包含兩個獨立元件，共用同一套 Python 核心：

| 元件 | 說明 | 適合的情境 |
|---|---|---|
| **Python 工具箱**（`SAM3_Toolbox.pyt`） | 標準 ArcGIS 地理處理工具 | 對整個範圍做批次／無人值守分割 |
| **SAM Interactive C# 增益集**（`csharp_addin/`） | ArcGIS Pro 功能區增益集，含即時地圖工具 | TagLab 風格的逐點數化，含即時遮罩預覽 |

增益集透過 `http://127.0.0.1:<port>` 與本機 Python 推論伺服器
（`python_server/sam_server.py`）溝通。伺服器會從目前的地圖檢視凍結一個
**工作區（work area）**，並快取該區域的影像 embedding，因此即使在很大的
影像上，連續點擊也能維持流暢。

---

## 目錄

1. [功能](#1-功能)
2. [系統需求](#2-系統需求)
3. [你必須自行提供的憑證](#3-你必須自行提供的憑證)
4. [本 repo 未包含的檔案](#4-本-repo-未包含的檔案)
5. [安裝方式一：一鍵安裝](#5-安裝方式一一鍵安裝)
6. [安裝方式二：手動安裝](#6-安裝方式二手動安裝)
7. [使用 Python 工具箱](#7-使用-python-工具箱)
8. [使用互動增益集](#8-使用互動增益集)
9. [設定參數說明](#9-設定參數說明)
10. [從原始碼建置](#10-從原始碼建置)
11. [專案結構](#11-專案結構)
12. [疑難排解](#12-疑難排解)
13. [更新方式](#13-更新方式)
14. [版本紀錄](#14-版本紀錄)
15. [授權與致謝](#15-授權與致謝)

---

## 1. 功能

### Python 工具箱 — 5 個地理處理工具

| # | 工具 | 提示（prompt）輸入 | 輸出 |
|---|---|---|---|
| 1 | **Segment With Text Prompt**<br>文字提示分割 | 簡短的英文名詞片語（例如 `building`、`coral`、`car`） | 範圍內所有符合的物件 |
| 2 | **Segment With Point Prompts**<br>點提示分割 | 點圖層；可選一個整數欄位，1 = 前景、0 = 背景 | 每個點產生一個物件，或所有點合成一個物件 |
| 3 | **Segment With Box Prompts**<br>框提示分割 | 面圖層（以外接矩形作為 box） | 每個面產生一個物件 |
| 4 | **Segment Everything**<br>全自動分割 | 不需要，自動產生點陣列 | 範圍內偵測到的所有物件 |
| 5 | **Interactive Edit（正負點擊）** | 含正／負點擊的點圖層 | 每次執行產生一個物件，附加到既有面圖層；僅分析目前地圖檢視範圍 |

所有工具都會輸出到面圖徵類別、沿用地圖的空間參考，並在推論前把匯出的
影像重新取樣到 `DEFAULT_MAX_IMAGE_SIZE`（長邊 2048 像素）。

### 互動增益集

- **即時點擊分割** — 左鍵 = 正點、右鍵 = 負點；每點一次遮罩預覽就重繪。
- **功能區模型下拉選單** — 可在 RITM、SAM 2.1 Tiny、SAM 2.1 Small、
  SAM 3 之間切換，**不需要編輯任何設定檔**，選擇會自動保存；切換後
  新模型會立刻在背景開始載入，不會讓你的下一次點擊等待。
- **凍結工作區** — 第一次點擊時擷取範圍並快取其 embedding，之後的點擊
  幾乎是即時反應。
- **地圖疊加面板** — 顯示目前模型、點擊數量、信心分數與狀態，並有
  儲存／清除點擊／重設工作區按鈕。
- **快速鍵**：`Space` = 存成多邊形、`Ctrl+Z` = 復原上一次點擊、
  `Esc` = 清除所有點擊（工作區保留）。
- **背景預熱** — ArcGIS Pro 啟動幾秒後推論伺服器就會自動啟動，並在
  背景載入 arcpy 與所選模型，因此切換到 *Click Segment* 不需要等待。
  想關閉可在 `config.json` 設 `"auto_start_server": false`。

### 推論引擎

| 引擎 | 模型 | 大小 | 需授權？ | 說明 |
|---|---|---|---|---|
| `ritm`（預設） | `ritm_corals.pth` | 約 39 MB | 否 | TagLab 針對珊瑚微調的點擊網路；一兩秒載入、對 CPU 友善、幾乎不吃 VRAM，且不需要 embedding 階段 |
| `sam` | `facebook/sam2.1-hiera-tiny` | 約 155 MB | **否** | 幾秒內載入，點擊品質是 SAM 等級 |
| `sam` | `facebook/sam2.1-hiera-small` | 約 185 MB | **否** | 品質略好 |
| `sam` | `facebook/sam3` | 數 GB | **是** | 品質最好且支援文字提示；需接受 Meta 授權條款 |

SAM 權重只有在你真的於功能區選了 SAM 模型時才會下載／載入 — 預設的
RITM 設定完全不會碰到它們。若缺少 `models/ritm_corals.pth`，增益集會
自動退回 `facebook/sam2.1-hiera-tiny`。

---

## 2. 系統需求

- **ArcGIS Pro 3.x**，Windows 10 或 11（目標版本 3.6.x）
- **約 15 GB 可用磁碟空間** — 複製的 conda 環境 + PyTorch + 模型權重
- **強烈建議 NVIDIA GPU，VRAM ≥ 8 GB**
  （CPU 也能跑但很慢；RITM 引擎是對 CPU 友善的選項）
- **`transformers >= 4.57`** — 提供 `Sam3Model` / `Sam3TrackerModel`
- **`scikit-image`**（安裝在 `sam3_env` 內）— 互動增益集需要
- **免費的 Hugging Face 帳號** — 只有在你要使用受管制的
  `facebook/sam3` 模型時才需要（見下一節）
- **.NET 8 SDK** — 只有在你要自行重新編譯 C# 增益集時才需要

---

## 3. 你必須自行提供的憑證

> **本 repo 不含任何 API 金鑰、token 或帳號憑證。**
> 程式中沒有任何硬編碼的憑證 — 以下所有憑證都是在執行時從你自己的
> 電腦讀取。你必須自行申請並設定。

### 3.1 Hugging Face 存取權杖（僅 `facebook/sam3` 需要）

預設引擎 `ritm` 與備援的 `facebook/sam2.1-hiera-tiny` 都 **不受管制**，
完全不需要登入。
只有當你想使用 SAM 3 時才需要權杖 — SAM 3 是文字提示分割（工具 1）
以及最高品質點擊結果的基礎。

**如何取得：**

1. 到 <https://huggingface.co/join> 註冊一個免費帳號。
2. 前往 <https://huggingface.co/facebook/sam3>，點擊
   **Agree and access repository** 接受 Meta 的模型授權條款。
   多數情況下會立即開通。
3. 前往 **Settings → Access Tokens**
   （<https://huggingface.co/settings/tokens>），點擊
   **Create new token**。選擇 **Read** 權限即可 — 不要為了這個用途
   建立 Write 權杖。
4. 複製權杖，格式類似 `hf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`。
   **請把它當成密碼看待。**

**如何使用（擇一）：**

```bat
:: 建議做法 — 互動式登入，權杖會存到你的使用者設定檔
"%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe" -m huggingface_hub.commands.huggingface_cli login

:: 或者，若 sam3_env 內的 hf 指令已在 PATH 中：
hf auth login
```

依提示貼上權杖即可。它會被存到
`%USERPROFILE%\.cache\huggingface\token` — **在本 repo 之外**，
所以絕對不會不小心被 commit 進版本控制。

或者，只在目前的 session 設定環境變數：

```powershell
$env:HF_TOKEN = "hf_你的權杖"
```

**千萬不要**把權杖貼進 `sam3_tools/config.py`、`config.json`，
或本 repo 內的任何檔案。

**驗證是否成功：**

```bat
"%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe" scripts\check_install.py
```

應該會看到 `[ OK ] Hugging Face access to facebook/sam3 confirmed`。

### 3.2 本機伺服器連接埠

增益集與 Python 伺服器透過 `127.0.0.1` 上的某個埠溝通，埠號存在你本機
的 `config.json`（預設 `8765`）。這是**僅限本機回送位址**，不會對外
網路開放；但如果你的電腦上 8765 已被占用，請改成別的埠。一鍵安裝程式
會自動挑選一個空閒的埠。

### 3.3 與電腦相關的路徑

`%LOCALAPPDATA%\SAM3Interactive\config.json` 內含指向**你的** Python
執行檔與**你的** repo 複本的絕對路徑。它是由
`scripts\install_addin_config.bat` 在你的電腦上產生的，並已列入
`.gitignore` — 請不要 commit，也不要直接拿別人的檔案來用。

---

## 4. 本 repo 未包含的檔案

為了讓 repo 保持輕量並尊重第三方授權，以下項目**未納入版本控制**，
需要時才下載或建置：

| 缺少的項目 | 大小 | 如何取得 |
|---|---|---|
| `models/ritm_corals.pth` | 約 39 MB | 執行 `scripts\get_ritm.bat`，或自行從 <http://taglab.isti.cnr.it/models/ritm_corals.pth> 下載後放進 `models\` |
| `python_server/isegm/` | 約 1 MB | 由 `scripts\get_ritm.bat` 下載（取得 TagLab 保存的 RITM 推論程式碼） |
| SAM 2.1 / SAM 3 權重 | 155 MB ～ 數 GB | 首次使用時由 `transformers` 自動下載到 `%USERPROFILE%\.cache\huggingface` |
| `dist_package\ArcGIS_SAM_plugin_Setup.zip` | 約 36 MB | 自行建置：`powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1` |
| `csharp_addin\**\obj`、`bin` | — | 由 `scripts\build_addin.ps1` 重新產生 |

已編譯好的增益集 `csharp_addin\dist\SAM3Interactive.esriAddinX`
**有**包含在 repo 內，所以一般安裝不需要 Visual Studio 或 .NET SDK。

---

## 5. 安裝方式一：一鍵安裝

適合部署到一台全新的電腦。

1. **在已有 repo 的電腦上建置可攜式安裝包：**

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1
   ```

   會產生 `dist_package\ArcGIS_SAM_plugin_Setup.zip`。

2. **把 zip 複製**到目標電腦，解壓縮到任意位置。

3. **關閉 ArcGIS Pro**，然後雙擊 **`INSTALL.bat`**。

安裝程式會依序執行：

1. 找到 ArcGIS Pro 並檢查版本
2. 檢查可用磁碟空間
3. 建立 `sam3_env` conda 環境（複製自 `arcgispro-py3`）
4. 安裝 PyTorch（自動偵測 GPU）
5. 安裝其餘 Python 套件
6. 把執行檔複製到 `%LOCALAPPDATA%\SAM3Interactive\app`
7. 寫入 `config.json`，自動挑選空閒的埠
8. 安裝增益集（`.esriAddinX`）
9. 驗證 Python 環境

每個失敗都會印出一行 `PROBLEM:` 與對應的 `FIX:`，全部記錄在
`INSTALL.bat` 旁邊的 `install.log`。重新執行會跳過已完成的步驟。

**選項：**

```bat
INSTALL.bat -Recreate   :: 刪除並從頭重建 sam3_env
INSTALL.bat -CpuOnly    :: 強制安裝 CPU 版 PyTorch
```

安裝完成後，若要使用 SAM 3，請依
[3.1 節](#31-hugging-face-存取權杖僅-facebooksam3-需要)完成 Hugging Face 登入。

### 純 RITM 版（RITM-only）

如果你只用互動式 Click Segment 搭配 RITM（珊瑚／底棲製圖的常見情況），
可以改裝精簡版：雙擊 **`INSTALL_RITM_ONLY.bat`**，或打包一份只含 RITM
的安裝檔：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1 -RitmOnly
```

會產生 `dist_package\ArcGIS_SAM_plugin_RITM_Setup.zip`，裡面唯一的進入點
就是 `INSTALL_RITM_ONLY.bat`。

| | 完整版 | 純 RITM 版 |
|---|---|---|
| `sam3_env` conda 複製 | 有 | 有 |
| PyTorch（CUDA 版約 3 GB） | 有 | 有 |
| `transformers`、`accelerate`、`huggingface_hub` | 有 | **無** |
| 首次使用時下載 SAM 權重 | 155 MB 起 | **完全不用** |
| Hugging Face 帳號／授權 | 只有 SAM 3 需要 | **永遠不需要** |
| `SAM3_Toolbox.pyt` 地理處理工具 | 有 | **無**（五個工具全部走 SAM） |
| Click Segment 增益集（RITM） | 有 | 有 |

**步驟數其實一樣**（都是雙擊一個 bat），而且最花時間的兩步（複製 conda
環境、安裝 PyTorch）完全不變，所以不會快很多。真正省下的是：少下載幾百
MB、不需要 Hugging Face 帳號、不會遇到 `transformers` 版本衝突（常見的
安裝失敗原因），以及功能區下拉選單只會列出 RITM，不會出現選了就報錯的
SAM 選項。安裝時會在 `config.json` 記錄 `"ritm_only": true`；把它改成
`false`（並補裝 SAM 套件）就能把 SAM 選項找回來。

---

## 6. 安裝方式二：手動安裝

1. **關閉 ArcGIS Pro**，執行：

   ```bat
   scripts\setup_env.bat
   ```

   會把 `arcgispro-py3` 複製成 `sam3_env`，安裝 PyTorch、
   `transformers`、`scikit-image` 等套件，並切換 Pro 的作用中環境。

2. **（可選，使用 SAM 3 才需要）** 到
   <https://huggingface.co/facebook/sam3> 接受授權並登入 — 見
   [3.1 節](#31-hugging-face-存取權杖僅-facebooksam3-需要)。

3. **驗證環境：**

   ```bat
   "%LOCALAPPDATA%\ESRI\conda\envs\sam3_env\python.exe" scripts\check_install.py
   ```

   加上 `--download` 會順便現在就把 SAM 3 權重抓下來（數 GB）。

4. **載入工具箱：** 啟動 ArcGIS Pro，確認作用中環境是 `sam3_env`
   （專案 → Package Manager），然後
   **目錄 → 工具箱 → 右鍵 → 加入工具箱 → `SAM3_Toolbox.pyt`**。

5. **安裝增益集：** 雙擊
   `csharp_addin\dist\SAM3Interactive.esriAddinX`。

6. **寫入增益集設定**（只需一次）：

   ```bat
   scripts\install_addin_config.bat
   ```

7. **（可選）啟用 RITM 引擎：**

   ```bat
   scripts\get_ritm.bat
   ```

   會下載 isegm 程式碼與 `ritm_corals.pth`、安裝 `opencv-python`
   與 `easydict`，並把設定切換為 `"engine": "ritm"`。

---

## 7. 使用 Python 工具箱

1. 依上述步驟把 `SAM3_Toolbox.pyt` 加入專案。
2. 把地圖移動到你要處理的區域 — 有數個工具使用**目前的檢視範圍**，
   所以請先縮放到適當位置。範圍越小，速度越快、結果也越準。
3. 開啟工具，填入：
   - **輸入影像圖層** — 要分割的影像
   - **提示輸入** — 依工具不同為文字／點／面
   - **輸出圖徵類別** — 多邊形要寫到哪裡
   - **Advanced → Model ID** — 覆寫 Hugging Face 模型 id
     （預設值來自 `sam3_tools/config.py`）
   - **Advanced → 門檻值** — 分數門檻（預設 `0.5`）與遮罩二值化門檻
     （預設 `0.5`）
4. 執行。某個模型的第一次執行會下載權重，可能需要一段時間；之後就會
   使用快取。

**小技巧**

- 文字提示請使用**簡短的英文名詞片語**（`building`，而不是
  `the large red building on the left`）。
- 文字提示需要 SAM 3，也就需要完成 Hugging Face 登入。
- 使用點提示時，加一個整數欄位，`1` = 前景、`0` = 背景，可以引導模型
  避開周圍的物件。
- 如果出現太多細碎的誤判，請提高分數門檻，或調高
  `sam3_tools/config.py` 中的 `DEFAULT_MIN_MASK_AREA_PX`。

---

## 8. 使用互動增益集

1. 啟動 ArcGIS Pro，開啟 **SAM Segmentation**（SAM 分割）功能區頁籤。
2. 從功能區下拉選單挑選你的 **Imagery（影像）** 圖層與
   **Target（目標）** 面圖層，並選擇一個 **Model（模型）**。
3. 點擊 **Start Server（啟動伺服器）**（arcpy 與模型預載時會出現進度
   對話框）。等待狀態轉為綠色。
4. 點擊 **Segment** 工具，然後在地圖上點擊：

   | 動作 | 效果 |
   |---|---|
   | **左鍵** | 加入一個**正點**（在物件內部） |
   | **右鍵** | 加入一個**負點**（在物件外部） |
   | **Space** | 把目前遮罩存成多邊形，寫入目標圖層 |
   | **Ctrl+Z** | 復原上一次點擊 |
   | **Esc** | 清除所有點擊；工作區保留 |

5. **工作區**會在你第一次點擊時從當時的地圖檢視凍結。使用功能區的
   **New Work Area** / **Cancel Work Area** 按鈕來移動或取消它。
6. 地圖上的疊加面板會顯示模型、點擊數量、信心分數與狀態，並有
   儲存／清除點擊／重設工作區按鈕。

**小技巧**

- 先在物件中央按**一個**正點，然後只在遮罩溢出的地方補負點。
- 第一次點擊前請先縮放到合理比例。伺服器會拒絕涵蓋超過
  `MAX_WORKAREA_NATIVE_PX`（5.12 億個原生影像像素）的工作區並要求你
  放大 — 這是為了避免耗時數分鐘、看起來像當機的匯出作業。
- 切換模型會重啟伺服器；工作區會在下一次點擊時重建。
- 對於珊瑚／底棲影像，**RITM** 引擎的開箱表現常常勝過 SAM，而且在
  CPU 上就跑得很順。

---

## 9. 設定參數說明

### 9.1 `sam3_tools/config.py` — 工具箱預設值（已納入版本控制）

| 常數 | 預設值 | 意義 |
|---|---|---|
| `DEFAULT_MODEL_ID` | `facebook/sam3` | 地理處理工具使用的模型 |
| `DEFAULT_INTERACTIVE_ENGINE` | `ritm` | `ritm` 或 `sam` |
| `DEFAULT_INTERACTIVE_MODEL_ID` | `facebook/sam2.1-hiera-tiny` | 增益集使用的模型 |
| `RITM_CHECKPOINT_FILENAME` | `ritm_corals.pth` | 在 `models\` 內尋找 |
| `DEFAULT_MAX_IMAGE_SIZE` | `2048` | 送進模型的影像長邊像素 |
| `ABSOLUTE_MAX_IMAGE_SIZE` | `8192` | 防止誤操作匯出超大影像的硬上限 |
| `MAX_WORKAREA_NATIVE_PX` | `512_000_000` | 超過此原生像素數的工作區會被拒絕 |
| `DEFAULT_SCORE_THRESHOLD` | `0.5` | 文字提示的信心門檻 |
| `DEFAULT_MASK_THRESHOLD` | `0.5` | 遮罩二值化門檻 |
| `STRETCH_PERCENTILES` | `(2.0, 98.0)` | 非 8-bit 影像的百分位拉伸 |
| `DEFAULT_GRID_POINTS_PER_SIDE` | `32` | 「全自動分割」的點陣列密度 |
| `DEFAULT_IOU_DEDUP_THRESHOLD` | `0.75` | 去除重複的 IoU 門檻 |
| `DEFAULT_MIN_MASK_AREA_PX` | `64` | 小於此面積的遮罩會被丟棄 |

### 9.2 `%LOCALAPPDATA%\SAM3Interactive\config.json` — 增益集設定（**絕對不要 commit**）

由 `scripts\install_addin_config.bat` 產生，內容與電腦相關：

```jsonc
{
  "python_exe":      "C:\\Users\\<你的使用者名稱>\\AppData\\Local\\ESRI\\conda\\envs\\sam3_env\\python.exe",
  "server_script":   "<你的-repo-路徑>\\python_server\\sam_server.py",
  "port":            8765,
  "engine":          "ritm",
  "model_id":        "facebook/sam2.1-hiera-tiny",
  "ritm_checkpoint": "<你的-repo-路徑>\\models\\ritm_corals.pth",
  "max_image_size":  2048,
  "auto_start_server": true
}
```

請把 `<你的使用者名稱>` 與 `<你的-repo-路徑>` 換成你自己的值 —
或者直接執行 `install_addin_config.bat`，它會自動依你的電腦填好。
伺服器記錄檔就在旁邊：
`%LOCALAPPDATA%\SAM3Interactive\server.log`。

---

## 10. 從原始碼建置

重新編譯 C# 增益集只需要 **.NET 8 SDK**，不需要 Visual Studio：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_addin.ps1
```

輸出：`csharp_addin\dist\SAM3Interactive.esriAddinX`。

建置可攜式安裝包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make_package.ps1
```

輸出：`dist_package\ArcGIS_SAM_plugin_Setup.zip`。

---

## 11. 專案結構

```
SAM3_Toolbox.pyt              工具箱進入點（把這個加進 ArcGIS Pro）
INSTALL.bat                   一鍵安裝進入點
INSTALL_RITM_ONLY.bat         同上，精簡版（只裝 RITM 引擎）
sam3_tools/                   Python 核心套件（必須與 .pyt 放在一起）
  config.py                   預設值：模型 id、門檻值、影像尺寸
  engine.py                   SAM 2/3 推論 + InteractiveSession（embedding 快取）
  ritm_engine.py              RITM 互動推論（TagLab 的點擊網路）
  geoutils.py                 影像匯出、座標轉換、遮罩 -> 多邊形
  masktools.py                遮罩後處理（輪廓、平滑；不依賴 arcpy）
python_server/
  sam_server.py               增益集使用的本機 HTTP 推論伺服器
  isegm/                      RITM 原始碼        [未納入 - 由 get_ritm.bat 取得]
models/
  ritm_corals.pth             TagLab 珊瑚權重     [未納入 - 由 get_ritm.bat 取得]
csharp_addin/
  SAM3Interactive/            ArcGIS Pro SDK 增益集原始碼（net8.0-windows）
    Config.daml               功能區／頁籤／按鈕定義
    InteractiveSegmentTool.cs 地圖工具：點擊處理、即時預覽
    SamServerClient.cs        連接 Python 伺服器的 HTTP 用戶端
    SamServerManager.cs       伺服器生命週期（啟動／停止／健康檢查）
    ServerConfig.cs           config.json 結構與驗證
    ModelComboBox.cs          模型下拉選單
    SegmentOverlayView*.*     地圖疊加面板（WPF）
  dist/                       編譯好的 SAM3Interactive.esriAddinX（雙擊安裝）
installer/
  install.ps1                 一鍵安裝程式的主要邏輯
scripts/
  setup_env.bat               conda 環境設定
  check_install.py            環境／模型存取權驗證
  install_addin_config.bat    寫入增益集的 config.json
  get_ritm.bat                啟用 RITM 引擎（下載程式碼與權重）
  fetch_isegm.ps1             下載 TagLab 保存的 isegm 程式碼
  build_addin.ps1             不需 Visual Studio 即可建置與打包增益集
  make_package.ps1            建置可攜式安裝 zip
docs/
  User_Manual.html            完整繁體中文手冊
```

---

## 12. 疑難排解

| 症狀 | 原因 | 解法 |
|---|---|---|
| `Cannot access facebook/sam3` | 未接受授權，或未登入 | 到 <https://huggingface.co/facebook/sam3> 接受條款，再依 [3.1](#31-hugging-face-存取權杖僅-facebooksam3-需要) 登入 |
| Hugging Face 回傳 `401 Unauthorized` | 權杖過期或已被撤銷 | 重新建立一個 **Read** 權杖並再次登入 |
| `transformers is too old, missing: Sam3Model` | `transformers < 4.57` | 在 `sam3_env` 內執行 `pip install --upgrade "transformers>=4.57"` |
| 缺少 `scikit-image` | 環境是 v2.0.0 之前建立的 | 在 `sam3_env` 內執行 `pip install scikit-image` |
| **OMP Error #15** ／啟動時當掉 | OpenMP 執行環境重複載入 | 2.2.0 已修正 — 請更新，或設定 `KMP_DUPLICATE_LIB_OK=TRUE` |
| 伺服器無法啟動 | `config.json` 路徑錯誤 | 重新執行 `scripts\install_addin_config.bat`；查看 `%LOCALAPPDATA%\SAM3Interactive\server.log` |
| 連接埠已被占用 | 其他程式占用 8765 | 修改 `config.json` 的 `"port"`，或重新執行一鍵安裝程式自動挑選 |
| 第一次點擊時出現「請放大」錯誤 | 工作區超過 `MAX_WORKAREA_NATIVE_PX` | 放大地圖，或調高 `sam3_tools/config.py` 的上限 |
| 找不到 RITM 權重 | 缺少 `models\ritm_corals.pth` | 執行 `scripts\get_ritm.bat` |
| 工具箱的工具呈灰色 | Pro 沒有使用 `sam3_env` | 專案 → Package Manager → 切換作用中環境，重啟 Pro |
| 推論非常慢 | 正在使用 CPU | 用 `check_install.py` 確認是否顯示 `CUDA available`；或改用 RITM 引擎 |

完整的疑難排解章節（繁體中文）請見
[`docs/User_Manual.html`](docs/User_Manual.html)，用任何瀏覽器開啟即可。

---

## 13. 更新方式

- **外掛程式碼** — 直接覆蓋 `SAM3_Toolbox.pyt` 與 `sam3_tools\`，
  然後在目錄中對工具箱按右鍵 → **重新整理**。
- **套件** — 啟用 `sam3_env` 後執行
  `pip install --upgrade transformers accelerate`，再跑一次
  `check_install.py`。
- **模型** — 在各工具的 *Advanced → Model ID* 填入新的 Hugging Face
  repo id，或修改 `sam3_tools/config.py` 的 `DEFAULT_MODEL_ID`。
- **ArcGIS Pro 大版本升級之後** — 刪除 `sam3_env` 並重新執行
  `setup_env.bat`（手冊 §8.4）；提高 `Esri.ArcGISPro.Extensions30`
  套件版本與 DAML 的 `desktopVersion`，然後重建增益集（手冊 §5.4）。

---

## 14. 版本紀錄

### 2.6.0 — 2026-08-08
啟動延遲最佳化。**預設引擎改為 RITM**（小、對 CPU 友善、不需 embedding
階段）；SAM 權重只有在功能區真的選了 SAM 模型時才會載入，而且一選就
立刻在背景預先載入。推論伺服器會在 **ArcGIS Pro 啟動約 10 秒後自行
啟動**，並在背景載入 arcpy 與模型（可用 `"auto_start_server": false`
關閉）。切換到 *Click Segment* 不再跳出擋畫面的「Starting the server」
對話框、也不再等待——伺服器在背景就緒，第一次點擊會接上同一個工作。
伺服器端：新增 `/warm` 端點、改用多執行緒 HTTP 伺服器（長時間
`set_image` 期間 `/ping` 仍可回應），啟動輪詢由每秒一次改為每 250 毫秒。

### 2.2.0 — 2026-07-05
UI 全面翻新：中文功能區（`SAM 分割` 頁籤）、**模型下拉選單**
（SAM 2.1 Tiny/Small、SAM 3、RITM — 可在 UI 直接切換並自動保存，
不需編輯設定檔）、地圖疊加面板（模型／點擊數／分數／狀態，加上儲存、
清除點擊、重設工作區按鈕）、伺服器啟動與工作區準備的進度對話框、
背景伺服器預熱（arcpy + 模型預載）。
修正：RITM 權重的 `models.isegm` 命名空間載入問題；OpenMP 執行環境
衝突導致的當機（OMP Error #15）；`get_ritm.bat` 中 isegm 下載無聲失敗
（原始 RITM repo 已消失 — 改為取得 TagLab 保存的版本）。

### 2.1.0 — 2026-07-04
雙引擎互動後端，可透過增益集設定（`engine`）切換。預設改為
`facebook/sam2.1-hiera-tiny`（約 155 MB，**不受管制**，數秒載入、
不需預熱）。新增 **RITM 引擎** — TagLab 的正負點擊工具所使用的完全相同
網路與珊瑚微調權重（`ritm_corals.pth`），對 CPU 友善、幾乎不吃 VRAM，
用 `scripts\get_ritm.bat` 啟用。新增 `sam3_tools/ritm_engine.py`；
SAM 引擎現在會依模型 id 自動選擇 SAM 2 / SAM 3 類別。

### 2.0.0 — 2026-07-04
全新 **SAM3 Interactive C# 增益集**：直接在地圖上進行 TagLab 風格的
即時點擊分割（左／右鍵 = 正／負點、即時遮罩預覽、Space 存檔、
Ctrl+Z 復原、Esc 重設）。工作區在第一次點擊時從當時的檢視凍結；
每個工作區快取影像 embedding。新增 `python_server/sam_server.py`
（本機推論伺服器）、`engine.InteractiveSession`、
`sam3_tools/masktools.py`、`scripts/build_addin.ps1`、
`scripts/install_addin_config.bat`；環境設定加入 `scikit-image`。

### 1.1.0 — 2026-07-04
新增工具 *5 - Interactive Edit（正負點擊）*（地理處理工具版本，
非即時，保留作為後備）：TagLab 風格的互動式分割。直接在地圖上點擊
正／負點，分析限制在目前的地圖檢視範圍（對大影像友善），結果多邊形
附加到既有的目標圖層；可選邊界平滑。

### 1.0.0 — 2026-07-03
初版：文字／點／框提示、自動分割、HTML 使用手冊。

---

## 15. 授權與致謝

本 repo 包含的是 ArcGIS 整合程式碼。它所使用的模型與第三方元件各有
**自己的授權條款**，你必須自行檢視並接受：

- **SAM 2.1 / SAM 3** — 模型權重由 **Meta** 透過 Hugging Face 發布
  （`facebook/sam2.1-hiera-tiny`、`facebook/sam3`），適用 Meta 自己的
  授權條款。`facebook/sam3` 是**受管制（gated）**的 repo，使用前必須
  在 <https://huggingface.co/facebook/sam3> 接受條款。
- **RITM** — `isegm` 推論程式碼源自
  [SamsungLabs/ritm_interactive_segmentation](https://github.com/SamsungLabs/ritm_interactive_segmentation)
  （MIT）。原始 repo 已不存在，因此 `scripts\get_ritm.bat` 改為取得
  TagLab 保存的版本。
- **`ritm_corals.pth`** — 來自 [TagLab](https://taglab.isti.cnr.it/)
  （ISTI-CNR）的珊瑚微調權重。若用於發表的研究，請引用 TagLab。
- **ArcGIS Pro SDK** — 增益集參照 Esri 的
  `Esri.ArcGISPro.Extensions30` 組件，需要有授權的 ArcGIS Pro 安裝。

---

### 關於隱私的提醒

如果你要 fork 或散布本專案，請把以下項目排除在版本控制之外
（`.gitignore` 已經處理好）：

- `.claude/` 以及任何本機編輯器／代理設定 — 內含帶有你 Windows
  使用者名稱的絕對路徑
- `config.json` — 與電腦相關的路徑與連接埠
- `install.log` 與 `*.log` — 內含你的使用者名稱與完整本機路徑
- `csharp_addin/**/obj/` 與 `bin/` — MSBuild 會把你的絕對路徑寫進
  `project.assets.json` 等檔案
- 任何存有 Hugging Face 權杖的檔案
