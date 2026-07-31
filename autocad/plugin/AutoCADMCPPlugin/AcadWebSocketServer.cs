using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
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
    /// </summary>
    public class AcadWebSocketServer
    {
        private const int AuthTokenReadAttempts = 20;
        private const int AuthTokenReadDelayMilliseconds = 25;
        private readonly int _port;
        private readonly CommandDispatcher _dispatcher;
        private readonly string _authToken;
        private readonly object _lifecycleLock = new object();
        private ServerRun _activeRun;
        private bool _started;

        private sealed class ServerRun
        {
            public ServerRun(
                HttpListener listener,
                CancellationTokenSource cancellation)
            {
                Listener = listener;
                Cancellation = cancellation;
            }

            public HttpListener Listener { get; }
            public CancellationTokenSource Cancellation { get; }
            public object ConnectionsLock { get; } = new object();
            public HashSet<WebSocket> Connections { get; } =
                new HashSet<WebSocket>();
            public HashSet<Task> ConnectionTasks { get; } =
                new HashSet<Task>();
            public Task ListenTask { get; set; }
            public int StopInitiated;
        }

        // Tier 1 harness — idempotency cache. Same shape as Revit MCP's:
        // re-sending a side-effect command with the same idempotency key
        // (or request id) within the TTL returns the cached response and
        // skips the AutoCAD API call. Read-only commands are never cached.
        private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromMinutes(15);
        private const int MaxIdempotencyEntries = 1000;
        private readonly Dictionary<string, CachedResult> _idempotencyCache =
            new Dictionary<string, CachedResult>();
        private readonly object _cacheLock = new object();
        // AutoCAD's command context and the shared PICKFIRST snapshot are
        // process-global. Serialize all command dispatches; this also provides
        // the required global single-flight boundary for side effects.
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        private class CachedResult
        {
            public object Data { get; set; }
            public string CommandName { get; set; }
            public string ParameterHash { get; set; }
            public DateTime CachedAt { get; set; }
        }

        // Commands whose responses we cache. Conservative — must match what
        // RevitWebSocketServer caches, kept in sync.
        private static readonly HashSet<string> _sideEffectPrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "create_", "modify_", "delete_", "move_", "copy_", "mirror_",
            "rotate_", "array_", "rename_", "duplicate_", "change_", "place_", "load_", "purge_",
            "set_", "batch_", "fix_", "apply_", "tag_", "isolate_",
            "reset_", "select_", "export_",
        };

        public AcadWebSocketServer(int port = 8182)
        {
            _port = port;
            _dispatcher = new CommandDispatcher();
            _authToken = LoadOrCreateAuthToken();
        }

        public bool Start()
        {
            lock (_lifecycleLock)
            {
                if (_started && _activeRun != null)
                    return true;

                var cts = new CancellationTokenSource();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                try
                {
                    listener.Start();
                    var run = new ServerRun(listener, cts);
                    _activeRun = run;
                    _started = true;
                    run.ListenTask = Task.Run(() => ListenLoop(run));
                    ObserveListenTask(run);

                    if (!_started || !ReferenceEquals(_activeRun, run))
                        return false;

                    Debug.WriteLine($"[AutoCADMCP] WebSocket server started on port {_port}");
                    return true;
                }
                catch (Exception ex)
                {
                    if (_activeRun != null &&
                        ReferenceEquals(_activeRun.Listener, listener))
                    {
                        _activeRun = null;
                        _started = false;
                    }
                    try { listener.Close(); } catch { }
                    cts.Dispose();
                    Debug.WriteLine(
                        $"[AutoCADMCP] Failed to start WebSocket server: {ex.Message}");
                    return false;
                }
            }
        }

        public void Stop()
        {
            ServerRun run;
            lock (_lifecycleLock)
            {
                run = _activeRun;
                if (run == null)
                {
                    _started = false;
                    return;
                }

                _started = false;
                _activeRun = null;
            }

            StopRun(run);
            Debug.WriteLine("[AutoCADMCP] WebSocket server stopped.");
        }

        private void ObserveListenTask(ServerRun run)
        {
            _ = run.ListenTask.ContinueWith(
                completed =>
                {
                    var ownsLifecycle = false;
                    var unexpected =
                        !run.Cancellation.IsCancellationRequested;
                    lock (_lifecycleLock)
                    {
                        if (ReferenceEquals(_activeRun, run))
                        {
                            ownsLifecycle = true;
                            _activeRun = null;
                            _started = false;
                        }
                    }

                    if (!ownsLifecycle)
                        return;

                    if (completed.IsFaulted)
                    {
                        Debug.WriteLine(
                            $"[AutoCADMCP] Listener failed: " +
                            $"{completed.Exception?.GetBaseException().Message}");
                    }
                    else if (unexpected)
                    {
                        Debug.WriteLine(
                            "[AutoCADMCP] Listener exited unexpectedly; " +
                            "server state was reset for a safe restart.");
                    }

                    StopRun(run);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void StopRun(ServerRun run)
        {
            if (Interlocked.Exchange(ref run.StopInitiated, 1) != 0)
                return;

            try { run.Cancellation.Cancel(); } catch { }
            try { run.Listener.Stop(); } catch { }
            try { run.Listener.Close(); } catch { }

            WebSocket[] sockets;
            lock (run.ConnectionsLock)
                sockets = new List<WebSocket>(run.Connections).ToArray();

            foreach (var socket in sockets)
            {
                try { socket.Abort(); } catch { }
                try { socket.Dispose(); } catch { }
            }

            var listenTask = run.ListenTask ?? Task.CompletedTask;
            _ = listenTask.ContinueWith(
                _ => FinishRunShutdown(run),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void FinishRunShutdown(ServerRun run)
        {
            Task[] connectionTasks;
            WebSocket[] sockets;
            lock (run.ConnectionsLock)
            {
                sockets = new List<WebSocket>(run.Connections).ToArray();
                connectionTasks =
                    new List<Task>(run.ConnectionTasks).ToArray();
            }

            foreach (var socket in sockets)
            {
                try { socket.Abort(); } catch { }
                try { socket.Dispose(); } catch { }
            }

            var handlers = connectionTasks.Length == 0
                ? Task.CompletedTask
                : Task.WhenAll(connectionTasks);
            _ = handlers.ContinueWith(
                _ =>
                {
                    try { run.Listener.Close(); } catch { }
                    run.Cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task ListenLoop(ServerRun run)
        {
            var listener = run.Listener;
            var ct = run.Cancellation.Token;
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext httpContext;
                try
                {
                    httpContext = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Debug.WriteLine(
                            $"[AutoCADMCP] Listener error: {ex.Message}");
                    }
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Debug.WriteLine(
                        $"[AutoCADMCP] Listener error: {ex.Message}");
                    if (!listener.IsListening)
                        break;
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
                    if (!IsAllowedOrigin(
                            httpContext.Request.Headers["Origin"]))
                    {
                        httpContext.Response.StatusCode =
                            (int)HttpStatusCode.Forbidden;
                        httpContext.Response.Close();
                        continue;
                    }
                    if (!IsAuthorized(
                            httpContext.Request.Headers["Authorization"]))
                    {
                        httpContext.Response.StatusCode =
                            (int)HttpStatusCode.Unauthorized;
                        httpContext.Response.Headers["WWW-Authenticate"] =
                            "Bearer";
                        httpContext.Response.Close();
                        continue;
                    }

                    // Health probe has the same authentication boundary as
                    // the WebSocket endpoint.
                    if (!httpContext.Request.IsWebSocketRequest)
                    {
                        var body = Encoding.UTF8.GetBytes(
                            "{\"status\":\"ok\"," +
                            "\"server\":\"autocad-mcp-plugin\"}");
                        httpContext.Response.ContentType = "application/json";
                        httpContext.Response.OutputStream.Write(
                            body,
                            0,
                            body.Length);
                        httpContext.Response.Close();
                        continue;
                    }

                    var task = HandleWebSocket(run, httpContext);
                    TrackConnectionTask(run, task);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[AutoCADMCP] Connection setup failed: {ex.Message}");
                    try { httpContext.Response.Abort(); } catch { }
                }
            }
        }

        private static void TrackConnectionTask(ServerRun run, Task task)
        {
            lock (run.ConnectionsLock)
                run.ConnectionTasks.Add(task);

            _ = task.ContinueWith(
                completed =>
                {
                    lock (run.ConnectionsLock)
                        run.ConnectionTasks.Remove(completed);

                    if (completed.IsFaulted)
                    {
                        Debug.WriteLine(
                            $"[AutoCADMCP] Connection task failed: " +
                            $"{completed.Exception?.GetBaseException().Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task HandleWebSocket(
            ServerRun run,
            HttpListenerContext ctx)
        {
            var ct = run.Cancellation.Token;
            HttpListenerWebSocketContext wsContext;
            try
            {
                wsContext = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Debug.WriteLine(
                        $"[AutoCADMCP] WS accept failed: {ex.Message}");
                }
                return;
            }

            var ws = wsContext.WebSocket;
            lock (run.ConnectionsLock)
                run.Connections.Add(ws);

            var buf = new byte[64 * 1024];

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var msg = await ReadFullMessage(ws, buf, ct).ConfigureAwait(false);
                    if (msg == null) break;

                    var responseJson = await DispatchSafely(msg, ct).ConfigureAwait(false);
                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    await ws.SendAsync(
                        new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text,
                        endOfMessage: true, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal server shutdown or command cancellation.
            }
            catch (ObjectDisposedException)
            {
                // Normal shutdown race after the socket is aborted.
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Debug.WriteLine(
                        $"[AutoCADMCP] WS handler error: {ex.Message}");
                }
            }
            finally
            {
                try
                {
                    if (!ct.IsCancellationRequested &&
                        ws.State == WebSocketState.Open)
                    {
                        using var closeCts =
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(1));
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "shutdown",
                            closeCts.Token).ConfigureAwait(false);
                    }
                }
                catch { /* ignore */ }

                lock (run.ConnectionsLock)
                    run.Connections.Remove(ws);
                try { ws.Dispose(); } catch { }
            }
        }

        private static async Task<string> ReadFullMessage(
            WebSocket ws, byte[] buf, CancellationToken ct)
        {
            const int maxMessageBytes = 16 * 1024 * 1024;
            using var bytes = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Only text WebSocket messages are supported.");
                if (bytes.Length + result.Count > maxMessageBytes)
                    throw new InvalidDataException(
                        $"WebSocket message exceeds the {maxMessageBytes} byte limit.");
                bytes.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }

        // ─── Dispatch ──────────────────────────────────────────────────

        /// <summary>Catches everything — protocol always returns a JSON envelope.</summary>
        private async Task<string> DispatchSafely(
            string requestJson,
            CancellationToken serverCancellationToken)
        {
            string id = "";
            try
            {
                using var doc = JsonDocument.Parse(requestJson);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return ErrorEnvelope(id, "VALIDATION_ERROR",
                        "Request must be a JSON object.", recoverable: false);

                if (!root.TryGetProperty("id", out var idEl)
                    || idEl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idEl.GetString()))
                {
                    return ErrorEnvelope(id, "VALIDATION_ERROR",
                        "Missing or invalid string 'id' field.", recoverable: false);
                }
                id = idEl.GetString();
                if (id.Length > 512)
                    return ErrorEnvelope(id, "VALIDATION_ERROR",
                        "Request 'id' must not exceed 512 characters.", recoverable: false);

                var commandName = root.TryGetProperty("command", out var cmdEl)
                    && cmdEl.ValueKind == JsonValueKind.String
                    ? cmdEl.GetString()
                    : null;
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

                if (paramsEl.HasValue
                    && paramsEl.Value.ValueKind != JsonValueKind.Object
                    && paramsEl.Value.ValueKind != JsonValueKind.Null)
                {
                    return ErrorEnvelope(id, "VALIDATION_ERROR",
                        "'params' must be a JSON object.", recoverable: false);
                }
                var parameters = ConvertJsonElement(paramsEl) as Dictionary<string, object>
                    ?? new Dictionary<string, object>();

                var timeoutMs = 30_000;
                if (root.TryGetProperty("timeout_ms", out var timeoutEl))
                {
                    if (timeoutEl.ValueKind != JsonValueKind.Number
                        || !timeoutEl.TryGetInt32(out timeoutMs)
                        || timeoutMs < 1
                        || timeoutMs > 600_000)
                    {
                        return ErrorEnvelope(id, "VALIDATION_ERROR",
                            "'timeout_ms' must be an integer from 1 to 600000.",
                            recoverable: true,
                            suggestion: "Use a longer timeout for large drawings, up to 600000 ms.");
                    }
                }

                var acadDocs = Application.DocumentManager;
                var activeDoc = acadDocs.MdiActiveDocument;
                if (activeDoc == null)
                {
                    return ErrorEnvelope(id, "CAD_API_ERROR",
                        "No active drawing. Open a drawing in AutoCAD first.",
                        recoverable: true);
                }

                // Idempotency cache lookup for side-effect commands.
                var idempotencyKey = ExtractIdempotencyKey(parameters, id);
                if (parameters.TryGetValue(
                        "idempotency_key",
                        out var suppliedIdempotencyKey)
                    && (!(suppliedIdempotencyKey is string suppliedKey)
                        || string.IsNullOrWhiteSpace(suppliedKey)))
                {
                    return ErrorEnvelope(
                        id,
                        "VALIDATION_ERROR",
                        "idempotency_key must be a non-empty string when supplied.",
                        recoverable: true,
                        suggestion: "Use a compact UUID or omit the field.");
                }
                if (idempotencyKey.Length > 512)
                {
                    return ErrorEnvelope(
                        id,
                        "VALIDATION_ERROR",
                        "idempotency_key must not exceed 512 characters.",
                        recoverable: true,
                        suggestion: "Use a compact UUID or similarly unique token.");
                }
                var parameterHash = ComputeParameterHash(parameters);
                var isSideEffect = IsSideEffectCommand(commandName);
                var commandGateAcquired = false;
                using var executionCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        serverCancellationToken);
                executionCts.CancelAfter(timeoutMs);

                try
                {
                    await _commandGate
                        .WaitAsync(executionCts.Token)
                        .ConfigureAwait(false);
                    commandGateAcquired = true;
                }
                catch (OperationCanceledException)
                    when (!serverCancellationToken.IsCancellationRequested)
                {
                    return ErrorEnvelope(
                        id,
                        "TIMEOUT_ERROR",
                        $"Command exceeded timeout_ms ({timeoutMs} ms) while waiting for another AutoCAD operation.",
                        recoverable: true,
                        suggestion:
                            "Retry with a longer timeout_ms. Reuse the same " +
                            "idempotency_key only for an identical request.");
                }
                try
                {
                    executionCts.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(
                            activeDoc,
                            acadDocs.MdiActiveDocument))
                    {
                        return ErrorEnvelope(
                            id,
                            "CAD_API_ERROR",
                            "The active drawing changed while this request was queued.",
                            recoverable: true,
                            suggestion:
                                "Retry against the intended active drawing with " +
                                "a new request id.");
                    }

                    var documentScope = ComputeDocumentScope(activeDoc);
                    var cacheKey =
                        $"{documentScope}:{HashText(idempotencyKey)}";
                    if (isSideEffect)
                    {
                        if (TryGetCached(
                            cacheKey,
                            commandName,
                            parameterHash,
                            out var cachedData,
                            out var keyMismatch))
                        {
                            Debug.WriteLine($"[AutoCADMCP] Idempotency hit for {commandName} (key={idempotencyKey})");
                            return SuccessEnvelope(id, cachedData);
                        }
                        if (keyMismatch)
                        {
                            return ErrorEnvelope(id, "IDEMPOTENCY_CONFLICT",
                                "The idempotency_key was already used with different parameters in this drawing.",
                                recoverable: false,
                                suggestion: "Reuse a key only for an identical retry, or generate a new key for a changed request.");
                        }
                    }

                    var command = _dispatcher.GetCommand(commandName);

                    // Marshal to AutoCAD's main thread.
                    CommandResult result = null;
                    Exception captured = null;
                    object responseData = null;
                    var transactionCommitted = false;

                    // Pre-capture PICKFIRST before entering command context.
                    Autodesk.AutoCAD.DatabaseServices.ObjectId[] preSelection
                        = Array.Empty<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
                    try
                    {
                        using (var dlock = activeDoc.LockDocument(
                            Autodesk.AutoCAD.ApplicationServices.DocumentLockMode.Read,
                            "MCP-CapturePickFirst", "MCP-CapturePickFirst", true))
                        {
                            var sr = activeDoc.Editor.SelectImplied();
                            if (sr.Status ==
                                    Autodesk.AutoCAD.EditorInput.PromptStatus.OK
                                && sr.Value != null)
                            {
                                preSelection = sr.Value.GetObjectIds();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[AutoCADMCP] PICKFIRST capture failed: {ex.Message}");
                    }
                    AutoCADMCP.CommandSet.Interfaces.SelectionContext.Current =
                        preSelection;

                    try
                    {
                        await acadDocs.ExecuteInCommandContextAsync(
                            async (object _) =>
                        {
                            Transaction tr = null;
                            try
                            {
                                using (tr = activeDoc.Database
                                           .TransactionManager
                                           .StartTransaction())
                                {
                                    result = await command.ExecuteAsync(
                                        activeDoc.Database,
                                        tr,
                                        parameters,
                                        executionCts.Token);
                                    executionCts.Token
                                        .ThrowIfCancellationRequested();
                                    if (result != null && result.Success)
                                    {
                                        // Validate a provisional payload
                                        // before commit. Serialization errors
                                        // must still be rollback-safe.
                                        responseData =
                                            JsonSerializer.SerializeToElement(
                                                result.Data);
                                        executionCts.Token
                                            .ThrowIfCancellationRequested();
                                        tr.Commit();
                                        transactionCommitted = true;
                                    }
                                    else
                                        tr.Abort();
                                }
                            }
                            catch (Exception ex)
                            {
                                captured = ex;
                                if (!transactionCommitted)
                                {
                                    try { tr?.Abort(); }
                                    catch { /* already rolled back/disposed */ }
                                }
                            }

                            // The command transaction has been disposed here.
                            // Only now may final verification open a fresh
                            // read transaction against the committed ObjectId.
                            if (transactionCommitted)
                            {
                                try
                                {
                                    FinalizePostCommitVerification(
                                        activeDoc.Database,
                                        commandName,
                                        result,
                                        parameters);
                                    if (isSideEffect)
                                        AddMutationCommittedMarker(result);
                                    responseData =
                                        JsonSerializer.SerializeToElement(
                                            result.Data);
                                }
                                catch (Exception verificationError)
                                {
                                    Debug.WriteLine(
                                        "[AutoCADMCP] Post-commit " +
                                        "verification warning: " +
                                        verificationError.Message);
                                    responseData =
                                        BuildPostCommitFallback(
                                            responseData,
                                            commandName,
                                            isSideEffect,
                                            verificationError);
                                }
                            }
                        }, null);
                    }
                    catch (Exception contextError)
                    {
                        captured = contextError;
                    }
                    finally
                    {
                        // Restore PICKFIRST so the user's selection survives
                        // MCP command execution.
                        if (preSelection.Length > 0)
                        {
                            try
                            {
                                using (var dlock2 = activeDoc.LockDocument(
                                    Autodesk.AutoCAD.ApplicationServices.DocumentLockMode.Write,
                                    "MCP-RestorePickFirst",
                                    "MCP-RestorePickFirst",
                                    true))
                                {
                                    activeDoc.Editor.SetImpliedSelection(
                                        preSelection);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine(
                                    $"[AutoCADMCP] PICKFIRST restore failed: " +
                                    ex.Message);
                            }
                        }
                        AutoCADMCP.CommandSet.Interfaces.SelectionContext.Current
                            = Array.Empty<
                                Autodesk.AutoCAD.DatabaseServices.ObjectId>();
                    }

                    if (captured != null && !transactionCommitted)
                    {
                        if (captured is OperationCanceledException
                            && executionCts.IsCancellationRequested)
                        {
                            return ErrorEnvelope(
                                id,
                                "TIMEOUT_ERROR",
                                $"Command exceeded timeout_ms ({timeoutMs} ms) and was rolled back.",
                                recoverable: true,
                                suggestion:
                                    "Retry with a longer timeout_ms. Reuse the " +
                                    "same idempotency_key only for an identical request.");
                        }
                        return ErrorEnvelope(
                            id,
                            "CAD_API_ERROR",
                            captured.Message,
                            recoverable: true,
                            suggestion:
                                "AutoCAD API call failed; see plugin logs.");
                    }
                    if (captured != null)
                    {
                        // ExecuteInCommandContextAsync can still report a
                        // teardown error after the transaction has committed.
                        // Returning an error here would invite a duplicate
                        // retry, so keep the verified success payload.
                        Debug.WriteLine(
                            "[AutoCADMCP] Post-commit command-context warning: " +
                            captured.Message);
                        try
                        {
                            responseData = AddPostCommitWarning(
                                responseData,
                                captured.Message);
                        }
                        catch (Exception warningError)
                        {
                            Debug.WriteLine(
                                "[AutoCADMCP] Could not attach post-commit " +
                                "warning: " + warningError.Message);
                        }
                    }

                    if (result == null)
                    {
                        return ErrorEnvelope(
                            id,
                            "INTERNAL_ERROR",
                            "Command produced no result.",
                            recoverable: false);
                    }

                    if (!result.Success)
                    {
                        return ErrorEnvelope(
                            id,
                            "CAD_API_ERROR",
                            result.ErrorMessage ?? "Unknown error",
                            recoverable: true,
                            suggestion: result.Suggestion);
                    }

                    var responseJson = SuccessEnvelope(id, responseData);

                    // Cache the immutable, prevalidated snapshot. Cache failure
                    // must never hide an already committed mutation.
                    if (isSideEffect)
                    {
                        try
                        {
                            StoreCached(
                                cacheKey,
                                commandName,
                                parameterHash,
                                responseData);
                        }
                        catch (Exception cacheError)
                        {
                            Debug.WriteLine(
                                $"[AutoCADMCP] Idempotency cache store failed: " +
                                cacheError.Message);
                        }
                    }
                    return responseJson;
                }
                finally
                {
                    if (commandGateAcquired)
                        _commandGate.Release();
                }
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

        // ─── Idempotency cache ─────────────────────────────────────────

        private static bool IsSideEffectCommand(string commandName)
        {
            if (string.Equals(
                    commandName,
                    "execute_script",
                    StringComparison.Ordinal))
            {
                // An escape hatch is not a sandbox even when it is described
                // as a query, so serialize and cache every invocation.
                return true;
            }

            foreach (var prefix in _sideEffectPrefixes)
                if (commandName.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void AddMutationCommittedMarker(CommandResult result)
        {
            if (result.Data is Dictionary<string, object> data)
            {
                data["mutation_committed"] = true;
                return;
            }

            result.Data = new Dictionary<string, object>
            {
                ["result"] = result.Data,
                ["mutation_committed"] = true,
            };
        }

        private static void FinalizePostCommitVerification(
            Database database,
            string commandName,
            CommandResult result,
            Dictionary<string, object> parameters)
        {
            if (!string.Equals(
                    commandName,
                    "create_line",
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!(result.Data is Dictionary<string, object> data) ||
                !data.TryGetValue("entity_id", out var entityIdValue) ||
                !(entityIdValue is string entityId) ||
                !long.TryParse(
                    entityId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var handleValue) ||
                handleValue <= 0)
            {
                throw new InvalidDataException(
                    "create_line did not return a valid committed entity_id.");
            }

            if (!TryReadPoint(parameters, "start", out var expectedStart) ||
                !TryReadPoint(parameters, "end", out var expectedEnd))
            {
                throw new InvalidDataException(
                    "create_line verification could not read requested points.");
            }

            if (!data.TryGetValue("layer", out var layerValue) ||
                !(layerValue is string expectedLayer))
            {
                throw new InvalidDataException(
                    "create_line did not return its expected layer.");
            }

            var objectId = database.GetObjectId(
                false,
                new Handle(handleValue),
                0);
            if (objectId.IsNull)
            {
                throw new InvalidDataException(
                    "Committed line ObjectId could not be resolved.");
            }

            Autodesk.AutoCAD.Geometry.Point3d actualStart;
            Autodesk.AutoCAD.Geometry.Point3d actualEnd;
            double actualLength;
            string actualLayer;
            using (var verificationTransaction = database
                       .TransactionManager.StartOpenCloseTransaction())
            {
                var line = verificationTransaction.GetObject(
                    objectId,
                    OpenMode.ForRead) as Line;
                if (line == null)
                {
                    throw new InvalidDataException(
                        "Committed ObjectId is not a Line.");
                }

                actualStart = line.StartPoint;
                actualEnd = line.EndPoint;
                actualLength = line.Length;
                actualLayer = line.Layer;
                verificationTransaction.Commit();
            }

            const double tolerance = 1e-6;
            var startMatch = NearlyEqual(
                actualStart,
                expectedStart,
                tolerance);
            var endMatch = NearlyEqual(
                actualEnd,
                expectedEnd,
                tolerance);
            var layerMatch = string.Equals(
                actualLayer,
                expectedLayer,
                StringComparison.OrdinalIgnoreCase);
            var issues = new List<string>();
            if (!startMatch)
                issues.Add("Committed start point differs from the request.");
            if (!endMatch)
                issues.Add("Committed end point differs from the request.");
            if (!layerMatch)
                issues.Add("Committed layer differs from the created object.");

            data["verification"] = new Dictionary<string, object>
            {
                ["performed"] = true,
                ["phase"] = "post_commit",
                ["provisional"] = false,
                ["commit_verified"] = true,
                ["match"] =
                    startMatch && endMatch && layerMatch,
                ["entity_exists"] = true,
                ["start_match"] = startMatch,
                ["end_match"] = endMatch,
                ["layer_match"] = layerMatch,
                ["actual_start"] = new[]
                {
                    actualStart.X,
                    actualStart.Y,
                    actualStart.Z
                },
                ["actual_end"] = new[]
                {
                    actualEnd.X,
                    actualEnd.Y,
                    actualEnd.Z
                },
                ["actual_length"] = actualLength,
                ["actual_layer"] = actualLayer,
                ["issues"] = issues,
            };
        }

        private static bool TryReadPoint(
            Dictionary<string, object> parameters,
            string key,
            out Autodesk.AutoCAD.Geometry.Point3d point)
        {
            point = default;
            if (!parameters.TryGetValue(key, out var value) ||
                !(value is List<object> coordinates) ||
                coordinates.Count < 2 ||
                coordinates.Count > 3 ||
                !TryReadFiniteDouble(coordinates[0], out var x) ||
                !TryReadFiniteDouble(coordinates[1], out var y))
            {
                return false;
            }

            var z = 0d;
            if (coordinates.Count == 3 &&
                !TryReadFiniteDouble(coordinates[2], out z))
            {
                return false;
            }

            point = new Autodesk.AutoCAD.Geometry.Point3d(x, y, z);
            return true;
        }

        private static bool TryReadFiniteDouble(
            object value,
            out double number)
        {
            switch (value)
            {
                case double doubleValue:
                    number = doubleValue;
                    break;
                case float floatValue:
                    number = floatValue;
                    break;
                case long longValue:
                    number = longValue;
                    break;
                case int intValue:
                    number = intValue;
                    break;
                case decimal decimalValue:
                    number = (double)decimalValue;
                    break;
                case string text when double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed):
                    number = parsed;
                    break;
                default:
                    number = 0;
                    return false;
            }

            return !double.IsNaN(number) &&
                   !double.IsInfinity(number);
        }

        private static bool NearlyEqual(
            Autodesk.AutoCAD.Geometry.Point3d left,
            Autodesk.AutoCAD.Geometry.Point3d right,
            double tolerance)
        {
            return Math.Abs(left.X - right.X) <= tolerance &&
                   Math.Abs(left.Y - right.Y) <= tolerance &&
                   Math.Abs(left.Z - right.Z) <= tolerance;
        }

        private static object BuildPostCommitFallback(
            object provisionalSnapshot,
            string commandName,
            bool isSideEffect,
            Exception verificationError)
        {
            var augmented = new Dictionary<string, object>();
            if (provisionalSnapshot is JsonElement element &&
                element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                    augmented[property.Name] = property.Value.Clone();
            }
            else
            {
                augmented["result"] = provisionalSnapshot;
            }

            if (isSideEffect)
                augmented["mutation_committed"] = true;

            var warning =
                "The transaction committed, but post-commit verification " +
                $"could not be completed: {verificationError.Message}";
            augmented["warning"] = warning;
            if (string.Equals(
                    commandName,
                    "create_line",
                    StringComparison.Ordinal))
            {
                augmented["verification"] =
                    new Dictionary<string, object>
                    {
                        ["performed"] = false,
                        ["phase"] = "post_commit",
                        ["provisional"] = false,
                        ["commit_verified"] = false,
                        ["match"] = false,
                        ["issues"] = new[] { warning },
                    };
            }

            return JsonSerializer.SerializeToElement(augmented);
        }

        private static object AddPostCommitWarning(
            object snapshot,
            string warning)
        {
            if (snapshot is JsonElement element &&
                element.ValueKind == JsonValueKind.Object)
            {
                var augmented = new Dictionary<string, object>();
                foreach (var property in element.EnumerateObject())
                    augmented[property.Name] = property.Value.Clone();
                augmented["command_context_succeeded"] = false;
                augmented["warning"] =
                    "The transaction committed, but AutoCAD reported a " +
                    $"command-context teardown error: {warning}";
                return JsonSerializer.SerializeToElement(augmented);
            }

            return JsonSerializer.SerializeToElement(
                new Dictionary<string, object>
                {
                    ["result"] = snapshot,
                    ["mutation_committed"] = true,
                    ["command_context_succeeded"] = false,
                    ["warning"] =
                        "The transaction committed, but AutoCAD reported a " +
                        $"command-context teardown error: {warning}",
                });
        }

        private static string ComputeDocumentScope(
            Autodesk.AutoCAD.ApplicationServices.Document document)
        {
            var fingerprint = document.Database.FingerprintGuid;
            if (!string.IsNullOrWhiteSpace(fingerprint))
                return HashText(
                    "fingerprint:" + fingerprint.Trim().ToUpperInvariant());

            // Unsaved/temporary databases may not yet expose a stable
            // fingerprint. Keep those isolated for this plugin process.
            return HashText(string.Join(
                "\n",
                document.Name ?? "",
                RuntimeHelpers.GetHashCode(document.Database).ToString()));
        }

        private static string ExtractIdempotencyKey(Dictionary<string, object> parameters, string requestId)
        {
            if (parameters.TryGetValue("idempotency_key", out var v) &&
                v is string s &&
                !string.IsNullOrWhiteSpace(s))
            {
                return s.Trim();
            }
            return string.IsNullOrEmpty(requestId) ? null : requestId;
        }

        private bool TryGetCached(
            string key,
            string commandName,
            string parameterHash,
            out object data,
            out bool keyMismatch)
        {
            keyMismatch = false;
            lock (_cacheLock)
            {
                if (_idempotencyCache.TryGetValue(key, out var entry))
                {
                    if (DateTime.UtcNow - entry.CachedAt < IdempotencyTtl)
                    {
                        if (!string.Equals(
                                entry.CommandName, commandName, StringComparison.Ordinal)
                            || !string.Equals(
                                entry.ParameterHash, parameterHash, StringComparison.Ordinal))
                        {
                            data = null;
                            keyMismatch = true;
                            return false;
                        }
                        data = entry.Data;
                        return true;
                    }
                    _idempotencyCache.Remove(key);
                }
            }
            data = null;
            return false;
        }

        private void StoreCached(
            string key,
            string commandName,
            string parameterHash,
            object data)
        {
            lock (_cacheLock)
            {
                PruneExpired_NoLock();
                while (_idempotencyCache.Count >= MaxIdempotencyEntries)
                {
                    string oldestKey = null;
                    var oldestTime = DateTime.MaxValue;
                    foreach (var pair in _idempotencyCache)
                    {
                        if (pair.Value.CachedAt < oldestTime)
                        {
                            oldestKey = pair.Key;
                            oldestTime = pair.Value.CachedAt;
                        }
                    }
                    if (oldestKey == null) break;
                    _idempotencyCache.Remove(oldestKey);
                }
                _idempotencyCache[key] = new CachedResult
                {
                    Data = data,
                    CommandName = commandName,
                    ParameterHash = parameterHash,
                    CachedAt = DateTime.UtcNow,
                };
            }
        }

        private void PruneExpired_NoLock()
        {
            var cutoff = DateTime.UtcNow - IdempotencyTtl;
            var expired = new List<string>();
            foreach (var kv in _idempotencyCache)
                if (kv.Value.CachedAt < cutoff) expired.Add(kv.Key);
            foreach (var k in expired)
                _idempotencyCache.Remove(k);
        }

        private static string HashText(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static string ComputeParameterHash(Dictionary<string, object> parameters)
        {
            var canonical = Canonicalize(parameters);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static object Canonicalize(object value)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var pair in dictionary)
                {
                    if (pair.Key == "idempotency_key") continue;
                    sorted[pair.Key] = Canonicalize(pair.Value);
                }
                return sorted;
            }
            if (value is IEnumerable<object> sequence && value is not string)
            {
                var list = new List<object>();
                foreach (var item in sequence) list.Add(Canonicalize(item));
                return list;
            }
            return value;
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

        private bool IsAuthorized(string authorization)
        {
            const string prefix = "Bearer ";
            if (string.IsNullOrWhiteSpace(authorization)
                || !authorization.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            return FixedTimeEquals(
                Encoding.UTF8.GetBytes(_authToken),
                Encoding.UTF8.GetBytes(authorization.Substring(prefix.Length)));
        }

        private static bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin)) return true;
            var allowed = Environment.GetEnvironmentVariable("REVIT_MCP_ALLOWED_ORIGINS");
            if (string.IsNullOrWhiteSpace(allowed)) return false;
            foreach (var item in allowed.Split(','))
                if (string.Equals(item.Trim(), origin, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            var diff = left.Length ^ right.Length;
            var count = Math.Max(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            {
                var l = i < left.Length ? left[i] : (byte)0;
                var r = i < right.Length ? right[i] : (byte)0;
                diff |= l ^ r;
            }
            return diff == 0;
        }

        private static string LoadOrCreateAuthToken()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("REVIT_MCP_AUTH_TOKEN");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment.Trim();

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP");
            var path = Path.Combine(directory, "auth-token");
            Directory.CreateDirectory(directory);
            EnsureAuthTokenDirectoryIsSafe(directory);
            if (File.Exists(path) || Directory.Exists(path))
                return ReadValidToken(path);

            var token = GenerateStrongToken();
            var temporaryPath = Path.Combine(
                directory,
                ".auth-token-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(token);
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
                File.Move(temporaryPath, path);
                return token;
            }
            catch (IOException)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw;

                // Revit or another AutoCAD process won the atomic move race.
                // Retry briefly for antivirus/file-lock delays and older
                // plugin versions that wrote the shared file directly.
                return ReadValidToken(path);
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
                    // Do not fail startup after the shared token is usable.
                }
            }
        }

        private static string ReadValidToken(string path)
        {
            Exception lastError = null;
            for (var attempt = 0;
                 attempt < AuthTokenReadAttempts;
                 attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        throw new InvalidDataException(
                            $"Authentication token path is a directory: {path}");
                    }

                    EnsureAuthTokenFileIsSafe(path);
                    var token = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (token.Length >= 32 && token.Length <= 4096)
                        return token;

                    lastError = new InvalidDataException(
                        $"Authentication token file is empty, incomplete, " +
                        $"or too large: {path}");
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
                $"{AuthTokenReadAttempts} attempts: {path}",
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

        private static void EnsureAuthTokenFileIsSafe(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Authentication token path is not a regular file: {path}");
            }
        }

        private static string GenerateStrongToken()
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
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
