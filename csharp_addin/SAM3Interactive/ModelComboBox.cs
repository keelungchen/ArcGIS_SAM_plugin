using System.Linq;
using ArcGIS.Desktop.Framework.Contracts;

namespace SAM3Interactive
{
    /// <summary>Ribbon drop-down for picking the inference model.
    /// Selection is applied immediately (next work area) and persisted
    /// to config.json - no manual config editing needed.</summary>
    internal class ModelComboBox : ComboBox
    {
        private class Option : ComboBoxItem
        {
            public string Engine { get; }
            public string ModelId { get; }

            public Option(string text, string engine, string modelId)
                : base(text)
            {
                Engine = engine;
                ModelId = modelId;
            }
        }

        public ModelComboBox()
        {
            // RITM first: it is the default engine and the one that
            // needs no embedding pass. The SAM entries load their
            // weights only when picked (see SamModule).
            Add(new Option("RITM (TagLab corals, default)",
                "ritm", null));
            Add(new Option("SAM2.1 Tiny (fast)",
                "sam", "facebook/sam2.1-hiera-tiny"));
            Add(new Option("SAM2.1 Small (more accurate)",
                "sam", "facebook/sam2.1-hiera-small"));
            Add(new Option("SAM3 (heaviest, needs HF login)",
                "sam", "facebook/sam3"));

            var engine = SamModule.CurrentEngine;
            var modelId = SamModule.CurrentModelId;
            SelectedItem = ItemCollection.OfType<Option>()
                .FirstOrDefault(o => o.Engine == engine &&
                    (engine == "ritm" || o.ModelId == modelId))
                ?? ItemCollection.OfType<Option>().First();
        }

        protected override void OnSelectionChange(ComboBoxItem item)
        {
            if (item is not Option o)
                return;
            SamModule.SetModelSelection(
                o.Engine, o.ModelId ?? SamModule.CurrentModelId);
        }
    }
}
