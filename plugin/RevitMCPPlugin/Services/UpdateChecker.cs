using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Checks the latest stable GitHub release and selects only the assets
    /// that exactly match the running Revit year and release version.
    /// Auto-install is offered only when both assets include valid SHA-256
    /// digests supplied by GitHub.
    /// </summary>
    internal sealed class UpdateChecker : IDisposable
    {
        private readonly string _owner;
        private readonly string _repo;
        private readonly Version _currentVersion;
        private readonly HttpClient _http;
        private readonly string _cachePath;
        private readonly object _checkLock = new object();
        private Task<bool> _checkTask;
        private bool _disposed;

        public string RevitYear { get; }
        public string RevitYearTag => "Revit" + RevitYear;
        public string LatestVersion { get; private set; }
        public string LatestTag { get; private set; }
        public string ReleaseNotesUrl { get; private set; }
        public string PluginZipUrl { get; private set; }
        public string UpdaterZipUrl { get; private set; }
        public string PluginZipSha256 { get; private set; }
        public string UpdaterZipSha256 { get; private set; }
        public long PluginZipSize { get; private set; }
        public long UpdaterZipSize { get; private set; }
        public string DownloadUrl => PluginZipUrl;
        public string ReleaseTitle { get; private set; }
        public string CurrentVersionText { get; }

        public UpdateChecker(
            string owner,
            string repo,
            Version currentVersion,
            string revitYear)
        {
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("Repository owner is required.", nameof(owner));
            if (string.IsNullOrWhiteSpace(repo))
                throw new ArgumentException("Repository name is required.", nameof(repo));
            if (!IsFourDigitYear(revitYear))
                throw new ArgumentException(
                    "The running Revit year must contain exactly four digits.",
                    nameof(revitYear));

            _owner = owner;
            _repo = repo;
            _currentVersion = currentVersion ?? new Version(0, 0, 0, 0);
            RevitYear = revitYear;
            CurrentVersionText = _currentVersion.ToString(3);

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"revit-mcp-v2/{CurrentVersionText} (+https://github.com/{owner}/{repo})");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-GitHub-Api-Version",
                "2022-11-28");

            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP");
            Directory.CreateDirectory(cacheDirectory);
            _cachePath = Path.Combine(cacheDirectory, "update-cache.json");
        }

        /// <summary>
        /// Process-wide instance single-flight: repeated callers share the same
        /// check task rather than racing GitHub or mutating discovery state.
        /// </summary>
        public Task<bool> CheckAsync()
        {
            lock (_checkLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(UpdateChecker));
                return _checkTask ??= CheckCoreAsync();
            }
        }

        private async Task<bool> CheckCoreAsync()
        {
            try
            {
                if (IsSnoozed())
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[RevitMCP.Update] Snoozed; skipping check.");
                    return false;
                }

                var url =
                    $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var release = JsonSerializer.Deserialize<GitHubRelease>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (release == null || release.Draft || release.Prerelease)
                    return false;
                if (!TryParseTag(release.TagName, out var latest))
                    return false;
                if (latest <= _currentVersion)
                    return false;

                var versionText = latest.ToString(3);
                var expectedPluginName =
                    $"RevitMCPPlugin-{versionText}-Revit{RevitYear}.zip";
                var expectedUpdaterName =
                    $"RevitMCPUpdater-{versionText}.zip";

                var pluginAsset = FindExactAsset(
                    release.Assets,
                    expectedPluginName);
                var updaterAsset = FindExactAsset(
                    release.Assets,
                    expectedUpdaterName);

                if (!IsUsableAsset(pluginAsset, out var pluginDigest) ||
                    !IsUsableAsset(updaterAsset, out var updaterDigest))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP.Update] Release {release.TagName} does not " +
                        $"contain digest-protected exact assets " +
                        $"'{expectedPluginName}' and '{expectedUpdaterName}'.");
                    return false;
                }

                LatestVersion = versionText;
                LatestTag = release.TagName;
                ReleaseNotesUrl = release.HtmlUrl;
                ReleaseTitle = !string.IsNullOrWhiteSpace(release.Name)
                    ? release.Name
                    : release.TagName;
                PluginZipUrl = pluginAsset.DownloadUrl;
                UpdaterZipUrl = updaterAsset.DownloadUrl;
                PluginZipSha256 = pluginDigest;
                UpdaterZipSha256 = updaterDigest;
                PluginZipSize = pluginAsset.Size;
                UpdaterZipSize = updaterAsset.Size;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP.Update] Check failed (non-fatal): {ex.Message}");
                return false;
            }
        }

        public void SnoozeForToday()
        {
            try
            {
                var cache = LoadCache();
                cache.SnoozeUntilUtc = DateTime.UtcNow.Date.AddDays(1);
                cache.LastCheckUtc = DateTime.UtcNow;
                cache.LastKnownVersion = LatestVersion;
                SaveCache(cache);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP.Update] Snooze save failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            lock (_checkLock)
            {
                if (_disposed) return;
                _disposed = true;
                _http.Dispose();
            }
        }

        private bool IsSnoozed()
        {
            var cache = LoadCache();
            return cache.SnoozeUntilUtc > DateTime.UtcNow;
        }

        private UpdateCache LoadCache()
        {
            try
            {
                if (!File.Exists(_cachePath))
                    return new UpdateCache();
                var json = File.ReadAllText(_cachePath);
                return JsonSerializer.Deserialize<UpdateCache>(json)
                       ?? new UpdateCache();
            }
            catch
            {
                return new UpdateCache();
            }
        }

        private void SaveCache(UpdateCache cache)
        {
            var json = JsonSerializer.Serialize(
                cache,
                new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = _cachePath + ".tmp";
            File.WriteAllText(temporaryPath, json);

            if (File.Exists(_cachePath))
            {
                var backupPath = _cachePath + ".bak";
                File.Replace(temporaryPath, _cachePath, backupPath);
            }
            else
            {
                File.Move(temporaryPath, _cachePath);
            }
        }

        private static GitHubAsset FindExactAsset(
            IEnumerable<GitHubAsset> assets,
            string expectedName)
        {
            return assets?.FirstOrDefault(asset =>
                asset != null &&
                string.Equals(
                    asset.Name,
                    expectedName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUsableAsset(
            GitHubAsset asset,
            out string sha256)
        {
            sha256 = null;
            return asset != null &&
                   asset.Size > 0 &&
                   IsHttpsUrl(asset.DownloadUrl) &&
                   TryParseSha256Digest(asset.Digest, out sha256);
        }

        private static bool IsHttpsUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   string.Equals(
                       uri.Scheme,
                       Uri.UriSchemeHttps,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseSha256Digest(
            string digest,
            out string sha256)
        {
            sha256 = null;
            if (string.IsNullOrWhiteSpace(digest))
                return false;

            const string prefix = "sha256:";
            var trimmed = digest.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var value = trimmed.Substring(prefix.Length);
            if (value.Length != 64 || value.Any(character =>
                    !((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F'))))
            {
                return false;
            }

            sha256 = value.ToLowerInvariant();
            return true;
        }

        private static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag))
                return false;
            var trimmed = tag.TrimStart('v', 'V').Trim();
            return Version.TryParse(trimmed, out version);
        }

        private static bool IsFourDigitYear(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Length == 4 &&
                   value.All(character => character >= '0' && character <= '9');
        }

        private sealed class UpdateCache
        {
            [JsonPropertyName("last_check_utc")]
            public DateTime LastCheckUtc { get; set; }

            [JsonPropertyName("snooze_until_utc")]
            public DateTime SnoozeUntilUtc { get; set; }

            [JsonPropertyName("last_known_version")]
            public string LastKnownVersion { get; set; }
        }
    }
}
