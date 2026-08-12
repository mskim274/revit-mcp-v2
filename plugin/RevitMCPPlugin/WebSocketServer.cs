using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Revit.Async;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.Plugin
{
    /// <summary>
    /// Loopback-only WebSocket server running inside Revit.
    /// All Revit API calls are dispatched through Revit.Async.
    /// </summary>
    public sealed class RevitWebSocketServer
    {
        private const int MaxInboundMessageBytes = 16 * 1024 * 1024;
        private const int MaxTimeoutMs = 10 * 60 * 1000;
        private const int MaxIdempotencyKeyLength = 512;
        private const int MaxIdempotencyEntries = 1000;
        private const int AuthTokenReadAttempts = 20;
        private const int AuthTokenReadDelayMilliseconds = 25;
        private const long JavaScriptMaxSafeInteger = 9_007_199_254_740_991L;
        private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromMinutes(15);
        private static readonly JsonSerializerOptions CommandDataJsonOptions =
            CreateCommandDataJsonOptions();

        private readonly UIApplication _uiApp;
        private readonly CommandDispatcher _dispatcher;
        private readonly int _port;
        private readonly string _sessionId;
        private readonly string _authToken;
        private readonly string _authTokenSource;
        private readonly object _lifecycleLock = new object();
        private readonly object _connectionsLock = new object();
        private readonly object _cacheLock = new object();
        private readonly HashSet<WebSocket> _connections = new HashSet<WebSocket>();
        private readonly HashSet<Task> _connectionTasks = new HashSet<Task>();
        private readonly SemaphoreSlim _sideEffectGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, CachedResult> _idempotencyCache =
            new Dictionary<string, CachedResult>();
        private readonly Dictionary<string, IdempotencyBinding> _idempotencyBindings =
            new Dictionary<string, IdempotencyBinding>();

        private HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private bool _started;

        private sealed class CachedResult
        {
            public string SerializedData { get; set; }
            public DateTime CachedAt { get; set; }
        }

        private sealed class IdempotencyBinding
        {
            public string Command { get; set; }
            public string ParametersHash { get; set; }
            public string CacheKey { get; set; }
            public DateTime CachedAt { get; set; }
        }

        private enum CacheLookupStatus
        {
            Miss,
            Hit,
            Conflict
        }

        private sealed class CacheLookup
        {
            public CacheLookupStatus Status { get; set; }
            public string SerializedData { get; set; }
            public string ConflictMessage { get; set; }
        }

        private sealed class JavaScriptSafeInt64Converter : JsonConverter<long>
        {
            public override long Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Number &&
                    reader.TryGetInt64(out var numericValue))
                {
                    return numericValue;
                }

                if (reader.TokenType == JsonTokenType.String &&
                    long.TryParse(
                        reader.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var stringValue))
                {
                    return stringValue;
                }

                throw new JsonException(
                    "Expected a signed 64-bit integer or decimal string.");
            }

            public override void Write(
                Utf8JsonWriter writer,
                long value,
                JsonSerializerOptions options)
            {
                if (value >= -JavaScriptMaxSafeInteger &&
                    value <= JavaScriptMaxSafeInteger)
                {
                    writer.WriteNumberValue(value);
                }
                else
                {
                    writer.WriteStringValue(
                        value.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        public RevitWebSocketServer(
            UIApplication uiApp,
            int port = 8181,
            string sessionId = null)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
            _dispatcher = new CommandDispatcher();
            _port = port;
            _sessionId = string.IsNullOrWhiteSpace(sessionId)
                ? Guid.NewGuid().ToString("N")
                : sessionId.Trim();
            (_authToken, _authTokenSource) = LoadOrCreateAuthToken();
        }

        public int Port => _port;
        public string SessionId => _sessionId;

        /// <summary>
        /// Starts listening. Returns false when the listener could not be started,
        /// so the caller can retry instead of retaining a half-started instance.
        /// </summary>
        public bool Start()
        {
            lock (_lifecycleLock)
            {
                if (_started)
                    return true;

                var cts = new CancellationTokenSource();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

                try
                {
                    listener.Start();
                    _cts = cts;
                    _httpListener = listener;
                    _started = true;
                    _listenTask = ListenLoop(listener, cts.Token);

                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] WebSocket server started on 127.0.0.1:{_port}; " +
                        $"authentication source: {_authTokenSource}");
                    return true;
                }
                catch (Exception ex)
                {
                    try { listener.Close(); } catch { }
                    cts.Dispose();
                    _cts = null;
                    _httpListener = null;
                    _listenTask = null;
                    _started = false;

                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] Failed to start WebSocket server: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Cancels the listener and aborts active sockets. Tasks are tracked until
        /// they complete; cancellation sources are disposed after the last task
        /// exits so shutdown does not race an in-flight handler.
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            HttpListener listener;
            Task listenTask;

            lock (_lifecycleLock)
            {
                if (!_started && _cts == null)
                    return;

                _started = false;
                cts = _cts;
                listener = _httpListener;
                listenTask = _listenTask;
                _cts = null;
                _httpListener = null;
                _listenTask = null;
            }

            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }

            Task[] connectionTasks;
            WebSocket[] sockets;
            lock (_connectionsLock)
            {
                sockets = _connections.ToArray();
                connectionTasks = _connectionTasks.ToArray();
            }

            foreach (var socket in sockets)
            {
                try { socket.Abort(); } catch { }
                try { socket.Dispose(); } catch { }
            }

            var pending = new List<Task>(connectionTasks);
            if (listenTask != null)
                pending.Add(listenTask);

            if (cts != null)
            {
                if (pending.Count == 0)
                {
                    cts.Dispose();
                }
                else
                {
                    _ = Task.WhenAll(pending).ContinueWith(
                        _ => cts.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            System.Diagnostics.Debug.WriteLine("[RevitMCP] WebSocket server stopped");
        }

        private async Task ListenLoop(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] Listen error: {ex.Message}");
                    try
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                try
                {
                    var origin = context.Request.Headers["Origin"];
                    if (!IsAllowedOrigin(origin))
                    {
                        await RejectHttpRequest(
                            context,
                            403,
                            "Origin is not allowed.",
                            ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!HasValidAuthorization(context.Request.Headers["Authorization"]))
                    {
                        context.Response.Headers["WWW-Authenticate"] = "Bearer";
                        await RejectHttpRequest(
                            context,
                            401,
                            "Missing or invalid bearer token.",
                            ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!context.Request.IsWebSocketRequest)
                    {
                        await WriteHttpJson(
                            context,
                            200,
                            "{\"status\":\"ok\",\"server\":\"revit-mcp-plugin\"}",
                            ct).ConfigureAwait(false);
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null)
                        .ConfigureAwait(false);
                    var socket = wsContext.WebSocket;

                    lock (_connectionsLock)
                        _connections.Add(socket);

                    var task = HandleConnection(socket, ct);
                    TrackConnectionTask(task);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] Connection setup error: {ex.Message}");
                    try { context.Response.Abort(); } catch { }
                }
            }
        }

        private void TrackConnectionTask(Task task)
        {
            lock (_connectionsLock)
                _connectionTasks.Add(task);

            _ = task.ContinueWith(
                completed =>
                {
                    lock (_connectionsLock)
                        _connectionTasks.Remove(completed);

                    if (completed.IsFaulted)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[RevitMCP] Connection task failed: " +
                            $"{completed.Exception?.GetBaseException().Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task HandleConnection(WebSocket ws, CancellationToken serverToken)
        {
            var buffer = new byte[64 * 1024];
            System.Diagnostics.Debug.WriteLine("[RevitMCP] Authenticated client connected");

            try
            {
                while (ws.State == WebSocketState.Open &&
                       !serverToken.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    string message;

                    using (var messageStream = new MemoryStream())
                    {
                        do
                        {
                            result = await ws.ReceiveAsync(
                                new ArraySegment<byte>(buffer),
                                serverToken).ConfigureAwait(false);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                if (ws.State == WebSocketState.Open ||
                                    ws.State == WebSocketState.CloseReceived)
                                {
                                    await ws.CloseAsync(
                                        WebSocketCloseStatus.NormalClosure,
                                        "Closing",
                                        serverToken).ConfigureAwait(false);
                                }
                                return;
                            }

                            if (result.MessageType != WebSocketMessageType.Text)
                            {
                                await ws.CloseAsync(
                                    WebSocketCloseStatus.InvalidMessageType,
                                    "Only text messages are supported.",
                                    serverToken).ConfigureAwait(false);
                                return;
                            }

                            messageStream.Write(buffer, 0, result.Count);
                            if (messageStream.Length > MaxInboundMessageBytes)
                            {
                                await ws.CloseAsync(
                                    WebSocketCloseStatus.MessageTooBig,
                                    "Message exceeds 16MB limit.",
                                    serverToken).ConfigureAwait(false);
                                return;
                            }
                        }
                        while (!result.EndOfMessage);

                        message = Encoding.UTF8.GetString(messageStream.ToArray());
                    }

                    var response = await HandleMessage(message, serverToken)
                        .ConfigureAwait(false);
                    var responseBytes = Encoding.UTF8.GetBytes(response);

                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.SendAsync(
                            new ArraySegment<byte>(responseBytes),
                            WebSocketMessageType.Text,
                            true,
                            serverToken).ConfigureAwait(false);
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
                // Normal shutdown or command cancellation.
            }
            catch (ObjectDisposedException)
            {
                // Normal shutdown race.
            }
            finally
            {
                lock (_connectionsLock)
                    _connections.Remove(ws);

                try { ws.Dispose(); } catch { }
                System.Diagnostics.Debug.WriteLine("[RevitMCP] Client disconnected");
            }
        }

        private async Task<string> HandleMessage(
            string message,
            CancellationToken serverToken)
        {
            CommandRequest request;
            try
            {
                request = JsonSerializer.Deserialize<CommandRequest>(
                    message,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            }
            catch (Exception ex)
            {
                return BuildErrorResponse(
                    "",
                    "VALIDATION_ERROR",
                    $"Invalid request format: {ex.Message}",
                    true,
                    "Send a JSON object with id, command, params, and optional timeout_ms.");
            }

            if (request == null)
            {
                return BuildErrorResponse(
                    "",
                    "VALIDATION_ERROR",
                    "Request body must be a JSON object, not null.",
                    true,
                    "Send a JSON object with id, command, params, and optional timeout_ms.");
            }

            request.Id = request.Id?.Trim() ?? "";
            request.Command = request.Command?.Trim() ?? "";
            request.Params ??= new Dictionary<string, object>();

            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return BuildErrorResponse(
                    "",
                    "VALIDATION_ERROR",
                    "Request id is required.",
                    true,
                    "Generate a unique request id and retry.");
            }

            if (string.IsNullOrWhiteSpace(request.Command))
            {
                return BuildErrorResponse(
                    request.Id,
                    "VALIDATION_ERROR",
                    "Command is required.",
                    true,
                    "Provide a registered command name.");
            }

            if (request.TimeoutMs <= 0 || request.TimeoutMs > MaxTimeoutMs)
            {
                return BuildErrorResponse(
                    request.Id,
                    "VALIDATION_ERROR",
                    $"timeout_ms must be between 1 and {MaxTimeoutMs}.",
                    true,
                    "Use a positive timeout no greater than 10 minutes.");
            }

            if (!TryValidateAndNormalizeTargetGuard(
                    request,
                    out var targetGuardError))
            {
                return BuildErrorResponse(
                    request.Id,
                    "VALIDATION_ERROR",
                    targetGuardError,
                    true,
                    "Refresh the Revit session list and supply both " +
                    "target_session_id and expected_document_fingerprint " +
                    "from the same current session record.");
            }

            if (!_dispatcher.HasCommand(request.Command))
            {
                return BuildErrorResponse(
                    request.Id,
                    "VALIDATION_ERROR",
                    $"Unknown command: '{request.Command}'",
                    true,
                    $"Available commands: {string.Join(", ", _dispatcher.GetCommandNames())}");
            }

            var sideEffect = IsSideEffectRequest(request);
            if (!TryResolveIdempotencyKey(
                    request,
                    out var requestKey,
                    out var idempotencyError))
            {
                return BuildErrorResponse(
                    request.Id,
                    "VALIDATION_ERROR",
                    idempotencyError,
                    true,
                    "Supply a non-empty string of at most 512 characters, " +
                    "or omit idempotency_key to use the request id.");
            }

            var parametersHash = ComputeCanonicalParametersHash(request.Params);
            var gateEntered = false;

            try
            {
                using var timeoutCts = new CancellationTokenSource(request.TimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutCts.Token,
                    serverToken);

                if (sideEffect)
                {
                    await _sideEffectGate.WaitAsync(linkedCts.Token)
                        .ConfigureAwait(false);
                    gateEntered = true;
                }

                return await RevitTask.RunAsync(() =>
                {
                    var uiDocument = _uiApp.ActiveUIDocument;
                    var doc = uiDocument?.Document;
                    var documentFingerprint =
                        Services.SessionIdentity.ComputeDocumentFingerprint(doc);
                    if (request.TargetSessionId != null &&
                        !string.Equals(
                            request.TargetSessionId,
                            _sessionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(BuildErrorResponse(
                            request.Id,
                            "TARGET_SESSION_MISMATCH",
                            $"Request targets Revit session " +
                            $"'{request.TargetSessionId}', but this process is " +
                            $"session '{_sessionId}' on port {_port}.",
                            true,
                            "Refresh the Revit session list and retry using this " +
                            "process's current session_id, or route the command to " +
                            "the intended Revit process."));
                    }

                    if (request.ExpectedDocumentFingerprint != null &&
                        !string.Equals(
                            request.ExpectedDocumentFingerprint,
                            documentFingerprint,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(BuildErrorResponse(
                            request.Id,
                            "TARGET_DOCUMENT_MISMATCH",
                            $"The active document changed before execution. " +
                            $"Expected fingerprint " +
                            $"'{request.ExpectedDocumentFingerprint}', " +
                            $"but '{doc?.Title ?? "(no active document)"}' has fingerprint " +
                            $"'{documentFingerprint}'.",
                            true,
                            "Refresh the Revit session list, explicitly select " +
                            "the intended document in Revit, and retry with its " +
                            "current document_fingerprint."));
                    }

                    if (doc == null)
                    {
                        return Task.FromResult(BuildCommandErrorResponse(
                            request.Id,
                            CommandResult.Fail(
                                "No active Revit document",
                                "Open a Revit project file first.")));
                    }

                    var documentScope = ComputeDocumentScope(doc);
                    if (sideEffect)
                    {
                        var lookup = TryGetCachedResult(
                            documentScope,
                            requestKey,
                            request.Command,
                            parametersHash);

                        if (lookup.Status == CacheLookupStatus.Conflict)
                        {
                            return Task.FromResult(BuildErrorResponse(
                                request.Id,
                                "IDEMPOTENCY_CONFLICT",
                                lookup.ConflictMessage,
                                false,
                                "Generate a new idempotency_key for the changed request."));
                        }

                        if (lookup.Status == CacheLookupStatus.Hit)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[RevitMCP] Idempotency replay: command={request.Command}");
                            return Task.FromResult(BuildSuccessResponse(
                                request.Id,
                                lookup.SerializedData));
                        }
                    }

                    var command = _dispatcher.GetCommand(request.Command);
                    var nativeParams = ConvertJsonElements(request.Params);

                    try
                    {
                        var selected = uiDocument?.Selection?.GetElementIds();
                        SelectionContext.Current = selected != null
                            ? selected.ToArray()
                            : Array.Empty<ElementId>();
                    }
                    catch
                    {
                        SelectionContext.Current = Array.Empty<ElementId>();
                    }

                    CommandResult commandResult;
                    try
                    {
                        commandResult = command.ExecuteAsync(
                                doc,
                                nativeParams,
                                linkedCts.Token)
                            .GetAwaiter()
                            .GetResult();
                    }
                    finally
                    {
                        SelectionContext.Current = Array.Empty<ElementId>();
                    }

                    // Commands may translate OperationCanceledException into
                    // a failed CommandResult. Normalize that to TIMEOUT_ERROR.
                    // If a side-effect command already returned success, keep
                    // processing and cache that result: cancellation may have
                    // arrived just after commit, and reporting a timeout would
                    // invite a duplicate retry.
                    if (linkedCts.IsCancellationRequested &&
                        !(sideEffect && commandResult?.Success == true))
                    {
                        linkedCts.Token.ThrowIfCancellationRequested();
                    }

                    if (commandResult == null)
                    {
                        commandResult = CommandResult.Fail(
                            $"Command '{request.Command}' returned no result.",
                            "Retry once; if this repeats, inspect the command implementation.");
                    }

                    if (!commandResult.Success)
                    {
                        return Task.FromResult(BuildCommandErrorResponse(
                            request.Id,
                            commandResult));
                    }

                    EnrichSessionContext(
                        request.Command,
                        commandResult,
                        doc,
                        documentFingerprint);

                    try
                    {
                        ProcessUiAction(
                            uiDocument,
                            doc,
                            commandResult,
                            linkedCts.Token);
                    }
                    catch (Exception actionError)
                    {
                        if (!HasCommittedMutation(commandResult.Data))
                            throw;

                        // The model mutation already committed. Returning an
                        // uncached error here would make a retry duplicate the
                        // mutation (notably duplicate_views + activate=true).
                        // Preserve/cache the committed payload and expose the
                        // failed UI follow-up as an explicit warning instead.
                        if (commandResult.Data is Dictionary<string, object> actionData)
                        {
                            actionData["ui_action_succeeded"] = false;
                            actionData["ui_action_error"] = actionError.Message;
                            actionData["ui_action_warning"] =
                                "The model mutation committed, but the requested " +
                                "UI follow-up failed. Do not retry with a new " +
                                "idempotency key; apply the UI action separately.";
                        }
                    }
                    var serializedData = JsonSerializer.Serialize(
                        commandResult.Data,
                        CommandDataJsonOptions);

                    if (sideEffect)
                    {
                        StoreCachedResult(
                            documentScope,
                            requestKey,
                            request.Command,
                            parametersHash,
                            serializedData);
                    }

                    return Task.FromResult(BuildSuccessResponse(
                        request.Id,
                        serializedData));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return BuildErrorResponse(
                    request.Id,
                    serverToken.IsCancellationRequested
                        ? "SERVER_SHUTDOWN"
                        : "TIMEOUT_ERROR",
                    serverToken.IsCancellationRequested
                        ? "The Revit MCP server is shutting down."
                        : $"Command '{request.Command}' timed out after {request.TimeoutMs}ms.",
                    true,
                    serverToken.IsCancellationRequested
                        ? "Reconnect after the Revit plugin restarts."
                        : "Reduce the request scope or increase timeout_ms.");
            }
            catch (Exception ex)
            {
                return BuildErrorResponse(
                    request.Id,
                    "INTERNAL_ERROR",
                    ex.Message,
                    false,
                    _dispatcher.GetSuggestion(request.Command, ex));
            }
            finally
            {
                if (gateEntered)
                    _sideEffectGate.Release();
            }
        }

        private void ProcessUiAction(
            UIDocument uiDocument,
            Document doc,
            CommandResult commandResult,
            CancellationToken cancellationToken)
        {
            if (uiDocument == null ||
                !(commandResult.Data is Dictionary<string, object> data) ||
                !data.TryGetValue("action", out var action))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var actionName = action?.ToString();
            if (actionName == "activate_view" &&
                data.TryGetValue("view_id", out var viewIdValue))
            {
                var viewId = ElementIdCompatibility.Create(viewIdValue);
                var view = doc.GetElement(viewId) as Autodesk.Revit.DB.View;
                if (view != null)
                {
                    uiDocument.ActiveView = view;
                    data["activated"] = true;
                    data["ui_action_succeeded"] =
                        uiDocument.ActiveView.Id.GetValue() == view.Id.GetValue();
                    if (!(bool)data["ui_action_succeeded"])
                    {
                        throw new InvalidOperationException(
                            $"View {view.Id.GetValue()} was not activated.");
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"View {viewId.GetValue()} was not found.");
                }
                return;
            }

            if (actionName == "select_elements" &&
                data.TryGetValue("element_ids", out var selectionValue) &&
                TryConvertElementIds(selectionValue, out var selectionIds))
            {
                uiDocument.Selection.SetElementIds(selectionIds);
                data["selected"] = true;
                var actualSelection = uiDocument.Selection.GetElementIds();
                data["ui_action_succeeded"] =
                    actualSelection.Count == selectionIds.Count &&
                    selectionIds.All(id => actualSelection.Contains(id));
                if (!(bool)data["ui_action_succeeded"])
                {
                    throw new InvalidOperationException(
                        "Revit selection did not match the requested element IDs.");
                }
                return;
            }

            if ((actionName == "temporary_hide_isolate" ||
                 actionName == "isolate_in_view") &&
                data.TryGetValue("element_ids", out var isolationValue) &&
                TryConvertElementIds(isolationValue, out var isolationIds))
            {
                var mode = data.TryGetValue("mode", out var modeValue)
                    ? modeValue?.ToString()?.Trim().ToLowerInvariant()
                    : "isolate";
                if (mode != "isolate" && mode != "hide")
                {
                    throw new InvalidOperationException(
                        $"Unsupported temporary visibility mode '{mode}'.");
                }

                var targetView = uiDocument.ActiveView;
                if (data.TryGetValue("view_id", out var targetViewIdValue) &&
                    targetViewIdValue != null)
                {
                    var targetViewId = Convert.ToInt64(targetViewIdValue);
                    targetView = doc.GetElement(
                        ElementIdCompatibility.Create(
                            targetViewId)) as Autodesk.Revit.DB.View;
                    if (targetView == null)
                    {
                        throw new InvalidOperationException(
                            $"Target view {targetViewId} was not found.");
                    }
                }

                if (targetView.IsTemplate)
                {
                    throw new InvalidOperationException(
                        $"Target view {targetView.Id.GetValue()} is a template.");
                }

                var activatedTargetView =
                    uiDocument.ActiveView.Id.GetValue() !=
                    targetView.Id.GetValue();
                if (activatedTargetView)
                    uiDocument.ActiveView = targetView;

                bool mutationCommitted;
                using (var tx = new Transaction(
                           doc,
                           "MCP: Temporary hide/isolate"))
                {
                    tx.Start();
                    if (mode == "hide")
                        targetView.HideElementsTemporary(isolationIds);
                    else
                        targetView.IsolateElementsTemporary(isolationIds);

                    cancellationToken.ThrowIfCancellationRequested();
                    var transactionStatus = tx.Commit();
                    mutationCommitted =
                        transactionStatus == TransactionStatus.Committed;
                    data["mutation_committed"] = mutationCommitted;
                }

                var temporaryModeActive = targetView.IsInTemporaryViewMode(
                    TemporaryViewMode.TemporaryHideIsolate);
                var activeViewMatches =
                    uiDocument.ActiveView.Id.GetValue() ==
                    targetView.Id.GetValue();
                data["isolated"] = mode == "isolate";
                data["hidden"] = mode == "hide";
                data["activated_target_view"] = activatedTargetView;
                data["ui_action_succeeded"] =
                    mutationCommitted &&
                    temporaryModeActive &&
                    activeViewMatches;
                data["verification"] = new Dictionary<string, object>
                {
                    ["performed"] = true,
                    ["target_view_id"] = targetView.Id.GetValue(),
                    ["mode"] = mode,
                    ["requested_element_count"] = isolationIds.Count,
                    ["temporary_hide_isolate_active"] = temporaryModeActive,
                    ["active_view_matches_target"] = activeViewMatches,
                    ["match"] =
                        temporaryModeActive &&
                        activeViewMatches &&
                        mutationCommitted
                };

                if (!mutationCommitted ||
                    !temporaryModeActive ||
                    !activeViewMatches)
                {
                    throw new InvalidOperationException(
                        $"Temporary {mode} did not verify on target view " +
                        $"{targetView.Id.GetValue()}.");
                }
            }
        }

        private void EnrichSessionContext(
            string commandName,
            CommandResult commandResult,
            Document document,
            string documentFingerprint)
        {
            if (!string.Equals(commandName, "ping", StringComparison.Ordinal) &&
                !string.Equals(
                    commandName,
                    "get_project_info",
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!(commandResult?.Data is Dictionary<string, object> data))
                return;

            data["session_id"] = _sessionId;
            data["port"] = _port;
            data["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            data["document_fingerprint"] = documentFingerprint ?? "";
            data["document_title"] = document?.Title ?? "";
            data["document_path"] = document?.PathName ?? "";
        }

        private static bool HasCommittedMutation(object data)
        {
            if (!(data is Dictionary<string, object> dictionary) ||
                !dictionary.TryGetValue(
                    "mutation_committed",
                    out var committedValue) ||
                committedValue == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(committedValue);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertElementIds(
            object value,
            out List<ElementId> elementIds)
        {
            elementIds = new List<ElementId>();
            if (!(value is IEnumerable enumerable) || value is string)
                return false;

            try
            {
                foreach (var item in enumerable)
                    elementIds.Add(ElementIdCompatibility.Create(item));
                return true;
            }
            catch
            {
                elementIds.Clear();
                return false;
            }
        }

        private static Dictionary<string, object> ConvertJsonElements(
            Dictionary<string, object> dict)
        {
            if (dict == null)
                return new Dictionary<string, object>();

            var result = new Dictionary<string, object>(dict.Count);
            foreach (var pair in dict)
                result[pair.Key] = ConvertJsonElement(pair.Value);
            return result;
        }

        private static object ConvertJsonElement(object value)
        {
            if (!(value is JsonElement element))
                return value;

            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out var intValue)) return intValue;
                    if (element.TryGetInt64(out var longValue)) return longValue;
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in element.EnumerateArray())
                        list.Add(ConvertJsonElement(item));
                    return list;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var property in element.EnumerateObject())
                        dict[property.Name] = ConvertJsonElement(property.Value);
                    return dict;
                default:
                    return element.ToString();
            }
        }

        private CacheLookup TryGetCachedResult(
            string documentScope,
            string requestKey,
            string command,
            string parametersHash)
        {
            var bindingKey = BuildBindingKey(documentScope, requestKey);
            lock (_cacheLock)
            {
                var now = DateTime.UtcNow;
                if (!_idempotencyBindings.TryGetValue(bindingKey, out var binding))
                    return new CacheLookup { Status = CacheLookupStatus.Miss };

                if (now - binding.CachedAt > IdempotencyTtl)
                {
                    _idempotencyBindings.Remove(bindingKey);
                    _idempotencyCache.Remove(binding.CacheKey);
                    return new CacheLookup { Status = CacheLookupStatus.Miss };
                }

                if (!string.Equals(binding.Command, command, StringComparison.Ordinal) ||
                    !string.Equals(
                        binding.ParametersHash,
                        parametersHash,
                        StringComparison.Ordinal))
                {
                    return new CacheLookup
                    {
                        Status = CacheLookupStatus.Conflict,
                        ConflictMessage =
                            "The idempotency key was already used for a different " +
                            "command or parameter payload in this document."
                    };
                }

                if (!_idempotencyCache.TryGetValue(binding.CacheKey, out var cached) ||
                    now - cached.CachedAt > IdempotencyTtl)
                {
                    _idempotencyCache.Remove(binding.CacheKey);
                    _idempotencyBindings.Remove(bindingKey);
                    return new CacheLookup { Status = CacheLookupStatus.Miss };
                }

                return new CacheLookup
                {
                    Status = CacheLookupStatus.Hit,
                    SerializedData = cached.SerializedData
                };
            }
        }

        private void StoreCachedResult(
            string documentScope,
            string requestKey,
            string command,
            string parametersHash,
            string serializedData)
        {
            var bindingKey = BuildBindingKey(documentScope, requestKey);
            var cacheKey = string.Join(
                "|",
                command,
                documentScope,
                parametersHash,
                HashText(requestKey));
            var now = DateTime.UtcNow;

            lock (_cacheLock)
            {
                _idempotencyCache[cacheKey] = new CachedResult
                {
                    SerializedData = serializedData,
                    CachedAt = now
                };
                _idempotencyBindings[bindingKey] = new IdempotencyBinding
                {
                    Command = command,
                    ParametersHash = parametersHash,
                    CacheKey = cacheKey,
                    CachedAt = now
                };

                if (_idempotencyBindings.Count % 50 == 0)
                    PruneExpiredCacheEntries(now);

                while (_idempotencyBindings.Count > MaxIdempotencyEntries)
                {
                    var oldest = _idempotencyBindings
                        .OrderBy(pair => pair.Value.CachedAt)
                        .First();
                    _idempotencyBindings.Remove(oldest.Key);
                    _idempotencyCache.Remove(oldest.Value.CacheKey);
                }
            }
        }

        private void PruneExpiredCacheEntries(DateTime now)
        {
            var expiredBindings = _idempotencyBindings
                .Where(pair => now - pair.Value.CachedAt > IdempotencyTtl)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in expiredBindings)
            {
                var cacheKey = _idempotencyBindings[key].CacheKey;
                _idempotencyBindings.Remove(key);
                _idempotencyCache.Remove(cacheKey);
            }
        }

        private static string BuildBindingKey(
            string documentScope,
            string requestKey)
        {
            return documentScope + "|" + HashText(requestKey);
        }

        private static bool TryResolveIdempotencyKey(
            CommandRequest request,
            out string key,
            out string error)
        {
            key = null;
            error = null;
            if (!request.Params.TryGetValue(
                    "idempotency_key",
                    out var value))
            {
                key = request.Id;
                return true;
            }

            string suppliedKey;
            if (value is JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    error =
                        "idempotency_key must be a non-empty JSON string " +
                        "when supplied.";
                    return false;
                }
                suppliedKey = element.GetString();
            }
            else if (value is string text)
            {
                suppliedKey = text;
            }
            else
            {
                error =
                    "idempotency_key must be a non-empty string when supplied.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(suppliedKey))
            {
                error = "idempotency_key must not be empty or whitespace.";
                return false;
            }

            key = suppliedKey.Trim();
            if (key.Length > MaxIdempotencyKeyLength)
            {
                error =
                    $"idempotency_key exceeds " +
                    $"{MaxIdempotencyKeyLength} characters.";
                return false;
            }

            return true;
        }

        private static bool TryValidateAndNormalizeTargetGuard(
            CommandRequest request,
            out string error)
        {
            error = null;
            var sessionWasSupplied = request.TargetSessionId != null;
            var fingerprintWasSupplied =
                request.ExpectedDocumentFingerprint != null;

            if (!sessionWasSupplied && !fingerprintWasSupplied)
                return true;

            if (!sessionWasSupplied || !fingerprintWasSupplied ||
                string.IsNullOrWhiteSpace(request.TargetSessionId) ||
                string.IsNullOrWhiteSpace(
                    request.ExpectedDocumentFingerprint))
            {
                error =
                    "target_session_id and expected_document_fingerprint " +
                    "must be supplied together as non-empty strings.";
                return false;
            }

            request.TargetSessionId = request.TargetSessionId.Trim();
            request.ExpectedDocumentFingerprint =
                request.ExpectedDocumentFingerprint.Trim().ToLowerInvariant();

            if (request.TargetSessionId.Length > 128)
            {
                error = "target_session_id must not exceed 128 characters.";
                return false;
            }

            if (request.ExpectedDocumentFingerprint.Length != 64 ||
                request.ExpectedDocumentFingerprint.Any(
                    character =>
                        !((character >= '0' && character <= '9') ||
                          (character >= 'a' && character <= 'f'))))
            {
                error =
                    "expected_document_fingerprint must be exactly 64 " +
                    "hexadecimal characters.";
                return false;
            }

            return true;
        }

        private static bool IsSideEffectRequest(CommandRequest request)
        {
            var command = request.Command;
            if (string.IsNullOrEmpty(command))
                return false;

            if (string.Equals(
                    command,
                    "execute_script",
                    StringComparison.OrdinalIgnoreCase))
            {
                // "query" mode has no Revit transaction, but this escape
                // hatch is not a sandbox and can still trigger non-model
                // side effects. Serialize and cache every script invocation.
                return true;
            }

            return command.StartsWith("create_", StringComparison.Ordinal)
                || command.StartsWith("modify_", StringComparison.Ordinal)
                || command.StartsWith("delete_", StringComparison.Ordinal)
                || command.StartsWith("move_", StringComparison.Ordinal)
                || command.StartsWith("copy_", StringComparison.Ordinal)
                || command.StartsWith("mirror_", StringComparison.Ordinal)
                || command.StartsWith("rotate_", StringComparison.Ordinal)
                || command.StartsWith("array_", StringComparison.Ordinal)
                || command.StartsWith("rename_", StringComparison.Ordinal)
                || command.StartsWith("duplicate_", StringComparison.Ordinal)
                || command.StartsWith("change_", StringComparison.Ordinal)
                || command.StartsWith("place_", StringComparison.Ordinal)
                || command.StartsWith("load_", StringComparison.Ordinal)
                || command.StartsWith("purge_", StringComparison.Ordinal)
                || command.StartsWith("set_", StringComparison.Ordinal)
                || command.StartsWith("batch_", StringComparison.Ordinal)
                || command.StartsWith("fix_", StringComparison.Ordinal)
                || command.StartsWith("apply_", StringComparison.Ordinal)
                || command.StartsWith("tag_", StringComparison.Ordinal)
                || command.StartsWith("isolate_", StringComparison.Ordinal)
                || command.StartsWith("reset_", StringComparison.Ordinal)
                || command.StartsWith("select_", StringComparison.Ordinal)
                || command.StartsWith("export_", StringComparison.Ordinal);
        }

        private static string ComputeDocumentScope(Document doc)
        {
            var identity = string.Join(
                "\n",
                doc.Title ?? "",
                doc.PathName ?? "",
                RuntimeHelpers.GetHashCode(doc).ToString());
            return HashText(identity);
        }

        private static string ComputeCanonicalParametersHash(
            Dictionary<string, object> parameters)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonicalDictionary(
                    writer,
                    parameters ?? new Dictionary<string, object>(),
                    excludeIdempotencyKey: true);
            }
            return HashBytes(stream.ToArray());
        }

        private static void WriteCanonicalDictionary(
            Utf8JsonWriter writer,
            IDictionary<string, object> dictionary,
            bool excludeIdempotencyKey)
        {
            writer.WriteStartObject();
            foreach (var pair in dictionary.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (excludeIdempotencyKey &&
                    string.Equals(
                        pair.Key,
                        "idempotency_key",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writer.WritePropertyName(pair.Key);
                WriteCanonicalValue(writer, pair.Value);
            }
            writer.WriteEndObject();
        }

        private static void WriteCanonicalJsonElement(
            Utf8JsonWriter writer,
            JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject()
                                 .OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonicalJsonElement(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteCanonicalJsonElement(writer, item);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    break;
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var signed))
                        writer.WriteNumberValue(signed);
                    else if (element.TryGetUInt64(out var unsigned))
                        writer.WriteNumberValue(unsigned);
                    else if (element.TryGetDecimal(out var decimalValue))
                        writer.WriteNumberValue(decimalValue);
                    else
                        writer.WriteNumberValue(element.GetDouble());
                    break;
                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }
        }

        private static void WriteCanonicalValue(Utf8JsonWriter writer, object value)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            if (value is JsonElement element)
            {
                WriteCanonicalJsonElement(writer, element);
                return;
            }

            if (value is IDictionary<string, object> stringDictionary)
            {
                WriteCanonicalDictionary(writer, stringDictionary, false);
                return;
            }

            if (value is IDictionary dictionary)
            {
                writer.WriteStartObject();
                var entries = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add(new KeyValuePair<string, object>(
                        entry.Key?.ToString() ?? "",
                        entry.Value));
                }
                foreach (var entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(entry.Key);
                    WriteCanonicalValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;
            }

            if (value is IEnumerable sequence && !(value is string))
            {
                writer.WriteStartArray();
                foreach (var item in sequence)
                    WriteCanonicalValue(writer, item);
                writer.WriteEndArray();
                return;
            }

            switch (value)
            {
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case bool boolean:
                    writer.WriteBooleanValue(boolean);
                    break;
                case int intValue:
                    writer.WriteNumberValue(intValue);
                    break;
                case long longValue:
                    writer.WriteNumberValue(longValue);
                    break;
                case double doubleValue:
                    writer.WriteNumberValue(doubleValue);
                    break;
                case float floatValue:
                    writer.WriteNumberValue(floatValue);
                    break;
                case decimal decimalValue:
                    writer.WriteNumberValue(decimalValue);
                    break;
                default:
                    writer.WriteStringValue(value.ToString());
                    break;
            }
        }

        private static string HashText(string value)
        {
            return HashBytes(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string HashBytes(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        private static JsonSerializerOptions CreateCommandDataJsonOptions()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JavaScriptSafeInt64Converter());
            return options;
        }

        private static (string token, string source) LoadOrCreateAuthToken()
        {
            var configured = Environment.GetEnvironmentVariable(
                "REVIT_MCP_AUTH_TOKEN");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var token = configured.Trim();
                return (token, "REVIT_MCP_AUTH_TOKEN");
            }

            var directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP");
            var tokenPath = Path.Combine(directory, "auth-token");
            Directory.CreateDirectory(directory);
            EnsureAuthTokenDirectoryIsSafe(directory);

            if (File.Exists(tokenPath) || Directory.Exists(tokenPath))
                return (ReadValidAuthToken(tokenPath), tokenPath);

            var generated = GenerateStrongToken();
            var temporaryPath = Path.Combine(
                directory,
                ".auth-token-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(generated);
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                EnsureAuthTokenDirectoryIsSafe(directory);
                File.Move(temporaryPath, tokenPath);
                return (generated, tokenPath);
            }
            catch (IOException)
            {
                if (!File.Exists(tokenPath) &&
                    !Directory.Exists(tokenPath))
                {
                    throw;
                }

                // Another CAD process won the atomic move race. Its target is
                // complete before it becomes visible, but retry briefly for
                // antivirus/file-lock delays and older direct-write versions.
                return (ReadValidAuthToken(tokenPath), tokenPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // A stale random-name temp file is safer than risking
                    // startup failure after the shared token was established.
                }
            }
        }

        private static string ReadValidAuthToken(string tokenPath)
        {
            Exception lastError = null;
            for (var attempt = 0;
                 attempt < AuthTokenReadAttempts;
                 attempt++)
            {
                try
                {
                    if (Directory.Exists(tokenPath))
                    {
                        throw new InvalidDataException(
                            $"Authentication token path is a directory: " +
                            $"{tokenPath}");
                    }

                    EnsureAuthTokenFileIsSafe(tokenPath);
                    var token = File.ReadAllText(
                        tokenPath,
                        Encoding.UTF8).Trim();
                    if (token.Length >= 32 && token.Length <= 4096)
                        return token;

                    lastError = new InvalidDataException(
                        $"Authentication token file is empty, incomplete, " +
                        $"or too large: {tokenPath}");
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }

                if (attempt + 1 < AuthTokenReadAttempts)
                    Thread.Sleep(AuthTokenReadDelayMilliseconds);
            }

            throw new InvalidOperationException(
                $"Could not read a valid authentication token after " +
                $"{AuthTokenReadAttempts} attempts: {tokenPath}",
                lastError);
        }

        private static void EnsureAuthTokenDirectoryIsSafe(string directory)
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Authentication token directory is not a safe physical " +
                    $"directory: {directory}");
            }
        }

        private static void EnsureAuthTokenFileIsSafe(string tokenPath)
        {
            var attributes = File.GetAttributes(tokenPath);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Authentication token path is not a regular file: " +
                    $"{tokenPath}");
            }
        }

        private static string GenerateStrongToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private bool HasValidAuthorization(string authorization)
        {
            const string prefix = "Bearer ";
            if (string.IsNullOrWhiteSpace(authorization) ||
                !authorization.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var supplied = authorization.Substring(prefix.Length).Trim();
            return FixedTimeEquals(supplied, _authToken);
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            byte[] leftDigest;
            byte[] rightDigest;
            using (var sha256 = SHA256.Create())
                leftDigest = sha256.ComputeHash(
                    Encoding.UTF8.GetBytes(left ?? ""));
            using (var sha256 = SHA256.Create())
                rightDigest = sha256.ComputeHash(
                    Encoding.UTF8.GetBytes(right ?? ""));

            var difference = 0;
            for (var i = 0; i < leftDigest.Length; i++)
                difference |= leftDigest[i] ^ rightDigest[i];

            return difference == 0;
        }

        private static bool IsAllowedOrigin(string origin)
        {
            // Native clients such as the TypeScript MCP server do not send Origin.
            if (string.IsNullOrWhiteSpace(origin))
                return true;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;

            var httpScheme =
                string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            return httpScheme && uri.IsLoopback;
        }

        private static async Task RejectHttpRequest(
            HttpListenerContext context,
            int statusCode,
            string message,
            CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(new
            {
                status = "error",
                error = message
            });
            await WriteHttpJson(context, statusCode, json, ct).ConfigureAwait(false);
        }

        private static async Task WriteHttpJson(
            HttpListenerContext context,
            int statusCode,
            string json,
            CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            try
            {
                await context.Response.OutputStream.WriteAsync(
                    bytes,
                    0,
                    bytes.Length,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Server shutdown while writing the response.
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private static string BuildSuccessResponse(
            string requestId,
            string serializedData)
        {
            using var dataDocument = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(serializedData)
                    ? "null"
                    : serializedData);
            return JsonSerializer.Serialize(new
            {
                id = requestId ?? "",
                status = "success",
                data = dataDocument.RootElement.Clone()
            });
        }

        private static string BuildCommandErrorResponse(
            string requestId,
            CommandResult result)
        {
            return BuildErrorResponse(
                requestId,
                "REVIT_API_ERROR",
                result?.ErrorMessage ?? "The Revit command failed without an error message.",
                true,
                result?.Suggestion ?? "");
        }

        private static string BuildErrorResponse(
            string requestId,
            string code,
            string message,
            bool recoverable,
            string suggestion)
        {
            return JsonSerializer.Serialize(new
            {
                id = requestId ?? "",
                status = "error",
                error = new
                {
                    code,
                    message,
                    recoverable,
                    suggestion = suggestion ?? ""
                }
            });
        }

        private sealed class CommandRequest
        {
            public string Id { get; set; } = "";
            public string Command { get; set; } = "";
            public Dictionary<string, object> Params { get; set; } =
                new Dictionary<string, object>();

            [JsonPropertyName("timeout_ms")]
            public int TimeoutMs { get; set; } = 30000;

            [JsonPropertyName("target_session_id")]
            public string TargetSessionId { get; set; }

            [JsonPropertyName("expected_document_fingerprint")]
            public string ExpectedDocumentFingerprint { get; set; }
        }
    }
}
