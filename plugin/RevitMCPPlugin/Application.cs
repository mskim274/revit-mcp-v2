using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI.Events;
using Revit.Async;
using RevitMCP.Plugin.Services;
using RevitMCP.Plugin.UI;

namespace RevitMCP.Plugin
{
    /// <summary>
    /// Revit Add-in entry point.
    /// Initializes Revit.Async for thread-safe API access
    /// and starts the WebSocket server for MCP communication.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Application : IExternalApplication
    {
        // GitHub repo used for auto-update checks (P0).
        private const string UpdateRepoOwner = "mskim274";
        private const string UpdateRepoName  = "revit-mcp-v2";

        private RevitWebSocketServer _wsServer;
        private static readonly int DefaultPort = 8181;
        private int _serverPort = DefaultPort;
        private int _serverStartPending;
        private int _shutdownRequested;
        private DateTime _nextServerRetryUtc = DateTime.MinValue;

        // One-shot update check state. We run the network call in OnStartup
        // (fire-and-forget) and render the dialog on the first Idling tick,
        // when Revit's UI thread is safely available.
        private UpdateChecker _updateChecker;
        private Task<bool> _updateCheckTask;
        private bool _updateDialogShown;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                Interlocked.Exchange(ref _shutdownRequested, 0);

                // Initialize Revit.Async — MUST be called in OnStartup
                RevitTask.Initialize(application);

                // Read port from environment variable (allows multi-instance)
                var port = DefaultPort;
                var portEnv = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
                if (!string.IsNullOrEmpty(portEnv) &&
                    int.TryParse(portEnv, out var parsed) &&
                    parsed >= 1 &&
                    parsed <= 65535)
                {
                    port = parsed;
                }
                _serverPort = port;

                // Start WebSocket server when ANY document becomes active.
                // DocumentOpened fires for existing .rvt files.
                // DocumentCreated fires for new/empty projects — previously
                // unhandled, which left the WebSocket dormant when the user
                // launched Revit with a blank document.
                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentCreated += OnDocumentCreated;
                application.ControlledApplication.DocumentClosing += OnDocumentClosing;
                application.Idling += OnIdlingEnsureServer;

                // Harness Engineering — Tier 1: self-update check.
                // Runs in a background task; completion is polled from the
                // UI thread on the first Idling event to show a dialog
                // only after Revit is fully ready. Any failure here is
                // non-fatal and must never block plugin startup.
                try
                {
                    _updateChecker = new UpdateChecker(
                        UpdateRepoOwner,
                        UpdateRepoName,
                        GetCurrentPluginVersion(),
                        NormalizeRevitYear(
                            application.ControlledApplication.VersionNumber));
                    _updateCheckTask = _updateChecker.CheckAsync();
                    application.Idling += OnIdlingShowUpdateDialog;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP.Update] Failed to schedule update check: {ex.Message}");
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] Plugin loaded. WebSocket will start on port {port} when a document opens.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] OnStartup failed: {ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            Interlocked.Exchange(ref _shutdownRequested, 1);
            _wsServer?.Stop();
            _wsServer = null;
            _updateChecker?.Dispose();
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentCreated -= OnDocumentCreated;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
            application.Idling -= OnIdlingEnsureServer;
            application.Idling -= OnIdlingShowUpdateDialog;

            System.Diagnostics.Debug.WriteLine("[RevitMCP] Plugin shut down.");
            return Result.Succeeded;
        }

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            StartWebSocketServerIfNeeded();
        }

        private void OnDocumentCreated(object sender, Autodesk.Revit.DB.Events.DocumentCreatedEventArgs e)
        {
            StartWebSocketServerIfNeeded();
        }

        /// <summary>
        /// Idempotent WebSocket server bootstrap. Both DocumentOpened and
        /// DocumentCreated funnel here so the server starts regardless of
        /// whether the user opened an existing .rvt or created a new one.
        /// </summary>
        private void StartWebSocketServerIfNeeded()
        {
            if (Volatile.Read(ref _shutdownRequested) != 0) return;
            if (_wsServer != null) return;
            if (Interlocked.Exchange(ref _serverStartPending, 1) != 0) return;

            try
            {
                // Get UIApplication through Revit.Async
                var startTask = RevitTask.RunAsync((uiApp) =>
                {
                    if (Volatile.Read(ref _shutdownRequested) != 0) return;
                    if (_wsServer != null) return;

                    var candidate = new RevitWebSocketServer(uiApp, _serverPort);
                    if (candidate.Start())
                    {
                        _wsServer = candidate;
                    }
                    else
                    {
                        candidate.Stop();
                        _nextServerRetryUtc = DateTime.UtcNow.AddSeconds(2);
                    }
                });

                _ = startTask.ContinueWith(
                    completed =>
                    {
                        Interlocked.Exchange(ref _serverStartPending, 0);
                        if (completed.IsFaulted)
                        {
                            _nextServerRetryUtc = DateTime.UtcNow.AddSeconds(2);
                            System.Diagnostics.Debug.WriteLine(
                                $"[RevitMCP] Failed to start server: " +
                                $"{completed.Exception?.GetBaseException().Message}");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _serverStartPending, 0);
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] Failed to start server: {ex.Message}");
            }
        }

        private void OnIdlingEnsureServer(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication uiApplication))
                return;

            if (Volatile.Read(ref _shutdownRequested) != 0)
            {
                uiApplication.Idling -= OnIdlingEnsureServer;
                return;
            }

            if (_wsServer != null)
            {
                uiApplication.Idling -= OnIdlingEnsureServer;
                return;
            }

            if (uiApplication.ActiveUIDocument == null ||
                DateTime.UtcNow < _nextServerRetryUtc)
            {
                return;
            }

            StartWebSocketServerIfNeeded();
        }

        private void OnDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            // Only stop if this is the last document
            // (Revit may have multiple documents open)
        }

        /// <summary>
        /// Fires repeatedly while Revit is idle. We use the first tick as
        /// a safe hand-off point to show the update notification on the
        /// main UI thread, then unsubscribe to avoid repeat prompts.
        /// </summary>
        private void OnIdlingShowUpdateDialog(object sender, IdlingEventArgs e)
        {
            // Only act once per Revit session
            if (_updateDialogShown) return;

            // Wait for the background GitHub check to finish. If it's
            // still pending, let the next Idling tick try again.
            if (_updateCheckTask == null || !_updateCheckTask.IsCompleted)
                return;

            _updateDialogShown = true;
            if (sender is UIApplication uiApp)
            {
                // Unsubscribe immediately to stop further Idling events.
                uiApp.Idling -= OnIdlingShowUpdateDialog;
            }

            try
            {
                var hasUpdate = _updateCheckTask.Result;
                if (!hasUpdate) return;

                var window = new UpdateNotificationWindow(_updateChecker);
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP.Update] Failed to show dialog (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve the currently running plugin version from assembly metadata.
        ///
        /// Prefers FileVersion (e.g., 0.2.0.0 injected by MinVer from the git tag)
        /// over AssemblyVersion. Rationale: MinVer pins AssemblyVersion to
        /// major.0.0.0 for binding-redirect compatibility, which would make
        /// every 0.x release appear as 0.0.0 here. FileVersion retains the
        /// actual release number and is what users expect to see.
        ///
        /// Falls back to 0.0.0 if unreadable — update check still runs and
        /// any valid GitHub release will be reported as newer.
        /// </summary>
        private static Version GetCurrentPluginVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                if (!string.IsNullOrWhiteSpace(fvi.FileVersion)
                    && Version.TryParse(fvi.FileVersion, out var fileVer))
                {
                    return fileVer;
                }
                return asm.GetName().Version ?? new Version(0, 0, 0);
            }
            catch
            {
                return new Version(0, 0, 0);
            }
        }

        private static string NormalizeRevitYear(string versionNumber)
        {
            if (!string.IsNullOrWhiteSpace(versionNumber))
            {
                var trimmed = versionNumber.Trim();
                if (trimmed.Length >= 4 &&
                    int.TryParse(trimmed.Substring(0, 4), out var year) &&
                    year >= 2000 &&
                    year <= 9999)
                {
                    return year.ToString();
                }
            }

            throw new InvalidOperationException(
                $"Could not determine the running Revit year from '{versionNumber}'.");
        }
    }
}
