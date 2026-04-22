using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using RevitMCP.Plugin.Services;

namespace RevitMCP.Plugin.UI
{
    /// <summary>
    /// Modal update notification shown when a newer plugin version is
    /// available on GitHub. Styled to roughly match SMART MEP's dialog.
    ///
    /// Clicking "Download" now drives the full auto-install flow:
    ///   1. Download the plugin zip matching the installed Revit year.
    ///   2. Download + extract RevitMCPUpdater.exe to %LocalAppData%.
    ///   3. Spawn the updater with --wait so it survives Revit shutdown.
    ///   4. Prompt the user to close Revit; extraction happens afterward.
    /// </summary>
    public partial class UpdateNotificationWindow : Window
    {
        private readonly UpdateChecker _checker;
        private bool _installLaunched;

        internal UpdateNotificationWindow(UpdateChecker checker)
        {
            _checker = checker ?? throw new ArgumentNullException(nameof(checker));
            InitializeComponent();

            CurrentVersionText.Text = "v" + _checker.CurrentVersionText;
            LatestVersionText.Text  = "v" + _checker.LatestVersion;

            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(_checker.ReleaseTitle)
                ? "Github Release Note 참조"
                : _checker.ReleaseTitle;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            HandleSnoozeIfChecked();
            Close();
        }

        private async void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            if (_installLaunched) { Close(); return; }

            // If the release didn't ship an auto-install-capable updater asset,
            // fall back to opening the plugin zip URL in the user's browser.
            if (string.IsNullOrWhiteSpace(_checker.UpdaterZipUrl))
            {
                OpenInBrowser(_checker.DownloadUrl ?? _checker.ReleaseNotesUrl);
                HandleSnoozeIfChecked();
                Close();
                return;
            }

            SetBusy(true, "다운로드 준비 중...");
            try
            {
                var (pluginZip, updaterExe) = await DownloadAndExtractAsync();
                LaunchUpdater(updaterExe, pluginZip);
                _installLaunched = true;

                SetBusy(false, null);
                StatusText.Text = "✅ 다운로드 완료. Revit을 종료하면 업데이트가 자동 적용됩니다.";
                DownloadButton.Content = "닫기";
                CloseButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP.Update] Auto-install failed: {ex}");
                SetBusy(false,
                    $"자동 설치 실패: {ex.Message}\n대신 브라우저로 다운로드 페이지를 엽니다...");
                OpenInBrowser(_checker.ReleaseNotesUrl ?? _checker.DownloadUrl);
            }
        }

        // ─── Download / install pipeline ─────────────────────────────────

        /// <summary>
        /// Download both zips to a fresh versioned folder under
        /// %LocalAppData%\RevitMCP\Updates\<version>\, extract the updater,
        /// and return paths to both artifacts.
        /// </summary>
        private async Task<(string pluginZipPath, string updaterExePath)>
            DownloadAndExtractAsync()
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP", "Updates", "v" + _checker.LatestVersion);
            Directory.CreateDirectory(baseDir);

            var pluginZipPath  = Path.Combine(baseDir, "plugin.zip");
            var updaterZipPath = Path.Combine(baseDir, "updater.zip");
            var updaterDir     = Path.Combine(baseDir, "updater");

            using (var http = CreateHttpClient())
            {
                SetStatus("플러그인 다운로드 중...");
                await DownloadFileAsync(http, _checker.DownloadUrl, pluginZipPath);

                SetStatus("업데이터 다운로드 중...");
                await DownloadFileAsync(http, _checker.UpdaterZipUrl, updaterZipPath);
            }

            SetStatus("업데이터 추출 중...");
            if (Directory.Exists(updaterDir)) Directory.Delete(updaterDir, true);
            Directory.CreateDirectory(updaterDir);
            ZipFile.ExtractToDirectory(updaterZipPath, updaterDir);

            var updaterExe = Path.Combine(updaterDir, "RevitMCPUpdater.exe");
            if (!File.Exists(updaterExe))
            {
                throw new FileNotFoundException(
                    "RevitMCPUpdater.exe not found in the downloaded updater zip.",
                    updaterExe);
            }

            return (pluginZipPath, updaterExe);
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "revit-mcp-v2-auto-installer");
            // GitHub redirects /releases/download/... through a CDN; HttpClient
            // follows redirects by default, so nothing extra needed here.
            return http;
        }

        private async Task DownloadFileAsync(HttpClient http, string url, string destPath)
        {
            // Stream to disk — GitHub release assets can be several MB and
            // buffering the full zip in memory is wasteful.
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var src  = await response.Content.ReadAsStreamAsync();
            using var dest = File.Create(destPath);
            await src.CopyToAsync(dest);
        }

        /// <summary>
        /// Launch RevitMCPUpdater.exe as a detached process. Using
        /// UseShellExecute=true ensures the updater is not parented to
        /// the Revit process and will survive Revit shutdown.
        /// </summary>
        private static void LaunchUpdater(string updaterExe, string pluginZipPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments = $"--zip \"{pluginZipPath}\" --revit-year {UpdateChecker.RevitYear} --wait",
                UseShellExecute = true,   // detach from Revit
                CreateNoWindow = false,   // keep console visible so user sees progress
                WorkingDirectory = Path.GetDirectoryName(updaterExe) ?? "",
            };
            Process.Start(psi);
        }

        private static void OpenInBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP.Update] OpenInBrowser failed: {ex.Message}");
            }
        }

        // ─── UI state helpers ────────────────────────────────────────────

        private void SetBusy(bool busy, string status)
        {
            DownloadButton.IsEnabled = !busy;
            CloseButton.IsEnabled    = !busy;
            SnoozeCheckBox.IsEnabled = !busy;
            if (status != null) StatusText.Text = status;
        }

        private void SetStatus(string message) => StatusText.Text = message;

        private void HandleSnoozeIfChecked()
        {
            if (SnoozeCheckBox.IsChecked == true)
            {
                _checker.SnoozeForToday();
            }
        }
    }
}
