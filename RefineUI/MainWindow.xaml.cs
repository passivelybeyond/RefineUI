using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using YoutubeExplode;
using YoutubeExplode.Common;

namespace RefineUI
{
    public partial class MainWindow : FluentWindow
    {
        private readonly YoutubeClient _youtube;
        private readonly PipeService _pipe;

        private string _selectedFormat = "mp4";
        private string _selectedQuality = "1080";
        private bool _isAudioOnly = false;
        private bool _setupReady = false;

        private string _downloadFolder;
        private bool _copyToClipboard = true;
        private DateTime _downloadStarted = DateTime.MinValue;
        private static readonly string _settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public MainWindow()
        {
            WindowBackdropType = WindowBackdropType.Mica;
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            InitializeComponent();

            _downloadFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "downloads");
            LoadSettings();

            _youtube = new YoutubeClient();
            _pipe = new PipeService();

            DownloadButton.IsEnabled = false;

            WireUpPipeEvents();
            _ = _pipe.ConnectAsync();

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                Dispatcher.Invoke(() =>
                {
                    if (!_setupReady)
                        SetStatus($"Still waiting for worker… check RefineCore.exe exists at:\n{AppDomain.CurrentDomain.BaseDirectory}");
                    ShowProgress(true);
                });
            });
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                var json = File.ReadAllText(_settingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("downloadFolder", out var el))
                {
                    var folder = el.GetString();
                    if (!string.IsNullOrEmpty(folder))
                        _downloadFolder = folder;
                }
                if (doc.RootElement.TryGetProperty("copyToClipboard", out var ct))
                    _copyToClipboard = ct.GetBoolean();
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var json = JsonSerializer.Serialize(new { downloadFolder = _downloadFolder, copyToClipboard = _copyToClipboard });
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        private void WireUpPipeEvents()
        {
            _pipe.OnWorkerReady += () => Dispatcher.Invoke(() =>
            {
                ShowProgress(true);
                SetStatus("Checking tools…");
            });

            _pipe.OnSetupStatus += (tool, status, percent) => Dispatcher.Invoke(() =>
            {
                string pctStr = percent.HasValue ? $"  {percent}%" : "";
                SetStatus($"{tool}: {status}{pctStr}");

                if (percent.HasValue)
                {
                    DownloadProgress.Visibility = Visibility.Visible;
                    DownloadProgress.Value = percent.Value;
                }
                else
                {
                    DownloadProgress.Visibility = Visibility.Visible;
                    DownloadProgress.IsIndeterminate = true;
                }
            });

            _pipe.OnSetupComplete += () => Dispatcher.Invoke(() =>
            {
                _setupReady = true;
                DownloadProgress.IsIndeterminate = false;
                ShowProgress(false);
                DownloadButton.IsEnabled = true;
                SetStatus("");
            });

            _pipe.OnSetupFailed += () => Dispatcher.Invoke(() =>
            {
                SetStatus("Setup failed — check your internet connection.");
                DownloadProgress.IsIndeterminate = false;
                ShowProgress(true);
            });

            _pipe.OnStarted += id => Dispatcher.Invoke(() =>
            {
                _downloadStarted = DateTime.Now;
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = 0;
                ShowProgress(true);
                SetStatus("Starting download…");
                DownloadButton.IsEnabled = false;
            });

            _pipe.OnProgress += p => Dispatcher.Invoke(() =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = p.Percent;
                SetStatus($"Downloading — {p.Percent:0.0}%  ETA {p.Eta}");
            });

            _pipe.OnFinished += id => Dispatcher.Invoke(() =>
            {
                DownloadProgress.Value = 100;
                DownloadButton.IsEnabled = true;

                if (_copyToClipboard)
                    TryCopyNewestFileToClipboard();

                SetStatus("Done ✓");

                var timer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, __) => { ShowProgress(false); timer.Stop(); };
                timer.Start();
            });

            _pipe.OnError += (id, msg) => Dispatcher.Invoke(() =>
            {
                SetStatus($"Error: {msg}");
                DownloadButton.IsEnabled = _setupReady;
            });

            _pipe.OnConnectError += msg => Dispatcher.Invoke(() =>
            {
                SetStatus($"Worker error: {msg}");
                ShowProgress(true);
                DownloadProgress.IsIndeterminate = false;
                DownloadButton.IsEnabled = true;
                _setupReady = true;
            });
        }

        private void ShowProgress(bool visible)
        {
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            DownloadProgress.Visibility = vis;
            StatusText.Visibility = vis;
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = string.IsNullOrEmpty(text)
                                    ? Visibility.Collapsed
                                    : Visibility.Visible;
        }

        private void FileType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem item) return;
            FileTypeDropdown.Content = item.Header;

            switch (item.Header?.ToString())
            {
                case "MP4 (Video)":
                    _selectedFormat = "mp4";
                    _isAudioOnly = false;
                    VideoQualityDropdown.IsEnabled = true;
                    break;
                case "MP3 (Audio)":
                    _selectedFormat = "mp3";
                    _isAudioOnly = true;
                    VideoQualityDropdown.IsEnabled = false;
                    break;
                case "WAV (Audio)":
                    _selectedFormat = "wav";
                    _isAudioOnly = true;
                    VideoQualityDropdown.IsEnabled = false;
                    break;
            }
        }

        private void VideoQuality_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem item) return;
            VideoQualityDropdown.Content = item.Header;

            _selectedQuality = item.Header?.ToString() switch
            {
                "4K (UHD)" => "2160",
                "1440p (QHD)" => "1440",
                "1080p (FHD)" => "1080",
                "720p" => "720",
                "480p" => "480",
                "360p" => "360",
                "144p" => "144",
                _ => "1080"
            };
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            if (Clipboard.ContainsText())
                URLTextbox.Text = Clipboard.GetText();
        }

        private void TryCopyNewestFileToClipboard()
        {
            try
            {
                if (!Directory.Exists(_downloadFolder)) return;
                var newest = new DirectoryInfo(_downloadFolder)
                    .GetFiles()
                    .Where(f => f.LastWriteTime >= _downloadStarted)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                if (newest != null)
                {
                    var files = new System.Collections.Specialized.StringCollection();
                    files.Add(newest.FullName);
                    Clipboard.SetFileDropList(files);
                }
            }
            catch { }
        }

        private void CopyToClipboard_Changed(object sender, RoutedEventArgs e)
        {
            _copyToClipboard = CopyToClipboardToggle.IsChecked == true;
            SaveSettings();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            bool showingSettings = SettingsView.Visibility == Visibility.Visible;
            if (showingSettings)
            {
                SettingsView.Visibility = Visibility.Collapsed;
                MainView.Visibility = Visibility.Visible;
            }
            else
            {
                DownloadFolderText.Text = _downloadFolder;
                CopyToClipboardToggle.IsChecked = _copyToClipboard;
                MainView.Visibility = Visibility.Collapsed;
                SettingsView.Visibility = Visibility.Visible;
            }
        }

        private void OpenDownloads_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(_downloadFolder))
                Directory.CreateDirectory(_downloadFolder);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _downloadFolder,
                UseShellExecute = true
            });
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select download folder",
                InitialDirectory = Directory.Exists(_downloadFolder) ? _downloadFolder : null
            };

            if (dialog.ShowDialog() == true)
            {
                _downloadFolder = dialog.FolderName;
                DownloadFolderText.Text = _downloadFolder;
                SaveSettings();
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_setupReady) return;

            string url = URLTextbox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(url)) return;

            DownloadButton.IsEnabled = false;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;

            try
            {
                ShowProgress(true);
                DownloadProgress.IsIndeterminate = true;
                SetStatus("Fetching info…");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var video = await _youtube.Videos.GetAsync(url, cts.Token);
                TitleTextBlock.Text = video.Title;

                var thumb = video.Thumbnails.TryGetWithHighestResolution();
                if (thumb != null)
                    ThumbnailImage.Source = new BitmapImage(new Uri(thumb.Url));

                DownloadProgress.IsIndeterminate = false;
                SetStatus("Starting download…");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Timed out fetching video info.");
                DownloadProgress.IsIndeterminate = false;
                DownloadButton.IsEnabled = true;
                return;
            }
            catch (Exception ex)
            {
                SetStatus($"Could not fetch video info: {ex.Message}");
                DownloadProgress.IsIndeterminate = false;
                DownloadButton.IsEnabled = true;
                return;
            }

            try
            {
                Directory.CreateDirectory(_downloadFolder);

                string jobId = Guid.NewGuid().ToString("N")[..8];

                await _pipe.SendDownloadAsync(
                    id: jobId,
                    url: URLTextbox.Text.Trim(),
                    quality: _selectedQuality,
                    audioOnly: _isAudioOnly,
                    format: _selectedFormat,
                    outputDir: _downloadFolder);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to send to RefineCore: {ex.Message}");
                DownloadButton.IsEnabled = true;
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            base.OnStateChanged(e);
        }

        private void FluentWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            FocusManager.SetFocusedElement(this, null);
            Keyboard.ClearFocus();
        }

        protected override void OnClosed(EventArgs e)
        {
            _pipe?.Dispose();
            base.OnClosed(e);
        }
    }
}
