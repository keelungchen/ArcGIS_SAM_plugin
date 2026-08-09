using System;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Mapping;

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

        private static string _targetLayerUri;

        /// <summary>Layer URI of the target polygon layer picked in the
        /// ribbon (null = first editable polygon layer).</summary>
        public static string TargetLayerUri
        {
            get => _targetLayerUri;
            set
            {
                if (_targetLayerUri == value)
                    return;
                _targetLayerUri = value;
                // Fields and their domains belong to the layer, so a
                // label picked for the previous one cannot carry over.
                ClearLabel();
            }
        }

        // ------------------------------------------------------------
        // Attribute label ("tag") applied to every saved polygon
        // ------------------------------------------------------------

        /// <summary>Field of the target layer that new polygons are
        /// tagged in (null = no tagging).</summary>
        public static string LabelFieldName { get; private set; }

        /// <summary>Value written into <see cref="LabelFieldName"/> for
        /// every saved polygon (null = nothing written).</summary>
        public static object LabelValue { get; private set; }

        /// <summary>Readable form of <see cref="LabelValue"/> (the coded
        /// value description) for the ribbon and the map panel.</summary>
        public static string LabelValueText { get; private set; }

        /// <summary>One-line label summary for the on-map panel.</summary>
        public static string LabelSummary =>
            LabelFieldName == null
                ? "Label: (none)"
                : string.Format("Label: {0} = {1}", LabelFieldName,
                    LabelValue == null ? "(not set)" : LabelValueText);

        /// <summary>Pick the field that carries the label; drops the
        /// previous value, which belonged to the previous field.</summary>
        public static void SetLabelField(string name)
        {
            LabelFieldName = name;
            LabelValue = null;
            LabelValueText = null;
            InteractiveSegmentTool.Instance?.RefreshPanel();
        }

        /// <summary>Pick the value written into the label field
        /// (null = save polygons without a label).</summary>
        public static void SetLabelValue(object value, string text)
        {
            LabelValue = value;
            LabelValueText = text;
            InteractiveSegmentTool.Instance?.RefreshPanel();
        }

        /// <summary>Forget field and value, and clear the ribbon
        /// drop-downs - their lists belong to the previous layer.
        /// Called directly rather than through an event so a re-created
        /// ribbon control cannot leave a stale subscription behind.</summary>
        public static void ClearLabel()
        {
            LabelFieldName = null;
            LabelValue = null;
            LabelValueText = null;
            LabelFieldComboBox.Instance?.ResetDisplay();
            LabelValueComboBox.Instance?.ResetDisplay();
            InteractiveSegmentTool.Instance?.RefreshPanel();
        }

        /// <summary>Polygon layer that saved segmentations are written
        /// to: the one picked in the ribbon, otherwise the first editable
        /// polygon layer of the map (null when there is none).</summary>
        internal static FeatureLayer FindTargetLayer()
        {
            var map = MapView.Active?.Map;
            if (map == null)
                return null;
            if (TargetLayerUri != null)
            {
                var picked = map.FindLayer(TargetLayerUri) as FeatureLayer;
                if (picked != null)
                    return picked;
            }
            // Fall back to an EDITABLE polygon layer only - writing into
            // a read-only layer fails and confuses the workflow.
            return map.GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .FirstOrDefault(l =>
                    l.ShapeType == esriGeometryType.esriGeometryPolygon &&
                    l.IsEditable);
        }

        /// <summary>True when the layer's extent intersects the map view.
        /// An extent that cannot be determined or projected counts as
        /// covering, so an odd data source never blocks the user. Must run
        /// on the MCT.</summary>
        internal static bool LayerCoversView(Layer layer, Envelope view,
            SpatialReference mapSr)
        {
            if (view == null || view.IsEmpty)
                return true;
            try
            {
                var extent = layer.QueryExtent();
                if (extent == null || extent.IsEmpty)
                    return true;
                if (mapSr != null && extent.SpatialReference != null &&
                    !extent.SpatialReference.IsEqual(mapSr))
                    extent = GeometryEngine.Instance.Project(extent, mapSr)
                        as Envelope;
                if (extent == null || extent.IsEmpty)
                    return true;
                return GeometryEngine.Instance.Intersects(view, extent);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Show a message in the ArcGIS Pro notification
        /// centre (the bell in the top right).</summary>
        internal static void Notify(string message)
        {
            FrameworkApplication.AddNotification(new Notification
            {
                Title = "SAM3 Interactive",
                Message = message,
            });
        }

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
            _engine = cfg.RitmOnly
                ? "ritm"
                : string.IsNullOrWhiteSpace(cfg.Engine)
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
