using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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

            if (string.IsNullOrWhiteSpace(_checker.PluginZipUrl) ||
                string.IsNullOrWhiteSpace(_checker.UpdaterZipUrl) ||
                string.IsNullOrWhiteSpace(_checker.PluginZipSha256) ||
                string.IsNullOrWhiteSpace(_checker.UpdaterZipSha256))
            {
                StatusText.Text =
                    "This release does not contain verified installer assets " +
                    "for the running Revit version.";
                return;
            }

            SetBusy(true, "다운로드 준비 중...");
            try
            {
                var (pluginZip, updaterExe) = await DownloadAndExtractAsync();
                LaunchUpdater(
                    updaterExe,
                    pluginZip,
                    _checker.RevitYear,
                    _checker.PluginZipSha256,
                    _checker.PluginZipSize);
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

        private static void ExtractZipSafely(string zipPath, string destinationDirectory)
        {
            var root = EnsureTrailingSeparator(
                Path.GetFullPath(destinationDirectory));
            var seenPaths = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (IsSymbolicLink(entry))
                {
                    throw new InvalidDataException(
                        $"Updater archive contains a symbolic link: {entry.FullName}");
                }

                var normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalizedName))
                {
                    throw new InvalidDataException(
                        $"Updater archive contains a rooted path: {entry.FullName}");
                }

                var destinationPath = Path.GetFullPath(
                    Path.Combine(root, normalizedName));
                if (!destinationPath.StartsWith(
                        root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Updater archive entry escapes its destination: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                if (!seenPaths.Add(destinationPath))
                {
                    throw new InvalidDataException(
                        $"Updater archive contains a duplicate path: {entry.FullName}");
                }

                Directory.CreateDirectory(
                    Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidDataException("Invalid updater archive path."));
                entry.ExtractToFile(destinationPath, overwrite: false);
            }
        }

        private static bool IsSymbolicLink(ZipArchiveEntry entry)
        {
            const int unixFileTypeMask = 0xF000;
            const int unixSymbolicLink = 0xA000;
            var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
            var windowsAttributes =
                (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
            return unixMode == unixSymbolicLink ||
                   (windowsAttributes & FileAttributes.ReparsePoint) != 0;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(
                       Path.DirectorySeparatorChar.ToString(),
                       StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
                builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        private static bool FixedTimeHexEquals(string actual, string expected)
        {
            var actualBytes = Encoding.ASCII.GetBytes(actual ?? "");
            var expectedBytes = Encoding.ASCII.GetBytes(
                (expected ?? "").ToLowerInvariant());
            var max = Math.Max(actualBytes.Length, expectedBytes.Length);
            var difference = actualBytes.Length ^ expectedBytes.Length;
            for (var index = 0; index < max; index++)
            {
                var actualByte =
                    index < actualBytes.Length ? actualBytes[index] : (byte)0;
                var expectedByte =
                    index < expectedBytes.Length ? expectedBytes[index] : (byte)0;
                difference |= actualByte ^ expectedByte;
            }
            return difference == 0;
        }

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
            var updaterDir = Path.Combine(
                baseDir,
                "updater-" + Guid.NewGuid().ToString("N"));

            using (var http = CreateHttpClient())
            {
                SetStatus("플러그인 다운로드 중...");
                await DownloadFileAsync(
                    http,
                    _checker.PluginZipUrl,
                    pluginZipPath,
                    _checker.PluginZipSha256,
                    _checker.PluginZipSize);

                SetStatus("업데이터 다운로드 중...");
                await DownloadFileAsync(
                    http,
                    _checker.UpdaterZipUrl,
                    updaterZipPath,
                    _checker.UpdaterZipSha256,
                    _checker.UpdaterZipSize);
            }

            SetStatus("업데이터 추출 중...");
            Directory.CreateDirectory(updaterDir);
            ExtractZipSafely(updaterZipPath, updaterDir);

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

        private async Task DownloadFileAsync(
            HttpClient http,
            string url,
            string destPath,
            string expectedSha256,
            long expectedSize)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                string.IsNullOrWhiteSpace(expectedSha256) ||
                expectedSize <= 0)
            {
                throw new InvalidOperationException(
                    "The release asset is missing URL, size, or SHA-256 metadata.");
            }

            var temporaryPath =
                destPath + ".download-" + Guid.NewGuid().ToString("N");
            try
            {
                using var response = await http.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value != expectedSize)
                {
                    throw new InvalidDataException(
                        $"Download size header mismatch. Expected {expectedSize} bytes, " +
                        $"received {response.Content.Headers.ContentLength.Value}.");
                }

                long downloaded = 0;
                byte[] digest;
                using (var sha256 = SHA256.Create())
                using (var source = await response.Content.ReadAsStreamAsync())
                using (var destination = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                using (var hashingStream = new CryptoStream(
                           destination,
                           sha256,
                           CryptoStreamMode.Write))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(
                               buffer,
                               0,
                               buffer.Length)) > 0)
                    {
                        downloaded += read;
                        if (downloaded > expectedSize)
                        {
                            throw new InvalidDataException(
                                $"Download exceeded the expected size of {expectedSize} bytes.");
                        }

                        await hashingStream.WriteAsync(buffer, 0, read);
                    }

                    hashingStream.FlushFinalBlock();
                    digest = sha256.Hash;
                }

                if (downloaded != expectedSize)
                {
                    throw new InvalidDataException(
                        $"Download size mismatch. Expected {expectedSize} bytes, " +
                        $"received {downloaded}.");
                }

                var actualSha256 = ToLowerHex(digest);
                if (!FixedTimeHexEquals(actualSha256, expectedSha256))
                {
                    throw new InvalidDataException(
                        "Downloaded asset SHA-256 does not match the GitHub release digest.");
                }

                if (File.Exists(destPath))
                    File.Replace(temporaryPath, destPath, null);
                else
                    File.Move(temporaryPath, destPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup; verification failure still propagates.
                }
            }
        }

        /// <summary>
        /// Launch RevitMCPUpdater.exe as a detached process. Using
        /// UseShellExecute=true ensures the updater is not parented to
        /// the Revit process and will survive Revit shutdown.
        /// </summary>
        private static void LaunchUpdater(
            string updaterExe,
            string pluginZipPath,
            string revitYear,
            string pluginZipSha256,
            long pluginZipSize)
        {
            var psi = new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments =
                    $"--zip \"{pluginZipPath}\" --product revit " +
                    $"--revit-year {revitYear} --sha256 {pluginZipSha256} " +
                    $"--size {pluginZipSize} --wait",
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
