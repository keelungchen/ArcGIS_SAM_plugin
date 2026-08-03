using System.Diagnostics;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;

namespace SAM3Interactive
{
    internal class StartServerButton : Button
    {
        protected override async void OnClick()
        {
            var error = await SamServerManager.EnsureRunningAsync();
            if (error != null)
            {
                MessageBox.Show(error, "SAM3 Interactive");
                return;
            }
            var ping = await SamServerClient.PingAsync(
                SamServerManager.Config.Port);
            FrameworkApplication.AddNotification(new Notification
            {
                Title = "SAM3 Interactive",
                Message = "SAM server is running" +
                          (ping?.Device != null
                              ? " (" + ping.Device + ")" : "") + ".",
            });
        }
    }

    internal class StopServerButton : Button
    {
        protected override void OnClick()
        {
            SamServerManager.Stop();
            FrameworkApplication.AddNotification(new Notification
            {
                Title = "SAM3 Interactive",
                Message = "SAM server stopped.",
            });
        }
    }

    internal class OpenConfigButton : Button
    {
        protected override void OnClick()
        {
            ServerConfig.Load();   // ensure the file exists
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + ServerConfig.ConfigPath + "\"",
                UseShellExecute = true,
            });
        }
    }
}
