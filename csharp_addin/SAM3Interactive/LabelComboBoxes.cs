using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace SAM3Interactive
{
    /// <summary>Where the values offered for a label field come from.</summary>
    internal enum LabelSource
    {
        /// <summary>No domain and no subtype: the value is typed in.</summary>
        FreeText,
        /// <summary>Coded value domain attached to the field.</summary>
        CodedDomain,
        /// <summary>The field is the subtype field of the layer.</summary>
        Subtype,
        /// <summary>Range domain: only min/max, the value is typed in.</summary>
        Range,
    }

    /// <summary>One value offered in the 'Label' drop-down.</summary>
    internal sealed class LabelValueInfo
    {
        /// <summary>Value written into the field (already the field's
        /// type, as stored in the domain / subtype definition).</summary>
        public object Code;

        /// <summary>Description shown to the user.</summary>
        public string Text;
    }

    /// <summary>A field of the target layer that can carry the label,
    /// together with the values its domain / subtype allows.</summary>
    internal sealed class LabelFieldInfo
    {
        public string Name;
        public string Alias;
        public FieldType Type;
        public LabelSource Source = LabelSource.FreeText;

        /// <summary>Bounds of a range domain, otherwise null.</summary>
        public string Hint;

        public readonly List<LabelValueInfo> Values =
            new List<LabelValueInfo>();

        /// <summary>Text shown in the 'Label field' drop-down: the field
        /// plus where its values come from, so a field without a domain
        /// is obvious before it is picked.</summary>
        public string Display
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(Alias) || Alias == Name
                    ? Name
                    : Alias + " (" + Name + ")";
                switch (Source)
                {
                    case LabelSource.CodedDomain:
                        return string.Format("{0}  [domain: {1} values]",
                            name, Values.Count);
                    case LabelSource.Subtype:
                        return string.Format("{0}  [subtypes: {1}]",
                            name, Values.Count);
                    case LabelSource.Range:
                        return name + "  [range domain]";
                    default:
                        return name + "  [no domain]";
                }
            }
        }
    }

    /// <summary>Reads the label-capable fields (and their domains) of the
    /// target polygon layer once, so both label drop-downs work from the
    /// same snapshot without hitting the geodatabase per keystroke.</summary>
    internal static class LabelCatalog
    {
        private static readonly List<LabelFieldInfo> _fields =
            new List<LabelFieldInfo>();
        private static string _layerUri;

        internal static IReadOnlyList<LabelFieldInfo> Fields => _fields;

        internal static LabelFieldInfo Find(string fieldName) =>
            fieldName == null
                ? null
                : _fields.FirstOrDefault(f => f.Name.Equals(
                    fieldName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Re-read the fields of the current target layer.
        /// Returns false when there is no target layer.</summary>
        internal static async Task<bool> RefreshAsync()
        {
            var layer = SamModule.FindTargetLayer();
            if (layer == null)
            {
                _fields.Clear();
                _layerUri = null;
                return false;
            }
            var scanned = await QueuedTask.Run(() => Scan(layer));
            _fields.Clear();
            _fields.AddRange(scanned);
            _layerUri = layer.URI;
            return true;
        }

        /// <summary>Refresh only when the snapshot is missing or belongs
        /// to a different layer.</summary>
        internal static async Task<bool> EnsureAsync()
        {
            var layer = SamModule.FindTargetLayer();
            if (layer != null && layer.URI == _layerUri)
                return true;
            return await RefreshAsync();
        }

        /// <summary>Must run on the MCT. Nothing from the geodatabase is
        /// kept alive: only names, types and value copies are stored, so
        /// the snapshot pins no table or datastore handles.</summary>
        private static List<LabelFieldInfo> Scan(FeatureLayer layer)
        {
            var result = new List<LabelFieldInfo>();
            using (var table = layer.GetTable())
            {
                var definition = table.GetDefinition();
                // Not every workspace (shapefiles, some services) supports
                // subtypes or domains - treat that as "no values".
                string subtypeField = null;
                try
                {
                    subtypeField = definition.GetSubtypeField();
                }
                catch (Exception)
                {
                    // no subtype support
                }
                foreach (var field in definition.GetFields())
                {
                    if (!IsCandidate(field))
                        continue;
                    var info = new LabelFieldInfo
                    {
                        Name = field.Name,
                        Alias = field.AliasName,
                        Type = field.FieldType,
                    };
                    try
                    {
                        if (!string.IsNullOrEmpty(subtypeField) &&
                            field.Name.Equals(subtypeField,
                                StringComparison.OrdinalIgnoreCase))
                            ReadSubtypes(definition, info);
                        else
                            ReadDomain(field, info);
                    }
                    catch (Exception)
                    {
                        // Unreadable domain: offer the field as free text
                        // rather than losing the whole list.
                        info.Source = LabelSource.FreeText;
                        info.Values.Clear();
                    }
                    result.Add(info);
                }
            }
            return result;
        }

        private static void ReadSubtypes(TableDefinition definition,
            LabelFieldInfo info)
        {
            foreach (var subtype in definition.GetSubtypes())
                info.Values.Add(new LabelValueInfo
                {
                    Code = subtype.GetCode(),
                    Text = subtype.GetName(),
                });
            if (info.Values.Count > 0)
                info.Source = LabelSource.Subtype;
        }

        private static void ReadDomain(Field field, LabelFieldInfo info)
        {
            var domain = field.GetDomain();
            if (domain is CodedValueDomain coded)
            {
                foreach (var pair in coded.GetCodedValuePairs())
                    info.Values.Add(new LabelValueInfo
                    {
                        Code = pair.Key,
                        Text = pair.Value,
                    });
                if (info.Values.Count > 0)
                    info.Source = LabelSource.CodedDomain;
            }
            else if (domain is RangeDomain range)
            {
                info.Source = LabelSource.Range;
                info.Hint = string.Format("{0} .. {1}",
                    range.GetMinValue(), range.GetMaxValue());
            }
        }

        private static bool IsCandidate(Field field)
        {
            switch (field.FieldType)
            {
                case FieldType.Geometry:
                case FieldType.OID:
                case FieldType.GlobalID:
                case FieldType.GUID:
                case FieldType.Raster:
                case FieldType.Blob:
                case FieldType.XML:
                    return false;
            }
            return field.IsEditable;
        }

        /// <summary>Convert text typed into the 'Label' box to the field
        /// type (fields without a coded value domain). Null = invalid.</summary>
        internal static object ParseValue(string text, FieldType type)
        {
            try
            {
                switch (type)
                {
                    case FieldType.SmallInteger:
                        return short.Parse(text, CultureInfo.CurrentCulture);
                    case FieldType.Integer:
                        return int.Parse(text, CultureInfo.CurrentCulture);
                    case FieldType.Single:
                        return float.Parse(text, CultureInfo.CurrentCulture);
                    case FieldType.Double:
                        return double.Parse(text, CultureInfo.CurrentCulture);
                    case FieldType.Date:
                        return DateTime.Parse(text, CultureInfo.CurrentCulture);
                    default:
                        return text;
                }
            }
            catch (FormatException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }
    }

    internal class LabelFieldItem : ComboBoxItem
    {
        /// <summary>Null for the "(no label)" entry.</summary>
        public LabelFieldInfo Field { get; }

        public LabelFieldItem(string text, LabelFieldInfo field) : base(text)
        {
            Field = field;
        }
    }

    internal class LabelValueItem : ComboBoxItem
    {
        /// <summary>Null for the "(no value)" entry.</summary>
        public object Code { get; }

        public LabelValueItem(string text, object code) : base(text)
        {
            Code = code;
        }
    }

    /// <summary>Ribbon drop-down picking WHICH field of the target
    /// polygon layer every new polygon is tagged in. Fields carrying a
    /// coded value domain (or the subtype field) offer a value list;
    /// fields without a domain are flagged as such.</summary>
    internal class LabelFieldComboBox : ComboBox
    {
        private const string NoLabel = "(no label)";

        internal static LabelFieldComboBox Instance { get; private set; }

        private bool _suppress;
        private bool _filling;

        public LabelFieldComboBox()
        {
            Instance = this;
            ResetDisplay();
        }

        /// <summary>Drop the field list, e.g. after the target layer
        /// changed - its fields no longer apply.</summary>
        internal void ResetDisplay()
        {
            _suppress = true;
            try
            {
                Clear();
                Add(new LabelFieldItem(NoLabel, null));
                SelectedItem = ItemCollection.OfType<LabelFieldItem>()
                    .FirstOrDefault();
            }
            finally
            {
                _suppress = false;
            }
        }

        protected override void OnDropDownOpened()
        {
            _ = FillAsync();
        }

        private async Task FillAsync()
        {
            if (_filling)
                return;   // a fill is already running - never nest them
            _filling = true;
            bool hasLayer;
            try
            {
                hasLayer = await LabelCatalog.RefreshAsync();
            }
            catch (Exception exc)
            {
                _filling = false;
                SamModule.Notify("Could not read the fields of the " +
                    "target layer: " + exc.Message);
                return;
            }
            _suppress = true;
            try
            {
                Clear();
                Add(new LabelFieldItem(NoLabel, null));
                foreach (var field in LabelCatalog.Fields)
                    Add(new LabelFieldItem(field.Display, field));
                SelectedItem = ItemCollection.OfType<LabelFieldItem>()
                    .FirstOrDefault(i => i.Field != null &&
                        i.Field.Name.Equals(SamModule.LabelFieldName,
                            StringComparison.OrdinalIgnoreCase))
                    ?? ItemCollection.OfType<LabelFieldItem>().First();
            }
            finally
            {
                _suppress = false;
                _filling = false;
            }

            if (!hasLayer)
                SamModule.Notify("Pick a 'Target' polygon layer first - " +
                    "the label fields are read from it.");
            else if (LabelCatalog.Fields.Count == 0)
                SamModule.Notify("The target layer has no editable " +
                    "attribute field that could hold a label.");
        }

        protected override void OnSelectionChange(ComboBoxItem item)
        {
            if (_suppress)
                return;
            var field = (item as LabelFieldItem)?.Field;
            SamModule.SetLabelField(field?.Name);
            LabelValueComboBox.Instance?.Reload();
            if (field == null)
                return;

            switch (field.Source)
            {
                case LabelSource.CodedDomain:
                case LabelSource.Subtype:
                    SamModule.Notify(string.Format(
                        "'{0}' offers {1} values - pick one in the " +
                        "'Label' drop-down; every polygon you save gets " +
                        "it.", field.Name, field.Values.Count));
                    break;
                case LabelSource.Range:
                    SamModule.Notify(string.Format(
                        "'{0}' has a RANGE domain ({1}), not a coded " +
                        "value list. Type the value into the 'Label' box " +
                        "and press Enter.", field.Name, field.Hint));
                    break;
                default:
                    SamModule.Notify(string.Format(
                        "'{0}' has NO domain, so there is no value list. " +
                        "Type the label into the 'Label' box and press " +
                        "Enter, or attach a coded value domain to the " +
                        "field in the geodatabase (Fields view -> Domain).",
                        field.Name));
                    break;
            }
        }
    }

    /// <summary>Ribbon drop-down picking WHICH value (label) is written
    /// into the label field. Filled from the field's coded value domain
    /// or the layer subtypes; free text is accepted for fields without
    /// a domain.</summary>
    internal class LabelValueComboBox : ComboBox
    {
        private const string NoValue = "(no value)";

        internal static LabelValueComboBox Instance { get; private set; }

        private bool _suppress;
        private bool _filling;

        public LabelValueComboBox()
        {
            Instance = this;
            ResetDisplay();
        }

        internal void ResetDisplay()
        {
            _suppress = true;
            try
            {
                Clear();
                Text = "";
            }
            finally
            {
                _suppress = false;
            }
        }

        /// <summary>Rebuild the value list after the label field changed.</summary>
        internal void Reload()
        {
            _ = FillAsync();
        }

        protected override void OnDropDownOpened()
        {
            _ = FillAsync();
        }

        private async Task FillAsync()
        {
            if (_filling)
                return;   // a fill is already running - never nest them
            _filling = true;
            try
            {
                await LabelCatalog.EnsureAsync();
            }
            catch (Exception exc)
            {
                _filling = false;
                SamModule.Notify("Could not read the values of the " +
                    "label field: " + exc.Message);
                return;
            }
            var field = LabelCatalog.Find(SamModule.LabelFieldName);
            _suppress = true;
            try
            {
                Clear();
                if (field == null)
                {
                    Text = "";
                    return;
                }
                Add(new LabelValueItem(NoValue, null));
                foreach (var value in field.Values)
                    Add(new LabelValueItem(value.Text, value.Code));

                var current = ItemCollection.OfType<LabelValueItem>()
                    .FirstOrDefault(i => i.Code != null &&
                        Equals(i.Code, SamModule.LabelValue));
                if (current != null)
                    SelectedItem = current;
                else
                    Text = SamModule.LabelValueText ?? "";
            }
            finally
            {
                _suppress = false;
                _filling = false;
            }
        }

        protected override void OnSelectionChange(ComboBoxItem item)
        {
            if (_suppress || item is not LabelValueItem value)
                return;
            if (SamModule.LabelFieldName == null)
            {
                SamModule.Notify("Pick a 'Label field' first.");
                return;
            }
            SamModule.SetLabelValue(value.Code,
                value.Code == null ? null : value.Text);
        }

        /// <summary>Enter in the editable box: accept a typed label for
        /// fields without a coded value domain (and let the user type an
        /// existing domain description instead of scrolling).</summary>
        protected override void OnEnter()
        {
            if (_suppress)
                return;
            var field = LabelCatalog.Find(SamModule.LabelFieldName);
            if (field == null)
            {
                SamModule.Notify("Pick a 'Label field' first.");
                return;
            }

            var text = Text?.Trim();
            if (string.IsNullOrEmpty(text) || text == NoValue)
            {
                SamModule.SetLabelValue(null, null);
                return;
            }

            if (field.Values.Count > 0)
            {
                var match = field.Values.FirstOrDefault(v =>
                    string.Equals(v.Text, text,
                        StringComparison.CurrentCultureIgnoreCase));
                if (match == null)
                {
                    SamModule.Notify(string.Format(
                        "'{0}' is not one of the values allowed for {1} " +
                        "- pick one from the list.", text, field.Name));
                    return;
                }
                SamModule.SetLabelValue(match.Code, match.Text);
                return;
            }

            var parsed = LabelCatalog.ParseValue(text, field.Type);
            if (parsed == null)
            {
                SamModule.Notify(string.Format(
                    "'{0}' is not a valid {1} value for {2}.",
                    text, field.Type, field.Name));
                return;
            }
            SamModule.SetLabelValue(parsed, text);
            SamModule.Notify(string.Format(
                "Label set: {0} = {1}", field.Name, text));
        }
    }
}
