# MCP↔CAD WebSocket Wire Protocol v1

This is the contract between the **TypeScript MCP server** (in `server/`)
and the **CAD plugin** (`plugin/RevitMCPPlugin/` or
`autocad/plugin/AutoCADMCPPlugin/`). Both ends must agree on this format. The shared runtime
implementation lives in [`@kimminsub/mcp-cad-core`](../packages/mcp-cad-core/).

> **Stability**: this is v1. Breaking changes require a `v2` document and
> a coordinated bump on both sides. Adding new optional fields is OK.

## Transport

- **WebSocket** over plain HTTP, `ws://127.0.0.1:<port>/`
- Default ports: `8181` (first Revit) and `8182` (AutoCAD). Additional Revit
  processes with no explicit port override scan `8183` through `8199`.
- Localhost-only by design. WebSocket upgrades require
  `Authorization: Bearer <token>`. The plugin creates a 256-bit token at
  `%LOCALAPPDATA%\RevitMCP\auth-token`, unless
  `REVIT_MCP_AUTH_TOKEN` is set. Both TypeScript servers use the same
  credential. Plain HTTP is acceptable only because the listener binds to
  `127.0.0.1`; the plugin must reject non-loopback connections.
- Browser-originated upgrades are rejected unless the origin policy allows
  them. Native MCP clients normally omit `Origin`.
- Plugin also exposes an authenticated `GET /` returning
  `{"status":"ok","server":"<name>"}` for health probes.
- One connection per MCP server process. Reconnect on close.

## Message envelope

Every logical message is one UTF-8 JSON object. WebSocket transport
fragments are reassembled by the plugin. A message larger than 16 MiB is
rejected.

### Request (server → plugin)

