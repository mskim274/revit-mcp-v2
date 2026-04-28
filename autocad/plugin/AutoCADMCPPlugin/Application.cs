using System;
using System.Diagnostics;
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
        private static AcadWebSocketServer _server;

        public void Initialize()
        {
            try
            {
                Debug.WriteLine("[AutoCADMCP] AcadMCPApp.Initialize() called.");

                _server = new AcadWebSocketServer(port: 8182);
                _server.Start();

                WriteToEditor("[AutoCADMCP] WebSocket server listening on :8182");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoCADMCP] Initialize failed: {ex.Message}");
                WriteToEditor($"[AutoCADMCP] Initialize failed: {ex.Message}");
            }
        }

        public void Terminate()
        {
            try
            {
                _server?.Stop();
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
