# MCP↔CAD WebSocket Wire Protocol v1

This is the contract between the **TypeScript MCP server** (in `server/`)
and the **CAD plugin** (currently `plugin/RevitMCPPlugin/`, soon also an
AutoCAD plugin). Both ends must agree on this format. The shared runtime
implementation lives in [`@kimminsub/mcp-cad-core`](../packages/mcp-cad-core/).

> **Stability**: this is v1. Breaking changes require a `v2` document and
> a coordinated bump on both sides. Adding new optional fields is OK.

## Transport

- **WebSocket** over plain HTTP, `ws://127.0.0.1:<port>/`
- Default port `8181` (Revit) — each plugin should pick its own default
- Localhost-only by design. No auth, no TLS. The plugin must reject
  non-loopback connections.
- Plugin also exposes `GET /` returning `{"status":"ok","server":"<name>"}`
  for health probes (used by `scripts/test-ws.js` and the verifier).
- One connection per MCP server process. Reconnect on close.

## Message envelope

Every message is a single UTF-8 JSON object. No streaming, no fragments.

### Request (server → plugin)

```jsonc
{
  "id": "uuid-v4",          // client-generated, echoed in response
  "command": "query_elements",
  "params": { "category": "Walls", "summary_only": true },
  "timeout_ms": 30000        // hint; plugin may enforce its own ceiling
}
```

### Response (plugin → server)

Three kinds, distinguished by `status`:

```jsonc
// Success
{ "id": "...", "status": "success", "data": { /* command-specific */ } }

// Error (recoverable or not — see `recoverable` flag)
{
  "id": "...",
  "status": "error",
  "error": {
    "code": "VALIDATION_ERROR",  // see Error codes below
    "message": "Missing required parameter: category",
    "recoverable": true,
    "suggestion": "Provide a category name like 'Walls'..."
  }
}

// Progress (long-running ops; the same id will eventually emit success/error)
{
  "id": "...",
  "status": "progress",
  "progress": { "current": 42, "total": 100, "message": "Iterating walls…" }
}
```

The TypeScript types are in [`packages/mcp-cad-core/src/types.ts`](../packages/mcp-cad-core/src/types.ts).
The C# side serializes these via `System.Text.Json`.

## Field semantics

### `id`
- UUID v4 string. Server generates on send, plugin echoes verbatim.
- Doubles as the default idempotency key for side-effect commands when no
  explicit `idempotency_key` param is supplied.

### `command`
- Snake_case, no `revit_`/`cad_` prefix.
- Plugin's `CommandDispatcher` does reflection-based discovery: every
  C# class implementing `IRevitCommand` (Revit) / `ICadCommand` (AutoCAD)
  registers itself. `Name` property is the wire value.

### `params`
- Object. `System.Text.Json` deserializes to `Dictionary<string, object>`
  with `JsonElement` values. The plugin's `ConvertJsonElements()` walks
  the dict recursively, converting:
  - `JsonValueKind.String/Number/True/False/Null` → primitive
  - `JsonValueKind.Array` → `List<object>` (recursive)
  - `JsonValueKind.Object` → `Dictionary<string, object>` (recursive)
  - This is required because commands receive `Dictionary<string, object>`
    and must NOT see raw `JsonElement` values. See `WebSocketServer.cs`.

### `timeout_ms`
- Server's hint to the plugin. Plugin may enforce a lower ceiling.
- Server clears its own pending timer at this value and returns a
  synthetic `TIMEOUT_ERROR` if the plugin hasn't replied.

### `data`
- Command-specific payload on success. Always an object, never raw scalars.
- Pagination follows the `PaginatedResult<T>` shape from `types.ts`.
- Summary mode follows `SummaryResult` shape (counts + by_type + by_level).

### `error.code`

