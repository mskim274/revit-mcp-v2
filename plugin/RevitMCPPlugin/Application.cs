using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using Revit.Async;

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
        private RevitWebSocketServer _wsServer;
        private static readonly int DefaultPort = 8181;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Initialize Revit.Async — MUST be called in OnStartup
                RevitTask.Initialize(application);

                // Read port from environment variable (allows multi-instance)
                var port = DefaultPort;
                var portEnv = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
                if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out var parsed))
                {
                    port = parsed;
                }

                // Start WebSocket server when a document is opened
                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentClosing += OnDocumentClosing;

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
            _wsServer?.Stop();
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;

            System.Diagnostics.Debug.WriteLine("[RevitMCP] Plugin shut down.");
            return Result.Succeeded;
        }

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            if (_wsServer != null) return; // Already running

            try
            {
                var port = DefaultPort;
                var portEnv = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
                if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out var parsed))
                {
                    port = parsed;
                }

                // Get UIApplication through Revit.Async
                RevitTask.RunAsync((uiApp) =>
                {
                    _wsServer = new RevitWebSocketServer(uiApp, port);
                    _wsServer.Start();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] Failed to start server: {ex.Message}");
            }
        }

        private void OnDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            // Only stop if this is the last document
            // (Revit may have multiple documents open)
        }
    }
}
