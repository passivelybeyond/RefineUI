using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace RefineLauncher
{
    public partial class MainWindow
    {
        private static readonly string ExeDir = AppContext.BaseDirectory;
        private const string GitHubRepo = "passivelybeyond/RefineUI";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { { "User-Agent", "RefineLauncher" } }
        };

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) => await RunAsync();
        }

        private async Task RunAsync()
        {
            TryDelete(Path.Combine(ExeDir, "RefineLauncher.exe.old"));

            SetStatus("Checking for updates…");
            try
            {
                var (hasUpdate, zipUrl, tagName) = await CheckForUpdateAsync();
                if (hasUpdate && zipUrl != null)
                {
                    SetStatus($"Downloading {tagName}…");
                    ShowProgress(true);
                    await DownloadAndApplyAsync(zipUrl);
                    ShowProgress(false);
                }
            }
            catch { }

            Launch();
        }

        private async Task<(bool hasUpdate, string? zipUrl, string tagName)> CheckForUpdateAsync()
        {
            var json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest").ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var versionStr = tagName.TrimStart('v');

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (!Version.TryParse(versionStr, out var remote) || remote is null || remote <= current)
                return (false, null, tagName);

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return (true, asset.GetProperty("browser_download_url").GetString(), tagName);
            }

            return (false, null, tagName);
        }

        private async Task DownloadAndApplyAsync(string zipUrl)
        {
            var zipPath = Path.Combine(Path.GetTempPath(), "RefineUpdate.zip");
            var extractDir = Path.Combine(Path.GetTempPath(), "RefineUpdate");

            using (var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                var total = response.Content.Headers.ContentLength ?? 0L;
                using var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var dst = File.Create(zipPath);

                var buf = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read)).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        await Dispatcher.InvokeAsync(() => UpdateProgress.Value = (double)done / total * 100);
                }
            }

            SetStatus("Applying update…");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            foreach (var src in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extractDir, src);
                var dest = Path.Combine(ExeDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                if (Path.GetFileName(src).Equals("RefineLauncher.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var old = dest + ".old";
                    TryDelete(old);
                    File.Move(dest, old);
                }

                File.Copy(src, dest, overwrite: true);
            }

            TryDelete(zipPath);
            try { Directory.Delete(extractDir, recursive: true); } catch { }
        }

        private void Launch()
        {
            var target = Path.Combine(ExeDir, "RefineUI.exe");
            if (!File.Exists(target))
            {
                SetStatus("RefineUI.exe not found.");
                return;
            }

            SetStatus("Launching…");
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                WorkingDirectory = ExeDir,
                UseShellExecute = false
            });

            Dispatcher.Invoke(() => Application.Current.Shutdown());
        }

        private static readonly CubicEase EaseOut = new() { EasingMode = EasingMode.EaseOut };

        private void SetStatus(string text) =>
            Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = text;
                StatusText.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                    { EasingFunction = EaseOut });
            });

        private void ShowProgress(bool visible) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (visible)
                {
                    UpdateProgress.Visibility = Visibility.Visible;
                    UpdateProgress.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                        { EasingFunction = EaseOut });
                }
                else
                {
                    var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
                    { EasingFunction = EaseOut };
                    fade.Completed += (_, _) => UpdateProgress.Visibility = Visibility.Collapsed;
                    UpdateProgress.BeginAnimation(OpacityProperty, fade);
                }
            });

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
