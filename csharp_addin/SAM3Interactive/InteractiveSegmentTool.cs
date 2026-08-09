using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.DDL;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using FieldDescription = ArcGIS.Core.Data.DDL.FieldDescription;

namespace SAM3Interactive
{
    /// <summary>TagLab-style interactive segmentation map tool.
    ///
    /// Left-click  = positive point (inside the object)
    /// Right-click = negative point (outside the object)
    /// Space       = save the previewed polygon to the target layer
    /// Ctrl+Z      = undo the last click
    /// Esc         = clear the current clicks (the work area stays)
    ///
    /// The work area (the image sent to SAM) is frozen from the current
    /// map view at the first click or via the "New Work Area" ribbon
    /// button, mirroring TagLab's behaviour, so very large rasters stay
    /// fast. Clicks outside the frozen work area are ignored with a
    /// warning; the work area is only replaced on request.</summary>
    internal class InteractiveSegmentTool : MapTool
    {
        private sealed class Click
        {
            public MapPoint ImagePoint;   // in the image spatial ref
            public double Col;
            public double Row;
            public int Label;             // 1 positive, 0 negative
            public IDisposable Marker;
        }

        /// <summary>Tool instance (the framework creates exactly one).
        /// The ribbon buttons act on it even while another map tool is
        /// current, so the work area survives using Select / Split /
        /// attribute editing in between.</summary>
        internal static InteractiveSegmentTool Instance { get; private set; }

        /// <summary>True while this tool is the current map tool.</summary>
        internal static bool IsToolActive { get; private set; }

        /// <summary>Feature class auto-created in the project default
        /// geodatabase when no target layer is picked.</summary>
        private const string DefaultTargetName = "SAM_Segments";

        private readonly List<Click> _clicks = new List<Click>();
        private ImageInfoDto _image;
        private SpatialReference _imageSr;
        private IDisposable _maskOverlay;
        private IDisposable _workAreaOverlay;
        private Polygon _previewPolygon;
        private double _lastScore;
        private bool _busy;
        // Non-null while a work area is being prepared; cancelling it
        // aborts the export/encoding request (Esc, ribbon 'Cancel Work
        // Area' or the panel buttons).
        private CancellationTokenSource _prepareCts;
        private int _settingsVersion = -1;
        private SegmentOverlayViewModel _panel;

        private CIMSymbolReference _maskSymbol;
        private CIMSymbolReference _workAreaSymbol;
        private CIMSymbolReference _positiveSymbol;
        private CIMSymbolReference _negativeSymbol;

        public InteractiveSegmentTool()
        {
            Instance = this;
            IsSketchTool = false;
            UseSnapping = false;
            OverlayControlID = "SAM3Interactive_SegmentOverlay";
            OverlayControlCanResize = false;
        }

        // ------------------------------------------------------------
        // Tool lifecycle
        // ------------------------------------------------------------

        protected override Task OnToolActivateAsync(bool active)
        {
            IsToolActive = true;
            _panel = OverlayEmbeddableControl as SegmentOverlayViewModel;
            if (_panel != null)
            {
                _panel.SaveRequested = () => _ = RunGuarded(CommitAsync);
                _panel.UndoRequested = () => _ = RunGuarded(UndoClickAsync);
                _panel.ClearRequested = () =>
                {
                    if (CancelPreparation())
                        return;
                    ClearClicks();
                    UpdatePanel();
                };
                _panel.ResetRequested = () =>
                {
                    if (CancelPreparation())
                        return;
                    ResetWorkArea();
                    UpdatePanel();
                };
            }
            UpdatePanel();
            // Activating the tool must feel instant, so the server is
            // never waited for here - it comes up in the background
            // (usually it is already warm, see SamModule.Initialize).
            // The first click joins the same task via SetWorkAreaAsync.
            _ = EnsureServerInBackgroundAsync();
            return Task.CompletedTask;
        }

        private async Task EnsureServerInBackgroundAsync()
        {
            if (SamServerManager.IsReady)
                return;
            UpdatePanel("Starting the inference server in the " +
                        "background - you can already zoom to the " +
                        "target object.");
            var error = await SamServerManager.EnsureRunningAsync();
            if (!IsToolActive)
                return;
            if (error != null)
                // No dialog for a background failure: it would pop up
                // out of nowhere. The first click reports it properly.
                UpdatePanel("Server start failed - press 'Start " +
                            "Server' on the ribbon for details.");
            else if (!_busy && _prepareCts == null)
                UpdatePanel();
        }

