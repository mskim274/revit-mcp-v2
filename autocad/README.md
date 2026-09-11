# AutoCAD MCP

Model Context Protocol bridge for Autodesk AutoCAD 2025. It shares the
[`@kimminsub/mcp-cad-core`](../packages/mcp-cad-core/) transport,
pagination, and response-safety layer with the Revit MCP server.

The current AutoCAD surface contains 15 tools:

- Utility: `cad_ping`
- Drawing/query: `cad_get_drawing_info`, `cad_get_layers`,
  `cad_query_entities` (current/model/all-paper space scope), `cad_extract_table`
- Selection: `cad_get_selected_entities`, `cad_get_selection_texts`,
  `cad_get_selection_dimensions`, `cad_parse_grid_schedule`
- Create: `cad_create_line`, `cad_create_entities`
- Blocks: `cad_blocks` (`list` or batch `insert` of loaded definitions)
- Modify: `cad_modify_entities`
- Export: `cad_plot_pdf` (current or exact-named layout)
- Script escape hatch: `cad_execute_script`

## Architecture

```text
MCP client ──stdio──> autocad/server (TypeScript)
                           |
                      WebSocket :8182
                           |
                    AutoCAD plugin (C#)
                           |
                    CommandSet (C#)
                           |
                    AutoCAD .NET API
```

AutoCAD API work is marshalled with
`Application.DocumentManager.ExecuteInCommandContextAsync`. Commands use the
transaction supplied by the plugin, which commits successful results and
aborts failures.

The host owns transaction cleanup through `TransactionBoundary`. Query scripts
still abort on success; execution failures roll back through `Dispose` without
calling `Abort` on a disposed native handle. Original exceptions are recorded
before cleanup in `%LOCALAPPDATA%\AutoCADMCP\logs\errors-<PID>.jsonl` (2 MiB
rotation, one previous file per process). Logs are local and may contain paths
from exception messages; do not publish them without review. Failed cleanup
returns `TRANSACTION_CLEANUP_ERROR` instead of claiming a confirmed rollback.
This host change requires an AutoCAD restart, not a Revit restart.

Transaction-lifecycle regression tests, without launching CAD:

```powershell
dotnet run --project tests/AutoCadTransactionSmoke -c Release
```

## Requirements

- AutoCAD 2025
- .NET 8 SDK
- Node.js 20 or newer

Only AutoCAD 2025 is currently built and tested. Do not assume binary
compatibility with another AutoCAD release.

This is intentionally a `net8.0-windows` / AutoCAD 2025 SDK build, not a
claimed AutoCAD 2027 binary. Autodesk's
[managed .NET compatibility table](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)
lists AutoCAD 2027 with the AutoCAD 2027 SDK and .NET 10; Autodesk also
[documents the .NET 10 transition in AutoCAD 2026.1.2](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-450FD531-B6F6-4BAE-9A8C-8230AAC48CB4.htm).
A supported 2027 build therefore needs a separately tested `net10.0-windows`
shell against the 2027 SDK, not a TargetFramework label change alone.

## Build

From the repository root:

```powershell
npm ci --workspaces --include-workspace-root
npm run build:autocad

# Uses installed AutoCAD 2025 assemblies when present. CI falls back to
# Autodesk's compile-only AutoCAD.NET NuGet package.
$env:AUTOCAD_2025_PATH = "C:\Program Files\Autodesk\AutoCAD 2025"
dotnet build autocad\AutoCADMCP.sln -c Release
```

## Load and connect

Script execution loads Roslyn in a private `AssemblyLoadContext`, because
AutoCAD 2025 may provide older `Microsoft.CodeAnalysis` assemblies. Deploy the
build's **script-engine/** subfolder along with the host and CommandSet. It
contains `AutoCADMCP.ScriptEngine.dll` and its four Roslyn DLLs. Do not replace
DLLs in the Autodesk installation directory. The host/CommandSet has no direct
Roslyn reference; Autodesk API objects and command contracts retain host type
identity, including in Roslyn's generated submissions. This is dependency
version isolation, not a security sandbox. Updating these binaries requires
AutoCAD to be closed; do not attempt to NETLOAD a second host into the same
process.

An offline regression test loads the installed host's Roslyn 4.0 alongside
the private Roslyn 4.9. It uses CAD type stubs, so it does not replace live CAD
verification:

```powershell
dotnet run --project tests/AutoCadScriptIsolationSmoke -c Release -- "C:\Program Files\Autodesk\AutoCAD 2025"
```

There is not yet a public AutoCAD installer. The AutoCAD TypeScript server is
also a private workspace package for now; it is built and package-tested in CI
but is not published to npm. For local development:

1. Run `NETLOAD` in AutoCAD 2025.
2. Select
   `autocad\plugin\AutoCADMCPPlugin\bin\Release\net8.0-windows\AutoCADMCPPlugin.dll`.
3. Point the MCP client at `autocad/server/dist/index.js` (do not use
   `CadMCPServer.exe`). Grok CLI / Orca read `.grok/config.toml`; Claude Code
   / Codex read `.mcp.json`. A manual stdio launch is
   `node autocad\server\dist\index.js`.

The plugin listens only on loopback and defaults to `127.0.0.1:8182`.
WebSocket upgrades require the shared local bearer token stored at
`%LOCALAPPDATA%\RevitMCP\auth-token`. The TypeScript server reads this file
automatically; `REVIT_MCP_AUTH_TOKEN` can override it for both CAD bridges.

Direct authenticated probe:

```powershell
$env:MCP_PORT = "8182"
node scripts\test-ws.js ping
```

Use `AUTOCAD_MCP_PORT` to change both the plugin listener and the TypeScript
client port. It must be an integer from 1 to 65535 and must be set before
starting AutoCAD and the MCP server. Set `MCP_PORT` to the same value when
using the shared direct-probe script.

```powershell
$env:AUTOCAD_MCP_PORT = "8282"
# Start AutoCAD and the TypeScript MCP server from this environment.
```

## Verification

Tests that do not require AutoCAD:

```powershell
npm test
dotnet build autocad\AutoCADMCP.sln -c Release
```

The npm test suite builds all workspaces, checks tool contracts and transport
behavior, and installs each packed npm artifact into a clean consumer.

## Safety notes

- `cad_execute_script` is disabled unless AutoCAD starts with
  `AUTOCAD_MCP_ENABLE_SCRIPT=1`. Its denylist is not a security sandbox, and
  unlike Revit there is no per-execution UI approval dialog.
- `cad_create_line` accepts an `idempotency_key`; the TypeScript bridge
  generates one when omitted, and identical retries return the cached
  committed result.
- A timed-out side-effect has an uncertain outcome from the MCP client's point
  of view. Verify the drawing and reuse the exact key returned in the MCP
  error for an identical retry.
- Keep customer drawing names, handles, extracted schedules, and ad-hoc
  reconciliation scripts out of public commits.

See the repository [security policy](../SECURITY.md) and
[contribution guide](../CONTRIBUTING.md) before publishing changes.
