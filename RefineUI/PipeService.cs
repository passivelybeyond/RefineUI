using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RefineUI
{
    public class DownloadProgressUpdate
    {
        public string Id { get; set; } = string.Empty;
        public float Percent { get; set; }
        public string Eta { get; set; } = string.Empty;
    }

    public class PipeService : IDisposable
    {
        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;
        private StreamReader? _reader;
        private readonly CancellationTokenSource _cts = new();
        private bool _isConnected = false;

        public event Action? OnWorkerReady;
        public event Action<string, string, int?>? OnSetupStatus;   // tool, status, percent?
        public event Action? OnSetupComplete;
        public event Action? OnSetupFailed;
        public event Action<string>? OnStarted;
        public event Action<DownloadProgressUpdate>? OnProgress;
        public event Action<string>? OnFinished;
        public event Action<string, string>? OnError;         // id, message
        public event Action<string>? OnConnectError;

        public async Task ConnectAsync()
        {
            try
            {
                string workerPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "RefineCore.exe");

                // Tell WPF if the exe is missing
                if (!File.Exists(workerPath))
                {
                    OnConnectError?.Invoke($"RefineCore.exe not found at:\n{workerPath}");
                    return;
                }

                // Kill any stale instance from a previous run
                foreach (var old in Process.GetProcessesByName("RefineCore"))
                {
                    try { old.Kill(); } catch { }
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = workerPath,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                _pipe = new NamedPipeClientStream(
                    ".", "RefinePipe",
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                // Give the worker up to 15s to create the pipe and start setup
                await _pipe.ConnectAsync(15000);
                _isConnected = true;

                _writer = new StreamWriter(_pipe) { AutoFlush = true };
                _reader = new StreamReader(_pipe);

                _ = ListenLoopAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                OnConnectError?.Invoke($"Failed to connect to worker: {ex.Message}");
            }
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            if (_reader == null) return;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line == null) break;

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();

                    switch (type)
                    {
                        case "ready":
                            OnWorkerReady?.Invoke();
                            break;

                        case "setup":
                            int? pct = root.TryGetProperty("percent", out var pEl)
                                       ? pEl.GetInt32() : null;
                            OnSetupStatus?.Invoke(
                                root.GetProperty("tool").GetString() ?? "",
                                root.GetProperty("status").GetString() ?? "",
                                pct);
                            break;

                        case "setup_complete":
                            OnSetupComplete?.Invoke();
                            break;

                        case "setup_failed":
                            OnSetupFailed?.Invoke();
                            break;

                        case "started":
                            OnStarted?.Invoke(root.GetProperty("id").GetString() ?? "");
                            break;

                        case "progress":
                            OnProgress?.Invoke(new DownloadProgressUpdate
                            {
                                Id      = root.GetProperty("id").GetString() ?? "",
                                Percent = root.GetProperty("percent").GetSingle(),
                                Eta     = root.GetProperty("eta").GetString() ?? ""
                            });
                            break;

                        case "finished":
                            OnFinished?.Invoke(root.GetProperty("id").GetString() ?? "");
                            break;

                        case "error":
                            OnError?.Invoke(
                                root.TryGetProperty("id", out var idProp)
                                    ? idProp.GetString() ?? "" : "",
                                root.GetProperty("message").GetString() ?? "");
                            break;
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                OnConnectError?.Invoke($"Pipe lost: {ex.Message}");
            }
        }

        public async Task SendDownloadAsync(
            string id, string url,
            string quality = "best",
            bool audioOnly = false,
            string format = "mp4",
            string outputDir = "downloads")
        {
            if (!_isConnected || _writer == null) return;

            var payload = JsonSerializer.Serialize(new
            {
                type = "download",
                id,
                url,
                quality,
                audioOnly,
                format,
                outputDir
            });
            await _writer.WriteLineAsync(payload);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
        }
    }
}