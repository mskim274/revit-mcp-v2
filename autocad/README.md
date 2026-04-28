# AutoCAD MCP

Model Context Protocol server for Autodesk AutoCAD. Sister project to
`server/` (Revit MCP), sharing
[`@kimminsub/mcp-cad-core`](../packages/mcp-cad-core/) for the WebSocket
client, response-formatter overflow spill, pagination, and protocol types.

> **Status:** Phase 4 MVP — 1 tool (`cad_ping`). Builds clean against
> AutoCAD 2025. Manual NETLOAD verified separately. More tools land in
> Phase 5.

## Architecture

Same 3-layer shape as Revit MCP:

```
Claude Desktop ──stdio──▶ autocad/server/ (TypeScript MCP server)
                              │
                         WebSocket :8182
                              │
                        autocad/plugin/ (C# IExtensionApplication)
                              │
                        autocad/commandset/ ── reflection auto-discovery
                              │
                       AutoCAD .NET API (acdbmgd, acmgd, accoremgd)
```

Key differences from Revit:

| | Revit | AutoCAD |
|---|---|---|
| Main-thread marshaling | `Revit.Async` (3rd-party NuGet) | `Application.DocumentManager.ExecuteInCommandContextAsync` (built-in) |
| Read transactions | Not required | Required (`tr.GetObject(id, OpenMode.ForRead)`) |
| Loading | `revit.addin` XML manifest | `NETLOAD` command or autoloader bundle (`*.bundle/PackageContents.xml`) |
| Default WS port | 8181 | 8182 |

## Build

```bash
# TypeScript server (from repo root — uses npm workspaces)
npm run build:autocad

# C# plugin (requires AutoCAD 2025 installed at default path,
# or set AUTOCAD_2025_PATH env var)
dotnet build autocad/AutoCADMCP.sln -c Release
```

## Loading the plugin into AutoCAD (Phase 4 — manual NETLOAD)

1. Open AutoCAD 2025 with any drawing (`acad.exe`).
2. At the command line, type `NETLOAD` and press Enter.
3. Browse to:
   ```
   <repo>\autocad\plugin\AutoCADMCPPlugin\bin\Release\net8.0-windows\AutoCADMCPPlugin.dll
   ```
4. The command line should print:
   ```
   [AutoCADMCP] WebSocket server listening on :8182
   ```
5. Verify externally:
   ```bash
   curl http://127.0.0.1:8182/
   # → {"status":"ok","server":"autocad-mcp-plugin"}
   ```

A proper autoloader bundle (`%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCP.bundle\`)
with `PackageContents.xml` arrives in Phase 5 — same auto-update flow as
the Revit plugin, routed through `RevitMCPUpdater.exe --product autocad`.

## Smoke test (no AutoCAD needed)

```bash
node autocad/scripts/smoke-test-server.mjs
```

Verifies that the rebuilt server loads, `AcadWebSocketClient` instantiates,
`sendAndFormat` resolves, and `connect()` fails cleanly when the plugin
isn't there. Does NOT require AutoCAD to be running.

## End-to-end test (AutoCAD must be loaded with NETLOAD)

```bash
# Direct WebSocket probe (no MCP layer)
node scripts/test-ws.js ping       # → uses port 8181 (Revit). For :8182, use:
REVIT_MCP_PORT=8182 node scripts/test-ws.js ping
```

> ⚠️ `scripts/test-ws.js` reads `REVIT_MCP_PORT` (legacy name) and is
> currently shared between products. A cleaner `cad-mcp` aware probe
> lands in Phase 5.

## Why this lives inside the Revit MCP repo (for now)

Phase 4 places `autocad/` as a sibling workspace inside `revit-mcp-v2`
so the AutoCAD server can directly resolve the `@kimminsub/mcp-cad-core`
workspace symlink — no `npm publish` ceremony, no cross-repo `file:` deps.

Once `mcp-cad-core` is published to npm and the AutoCAD MCP has
stabilized (Phase 5–6), the `autocad/` directory will be lifted into
its own [`autocad-mcp-v2`](https://github.com/mskim274/autocad-mcp-v2)
repository via `git filter-repo`. The TypeScript server will switch to
a regular published-package dependency at that point.

## Commands

| Wire name | MCP tool name | Status |
|---|---|---|
| `ping` | `cad_ping` | Phase 4 |
| `get_drawing_info` | `cad_get_drawing_info` | Phase 5 |
| `get_layers` | `cad_get_layers` | Phase 5 |
| `query_entities` | `cad_query_entities` | Phase 5 |
| `create_line` | `cad_create_line` | Phase 5 |
| `extract_schedule` | `cad_extract_schedule` | Phase 6 |

## Layout

```
autocad/
├── AutoCADMCP.sln                 ← Solution (commandset + plugin)
├── README.md                      ← This file
├── server/                        ← TS MCP server (npm workspace)
│   ├── package.json               ← @kimminsub/autocad-mcp
│   └── src/
│       ├── index.ts               ← Entry point
│       ├── constants.ts           ← AutoCAD-specific (port 8182, log prefix)
│       ├── services/
│       │   ├── websocket-client.ts  ← AcadWebSocketClient (extends core)
│       │   └── response-formatter.ts
│       └── tools/
│           └── utility.ts          ← cad_ping
├── commandset/                    ← C# command implementations
│   ├── CommandSet.csproj
│   ├── Interfaces/
│   │   └── ICadCommand.cs         ← Mirrors IRevitCommand
│   └── Commands/
│       └── PingCommand.cs
├── plugin/                        ← C# Add-in (IExtensionApplication)
│   └── AutoCADMCPPlugin/
│       ├── AutoCADMCPPlugin.csproj
│       ├── Application.cs         ← AcadMCPApp
│       ├── AcadWebSocketServer.cs ← :8182 + main-thread marshal
│       └── CommandDispatcher.cs   ← Reflection-based discovery
└── scripts/
    └── smoke-test-server.mjs      ← TS-only smoke test
```
