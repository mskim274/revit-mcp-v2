using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.Plugin
{
    /// <summary>
    /// WebSocket server running inside AutoCAD. Receives commands from the MCP
    /// TS server and dispatches to ICadCommand implementations. All AutoCAD
    /// API calls are marshalled onto the document's main thread via
    /// Application.DocumentManager.ExecuteInCommandContextAsync — the
    /// AutoCAD-native equivalent of Revit.Async.
    ///
    /// Phase 4 MVP: no idempotency cache, no overflow protection. Those will
    /// be ported from RevitWebSocketServer in Phase 5 once the basic loop
    /// is proven to work end-to-end.
    /// </summary>
    public class AcadWebSocketServer
    {
        private readonly int _port;
        private readonly CommandDispatcher _dispatcher;
        private HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private Task _listenTask;

        public AcadWebSocketServer(int port = 8182)
        {
            _port = port;
            _dispatcher = new CommandDispatcher();
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{_port}/");

            try
            {
                _httpListener.Start();
                _listenTask = Task.Run(() => ListenLoop(_cts.Token));
                Debug.WriteLine($"[AutoCADMCP] WebSocket server started on port {_port}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoCADMCP] Failed to start WebSocket server: {ex.Message}");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _httpListener?.Stop();
            try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _httpListener?.Close();
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext httpContext;
                try
                {
                    httpContext = await _httpListener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                // Health probe — same shape as Revit MCP for tooling parity.
                if (!httpContext.Request.IsWebSocketRequest)
                {
                    var body = Encoding.UTF8.GetBytes(
                        "{\"status\":\"ok\",\"server\":\"autocad-mcp-plugin\"}");
                    httpContext.Response.ContentType = "application/json";
                    httpContext.Response.OutputStream.Write(body, 0, body.Length);
                    httpContext.Response.Close();
                    continue;
                }

                // Each connection runs on its own task — naive for now, fine
                // because we only expect one MCP server client at a time.
                _ = Task.Run(() => HandleWebSocket(httpContext, ct));
            }
        }

        private async Task HandleWebSocket(HttpListenerContext ctx, CancellationToken ct)
        {
            HttpListenerWebSocketContext wsContext;
            try
            {
                wsContext = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoCADMCP] WS accept failed: {ex.Message}");
                return;
            }

            var ws = wsContext.WebSocket;
            var buf = new byte[64 * 1024];

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var msg = await ReadFullMessage(ws, buf, ct).ConfigureAwait(false);
                    if (msg == null) break;

                    var responseJson = await DispatchSafely(msg).ConfigureAwait(false);
                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    await ws.SendAsync(
                        new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text,
                        endOfMessage: true, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoCADMCP] WS handler error: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
                }
                catch { /* ignore */ }
            }
        }

        private static async Task<string> ReadFullMessage(
            WebSocket ws, byte[] buf, CancellationToken ct)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
            } while (!result.EndOfMessage);
            return sb.ToString();
        }

        // ─── Dispatch ──────────────────────────────────────────────────

        /// <summary>Catches everything — protocol always returns a JSON envelope.</summary>
        private async Task<string> DispatchSafely(string requestJson)
        {
            string id = "";
            try
            {
                using var doc = JsonDocument.Parse(requestJson);
                var root = doc.RootElement;

                id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var commandName = root.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
                var paramsEl = root.TryGetProperty("params", out var pEl) ? (JsonElement?)pEl : null;

                if (string.IsNullOrWhiteSpace(commandName))
                    return ErrorEnvelope(id, "VALIDATION_ERROR", "Missing 'command' field.", recoverable: false);

                if (!_dispatcher.HasCommand(commandName))
                {
                    return ErrorEnvelope(id, "VALIDATION_ERROR",
                        $"Unknown command: '{commandName}'",
                        recoverable: true,
                        suggestion: $"Available commands: {string.Join(", ", _dispatcher.GetCommandNames())}");
                }

                var parameters = ConvertJsonElement(paramsEl) as Dictionary<string, object>
                                 ?? new Dictionary<string, object>();

                var command = _dispatcher.GetCommand(commandName);

                // Marshal to AutoCAD's main thread. ExecuteInCommandContextAsync
                // is the official, built-in equivalent of Revit.Async.
                CommandResult result = null;
                Exception captured = null;
                var acadDocs = Application.DocumentManager;
                var activeDoc = acadDocs.MdiActiveDocument;

                if (activeDoc == null)
                {
                    return ErrorEnvelope(id, "REVIT_API_ERROR",
                        "No active drawing. Open a drawing in AutoCAD first.",
                        recoverable: true);
                }

                await acadDocs.ExecuteInCommandContextAsync(async (object _) =>
                {
                    using var tr = activeDoc.Database.TransactionManager.StartTransaction();
                    try
                    {
                        result = await command.ExecuteAsync(
                            activeDoc.Database, tr, parameters, CancellationToken.None);
                        tr.Commit();
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                        try { tr.Abort(); } catch { /* ignore */ }
                    }
                }, null);

                if (captured != null)
                {
                    return ErrorEnvelope(id, "REVIT_API_ERROR",
                        captured.Message, recoverable: true,
                        suggestion: "AutoCAD API call failed; see plugin logs.");
                }

                if (result == null)
                {
                    return ErrorEnvelope(id, "INTERNAL_ERROR",
                        "Command produced no result.", recoverable: false);
                }

                if (!result.Success)
                {
                    return ErrorEnvelope(id, "REVIT_API_ERROR",
                        result.ErrorMessage ?? "Unknown error",
                        recoverable: true, suggestion: result.Suggestion);
                }

                return SuccessEnvelope(id, result.Data);
            }
            catch (JsonException ex)
            {
                return ErrorEnvelope(id, "VALIDATION_ERROR",
                    $"Invalid JSON: {ex.Message}", recoverable: false);
            }
            catch (Exception ex)
            {
                return ErrorEnvelope(id, "INTERNAL_ERROR",
                    $"{ex.GetType().Name}: {ex.Message}", recoverable: false);
            }
        }

        // ─── JSON helpers ──────────────────────────────────────────────

        private static string SuccessEnvelope(string id, object data)
        {
            var env = new Dictionary<string, object>
            {
                ["id"] = id,
                ["status"] = "success",
                ["data"] = data,
            };
            return JsonSerializer.Serialize(env);
        }

        private static string ErrorEnvelope(
            string id, string code, string message, bool recoverable, string suggestion = null)
        {
            var error = new Dictionary<string, object>
            {
                ["code"] = code,
                ["message"] = message,
                ["recoverable"] = recoverable,
            };
            if (!string.IsNullOrEmpty(suggestion)) error["suggestion"] = suggestion;

            var env = new Dictionary<string, object>
            {
                ["id"] = id,
                ["status"] = "error",
                ["error"] = error,
            };
            return JsonSerializer.Serialize(env);
        }

        /// <summary>
        /// Recursively convert JsonElement → native CLR types so commands
        /// receive Dictionary&lt;string, object&gt; instead of JsonElement.
        /// Mirrors RevitWebSocketServer.ConvertJsonElements.
        /// </summary>
        private static object ConvertJsonElement(JsonElement? elOrNull)
        {
            if (elOrNull == null) return null;
            var el = elOrNull.Value;
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in el.EnumerateObject())
                        dict[prop.Name] = ConvertJsonElement(prop.Value);
                    return dict;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in el.EnumerateArray())
                        list.Add(ConvertJsonElement(item));
                    return list;
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out var l)) return l;
                    return el.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null;
                default: return null;
            }
        }
    }
}
