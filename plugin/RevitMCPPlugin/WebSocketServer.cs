using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Revit.Async;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.Plugin
{
    /// <summary>
    /// WebSocket server running inside Revit.
    /// Receives commands from MCP Server (TypeScript) and dispatches to CommandSet.
    /// All Revit API calls go through Revit.Async for thread safety.
    /// </summary>
    public class RevitWebSocketServer
    {
        private readonly UIApplication _uiApp;
        private readonly CommandDispatcher _dispatcher;
        private readonly int _port;
        private HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private Task _listenTask;

        public RevitWebSocketServer(UIApplication uiApp, int port = 8181)
        {
            _uiApp = uiApp;
            _dispatcher = new CommandDispatcher();
            _port = port;
        }

        /// <summary>
        /// Start the WebSocket server
        /// </summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{_port}/");

            try
            {
                _httpListener.Start();
                _listenTask = Task.Run(() => ListenLoop(_cts.Token));
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] WebSocket server started on port {_port}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] Failed to start WebSocket server: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the WebSocket server
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _httpListener?.Stop();
            _httpListener?.Close();
            System.Diagnostics.Debug.WriteLine("[RevitMCP] WebSocket server stopped");
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();

                    if (context.Request.IsWebSocketRequest)
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        _ = Task.Run(() => HandleConnection(wsContext.WebSocket, ct));
                    }
                    else
                    {
                        // Return simple status for HTTP requests
                        context.Response.StatusCode = 200;
                        var responseBytes = Encoding.UTF8.GetBytes(
                            "{\"status\":\"ok\",\"server\":\"revit-mcp-plugin\"}");
                        await context.Response.OutputStream.WriteAsync(
                            responseBytes, 0, responseBytes.Length, ct);
                        context.Response.Close();
                    }
                }
                catch (ObjectDisposedException)
                {
                    break; // Server shutting down
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] Listen error: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task HandleConnection(WebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024]; // 64KB buffer
            System.Diagnostics.Debug.WriteLine("[RevitMCP] Client connected");

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, "Closing", ct);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        var response = await HandleMessage(message);
                        var responseBytes = Encoding.UTF8.GetBytes(response);

                        await ws.SendAsync(
                            new ArraySegment<byte>(responseBytes),
                            WebSocketMessageType.Text,
                            true,
                            ct);
                    }
                }
            }
            catch (WebSocketException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitMCP] WebSocket error: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            finally
            {
                System.Diagnostics.Debug.WriteLine("[RevitMCP] Client disconnected");
            }
        }

        private async Task<string> HandleMessage(string message)
        {
            CommandRequest request;
            try
            {
                request = JsonSerializer.Deserialize<CommandRequest>(message,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    id = "",
                    status = "error",
                    error = new
                    {
                        code = "VALIDATION_ERROR",
                        message = $"Invalid request format: {ex.Message}",
                        recoverable = true
                    }
                });
            }

            try
            {
                if (!_dispatcher.HasCommand(request.Command))
                {
                    return JsonSerializer.Serialize(new
                    {
                        id = request.Id,
                        status = "error",
                        error = new
                        {
                            code = "VALIDATION_ERROR",
                            message = $"Unknown command: '{request.Command}'",
                            recoverable = true,
                            suggestion = $"Available commands: {string.Join(", ", _dispatcher.GetCommandNames())}"
                        }
                    });
                }

                // Create cancellation token with timeout
                using var timeoutCts = new CancellationTokenSource(request.TimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutCts.Token, _cts.Token);

                // Execute on Revit main thread via Revit.Async
                var result = await RevitTask.RunAsync(() =>
                {
                    var doc = _uiApp.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        return Task.FromResult(CommandResult.Fail(
                            "No active Revit document",
                            "Open a Revit project file first."));
                    }

                    var command = _dispatcher.GetCommand(request.Command);
                    var nativeParams = ConvertJsonElements(request.Params);
                    var cmdResult = command.ExecuteAsync(doc, nativeParams, linkedCts.Token).Result;

                    // Post-process UI actions that need UIDocument
                    if (cmdResult.Success && cmdResult.Data is Dictionary<string, object> data)
                    {
                        if (data.TryGetValue("action", out var action))
                        {
                            var actionStr = action?.ToString();

                            if (actionStr == "activate_view" && data.TryGetValue("view_id", out var vidObj))
                            {
                                var viewId = Convert.ToInt32(vidObj);
                                var view = doc.GetElement(new ElementId(viewId)) as Autodesk.Revit.DB.View;
                                if (view != null)
                                {
                                    _uiApp.ActiveUIDocument.ActiveView = view;
                                    data["activated"] = true;
                                }
                            }
                            else if (actionStr == "select_elements" && data.TryGetValue("element_ids", out var eidObj))
                            {
                                if (eidObj is List<int> idList)
                                {
                                    var elementIds = new List<ElementId>();
                                    foreach (var id in idList)
                                        elementIds.Add(new ElementId(id));
                                    _uiApp.ActiveUIDocument.Selection.SetElementIds(elementIds);
                                    data["selected"] = true;
                                }
                            }
                            else if (actionStr == "isolate_in_view" && data.TryGetValue("element_ids", out var isoIdsObj))
                            {
                                if (isoIdsObj is List<int> isoIdList)
                                {
                                    var elementIds = new List<ElementId>();
                                    foreach (var id in isoIdList)
                                        elementIds.Add(new ElementId(id));

                                    // Select elements first, then use temporary isolate
                                    _uiApp.ActiveUIDocument.Selection.SetElementIds(elementIds);

                                    // Use the active view to temporarily isolate
                                    var activeView = _uiApp.ActiveUIDocument.ActiveView;
                                    using (var tx = new Transaction(doc, "MCP: Isolate elements"))
                                    {
                                        tx.Start();
                                        activeView.IsolateElementsTemporary(elementIds);
                                        tx.Commit();
                                    }
                                    data["isolated"] = true;
                                }
                            }
                        }
                    }

                    return Task.FromResult(cmdResult);
                });

                if (result.Success)
                {
                    return JsonSerializer.Serialize(new
                    {
                        id = request.Id,
                        status = "success",
                        data = result.Data
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        id = request.Id,
                        status = "error",
                        error = new
                        {
                            code = "REVIT_API_ERROR",
                            message = result.ErrorMessage,
                            recoverable = true,
                            suggestion = result.Suggestion ?? ""
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                return JsonSerializer.Serialize(new
                {
                    id = request.Id,
                    status = "error",
                    error = new
                    {
                        code = "TIMEOUT_ERROR",
                        message = $"Command '{request.Command}' timed out after {request.TimeoutMs}ms",
                        recoverable = true,
                        suggestion = "Try reducing the scope with 'limit' parameter."
                    }
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    id = request.Id,
                    status = "error",
                    error = new
                    {
                        code = "INTERNAL_ERROR",
                        message = ex.Message,
                        recoverable = false,
                        suggestion = _dispatcher.GetSuggestion(request.Command, ex)
                    }
                });
            }
        }

        /// <summary>
        /// Convert JsonElement values in a dictionary to native .NET types.
        /// System.Text.Json deserializes Dictionary&lt;string, object&gt; values as JsonElement,
        /// which breaks Convert.ChangeType() in command handlers.
        /// </summary>
        private static Dictionary<string, object> ConvertJsonElements(Dictionary<string, object> dict)
        {
            if (dict == null) return new Dictionary<string, object>();

            var result = new Dictionary<string, object>(dict.Count);
            foreach (var kvp in dict)
            {
                result[kvp.Key] = ConvertJsonElement(kvp.Value);
            }
            return result;
        }

        private static object ConvertJsonElement(object value)
        {
            if (value is JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case JsonValueKind.String:
                        return je.GetString();
                    case JsonValueKind.Number:
                        if (je.TryGetInt32(out var intVal)) return intVal;
                        if (je.TryGetInt64(out var longVal)) return longVal;
                        return je.GetDouble();
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        return null;
                    case JsonValueKind.Array:
                        var list = new List<object>();
                        foreach (var item in je.EnumerateArray())
                        {
                            list.Add(ConvertJsonElement(item));
                        }
                        return list;
                    case JsonValueKind.Object:
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in je.EnumerateObject())
                        {
                            dict[prop.Name] = ConvertJsonElement(prop.Value);
                        }
                        return dict;
                    default:
                        return je.ToString();
                }
            }
            return value;
        }

        /// <summary>
        /// Internal request model matching the WebSocket protocol
        /// </summary>
        private class CommandRequest
        {
            public string Id { get; set; } = "";
            public string Command { get; set; } = "";
            public Dictionary<string, object> Params { get; set; } = new();
            public int TimeoutMs { get; set; } = 30000;
        }
    }
}
