using System;
using System.Windows.Input;
using System.Xml.Linq;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Controls;

namespace SAM3Interactive
{
    /// <summary>View model for the small instruction/status panel that
    /// floats on the map while the Click Segment tool is active.</summary>
    internal class SegmentOverlayViewModel : EmbeddableControl
    {
        public SegmentOverlayViewModel(XElement options,
            bool canChangeOptions) : base(options, canChangeOptions)
        {
            SaveCommand = new RelayCommand(
                () => SaveRequested?.Invoke(), () => true);
            UndoCommand = new RelayCommand(
                () => UndoRequested?.Invoke(), () => true);
            ClearCommand = new RelayCommand(
                () => ClearRequested?.Invoke(), () => true);
            ResetCommand = new RelayCommand(
                () => ResetRequested?.Invoke(), () => true);
        }

        // Wired by InteractiveSegmentTool on activation.
        public Action SaveRequested { get; set; }
        public Action UndoRequested { get; set; }
        public Action ClearRequested { get; set; }
        public Action ResetRequested { get; set; }

        public ICommand SaveCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand ResetCommand { get; }

        private string _modelText = "";
        public string ModelText
        {
            get => _modelText;
            set => SetProperty(ref _modelText, value);
        }

        private string _clicksText = "";
        public string ClicksText
        {
            get => _clicksText;
            set => SetProperty(ref _clicksText, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }
    }
}
