using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAM3Interactive
{
    /// <summary>Add-in configuration stored in
    /// %LOCALAPPDATA%\SAM3Interactive\config.json. Created by
    /// scripts\install_addin_config.bat (or by hand).</summary>
    internal class ServerConfig
    {
        [JsonPropertyName("python_exe")]
        public string PythonExe { get; set; } = "";

        [JsonPropertyName("server_script")]
        public string ServerScript { get; set; } = "";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 8765;

        /// <summary>"ritm" (TagLab's RITM network) or "sam" (SAM2/SAM3
        /// via Hugging Face). Empty = decide automatically: RITM when
        /// its checkpoint is there, otherwise SAM (see
        /// SamModule.LoadModelSelection).</summary>
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";

        [JsonPropertyName("model_id")]
        public string ModelId { get; set; } = "facebook/sam2.1-hiera-tiny";

        [JsonPropertyName("ritm_checkpoint")]
        public string RitmCheckpoint { get; set; } = "";

        [JsonPropertyName("max_image_size")]
        public int MaxImageSize { get; set; } = 2048;

        /// <summary>Start the Python server shortly after ArcGIS Pro
        /// launches so the Click Segment tool never has to wait for it.
        /// Set to false to only start it on demand (or manually with
        /// the ribbon's 'Start Server' button).</summary>
        [JsonPropertyName("auto_start_server")]
        public bool AutoStartServer { get; set; } = true;

        /// <summary>RITM-only installation: the SAM dependencies
        /// (transformers / accelerate / huggingface_hub) were not
        /// installed, so the SAM entries are hidden from the ribbon
        /// drop-down instead of failing at the first click. Written by
        /// INSTALL_RITM_ONLY.bat.</summary>
        [JsonPropertyName("ritm_only")]
        public bool RitmOnly { get; set; }

        public static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SAM3Interactive");

        public static string ConfigPath =>
            Path.Combine(ConfigDir, "config.json");

        public static string LogPath =>
            Path.Combine(ConfigDir, "server.log");

        /// <summary>Load the config, creating a template with guessed
        /// defaults when it does not exist yet.</summary>
        public static ServerConfig Load()
        {
            if (!File.Exists(ConfigPath))
            {
                var guess = new ServerConfig
                {
                    PythonExe = Path.Combine(
                        Environment.GetFolderPath(Environment
                            .SpecialFolder.LocalApplicationData),
                        @"ESRI\conda\envs\sam3_env\python.exe"),
                    ServerScript = "",
                };
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(
                    guess, new JsonSerializerOptions
                    { WriteIndented = true }));
                return guess;
            }
            return JsonSerializer.Deserialize<ServerConfig>(
                File.ReadAllText(ConfigPath)) ?? new ServerConfig();
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(
                this, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Configured RITM checkpoint, or the repository
        /// default (models\ritm_corals.pth next to the server
        /// script). Empty when the path cannot be derived.</summary>
        public string ResolveRitmCheckpoint()
        {
            if (!string.IsNullOrWhiteSpace(RitmCheckpoint))
                return RitmCheckpoint;
            if (string.IsNullOrWhiteSpace(ServerScript))
                return "";
            var repoDir = Path.GetDirectoryName(
                Path.GetDirectoryName(ServerScript));
            return repoDir == null
                ? ""
                : Path.Combine(repoDir, "models", "ritm_corals.pth");
        }

        /// <summary>True when the RITM weights are installed, i.e. RITM
        /// can serve as the default engine.</summary>
        public bool HasRitmCheckpoint()
        {
            var path = ResolveRitmCheckpoint();
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }

        /// <summary>Null when valid, otherwise a user-facing reason.</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(PythonExe) ||
                !File.Exists(PythonExe))
                return "python_exe not found: '" + PythonExe + "'.";
            if (string.IsNullOrWhiteSpace(ServerScript) ||
                !File.Exists(ServerScript))
                return "server_script not found: '" + ServerScript + "'.";
            return null;
        }
    }
}
