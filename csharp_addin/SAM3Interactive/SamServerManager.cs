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
        // Non-null while a start is in flight; shared by every caller
        // so the tool, the ribbon and the first click never start the
        // server (or wait for it) twice.
        private static Task<string> _startTask;
        private static bool _ready;

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

        /// <summary>True when the server answered /ping and has not
        /// exited since - callers can skip the "starting ..." wait.</summary>
        public static bool IsReady
        {
            get
            {
                lock (Gate)
                {
                    return _ready &&
                           (_process == null || !_process.HasExited);
                }
            }
        }

        /// <summary>Make sure the server is reachable; start it when
        /// needed. Returns null on success, else a user-facing error.
        /// Cheap to call repeatedly: a ready server returns instantly
        /// and concurrent callers share one start.</summary>
        public static Task<string> EnsureRunningAsync(
            Action<string> progress = null)
        {
            lock (Gate)
            {
                if (_ready && _process != null && !_process.HasExited)
                    return Task.FromResult<string>(null);
                if (_startTask != null && !_startTask.IsCompleted)
                    return _startTask;
                _startTask = StartAndWaitAsync(progress);
                return _startTask;
            }
        }

        /// <summary>Ask a running server to preload an engine/model in
        /// the background (fire and forget). Does nothing when the
        /// server is not up yet - it warms the selected model at start
        /// anyway.</summary>
        public static void RequestWarm(string engine, string modelId)
        {
            if (!IsReady)
                return;
            var cfg = Config ?? ServerConfig.Load();
            _ = SamServerClient.WarmAsync(cfg.Port, new WarmRequest
            {
                Engine = engine,
                ModelId = modelId,
                RitmCheckpoint = cfg.RitmCheckpoint,
            });
        }

        private static async Task<string> StartAndWaitAsync(
            Action<string> progress)
        {
            Config = ServerConfig.Load();

            var ping = await SamServerClient.PingAsync(Config.Port);
            if (ping != null && ping.Ok)
            {
                lock (Gate)
                    _ready = true;
                return null;
            }

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

            // Wait for /ping. The server answers as soon as it is
            // listening - torch, arcpy and the model load afterwards in
            // its warm-up thread - so poll in short steps instead of
            // wasting a whole second on a server that is already up.
            progress?.Invoke("Waiting for the SAM server ...");
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                ping = await SamServerClient.PingAsync(Config.Port, 1000);
                if (ping != null && ping.Ok)
                {
                    lock (Gate)
                        _ready = true;
                    return null;
                }
                if (!IsProcessAlive)
                    return "The Python server exited unexpectedly. " +
                           "Check the log:\n" + ServerConfig.LogPath;
                await Task.Delay(250);
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
                _ready = false;
                _startTask = null;
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