```jsonc
{
  "id": "uuid-v4",          // client-generated, echoed in response
  "command": "query_elements",
  "params": { "category": "Walls", "summary_only": true },
  "timeout_ms": 30000,       // hint; plugin may enforce its own ceiling
  "target_session_id": "...",              // optional multi-host guard
  "expected_document_fingerprint": "..."   // optional active-document guard
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
- Non-empty string, at most 512 characters. The TypeScript server generates
  a UUID v4 and the plugin echoes it.
- The plugin uses it as a last-resort idempotency key for a raw WebSocket
  caller. The official TypeScript bridge instead injects a separate explicit
  key for every side effect when the MCP caller omits one.

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
- Bounded numeric fields receive their default only when the property is
  absent. An explicitly supplied `null`, fractional value, non-finite value,
  or out-of-range value is a validation error.

### `timeout_ms`
- Integer from 1 through 600,000 milliseconds. Both plugins enforce it with
  cooperative cancellation; commands must observe the supplied token.
- Server clears its own pending timer at this value and returns a
  synthetic `TIMEOUT_ERROR` if the plugin hasn't replied.
- A client-side timeout on a side effect is an unknown outcome. Verify the
  model or drawing, then reuse the exact same non-empty
  `idempotency_key` only for an identical retry. Synthetic timeout and
  connection errors include the effective key in `error.idempotency_key`.

### `target_session_id` and `expected_document_fingerprint`

- Optional, backward-compatible top-level routing guards. They are not command
  parameters and therefore do not alter the command's business payload.
- When either is supplied to the Revit plugin, both must be supplied. The
  session id identifies one Revit process; the SHA-256 document fingerprint
  pins the active document that was selected by the MCP session router.
- Revit validates both values on its main thread immediately before command
  dispatch. A process restart, target change, active-document switch, close/
  reopen, or Save As causes a fail-closed error before any mutation.
- AutoCAD and older plugins may ignore these optional top-level fields under
  the forward-compatibility rule.
- When no Revit registry record is discoverable, the router first sends a
  read-only legacy-port ping. It forwards the requested command only if that
  response has no session identity (a genuine pre-session plugin); otherwise
  it fails closed until the registry is restored.

### `data`
- Command-specific payload on success. Always an object, never raw scalars.
- Pagination follows the `PaginatedResult<T>` shape from `types.ts`.
- Summary mode follows `SummaryResult` shape (counts + by_type + by_level).
- Revit `ElementId` values outside JavaScript's safe-integer range are
  serialized as decimal strings. Inputs accept a safe positive integer or a
  signed-64-bit positive decimal string.

### `error.code`

| Code              | Meaning                                                     |
|-------------------|-------------------------------------------------------------|
| `CONNECTION_ERROR`| TCP/WS layer broke; an in-flight side effect is an unknown outcome. |
| `TIMEOUT_ERROR`   | Client or plugin timeout; side effects may need verification.|
| `SERVER_SHUTDOWN` | Plugin is stopping; reconnect after the host restarts.       |
| `REVIT_API_ERROR` | Revit API threw. Often retry-safe; `recoverable` flag tells.|
| `CAD_API_ERROR`   | AutoCAD command/API failure.                                |
| `VALIDATION_ERROR`| Bad params. NOT retry-safe without fixing the call.         |
| `IDEMPOTENCY_CONFLICT` | Key reused for a different request.                    |
| `TARGET_SELECTION_REQUIRED` | Multiple live hosts exist and no target is pinned. |
| `SESSION_NOT_FOUND` | The selected local host session is no longer discoverable. |
| `TARGET_SESSION_MISMATCH` | Request reached a different host process than selected. |
| `TARGET_DOCUMENT_MISMATCH` | The host's active document changed after selection. |
| `INTERNAL_ERROR`  | Bug in plugin. Report.                                      |

Product-specific codes are intentional: Revit uses `REVIT_API_ERROR`,
while AutoCAD uses `CAD_API_ERROR`.

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

Each plugin holds a bounded, 15-minute cache of successful side-effect
responses. Entries are scoped to the active document/drawing. A logical
key is bound to the command and a canonical parameter hash; reusing it for
different work is a validation conflict. An identical replay returns the
cached data with the current request `id`, without another CAD API call.

The request `id` is only a single-call fallback for raw WebSocket clients.
The official TypeScript bridge generates an explicit UUID key when an MCP
side-effect call omits one. If transport failure or timeout makes the outcome
uncertain, the MCP error returns that effective key so an agent can verify
state and reuse it for one identical retry. A caller-supplied key must be a
stable, non-empty string and must never be reused for different work. Explicit
`null`, empty, or non-string keys are validation errors. Side-effect
classification covers create/modify/delete and other model, UI, export, or
script operations; it is not limited to a short prefix list.

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

- New optional fields **may** be added to the request envelope, `params`, and
  `data`. Receivers should ignore unknown fields, not error.
- `error.code` is an open enum — new codes may appear. Servers should
  treat unknown codes the same as `INTERNAL_ERROR`.
- `command` names are stable per major version. Renames require a v2.

## Health probe

```powershell
$token = Get-Content "$env:LOCALAPPDATA\RevitMCP\auth-token" -Raw
curl.exe -s -H "Authorization: Bearer $($token.Trim())" http://127.0.0.1:8181/
{"status":"ok","server":"revit-mcp-plugin"}
```

Used as an authenticated reachability check. Health and WebSocket requests
share the same origin and bearer-token boundary.

## Reference implementations

- TypeScript client: [`packages/mcp-cad-core/src/services/websocket-client.ts`](../packages/mcp-cad-core/src/services/websocket-client.ts)
- C# server (Revit): [`plugin/RevitMCPPlugin/WebSocketServer.cs`](../plugin/RevitMCPPlugin/WebSocketServer.cs)
- C# server (AutoCAD): [`autocad/plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs`](../autocad/plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs)
- Command interface (Revit): [`commandset/Interfaces/IRevitCommand.cs`](../commandset/Interfaces/IRevitCommand.cs)
- Authenticated direct client: [`scripts/test-ws.js`](../scripts/test-ws.js)
