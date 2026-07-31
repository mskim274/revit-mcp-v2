# AutoCAD MCP

Model Context Protocol bridge for Autodesk AutoCAD 2025. It shares the
[`@kimminsub/mcp-cad-core`](../packages/mcp-cad-core/) transport,
pagination, and response-safety layer with the Revit MCP server.

The current AutoCAD surface contains 10 tools:

- Utility: `cad_ping`
- Drawing/query: `cad_get_drawing_info`, `cad_get_layers`,
  `cad_query_entities`, `cad_extract_table`
- Selection: `cad_get_selected_entities`, `cad_get_selection_texts`,
  `cad_get_selection_dimensions`, `cad_parse_grid_schedule`
- Create: `cad_create_line`

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

## Requirements

- AutoCAD 2025
- .NET 8 SDK
- Node.js 20 or newer

Only AutoCAD 2025 is currently built and tested. Do not assume binary
compatibility with another AutoCAD release.

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

There is not yet a public AutoCAD installer. The AutoCAD TypeScript server is
also a private workspace package for now; it is built and package-tested in CI
but is not published to npm. For local development:

1. Run `NETLOAD` in AutoCAD 2025.
2. Select
   `autocad\plugin\AutoCADMCPPlugin\bin\Release\net8.0-windows\AutoCADMCPPlugin.dll`.
3. Start the TypeScript MCP server with
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

## Verification

Tests that do not require AutoCAD:

```powershell
npm test
dotnet build autocad\AutoCADMCP.sln -c Release
```

The npm test suite builds all workspaces, checks tool contracts and transport
behavior, and installs each packed npm artifact into a clean consumer.

## Safety notes

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
