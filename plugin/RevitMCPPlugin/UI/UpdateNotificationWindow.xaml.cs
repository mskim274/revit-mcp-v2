using System;
using System.Diagnostics;
using System.Windows;
using RevitMCP.Plugin.Services;

namespace RevitMCP.Plugin.UI
{
    /// <summary>
    /// Modal update notification shown when a newer plugin version is
    /// available on GitHub. Styled to roughly match SMART MEP's dialog
    /// (version comparison + release notes preview + download button).
    /// </summary>
    public partial class UpdateNotificationWindow : Window
    {
        private readonly UpdateChecker _checker;

        internal UpdateNotificationWindow(UpdateChecker checker)
        {
            _checker = checker ?? throw new ArgumentNullException(nameof(checker));
            InitializeComponent();

            CurrentVersionText.Text = "v" + _checker.CurrentVersionText;
            LatestVersionText.Text  = "v" + _checker.LatestVersion;

            // Release notes preview — minimal for now; clickable link opens
            // the full GitHub release page for details.
            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(_checker.ReleaseTitle)
                ? "Github Release Note 참조"
                : _checker.ReleaseTitle;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            HandleSnoozeIfChecked();
            Close();
        }

        private void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            HandleSnoozeIfChecked();
            try
            {
                // Prefer the direct asset URL (zip/msi). Fall back to
                // the release notes HTML page if no asset was attached.
                var url = !string.IsNullOrWhiteSpace(_checker.DownloadUrl)
                    ? _checker.DownloadUrl
                    : _checker.ReleaseNotesUrl;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP.Update] Failed to open download URL: {ex.Message}");
            }
            Close();
        }

        private void HandleSnoozeIfChecked()
        {
            if (SnoozeCheckBox.IsChecked == true)
            {
                _checker.SnoozeForToday();
            }
        }
    }
}
