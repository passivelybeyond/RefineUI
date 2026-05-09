using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

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
            WindowBackdropType = WindowBackdropType.Mica;
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
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
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest");

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

            using (var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                var total = response.Content.Headers.ContentLength ?? 0L;
                using var src = await response.Content.ReadAsStreamAsync();
                using var dst = File.Create(zipPath);

                var buf = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                        Dispatcher.Invoke(() => UpdateProgress.Value = (double)done / total * 100);
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
                UseShellExecute = true
            });

            Dispatcher.Invoke(() => Application.Current.Shutdown());
        }

        private void SetStatus(string text) =>
            Dispatcher.Invoke(() => StatusText.Text = text);

        private void ShowProgress(bool visible) =>
            Dispatcher.Invoke(() =>
                UpdateProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed);

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
