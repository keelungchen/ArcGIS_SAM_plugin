using System.Threading.Tasks;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace SAM3Interactive
{
    /// <summary>Ribbon buttons controlling the work area and the click
    /// history of the interactive segmentation tool. They act on the
    /// tool instance even while another edit tool (Select, Split, ...)
    /// is current, so mixed workflows keep the work area alive.</summary>
    internal static class SegmentToolAccess
    {
        /// <summary>Return the segment tool, optionally making it the
        /// current map tool first.</summary>
        public static async Task<InteractiveSegmentTool> GetAsync(
            bool activate)
        {
            if (activate && !InteractiveSegmentTool.IsToolActive)
                await FrameworkApplication.SetCurrentToolAsync(
                    "SAM3Interactive_SegmentTool");
            var tool = InteractiveSegmentTool.Instance;
            if (tool == null)
                FrameworkApplication.AddNotification(new Notification
                {
                    Title = "SAM3 Interactive",
                    Message = "Use the Click Segment tool once first.",
                });
            return tool;
        }
    }

    /// <summary>Freeze the current map view as the work area (replaces
    /// the existing work area, clicks included).</summary>
    internal class NewWorkAreaButton : Button
    {
        protected override async void OnClick()
        {
            var tool = await SegmentToolAccess.GetAsync(activate: true);
            tool?.RequestNewWorkArea();
        }
    }

    /// <summary>Discard the work area and all clicks.</summary>
    internal class CancelWorkAreaButton : Button
    {
        protected override async void OnClick()
        {
            var tool = await SegmentToolAccess.GetAsync(activate: false);
            tool?.RequestCancelWorkArea();
        }
    }

    /// <summary>Undo the last click (same as Ctrl+Z).</summary>
    internal class UndoClickButton : Button
    {
        protected override async void OnClick()
        {
            var tool = await SegmentToolAccess.GetAsync(activate: false);
            tool?.RequestUndoClick();
        }
    }

    /// <summary>Remove all clicks but keep the work area (same as
    /// Esc).</summary>
    internal class ClearClicksButton : Button
    {
        protected override async void OnClick()
        {
            var tool = await SegmentToolAccess.GetAsync(activate: false);
            tool?.RequestClearClicks();
        }
    }
}
