# Revit MCP v2

[![Release](https://img.shields.io/github/v/release/mskim274/revit-mcp-v2?label=release)](https://github.com/mskim274/revit-mcp-v2/releases/latest)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](#license)

Model Context Protocol server for Autodesk Revit. Lets Claude (and other MCP
clients) read, query, create, and modify elements in a running Revit
session through a natural-language interface — tested on projects up to
~500,000 elements.

> **Status**: v0.3.0 — 20 tools across Utility / Query / Create / Modify /
> View. Harness Engineering Tier 1 safeguards (idempotency cache,
> post-transaction verification, response-size overflow spill) are live.
> Auto-update delivered via GitHub Releases + a bundled one-click installer.

---

## Architecture

```
Claude Desktop ──stdio──▶ MCP Server (TypeScript, Node.js)
                              │
                         WebSocket :8181
                              │
                        Revit Plugin (C#, WPF, .NET 8)
                              │
                        CommandSet (C#) ─ reflection-discovered Revit API calls
                              │
                        Revit 2025 (+ 2023 via net48 target)
```

- **MCP Server** — speaks MCP over stdio to Claude Desktop. Zod-validates
  tool parameters, paginates large responses, spills oversized payloads
  to disk (25 KB soft / 500 KB hard limit).
- **Revit Plugin** — runs inside Revit as an `IExternalApplication`. Owns
  the WebSocket server, an idempotency cache (15 min TTL) for
  side-effect commands, and the self-update dialog.
- **CommandSet** — one `IRevitCommand` per file. Add a new file → the
  plugin's reflection-based dispatcher picks it up on next restart. No
  manual registration.
- **Updater** — standalone `.exe` that survives Revit shutdown and
  extracts downloaded plugin zips into the Addins folder.

---

## Installation

### 1. Plugin (inside Revit)

1. Download the latest **`RevitMCPPlugin-<ver>-Revit2025.zip`** from
   [Releases](https://github.com/mskim274/revit-mcp-v2/releases/latest).
2. Close Revit if it's open.
3. Extract all four files (`RevitMCPPlugin.dll`, `RevitMCP.CommandSet.dll`,
   `Revit.Async.dll`, `revit-mcp.addin`) to:
   ```
   %APPDATA%\Autodesk\Revit\Addins\2025\
   ```
4. Start Revit and open any project. The plugin starts a WebSocket
   server on `http://127.0.0.1:8181/`.
5. Future updates are delivered through the in-Revit dialog — click
   **"⬇ 다운로드 및 설치"** and the bundled updater handles the rest.

### 2. MCP Server (for Claude Desktop)

#### From source (current official path)

```powershell
git clone https://github.com/mskim274/revit-mcp-v2.git
cd revit-mcp-v2\server
npm install
npm run build
```

Then add to your Claude Desktop config
(`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": ["C:\\Users\\YOU\\Desktop\\revit-mcp-v2\\server\\dist\\index.js"],
      "env": { "REVIT_MCP_PORT": "8181" }
    }
  }
}
```

Restart Claude Desktop.

#### npm (planned — Phase P2)

Once published, this will become a single-line install:

```json
{
  "mcpServers": {
    "revit": { "command": "npx", "args": ["-y", "@kimminsub/revit-mcp"] }
  }
}
```

### 3. Verify

Ask Claude in Revit: *"Call revit_ping."* — you should get back the
Revit version, document name, and element count.

---

## Tool Inventory (20)

| Category | Tools |
|---|---|
| **Utility** (2) | `revit_ping`, `revit_get_project_info` |
| **Query** (8) | `revit_get_levels`, `revit_get_views`, `revit_get_grids`, `revit_query_elements`, `revit_get_element_info`, `revit_get_types_by_category`, `revit_get_family_types`, `revit_get_all_categories` |
| **Create** (2) | `revit_create_wall`, `revit_create_floor` |
| **Modify** (4) | `revit_modify_element_parameter`, `revit_delete_elements`, `revit_move_elements`, `revit_copy_elements` |
| **View** (4) | `revit_set_active_view`, `revit_isolate_elements`, `revit_reset_view_isolation`, `revit_select_elements` |

See [`CLAUDE.md`](CLAUDE.md) for parameters, validation rules, and
testing notes.

---

## Supported Revit versions

| Revit | Target framework | CI build | Local build |
|---|---|---|---|
| 2025+ | `net8.0-windows` | ✅ | ✅ |
| 2023 / 2024 | `net48` | ❌ (Nice3point package gap) | ✅ with `REVIT_2023_PATH` set |

End users on Revit 2023/2024 currently need to build locally. Re-enabling
CI builds for Revit 2023 is tracked as a future item.

---

## Contributing / extending

New commands are small, self-contained units. To add one:

1. Create `commandset/Commands/<Category>/<Name>Command.cs` implementing
   `IRevitCommand`. Wrap any mutations in a `Transaction`.
2. Add a `registerTool(...)` call in the matching
   `server/src/tools/<category>.ts` file. Shape the Zod schema to mirror
   your C# parameters.
3. Build + deploy. The dispatcher finds the new command by reflection —
   no registry edit needed.

Detailed conventions (naming, safety, transaction discipline, known
pitfalls) are in [`CLAUDE.md`](CLAUDE.md).

For release mechanics, see [`CHANGELOG.md`](CHANGELOG.md).

---

## License

MIT — see the full text at the repository root (coming with Phase P2).

---

## Credits

- Update-notification pattern modeled on
  [RevitLookup](https://github.com/lookup-foundation/RevitLookup)'s
  `SoftwareUpdateService`.
- Revit API NuGet stubs for CI builds courtesy of
  [Nice3point.Revit.Api](https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPI).
- Thread-safe Revit API bridging via
  [Revit.Async](https://github.com/KennanChan/Revit.Async).
