using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Checks GitHub Releases for a newer plugin version and manages
    /// local snooze state (the "don't show today" checkbox).
    ///
    /// Harness Engineering:
    /// - Fail-safe: network/filesystem errors are caught and logged.
    ///   An update check must NEVER prevent the plugin from starting.
    /// - Idempotent: calling CheckAsync multiple times is safe; only the
    ///   first network call hits GitHub within a single process lifetime.
    /// - Quiet fallback: when offline, we stay silent rather than nagging.
    /// </summary>
    internal sealed class UpdateChecker
    {
        // Target framework → Revit year mapping. Used to pick the right
        // release asset (plugin zips are suffixed with "-Revit<year>").
#if NET48
        public const string RevitYear    = "2023";
        public const string RevitYearTag = "Revit2023";
#else
        public const string RevitYear    = "2025";
        public const string RevitYearTag = "Revit2025";
#endif


        private readonly string _owner;
        private readonly string _repo;
        private readonly Version _currentVersion;
        private readonly HttpClient _http;
        private readonly string _cachePath;

        /// <summary>The version discovered on GitHub (e.g., "0.3.0").</summary>
        public string LatestVersion { get; private set; }

        /// <summary>Release tag (e.g., "v0.2.0") — preserves any leading 'v'.</summary>
        public string LatestTag { get; private set; }

        /// <summary>HTML link to the release notes page.</summary>
        public string ReleaseNotesUrl { get; private set; }

        /// <summary>
        /// Direct download URL for the plugin zip matching the current
        /// Revit year (e.g., RevitMCPPlugin-0.2.0-Revit2025.zip).
        /// </summary>
        public string PluginZipUrl { get; private set; }

        /// <summary>
        /// Direct download URL for the standalone updater zip
        /// (RevitMCPUpdater-0.2.0.zip). Required for auto-install flow.
        /// </summary>
        public string UpdaterZipUrl { get; private set; }

        /// <summary>
        /// Legacy alias for PluginZipUrl — preserved so earlier UI code
        /// opening the "Download" link in a browser still works.
        /// </summary>
        public string DownloadUrl => PluginZipUrl;

        /// <summary>Human-readable release title (falls back to tag).</summary>
        public string ReleaseTitle { get; private set; }

        /// <summary>The current running plugin version, exposed for the UI.</summary>
        public string CurrentVersionText { get; }

        public UpdateChecker(string owner, string repo, Version currentVersion)
        {
            _owner = owner;
            _repo = repo;
            _currentVersion = currentVersion ?? new Version(0, 0, 0, 0);
            CurrentVersionText = _currentVersion.ToString(3);

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"revit-mcp-v2/{CurrentVersionText} (+https://github.com/{owner}/{repo})");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP");
            Directory.CreateDirectory(cacheDir);
            _cachePath = Path.Combine(cacheDir, "update-cache.json");
        }

        /// <summary>
        /// Returns true if an update is available and the user has not
        /// snoozed notifications for today. All exceptions are swallowed
        /// (logged) so the caller can fire-and-forget.
        /// </summary>
        public async Task<bool> CheckAsync()
        {
            try
            {
                if (IsSnoozed())
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[RevitMCP.Update] Snoozed — skipping check.");
                    return false;
                }

                var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);

                var release = JsonSerializer.Deserialize<GitHubRelease>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (release == null || release.Draft || release.Prerelease)
                    return false;

                if (!TryParseTag(release.TagName, out var latest))
                    return false;

                if (latest <= _currentVersion)
                    return false;

                LatestVersion = latest.ToString(3);
                LatestTag = release.TagName;
                ReleaseNotesUrl = release.HtmlUrl;
                ReleaseTitle = !string.IsNullOrWhiteSpace(release.Name)
                    ? release.Name
                    : release.TagName;

                // Separate the two asset types our release workflow publishes:
                //   (1) Plugin zip matching the running Revit year
                //       e.g., RevitMCPPlugin-0.2.0-Revit2025.zip
                //   (2) Updater zip (no year suffix)
                //       e.g., RevitMCPUpdater-0.2.0.zip
                //
                // If the plugin zip for our year isn't present we fall back
                // to any plugin zip, then to the release HTML URL.
                var pluginAsset =
                    FindAsset(release.Assets, a =>
                        a.Name != null &&
                        a.Name.IndexOf(RevitYearTag, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? FindAsset(release.Assets, a =>
                        a.Name != null &&
                        a.Name.StartsWith("RevitMCPPlugin-", StringComparison.OrdinalIgnoreCase) &&
                        a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                var updaterAsset = FindAsset(release.Assets, a =>
                    a.Name != null &&
                    a.Name.StartsWith("RevitMCPUpdater-", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                PluginZipUrl  = pluginAsset?.DownloadUrl ?? release.HtmlUrl;
                UpdaterZipUrl = updaterAsset?.DownloadUrl;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP.Update] Check failed (non-fatal): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Persist "don't show again today" state so we don't re-prompt on
        /// the next Revit start within the same calendar day.
        /// </summary>
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

        // ─── Internals ────────────────────────────────────────────────────

        private bool IsSnoozed()
        {
            var cache = LoadCache();
            return cache.SnoozeUntilUtc > DateTime.UtcNow;
        }

        private UpdateCache LoadCache()
        {
            try
            {
                if (!File.Exists(_cachePath)) return new UpdateCache();
                var json = File.ReadAllText(_cachePath);
                return JsonSerializer.Deserialize<UpdateCache>(json) ?? new UpdateCache();
            }
            catch
            {
                // Corrupt cache file → start fresh; never propagate.
                return new UpdateCache();
            }
        }

        private void SaveCache(UpdateCache cache)
        {
            var json = JsonSerializer.Serialize(cache,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }

        /// <summary>
        /// Linear-scan helper for picking a GitHub release asset that
        /// satisfies a predicate. Kept tiny so it inlines cleanly.
        /// </summary>
        private static GitHubAsset FindAsset(
            System.Collections.Generic.List<GitHubAsset> assets,
            Func<GitHubAsset, bool> predicate)
        {
            if (assets == null) return null;
            foreach (var a in assets)
                if (a != null && predicate(a)) return a;
            return null;
        }

        /// <summary>
        /// Parse a GitHub tag like "v0.2.0" or "0.2.0" into a Version.
        /// Returns false for malformed or missing tags.
        /// </summary>
        private static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            var trimmed = tag.TrimStart('v', 'V').Trim();
            return Version.TryParse(trimmed, out version);
        }

        private class UpdateCache
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
