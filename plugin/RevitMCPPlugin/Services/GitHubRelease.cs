using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Minimal DTO for the GitHub Releases API response.
    /// Reference: https://docs.github.com/en/rest/releases/releases
    /// Only the fields required by our auto-update flow are modeled.
    /// </summary>
    internal class GitHubRelease
    {
        /// <summary>
        /// Tag name — the source of truth for the release version
        /// (e.g., "v0.2.0"). The leading 'v' is stripped before parsing.
        /// </summary>
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        /// <summary>
        /// Human-readable release name (e.g., "Harness Engineering Tier 1").
        /// May be null — callers should fall back to TagName.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Public GitHub URL for the release notes page — opened by the
        /// "View release notes" link in the update dialog.
        /// </summary>
        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; }

        /// <summary>
        /// True for draft releases (not yet published). We skip these —
        /// users should not receive notifications for unpublished work.
        /// </summary>
        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        /// <summary>
        /// True for pre-releases (beta/rc). We skip these on the stable
        /// update channel; can be flipped on later for a beta channel.
        /// </summary>
        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        /// <summary>
        /// Release body — markdown-formatted changelog.
        /// Shown optionally in a collapsible panel of the update dialog.
        /// </summary>
        [JsonPropertyName("body")]
        public string Body { get; set; }

        /// <summary>
        /// Downloadable assets (zip/msi/exe attached to the release).
        /// </summary>
        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new List<GitHubAsset>();
    }

    /// <summary>
    /// A downloadable file attached to a GitHub release (zip/msi/exe/etc).
    /// </summary>
    internal class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Direct CDN URL for downloading the binary asset. This is the
        /// public, unauthenticated URL — what we point the user to when
        /// they click "Download" in the update dialog.
        /// </summary>
        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("content_type")]
        public string ContentType { get; set; }
    }
}