        protected override Task OnToolDeactivateAsync(bool hasMapViewChanged)
        {
            IsToolActive = false;
            if (_panel != null)
            {
                _panel.SaveRequested = null;
                _panel.UndoRequested = null;
                _panel.ClearRequested = null;
                _panel.ResetRequested = null;
            }
            // Keep the work area, clicks and preview: switching to
            // Select / Split / the attribute table and back must not
            // throw away the frozen image or its cached embedding.
            // Only a map view change invalidates the state.
            if (hasMapViewChanged)
                ResetWorkArea();
            return Task.CompletedTask;
        }

        private async Task RunGuarded(Func<Task> action)
        {
            if (_busy)
                return;
            _busy = true;
            try
            {
                await action();
            }
            catch (Exception exc)
            {
                Toast("Error: " + exc.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        // ------------------------------------------------------------
        // Ribbon button entry points (see WorkAreaButtons.cs)
        // ------------------------------------------------------------

        /// <summary>Freeze the current map view as the work area
        /// (replaces any existing one).</summary>
        internal void RequestNewWorkArea() => _ = RunGuarded(async () =>
        {
            var mapView = MapView.Active;
            if (mapView == null)
                return;
            ResetWorkArea();
            if (await SetWorkAreaAsync(mapView))
                UpdatePanel("Work area ready - left-click inside an " +
                            "object to start.");
            else
                UpdatePanel();
        });

        /// <summary>Abort an in-progress work area preparation, or
        /// discard the finished work area and all clicks.</summary>
        internal void RequestCancelWorkArea()
        {
            if (CancelPreparation())
                return;   // feedback comes from the cancelled task
            if (_busy)
                return;
            ResetWorkArea();
            UpdatePanel();
            Toast("Work area cancelled.");
        }

        /// <summary>Cancel the running work-area preparation, if any.
        /// Returns true when a preparation was cancelled.</summary>
        internal bool CancelPreparation()
        {
            var cts = _prepareCts;
            if (cts == null)
                return false;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            return true;
        }

        /// <summary>Undo the last click (same as Ctrl+Z).</summary>
        internal void RequestUndoClick() => _ = RunGuarded(UndoClickAsync);

        /// <summary>Remove all clicks but keep the work area
        /// (same as Esc).</summary>
        internal void RequestClearClicks()
        {
            if (_busy)
                return;
            ClearClicks();
            UpdatePanel();
        }

        private static string CurrentModelLabel()
        {
            if (SamModule.CurrentEngine == "ritm")
                return "Model: RITM (TagLab corals)";
            var id = SamModule.CurrentModelId ?? "";
            var name = id.Contains('/')
                ? id.Substring(id.IndexOf('/') + 1) : id;
            return "Model: " + name;
        }

        private void UpdatePanel(string statusOverride = null)
        {
            if (_panel == null)
                return;
            var pos = _clicks.Count(c => c.Label == 1);
            var neg = _clicks.Count - pos;
            _panel.ModelText = CurrentModelLabel();
            _panel.ClicksText = string.Format(
                "Positive {0} | Negative {1}{2}", pos, neg,
                _previewPolygon != null
                    ? string.Format(" | Score {0:0.00}", _lastScore) : "");
            if (statusOverride != null)
                _panel.StatusText = statusOverride;
            else if (_image == null)
                _panel.StatusText =
                    "Zoom to the target, then left-click inside an " +
                    "object (the current view becomes the work area).";
            else if (_previewPolygon == null)
                _panel.StatusText =
                    "Left-click inside an object to get a preview.";
            else
                _panel.StatusText =
                    "Preview ready - press Save or Space to write to " +
                    "the target layer; add points to refine.";
        }

        // ------------------------------------------------------------
        // Mouse input
        // ------------------------------------------------------------

        protected override void OnToolMouseDown(
            MapViewMouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left ||
                e.ChangedButton == MouseButton.Right)
                e.Handled = true;   // right button: suppress context menu
        }

        protected override async Task HandleMouseDownAsync(
            MapViewMouseButtonEventArgs e)
        {
            if (_busy)
                return;
            _busy = true;
            try
            {
                var label = e.ChangedButton == MouseButton.Left ? 1 : 0;
                await AddClickAsync(e.ClientPoint, label);
            }
            catch (Exception exc)
            {
                Toast("Error: " + exc.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        // ------------------------------------------------------------
        // Keyboard input
        // ------------------------------------------------------------

        protected override void OnToolKeyDown(MapViewKeyEventArgs k)
        {
            var ctrlZ = k.Key == Key.Z &&
                (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (k.Key == Key.Space || k.Key == Key.Escape || ctrlZ)
                k.Handled = true;
            base.OnToolKeyDown(k);
        }

        protected override async Task HandleKeyDownAsync(
            MapViewKeyEventArgs k)
        {
            // Esc during 'Preparing work area' aborts the preparation
            // (works even while the tool is busy).
            if (k.Key == Key.Escape && CancelPreparation())
                return;
            if (_busy)
                return;
            _busy = true;
            try
            {
                if (k.Key == Key.Escape)
                {
                    // Esc only clears the clicks; the work area (and its
                    // cached embedding) survives. Cancel the work area
                    // with the ribbon button when needed.
                    ClearClicks();
                    UpdatePanel();
                }
                else if (k.Key == Key.Space)
                    await CommitAsync();
                else if (k.Key == Key.Z)
                    await UndoClickAsync();
            }
            catch (Exception exc)
            {
                Toast("Error: " + exc.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        // ------------------------------------------------------------
        // Core actions
        // ------------------------------------------------------------

        private async Task AddClickAsync(
            System.Windows.Point clientPoint, int label)
        {
            var mapView = MapView.Active;
            if (mapView == null)
                return;

            var mapPoint = await QueuedTask.Run(
                () => mapView.ClientToMap(clientPoint));
            if (mapPoint == null || mapPoint.IsEmpty)
                return;

            // Model changed in the ribbon -> rebuild the work area
            // before the next object (never mid-object).
            if (_image != null && _clicks.Count == 0 &&
                _settingsVersion != SamModule.SettingsVersion)
                ResetWorkArea();

            // Freeze the work area from the current view at first click.
            var justCreated = false;
            if (_image == null)
            {
                if (!await SetWorkAreaAsync(mapView))
                    return;
                justCreated = true;
            }

            var (col, row, imagePoint) = await MapToPixelAsync(mapPoint);
            var inside = col >= 0 && col <= _image.Cols - 1 &&
                         row >= 0 && row <= _image.Rows - 1;
            if (!inside)
            {
                // Never rebuild the work area on a stray click - that
                // re-exports and re-encodes the image, which is slow.
                Toast(justCreated
                    ? "The click is outside the imagery extent."
                    : "Click ignored - outside the work area (white " +
                      "dashed box). Use 'New Work Area' to re-frame " +
                      "the current view.");
                return;
            }

            var click = new Click
            {
                ImagePoint = imagePoint,
                Col = col,
                Row = row,
                Label = label,
            };
            click.Marker = await QueuedTask.Run(() => mapView.AddOverlay(
                imagePoint,
                label == 1 ? PositiveSymbol() : NegativeSymbol()));
            _clicks.Add(click);

            await PredictAndPreviewAsync();
            UpdatePanel();
        }

        private async Task PredictAndPreviewAsync()
        {
            if (_clicks.Count == 0 ||
                !_clicks.Any(c => c.Label == 1))
            {
                DisposeMask();
                return;
            }

            var req = new PredictRequest
            {
                Points = _clicks
                    .Select(c => new[] { c.Col, c.Row }).ToList(),
                Labels = _clicks.Select(c => c.Label).ToList(),
            };
            var resp = await SamServerClient.PredictAsync(
                SamServerManager.Config.Port, req);
            if (resp == null || !resp.Ok)
            {
                Toast("Prediction failed: " +
                      (resp?.Error ?? "no response from the server"));
                return;
            }
            _lastScore = resp.Score;

            if (resp.Rings == null || resp.Rings.Count == 0)
            {
                DisposeMask();
                _previewPolygon = null;
                Toast("The mask is empty - add more positive points " +
                      "inside the object.");
                return;
            }

            var mapView = MapView.Active;
            var overlay = await QueuedTask.Run(() =>
            {
                var pb = new PolygonBuilderEx(_imageSr);
                foreach (var ring in resp.Rings)
                    pb.AddPart(ring.Select(
                        p => new Coordinate2D(p[0], p[1])).ToList());
                var poly = GeometryEngine.Instance.SimplifyAsFeature(
                    pb.ToGeometry(), true) as Polygon;
                _previewPolygon = poly;
                return poly == null
                    ? null
                    : mapView.AddOverlay(poly, MaskSymbol());
            });
            DisposeMask();
            _maskOverlay = overlay;
        }

        private async Task UndoClickAsync()
        {
            if (_clicks.Count == 0)
                return;
            var last = _clicks[^1];
            _clicks.RemoveAt(_clicks.Count - 1);
            last.Marker?.Dispose();
            if (_clicks.Count == 0)
            {
                DisposeMask();
                _previewPolygon = null;
                UpdatePanel();
                return;
            }
            await PredictAndPreviewAsync();
            UpdatePanel();
        }

        private async Task CommitAsync()
        {
            if (_previewPolygon == null)
            {
                Toast("Nothing to save yet - left-click inside an " +
                      "object first.");
                return;
            }
            var layer = FindTargetLayer();
            if (layer == null)
            {
                // No editable polygon layer around: create one in the
                // project default geodatabase so saving always works.
                layer = await QueuedTask.Run(
                    () => CreateTargetLayer(_imageSr));
                if (layer == null)
                {
                    Toast("No editable polygon layer found and none " +
                          "could be created - pick one in the " +
                          "'Target' drop-down of the SAM ribbon.");
                    return;
                }
                SamModule.TargetLayerUri = layer.URI;
                Toast("Created target layer '" + DefaultTargetName +
                      "' in the project geodatabase.");
            }

            var score = _lastScore;
            var polygon = _previewPolygon;
            var imageSr = _imageSr;
            var (ok, message) = await QueuedTask.Run(() =>
            {
                Geometry geom = polygon;
                var layerSr = layer.GetSpatialReference();
                if (layerSr != null && !layerSr.IsEqual(imageSr))
                    geom = GeometryEngine.Instance.Project(geom, layerSr);

                var attrs = new Dictionary<string, object>
                {
                    { "SHAPE", geom },
                };
                using (var table = layer.GetTable())
                {
                    var fields = table.GetDefinition().GetFields();
                    if (fields.Any(f => f.Name.Equals("Score",
                            StringComparison.OrdinalIgnoreCase)))
                        attrs["Score"] = score;
                    if (fields.Any(f => f.Name.Equals("Prompt",
                            StringComparison.OrdinalIgnoreCase)))
                        attrs["Prompt"] = "click";
                }

                // Select the new feature so the Attributes pane and
                // selection-based edit tools (Split, Move, ...) can act
                // on it right away.
                var op = new EditOperation
                {
                    Name = "SAM3 click segmentation",
                    SelectNewFeatures = true,
                };
                op.Create(layer, attrs);
                var success = op.Execute();
                return (success, success ? null : op.ErrorMessage);
            });

            if (!ok)
            {
                Toast("Could not create the feature: " + message);
                return;
            }
            Toast(string.Format(
                "Polygon saved and selected (score {0:0.00}).",
                score));

            // Keep the frozen work area so nearby objects reuse the
            // cached embedding; only the clicks are cleared.
            ClearClicks();
            UpdatePanel("Saved + selected. Click the next object, edit " +
                        "attributes in the Attributes pane, or switch " +
                        "tools freely - the work area is kept.");
        }

        // ------------------------------------------------------------
        // Work area handling
        // ------------------------------------------------------------

        private async Task<bool> SetWorkAreaAsync(MapView mapView)
        {
            if (!SamServerManager.IsReady)
                UpdatePanel("Waiting for the inference server ...");
            var error = await SamServerManager.EnsureRunningAsync();
            if (error != null)
            {
                MessageBox.Show(error, "SAM Interactive Segmentation");
                return false;
            }

            var rasterLayer = FindRasterLayer();
            if (rasterLayer == null)
            {
                Toast("No imagery layer found in the map. Pick one in " +
                      "the 'Imagery' drop-down of the SAM ribbon.");
                return false;
            }

            // No modal progress dialog here: the UI stays responsive
            // and the preparation can be aborted at any time with Esc,
            // the ribbon 'Cancel Work Area' button or the panel.
            UpdatePanel("Preparing the work area (image export + " +
                        "model encoding) ... Esc or 'Cancel Work " +
                        "Area' aborts.");
            _prepareCts = new CancellationTokenSource();
            try
            {
                var (rasterPath, extent, mapSrWkt) =
                    await QueuedTask.Run(() =>
                    {
                        var path = GetRasterPath(rasterLayer);
                        var env = mapView.Extent;
                        var wkt = mapView.Map.SpatialReference?.Wkt;
                        return (path, env, wkt);
                    });

                var cfg = SamServerManager.Config;
                var resp = await SamServerClient.SetImageAsync(cfg.Port,
                    new SetImageRequest
                    {
                        RasterPath = rasterPath,
                        Extent = new ExtentDto
                        {
                            XMin = extent.XMin,
                            YMin = extent.YMin,
                            XMax = extent.XMax,
                            YMax = extent.YMax,
                        },
                        ExtentSrWkt = mapSrWkt,
                        MaxSize = cfg.MaxImageSize,
                        Engine = SamModule.CurrentEngine,
                        ModelId = SamModule.CurrentModelId,
                        RitmCheckpoint = cfg.RitmCheckpoint,
                    },
                    _prepareCts.Token);
                if (resp == null || !resp.Ok || resp.Image == null)
                {
                    UpdatePanel("Work area setup failed - see the " +
                                "error dialog.");
                    MessageBox.Show(
                        "Could not create the work area: " +
                        (resp?.Error ?? "no response from the server") +
                        "\n\nLog file: " + ServerConfig.LogPath,
                        "SAM Interactive Segmentation");
                    return false;
                }

                _settingsVersion = SamModule.SettingsVersion;
                _image = resp.Image;
                _workAreaOverlay = await QueuedTask.Run(() =>
                {
                    _imageSr = SpatialReferenceBuilder
                        .CreateSpatialReference(resp.Image.SrWkt);
                    var env = EnvelopeBuilderEx.CreateEnvelope(
                        resp.Image.XMin, resp.Image.YMin,
                        resp.Image.XMax, resp.Image.YMax, _imageSr);
                    return mapView.AddOverlay(
                        PolygonBuilderEx.CreatePolygon(env),
                        WorkAreaSymbol());
                });
                return true;
            }
            catch (OperationCanceledException)
            {
                Toast("Work area preparation cancelled.");
                UpdatePanel("Preparation cancelled - zoom / pan and " +
                            "click again (or 'New Work Area').");
                return false;
            }
            finally
            {
                _prepareCts?.Dispose();
                _prepareCts = null;
            }
        }

        private async Task<(double col, double row, MapPoint imagePoint)>
            MapToPixelAsync(MapPoint mapPoint)
        {
            return await QueuedTask.Run(() =>
            {
                var pt = mapPoint;
                if (_imageSr != null && pt.SpatialReference != null &&
                    !pt.SpatialReference.IsEqual(_imageSr))
                    pt = GeometryEngine.Instance.Project(pt, _imageSr)
                        as MapPoint;
                var col = (pt.X - _image.XMin) / _image.CellW;
                var row = (_image.YMax - pt.Y) / _image.CellH;
                return (col, row, pt);
            });
        }

        // ------------------------------------------------------------
        // State clean-up
        // ------------------------------------------------------------

        private void ClearClicks()
        {
            foreach (var c in _clicks)
                c.Marker?.Dispose();
            _clicks.Clear();
            DisposeMask();
            _previewPolygon = null;
        }

        private void ResetWorkArea()
        {
            ClearClicks();
            _workAreaOverlay?.Dispose();
            _workAreaOverlay = null;
            _image = null;
            _imageSr = null;
        }

        private void DisposeMask()
        {
            _maskOverlay?.Dispose();
            _maskOverlay = null;
        }

        // ------------------------------------------------------------
        // Layer helpers
        // ------------------------------------------------------------

        private static RasterLayer FindRasterLayer()
        {
            var map = MapView.Active?.Map;
            if (map == null)
                return null;
            if (SamModule.RasterLayerUri != null)
            {
                var picked = map.FindLayer(SamModule.RasterLayerUri)
                    as RasterLayer;
                if (picked != null)
                    return picked;
            }
            return map.GetLayersAsFlattenedList()
                .OfType<RasterLayer>().FirstOrDefault();
        }

        private static FeatureLayer FindTargetLayer()
        {
            var map = MapView.Active?.Map;
            if (map == null)
                return null;
            if (SamModule.TargetLayerUri != null)
            {
                var picked = map.FindLayer(SamModule.TargetLayerUri)
                    as FeatureLayer;
                if (picked != null)
                    return picked;
            }
            // Fall back to an EDITABLE polygon layer only - writing
            // into a read-only layer fails and confuses the workflow.
            return map.GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .FirstOrDefault(l =>
                    l.ShapeType == esriGeometryType.esriGeometryPolygon &&
                    l.IsEditable);
        }

        /// <summary>Create (or reuse) the default polygon target
        /// feature class in the project geodatabase and add it to the
        /// map. Must run on the MCT. Returns null on failure.</summary>
        private static FeatureLayer CreateTargetLayer(SpatialReference sr)
        {
            var map = MapView.Active?.Map;
            var gdbPath = Project.Current?.DefaultGeodatabasePath;
            if (map == null || string.IsNullOrEmpty(gdbPath))
                return null;
            try
            {
                using var gdb = new Geodatabase(
                    new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                FeatureClass fc;
                try
                {
                    fc = gdb.OpenDataset<FeatureClass>(DefaultTargetName);
                }
                catch
                {
                    var description = new FeatureClassDescription(
                        DefaultTargetName,
                        new List<FieldDescription>
                        {
                            new FieldDescription(
                                "Score", FieldType.Double),
                            FieldDescription.CreateStringField(
                                "Prompt", 64),
                        },
                        new ShapeDescription(GeometryType.Polygon, sr));
                    var builder = new SchemaBuilder(gdb);
                    builder.Create(description);
                    if (!builder.Build())
                        return null;
                    fc = gdb.OpenDataset<FeatureClass>(DefaultTargetName);
                }
                using (fc)
                {
                    return LayerFactory.Instance.CreateLayer<FeatureLayer>(
                        new FeatureLayerCreationParams(fc), map);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>File-system path of the raster (folder + dataset or
        /// file geodatabase + dataset). Must run on the MCT.</summary>
        private static string GetRasterPath(RasterLayer layer)
        {
            var raster = layer.GetRaster();
            var dataset = raster.GetRasterDataset();
            if (dataset == null)
                throw new InvalidOperationException(
                    "The imagery layer has no raster dataset (web " +
                    "services are not supported - use a local raster).");
            var store = dataset.GetDatastore();
            var uri = store.GetPath();
            if (uri == null || !uri.IsFile)
                throw new InvalidOperationException(
                    "Only file-based rasters (GeoTIFF, file " +
                    "geodatabase, ...) are supported.");
            return System.IO.Path.Combine(
                uri.LocalPath, dataset.GetName());
        }

        // ------------------------------------------------------------
        // Symbols (construct lazily on the MCT)
        // ------------------------------------------------------------

        private CIMSymbolReference MaskSymbol()
        {
            if (_maskSymbol != null)
                return _maskSymbol;
            var fill = ColorFactory.Instance.CreateRGBColor(
                0, 200, 255, 35);
            var stroke = SymbolFactory.Instance.ConstructStroke(
                ColorFactory.Instance.CreateRGBColor(0, 200, 255, 100),
                2.0, SimpleLineStyle.Solid);
            _maskSymbol = SymbolFactory.Instance.ConstructPolygonSymbol(
                fill, SimpleFillStyle.Solid, stroke)
                .MakeSymbolReference();
            return _maskSymbol;
        }

        private CIMSymbolReference WorkAreaSymbol()
        {
            if (_workAreaSymbol != null)
                return _workAreaSymbol;
            var noFill = ColorFactory.Instance.CreateRGBColor(
                0, 0, 0, 0);
            var stroke = SymbolFactory.Instance.ConstructStroke(
                ColorFactory.Instance.CreateRGBColor(255, 255, 255, 90),
                1.5, SimpleLineStyle.Dash);
            _workAreaSymbol = SymbolFactory.Instance
                .ConstructPolygonSymbol(noFill, SimpleFillStyle.Solid,
                    stroke)
                .MakeSymbolReference();
            return _workAreaSymbol;
        }

        private CIMSymbolReference PositiveSymbol()
        {
            _positiveSymbol ??= SymbolFactory.Instance
                .ConstructPointSymbol(
                    ColorFactory.Instance.CreateRGBColor(40, 200, 40),
                    10.0, SimpleMarkerStyle.Circle)
                .MakeSymbolReference();
            return _positiveSymbol;
        }

        private CIMSymbolReference NegativeSymbol()
        {
            _negativeSymbol ??= SymbolFactory.Instance
                .ConstructPointSymbol(
                    ColorFactory.Instance.CreateRGBColor(230, 40, 40),
                    10.0, SimpleMarkerStyle.X)
                .MakeSymbolReference();
            return _negativeSymbol;
        }

        private static void Toast(string message)
        {
            FrameworkApplication.AddNotification(new Notification
            {
                Title = "SAM3 Interactive",
                Message = message,
            });
        }
    }
}