| Code              | Meaning                                                     |
|-------------------|-------------------------------------------------------------|
| `CONNECTION_ERROR`| TCP/WS layer broke. Retry-safe.                             |
| `TIMEOUT_ERROR`   | Server-side timeout fired. Retry with smaller scope.        |
| `REVIT_API_ERROR` | Revit API threw. Often retry-safe; `recoverable` flag tells.|
| `VALIDATION_ERROR`| Bad params. NOT retry-safe without fixing the call.         |
| `INTERNAL_ERROR`  | Bug in plugin. Report.                                      |

For AutoCAD, the same codes apply (`REVIT_API_ERROR` covers AutoCAD API
errors too — the name is historical; see "Forward compatibility").

### `error.suggestion`
- Free-text. Aimed at the LLM, not the human. Should describe the next
  action the LLM can take to recover (e.g., "Use revit_get_all_categories
  to list valid names").

### `progress`
- Optional. Plugins MAY emit any number of `progress` messages with the
  same `id`, all before the final `success`/`error`. The server logs them
  to stderr but does not surface them to MCP clients (yet).

## Threading model

- WebSocket worker thread receives the request.
- Plugin marshals onto the CAD application's main thread:
  - **Revit**: `RevitTask.RunAsync(...)` (Revit.Async library)
  - **AutoCAD**: `Application.DocumentManager.ExecuteInCommandContextAsync(...)`
    (built into the AutoCAD .NET API, no dependency)
- Command's `ExecuteAsync` runs on the main thread with the live `Document`.
- Long blocking operations should yield via `cancellationToken.ThrowIfCancellationRequested()`
  in inner loops.

## Idempotency cache

Server-side and plugin-side both hold a 15-minute cache of recent
side-effect command responses, keyed by `idempotency_key` (or `id` if not
supplied). On a duplicate request, the cached response is returned
verbatim — same `id`, no Revit/AutoCAD API call.

Cached commands (Tier 1 harness): `create_*`, `modify_*`, `delete_*`,
`move_*`, `copy_*`, `mirror_*`, `rotate_*`, `array_*`, `rename_*`,
`place_*`, `load_*`, `purge_*`, `set_*`, `batch_create_*`, `fix_*`.

Read-only queries are **never** cached.

## Response size protection

Server-side, the formatter (`createResponseFormatter` in core) enforces:

- Soft limit (default 25 KB): spill the full payload to
  `%TEMP%\<spill-dir>\<command>-<ts>-<uuid>.json` and return a 12 KB
  preview + the spill path.
- Hard limit (default 500 KB): same behavior plus an explicit
  "exceeds hard limit" marker.

`spillDirName` is per-product (`revit-mcp-spill`, `autocad-mcp-spill`)
to avoid collisions when both servers run on one machine.

## Forward compatibility

- New fields **may** be added to `params` and `data`. Receivers should
  ignore unknown fields, not error.
- `error.code` is an open enum — new codes may appear. Servers should
  treat unknown codes the same as `INTERNAL_ERROR`.
- `command` names are stable per major version. Renames require a v2.

## Health probe

```bash
$ curl -s http://127.0.0.1:8181/
{"status":"ok","server":"revit-mcp-plugin"}
```

Used by `scripts/test-ws.js`, the regression verifier, and as a
reachability check before opening a WebSocket.

## Reference implementations

- TypeScript client: [`packages/mcp-cad-core/src/services/websocket-client.ts`](../packages/mcp-cad-core/src/services/websocket-client.ts)
- C# server (Revit): [`plugin/RevitMCPPlugin/WebSocketServer.cs`](../plugin/RevitMCPPlugin/WebSocketServer.cs)
- Command interface (Revit): [`commandset/Interfaces/IRevitCommand.cs`](../commandset/Interfaces/IRevitCommand.cs)
- Smoke client (Node, no deps): [`scripts/test-ws.js`](../scripts/test-ws.js)
- End-to-end regression: [`scripts/verify-server-shim.mjs`](../scripts/verify-server-shim.mjs)
