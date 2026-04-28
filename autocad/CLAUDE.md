# AutoCAD MCP — Agent Instructions

Sister to the parent `CLAUDE.md` (Revit MCP). Read that first — everything
about the MCP↔WebSocket↔plugin architecture, harness engineering, and
naming conventions transfers directly. This file only documents the
AutoCAD-specific deltas.

## What's the same as Revit MCP

- 3-layer architecture (TS server → WebSocket → C# plugin → CommandSet).
- Reflection-based command discovery (`CommandDispatcher`).
- WebSocket envelope (`CommandRequest` / `CommandResponse`) — see
  [`../protocol/WIRE_PROTOCOL.md`](../protocol/WIRE_PROTOCOL.md).
- Response-formatter overflow spill (25 KB / 500 KB), pagination cursors,
  log-prefix conventions — all inherited from `@kimminsub/mcp-cad-core`.
- Updater binary (`RevitMCPUpdater.exe`) — pass `--product autocad
  --bundle-name <name>` to route to the AutoCAD bundle folder.

## What's different

### Threading & transactions

- **No `Revit.Async` dependency.** AutoCAD's .NET API ships with
  `Application.DocumentManager.ExecuteInCommandContextAsync` (since 2016)
  which is the same idea: marshal a delegate onto the document's main
  thread. Used by `AcadWebSocketServer.DispatchSafely`.
- **Read transactions are required.** Even pure queries must use
  `tr.GetObject(id, OpenMode.ForRead)` inside a `StartTransaction()` block.
  The dispatcher gives every command an already-open `Transaction`; commit
  happens automatically after `ExecuteAsync` returns. Mutations should
  start their own nested transaction (or commit the supplied one and
  return).

### Loading

- **NETLOAD (manual)** — `Acad command line: NETLOAD` then browse to
  `AutoCADMCPPlugin.dll`. Loads in current session only.
- **Autoloader bundle (auto)** — drop a `<name>.bundle/` folder into
  `%APPDATA%\Autodesk\ApplicationPlugins\` containing a
  `PackageContents.xml` manifest. AutoCAD scans this folder on startup
  and loads matching versions. The auto-update flow lands in Phase 5.
- **No GUID in the manifest** the way Revit's `.addin` requires — but
  bundle name uniqueness still matters (the assembly's GUID
  `9D1A7F0D-64F2-46A9-BF8A-E37D608EB229` was issued fresh, do not reuse).

### `Exception` namespace shadow

`using Autodesk.AutoCAD.Runtime;` introduces an `Exception` type that
collides with `System.Exception` in catch blocks. The plugin keeps the
attribute fully-qualified
(`[assembly: Autodesk.AutoCAD.Runtime.ExtensionApplication(...)]`) and
omits the `using` so catches resolve to `System.Exception` cleanly.

### Default port

- Revit: `8181` (env `REVIT_MCP_PORT`)
- AutoCAD: `8182` (env `AUTOCAD_MCP_PORT`)

Both servers can run simultaneously on the same machine.

## Adding a new AutoCAD command

Same three-file recipe as Revit (see
[`../protocol/COMMAND_AUTHORING.md`](../protocol/COMMAND_AUTHORING.md))
with two adjustments:

1. C# class implements `ICadCommand` (not `IRevitCommand`) and lives at
   `autocad/commandset/Commands/<Name>Command.cs`.
2. ExecuteAsync signature takes `(Database db, Transaction tr,
   Dictionary<string, object> parameters, CancellationToken ct)`. The
   `tr` is supplied open by the dispatcher — for reads, just use it. For
   mutations, prefer starting your own nested transaction so partial
   success is contained.

TypeScript tool registration is identical (`cad_` prefix instead of
`revit_`, register from `autocad/server/src/tools/<category>.ts`).

## Build & test

```bash
# TS workspace (from repo root)
npm run build:autocad

# C# plugin
dotnet build autocad/AutoCADMCP.sln -c Release

# Smoke test (no AutoCAD needed — verifies imports + client wiring)
node autocad/scripts/smoke-test-server.mjs

# End-to-end (AutoCAD must be running with the plugin NETLOADed)
AUTOCAD_MCP_PORT=8182 node scripts/test-ws.js ping
```

## Known pitfalls (specific to AutoCAD)

- **`acmgd` / `acdbmgd` / `accoremgd` are mixed-mode assemblies.** The
  csproj sets `<Private>false</Private>` so they don't get copied into
  the output. AutoCAD itself loads them at runtime. MSBuild emits MSB3277
  warnings about transitive references — ignore.
- **`Editor.WriteMessage` may throw during plugin Initialize** if the
  document hasn't fully come up. `AcadMCPApp.WriteToEditor` catches all
  exceptions silently.
- **Bundle name collisions** — AutoCAD scans
  `%APPDATA%\Autodesk\ApplicationPlugins\` for every `*.bundle/` folder.
  If two bundles export the same command name (`MCPPING`), behavior is
  undefined. Keep the bundle folder name unique to the product.
