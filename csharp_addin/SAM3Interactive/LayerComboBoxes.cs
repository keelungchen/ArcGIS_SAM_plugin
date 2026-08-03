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
            var map = MapView.Active?.Map;
            if (map == null)
                return;
            var items = await QueuedTask.Run(() =>
                map.GetLayersAsFlattenedList()
                   .OfType<RasterLayer>()
                   .Select(l => new { l.Name, l.URI })
                   .ToList());
            Clear();
            foreach (var it in items)
                Add(new LayerComboItem(it.Name, it.URI));
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
        }

        protected override void OnSelectionChange(ComboBoxItem item)
        {
            SamModule.TargetLayerUri = (item as LayerComboItem)?.LayerUri;
        }
    }
}
