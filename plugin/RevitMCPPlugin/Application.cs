using System;
using System.Collections.Generic;
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
        private const int AutoPortMin = 8183;
        private const int AutoPortMax = 8199;
        private static readonly TimeSpan RegistryRetryInterval =
            TimeSpan.FromSeconds(10);
        private int _serverPort = DefaultPort;
        private bool _serverPortExplicit;
        private int _serverStartPending;
        private int _shutdownRequested;
        private DateTime _nextServerRetryUtc = DateTime.MinValue;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly object _registryLifecycleLock = new object();
        private RevitInstanceRegistry _instanceRegistry;
        private DateTime _nextRegistryRetryUtc = DateTime.MinValue;
        private string _revitVersion = "";
        private string _revitBuild = "";

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

                // Cache Revit-owned values while OnStartup is on Revit's UI
                // thread.  Registry retries use only these plain strings and
                // never touch ControlledApplication from another thread.
                _revitVersion =
                    application.ControlledApplication.VersionNumber ?? "";
                try
                {
                    _revitBuild =
                        application.ControlledApplication.VersionBuild ?? "";
                }
                catch
                {
                    _revitBuild = "";
                }

                // An explicitly configured port is a stable operator contract:
                // never silently route that process to another port.  With no
                // override, scan a bounded range so several Revit processes can
                // coexist.  8182 is reserved for the AutoCAD MCP bridge.
                var port = DefaultPort;
                var portEnv = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
                _serverPortExplicit = !string.IsNullOrWhiteSpace(portEnv);
                if (_serverPortExplicit)
                {
                    if (!int.TryParse(portEnv, out var parsed) ||
                        parsed < 1 ||
                        parsed > 65535)
                    {
                        throw new InvalidOperationException(
                            "REVIT_MCP_PORT must be an integer from 1 through 65535.");
                    }
                    port = parsed;
                }
                _serverPort = port;

                TryInitializeInstanceRegistry();

                // Start WebSocket server when ANY document becomes active.
                // DocumentOpened fires for existing .rvt files.
                // DocumentCreated fires for new/empty projects — previously
                // unhandled, which left the WebSocket dormant when the user
                // launched Revit with a blank document.
                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentCreated += OnDocumentCreated;
                application.ControlledApplication.DocumentClosing += OnDocumentClosing;
                application.Idling += OnIdlingEnsureServer;
                application.Idling += OnIdlingUpdateRegistrySnapshot;

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
                        NormalizeRevitYear(_revitVersion));
                    _updateCheckTask = _updateChecker.CheckAsync();
                    application.Idling += OnIdlingShowUpdateDialog;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP.Update] Failed to schedule update check: {ex.Message}");
                }

                var portDescription = _serverPortExplicit
                    ? port.ToString()
                    : $"{DefaultPort}, with automatic fallback to " +
                      $"{AutoPortMin}-{AutoPortMax}";
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] Plugin loaded. WebSocket will start on port " +
                    $"{portDescription} when a document opens.");

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
            RevitInstanceRegistry registryToDispose;
            lock (_registryLifecycleLock)
            {
                registryToDispose = _instanceRegistry;
                _instanceRegistry = null;
            }
            try
            {
                registryToDispose?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] Instance registry shutdown failed: {ex.Message}");
            }
            _updateChecker?.Dispose();
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentCreated -= OnDocumentCreated;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
            application.Idling -= OnIdlingEnsureServer;
            application.Idling -= OnIdlingUpdateRegistrySnapshot;
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

                    foreach (var candidatePort in GetCandidatePorts())
                    {
                        var candidate = new RevitWebSocketServer(
                            uiApp,
                            candidatePort,
                            _sessionId);
                        if (candidate.Start())
                        {
                            _wsServer = candidate;
                            _serverPort = candidatePort;
                            TryInitializeInstanceRegistry();
                            TryUpdateRegistrySnapshot(uiApp);
                            return;
                        }

                        candidate.Stop();
                    }

                    _nextServerRetryUtc = DateTime.UtcNow.AddSeconds(2);
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

        private IEnumerable<int> GetCandidatePorts()
        {
            if (_serverPortExplicit)
            {
                yield return _serverPort;
                yield break;
            }

            yield return DefaultPort;
            for (var port = AutoPortMin; port <= AutoPortMax; port++)
                yield return port;
        }

        private void OnIdlingUpdateRegistrySnapshot(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication uiApplication))
                return;

            if (Volatile.Read(ref _shutdownRequested) != 0)
            {
                uiApplication.Idling -= OnIdlingUpdateRegistrySnapshot;
                return;
            }

            if (_wsServer != null)
            {
                TryInitializeInstanceRegistry();
                TryUpdateRegistrySnapshot(uiApplication);
            }
        }

        /// <summary>
        /// Best-effort registry construction with bounded retry.  This method
        /// uses cached strings and filesystem APIs only; it never reads Revit
        /// API state.  Calls currently originate on Revit's UI thread, while
        /// the lock also protects against shutdown/lifecycle races.
        /// </summary>
        private bool TryInitializeInstanceRegistry()
        {
            lock (_registryLifecycleLock)
            {
                if (Volatile.Read(ref _shutdownRequested) != 0)
                    return false;

                if (_instanceRegistry != null)
                    return true;

                var now = DateTime.UtcNow;
                if (now < _nextRegistryRetryUtc)
                    return false;

                try
                {
                    _instanceRegistry = new RevitInstanceRegistry(
                        _sessionId,
                        _revitVersion,
                        _revitBuild);
                    _nextRegistryRetryUtc = DateTime.MaxValue;
                    System.Diagnostics.Debug.WriteLine(
                        "[RevitMCP] Instance registry initialized.");
                    return true;
                }
                catch (Exception ex)
                {
                    _instanceRegistry = null;
                    _nextRegistryRetryUtc =
                        now.Add(RegistryRetryInterval);
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] Instance registry initialization failed; " +
                        $"retrying after {RegistryRetryInterval.TotalSeconds:0}s: " +
                        $"{ex.Message}");
                    return false;
                }
            }
        }

        private void TryUpdateRegistrySnapshot(UIApplication uiApplication)
        {
            try
            {
                // Revit API reads remain on this UI-thread event.  The registry
                // service copies only strings/scalars, then its timer performs
                // the 2-second heartbeat file writes off the UI thread.
                RevitInstanceRegistry registry;
                lock (_registryLifecycleLock)
                    registry = _instanceRegistry;
                registry?.UpdateSnapshot(uiApplication, _serverPort);
            }
            catch (Exception ex)
            {
                // Discovery failure must not break Revit or command execution.
                // Keep retrying on later idle ticks so transient API reads recover.
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] Instance snapshot update failed: {ex.Message}");
            }
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
