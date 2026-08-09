using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace SAM3Interactive
{
    internal class LayerComboItem : ComboBoxItem
    {
        public string LayerUri { get; }

        public LayerComboItem(string text, string uri) : base(text)
        {
            LayerUri = uri;
        }
    }

    /// <summary>Ribbon drop-down listing the raster layers of the
    /// active map (imagery to segment).</summary>
    internal class RasterLayerComboBox : ComboBox
    {
        protected override void OnDropDownOpened()
        {
            _ = FillAsync();
        }

        private async Task FillAsync()
        {
            var mapView = MapView.Active;
            if (mapView?.Map == null)
                return;
            // Mark the rasters that actually cover the current view: a
            // project with several sites otherwise makes it easy to pick
            // imagery the view does not overlap.
            var items = await QueuedTask.Run(() =>
            {
                var view = mapView.Extent;
                var sr = mapView.Map.SpatialReference;
                return mapView.Map.GetLayersAsFlattenedList()
                   .OfType<RasterLayer>()
                   .Select(l => new
                   {
                       l.Name,
                       l.URI,
                       InView = SamModule.LayerCoversView(l, view, sr),
                   })
                   .ToList();
            });
            Clear();
            foreach (var it in items)
                Add(new LayerComboItem(
                    it.InView ? it.Name + "  [in view]" : it.Name, it.URI));
            // Refilling drops the displayed selection: restore it so the
            // ribbon keeps showing the layer that is actually in use.
            SelectedItem = ItemCollection.OfType<LayerComboItem>()
                .FirstOrDefault(i => i.LayerUri == SamModule.RasterLayerUri);
        }

        protected override void OnSelectionChange(ComboBoxItem item)
        {
            SamModule.RasterLayerUri = (item as LayerComboItem)?.LayerUri;
        }
    }

    /// <summary>Ribbon drop-down listing the polygon feature layers of
    /// the active map (target for confirmed segmentations).</summary>
    internal class TargetLayerComboBox : ComboBox
    {
        protected override void OnDropDownOpened()
        {
            _ = FillAsync();
        }

        private async Task FillAsync()
        {
            var map = MapView.Active?.Map;
            if (map == null)
                return;
            var items = await QueuedTask.Run(() =>
                map.GetLayersAsFlattenedList()
                   .OfType<FeatureLayer>()
                   .Where(l => l.ShapeType ==
                       esriGeometryType.esriGeometryPolygon)
                   .Select(l => new { l.Name, l.URI })
                   .ToList());
            Clear();
            foreach (var it in items)
                Add(new LayerComboItem(it.Name, it.URI));
            // Same here - a blank 'Target' box would also look as if the
            // label drop-downs had lost their layer.
            SelectedItem = ItemCollection.OfType<LayerComboItem>()
                .FirstOrDefault(i => i.LayerUri == SamModule.TargetLayerUri);
        }

        protected override void OnSelectionChange(ComboBoxItem item)
        {
            // Re-selecting the same layer is a no-op in the setter, so the
            // label field/value picked for it survive a list refresh.
            SamModule.TargetLayerUri = (item as LayerComboItem)?.LayerUri;
        }
    }
}
