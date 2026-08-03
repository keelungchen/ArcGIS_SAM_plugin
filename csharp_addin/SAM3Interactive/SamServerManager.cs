using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SAM3Interactive
{
    /// <summary>Starts / stops the Python inference server process and
    /// waits for it to become reachable.</summary>
    internal static class SamServerManager
    {
        private static Process _process;
        private static readonly object Gate = new object();

        public static ServerConfig Config { get; private set; }

        public static bool IsProcessAlive
        {
            get
            {
                lock (Gate)
                {
                    return _process != null && !_process.HasExited;
                }
            }
        }

        /// <summary>Make sure the server is reachable; start it when
        /// needed. Returns null on success, else a user-facing error.</summary>
        public static async Task<string> EnsureRunningAsync(
            Action<string> progress = null)
        {
            Config = ServerConfig.Load();

            var ping = await SamServerClient.PingAsync(Config.Port);
            if (ping != null && ping.Ok)
                return null;

            var invalid = Config.Validate();
            if (invalid != null)
                return invalid + "\n\nEdit the configuration file:\n" +
                       ServerConfig.ConfigPath +
                       "\n(or run scripts\\install_addin_config.bat).";

            lock (Gate)
            {
                if (_process == null || _process.HasExited)
                {
                    progress?.Invoke("Starting SAM server ...");
                    try
                    {
                        _process = StartProcess(Config);
                    }
                    catch (Exception exc)
                    {
                        return "Could not start the Python server: " +
                               exc.Message;
                    }
                }
            }

            // Wait for /ping. Plain startup is a few seconds; importing
            // torch on a slow disk can take longer.
            progress?.Invoke("Waiting for the SAM server ...");
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(1000);
                if (!IsProcessAlive)
                    return "The Python server exited unexpectedly. " +
                           "Check the log:\n" + ServerConfig.LogPath;
                ping = await SamServerClient.PingAsync(Config.Port);
                if (ping != null && ping.Ok)
                    return null;
            }
            return "The Python server did not answer within 120 s. " +
                   "Check the log:\n" + ServerConfig.LogPath;
        }

        private static Process StartProcess(ServerConfig cfg)
        {
            Directory.CreateDirectory(ServerConfig.ConfigDir);
            var log = new StreamWriter(new FileStream(
                ServerConfig.LogPath, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite))
            { AutoFlush = true };

            var args = "\"" + cfg.ServerScript + "\" --port " + cfg.Port +
                       " --warm-engine " + SamModule.CurrentEngine +
                       " --warm-model " + SamModule.CurrentModelId;
            if (!string.IsNullOrWhiteSpace(cfg.RitmCheckpoint))
                args += " --warm-ritm-checkpoint \"" +
                        cfg.RitmCheckpoint + "\"";

            var psi = new ProcessStartInfo
            {
                FileName = cfg.PythonExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory =
                    Path.GetDirectoryName(cfg.ServerScript) ?? ".",
            };
            var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            { if (e.Data != null) TryLog(log, e.Data); };
            proc.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) TryLog(log, e.Data); };
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => { try { log.Dispose(); } catch { } };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return proc;
        }

        private static void TryLog(StreamWriter log, string line)
        {
            try { log.WriteLine(line); } catch { }
        }

        public static void Stop()
        {
            Process proc;
            lock (Gate)
            {
                proc = _process;
                _process = null;
            }
            if (proc == null)
                return;
            try
            {
                var port = (Config ?? ServerConfig.Load()).Port;
                SamServerClient.ShutdownAsync(port).Wait(3000);
                if (!proc.WaitForExit(3000))
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }
    }
}
