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

        /// <summary>Current inference engine ("sam" | "ritm"),
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
            _engine = string.IsNullOrWhiteSpace(cfg.Engine)
                ? "sam" : cfg.Engine;
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
        }

        protected override bool CanUnload() => true;

        protected override void Uninitialize()
        {
            SamServerManager.Stop();
            base.Uninitialize();
        }
    }
}
