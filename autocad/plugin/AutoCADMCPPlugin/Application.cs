using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;

// Don't `using Autodesk.AutoCAD.Runtime;` here — it pulls in another `Exception`
// type that collides with System.Exception in catch blocks. Use full attribute
// path instead.
[assembly: Autodesk.AutoCAD.Runtime.ExtensionApplication(typeof(AutoCADMCP.Plugin.AcadMCPApp))]

namespace AutoCADMCP.Plugin
{
    /// <summary>
    /// AutoCAD MCP plugin entry point. Loads on NETLOAD or via the autoloader
    /// bundle. Starts the WebSocket server when AutoCAD finishes initializing.
    /// Class is intentionally NOT named "Application" to avoid shadowing
    /// Autodesk.AutoCAD.ApplicationServices.Application.
    /// </summary>
    public class AcadMCPApp : Autodesk.AutoCAD.Runtime.IExtensionApplication
    {
        private const int DefaultPort = 8182;
        private static AcadWebSocketServer _server;
        private static Timer _serverRetryTimer;
        private static int _shutdownRequested;
        private static readonly object _serverLifecycleLock = new object();

        public void Initialize()
        {
            try
            {
                Interlocked.Exchange(ref _shutdownRequested, 0);
                Debug.WriteLine("[AutoCADMCP] AcadMCPApp.Initialize() called.");

                var port = ResolveServerPort();
                _server = new AcadWebSocketServer(port);
                if (_server.Start())
                {
                    WriteToEditor(
                        $"[AutoCADMCP] WebSocket server listening on :{port}");
                }
                else
                {
                    WriteToEditor(
                        $"[AutoCADMCP] Could not bind :{port}; " +
                        "retrying every 2 seconds.");
                    _serverRetryTimer = new Timer(
                        _ =>
                        {
                            try
                            {
                                lock (_serverLifecycleLock)
                                {
                                    if (Volatile.Read(
                                            ref _shutdownRequested) != 0)
                                    {
                                        return;
                                    }

                                    if (_server?.Start() == true)
                                    {
                                        Debug.WriteLine(
                                            $"[AutoCADMCP] WebSocket retry " +
                                            $"succeeded on :{port}.");
                                        Interlocked.Exchange(
                                            ref _serverRetryTimer,
                                            null)?.Dispose();
                                    }
                                }
                            }
                            catch (Exception retryError)
                            {
                                Debug.WriteLine(
                                    $"[AutoCADMCP] WebSocket retry failed: {retryError.Message}");
                            }
                        },
                        null,
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoCADMCP] Initialize failed: {ex.Message}");
                WriteToEditor($"[AutoCADMCP] Initialize failed: {ex.Message}");
            }
        }

        private static int ResolveServerPort()
        {
            var configured = Environment.GetEnvironmentVariable(
                "AUTOCAD_MCP_PORT");
            if (string.IsNullOrWhiteSpace(configured))
                return DefaultPort;

            if (!int.TryParse(
                    configured.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port) ||
                port < 1 ||
                port > 65535)
            {
                throw new InvalidOperationException(
                    "AUTOCAD_MCP_PORT must be an integer from 1 to 65535.");
            }

            return port;
        }

        public void Terminate()
        {
            try
            {
                Interlocked.Exchange(ref _shutdownRequested, 1);
                Interlocked.Exchange(ref _serverRetryTimer, null)?.Dispose();
                lock (_serverLifecycleLock)
                {
                    Interlocked.Exchange(ref _server, null)?.Stop();
                }
                Debug.WriteLine("[AutoCADMCP] AcadMCPApp.Terminate() called.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoCADMCP] Terminate failed: {ex.Message}");
            }
        }

        // Diagnostic message → AutoCAD command-line editor.
        internal static void WriteToEditor(string message)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor?.WriteMessage($"\n{message}\n");
            }
            catch
            {
                // Editor may not be available during early init — silently swallow.
            }
        }
    }
}
