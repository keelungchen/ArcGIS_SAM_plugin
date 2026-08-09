using System;
using System.Threading.Tasks;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace SAM3Interactive
{
    /// <summary>Add-in module: holds ribbon state and shuts the
    /// Python server down when ArcGIS Pro unloads the add-in.</summary>
    internal class SamModule : Module
    {
        private static SamModule _this;

        public static SamModule Current =>
            _this ??= (SamModule)FrameworkApplication.FindModule(
                "SAM3Interactive_Module");

        /// <summary>Layer URI of the imagery layer picked in the ribbon
        /// (null = first raster layer in the map).</summary>
        public static string RasterLayerUri { get; set; }

        /// <summary>Layer URI of the target polygon layer picked in the
        /// ribbon (null = first editable polygon layer).</summary>
        public static string TargetLayerUri { get; set; }

        private static string _engine;
        private static string _modelId;

        /// <summary>Bumped whenever the model selection changes so the
        /// tool knows to rebuild its work area with the new model.</summary>
        public static int SettingsVersion { get; private set; }

        /// <summary>Current inference engine ("ritm" | "sam"),
        /// initialised from config.json.</summary>
        public static string CurrentEngine
        {
            get
            {
                if (_engine == null)
                    LoadModelSelection();
                return _engine;
            }
        }

        /// <summary>Current SAM model id, initialised from config.json.</summary>
        public static string CurrentModelId
        {
            get
            {
                if (_modelId == null)
                    LoadModelSelection();
                return _modelId;
            }
        }

        private static void LoadModelSelection()
        {
            var cfg = ServerConfig.Load();
            // RITM is the default: it loads in a second or two, runs
            // fine on the CPU and needs no image embedding, so the
            // first click comes back fast. The much heavier SAM
            // weights are only loaded once SAM is picked in the
            // ribbon. Without the RITM weights, fall back to SAM.
            _engine = string.IsNullOrWhiteSpace(cfg.Engine)
                ? (cfg.HasRitmCheckpoint() ? "ritm" : "sam")
                : cfg.Engine;
            _modelId = string.IsNullOrWhiteSpace(cfg.ModelId)
                ? "facebook/sam2.1-hiera-tiny" : cfg.ModelId;
        }

        /// <summary>Set from the ribbon model drop-down; persists the
        /// choice into config.json so it survives restarts.</summary>
        public static void SetModelSelection(string engine, string modelId)
        {
            _engine = engine;
            _modelId = modelId;
            SettingsVersion++;
            try
            {
                var cfg = ServerConfig.Load();
                cfg.Engine = engine;
                if (!string.IsNullOrWhiteSpace(modelId))
                    cfg.ModelId = modelId;
                cfg.Save();
            }
            catch
            {
                // Persisting is best-effort; the in-memory choice wins.
            }
            // Load the newly picked model right away instead of making
            // the next click pay for it - this is what keeps SAM out of
            // memory until it is actually selected.
            SamServerManager.RequestWarm(engine, modelId);
        }

        protected override bool Initialize()
        {
            _ = AutoStartServerAsync();
            return base.Initialize();
        }

        /// <summary>Bring the server up in the background while the
        /// user is still setting up the map, so switching to Click
        /// Segment never waits. Skipped when the add-in is not
        /// configured yet or auto_start_server is false.</summary>
        private static async Task AutoStartServerAsync()
        {
            try
            {
                // Let ArcGIS Pro finish its own start-up first: the
                // Python process imports arcpy and torch and would
                // otherwise compete with it for disk and CPU.
                await Task.Delay(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                if (_unloading)
                    return;   // Pro closed again before we got here
                var cfg = ServerConfig.Load();
                if (!cfg.AutoStartServer || cfg.Validate() != null)
                    return;
                await SamServerManager.EnsureRunningAsync()
                    .ConfigureAwait(false);
                if (_unloading)
                    SamServerManager.Stop();
            }
            catch
            {
                // Best effort: the tool starts the server on demand.
            }
        }

        protected override bool CanUnload() => true;

        private static volatile bool _unloading;

        protected override void Uninitialize()
        {
            _unloading = true;
            SamServerManager.Stop();
            base.Uninitialize();
        }
    }
}
