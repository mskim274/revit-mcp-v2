# Revit MCP v2

[![CI](https://github.com/mskim274/revit-mcp-v2/actions/workflows/ci.yml/badge.svg)](https://github.com/mskim274/revit-mcp-v2/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/mskim274/revit-mcp-v2?label=release)](https://github.com/mskim274/revit-mcp-v2/releases/latest)
[![npm](https://img.shields.io/npm/v/@kimminsub/revit-mcp.svg)](https://www.npmjs.com/package/@kimminsub/revit-mcp)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Model Context Protocol server for Autodesk Revit. It lets MCP clients such as
Codex and Claude query, create, modify, review, and export model data in a
running Revit session.

The current `main` branch registers **32 tools**. The latest stable release may
contain fewer tools; see [Releases](https://github.com/mskim274/revit-mcp-v2/releases)
and [CHANGELOG.md](CHANGELOG.md) for version-specific contents.

## Architecture

```text
MCP client ──stdio──▶ MCP Server (TypeScript, Node.js)
                           │
                      WebSocket :8181
                           │
                     Revit Plugin (C#, WPF)
                           │
                     Revit.Async
                           │
                     CommandSet (Revit API)
```

- **MCP Server** validates tool inputs, handles pagination, and limits response
  size.
- **Revit Plugin** owns the loopback WebSocket endpoint, command dispatch,
  idempotency cache, and update notification.
- **CommandSet** contains reflection-discovered Revit API commands.
- **Updater** waits for Revit to close before replacing locked add-in files.

## Install

Two components are required: the C# Revit add-in and the TypeScript MCP server.

### 1. Revit 2025 add-in

1. Download `RevitMCPPlugin-<version>-Revit2025.zip` and, when present,
   `SHA256SUMS.txt` from the
   [latest GitHub Release](https://github.com/mskim274/revit-mcp-v2/releases/latest).
   A release without checksums and an artifact attestation predates the
   hardened release workflow; prefer a newer verified release or build from
   the reviewed source.
2. Verify the archive as described in [Release verification](#release-verification).
3. Close Revit.
4. Extract **all files** from the plugin ZIP into:

   ```text
   %APPDATA%\Autodesk\Revit\Addins\2025\
   ```

5. Start Revit and open or create a project. The add-in listens on loopback
   port 8181. On first start it creates a local bearer token at
   `%LOCALAPPDATA%\RevitMCP\auth-token`; the npm server reads it
   automatically.

The archive contains the add-in manifest, plugin assemblies, runtime
dependencies, `.deps.json`, `RevitMCP.LICENSE.txt`,
`RevitMCP.THIRD-PARTY-NOTICES.md`, and `RevitMCP.release-manifest.json`. Do
not copy only the three primary DLLs.

### 2. MCP server from npm

Add this entry to the MCP client configuration:

```json
{
  "mcpServers": {
    "revit": {
      "command": "npx",
      "args": ["-y", "@kimminsub/revit-mcp@latest"],
      "env": {
        "REVIT_MCP_HOST": "127.0.0.1",
        "REVIT_MCP_PORT": "8181"
      }
    }
  }
}
```

Restart the MCP client after changing its configuration.

### 3. MCP server from source

Run workspace commands from the repository root so the shared core builds
before the Revit server:

```powershell
git clone https://github.com/mskim274/revit-mcp-v2.git
cd revit-mcp-v2
npm ci --workspaces --include-workspace-root
npm run build
```

Then point the MCP client at the absolute path to `server/dist/index.js`.

### 4. Verify

Open a Revit project and call `revit_ping`. A successful response includes the
Revit build and current document information.

## Tool inventory (32)

| Category | Count | Tools |
|---|---:|---|
| Utility | 2 | `revit_ping`, `revit_get_project_info` |
| Query | 10 | `revit_get_levels`, `revit_get_views`, `revit_get_grids`, `revit_query_elements`, `revit_get_element_info`, `revit_get_element_geometry`, `revit_get_selected_elements`, `revit_get_types_by_category`, `revit_get_family_types`, `revit_get_all_categories` |
| Create | 3 | `revit_create_wall`, `revit_create_floor`, `revit_create_pipe_run` |
| Modify | 8 | `revit_modify_element_parameter`, `revit_batch_modify_parameters`, `revit_delete_elements`, `revit_move_elements`, `revit_copy_elements`, `revit_duplicate_type`, `revit_rename_type`, `revit_change_instance_type` |
| View | 5 | `revit_set_active_view`, `revit_isolate_elements`, `revit_reset_view_isolation`, `revit_select_elements`, `revit_duplicate_views` |
| Export | 1 | `revit_export_schedule` |
| Visualize / Review | 2 | `revit_apply_color_filter`, `revit_tag_by_filter` |
| Script | 1 | `revit_execute_script` |

`revit_execute_script` is an advanced escape hatch, not a security sandbox.
It is disabled unless `REVIT_MCP_ENABLE_SCRIPT=1` and every execution requires
approval in Revit. Review script requests and mutation mode before use.

## Supported Revit versions

| Revit | Target framework | CI compile gate | Prebuilt release |
|---|---|---:|---:|
| 2025 | `net8.0-windows` | Yes | Yes |
| 2023 / 2024 | `net48` | Yes | No |

Revit 2023/2024 users currently build from source with a compatible local
Revit installation. The public release ZIP targets Revit 2025 exactly.
Revit 2026 and later are not supported until an API-specific build, release
asset, and test gate are added for each year.

## Release verification

For a release produced by the hardened workflow, compare the downloaded ZIP
hash with its `SHA256SUMS.txt`:

```powershell
Get-FileHash .\RevitMCPPlugin-<version>-Revit2025.zip -Algorithm SHA256
```

Also verify its GitHub provenance:

```powershell
gh attestation verify .\RevitMCPPlugin-<version>-Revit2025.zip `
  --repo mskim274/revit-mcp-v2
```

Each ZIP contains `RevitMCP.LICENSE.txt`,
`RevitMCP.THIRD-PARTY-NOTICES.md`, and `RevitMCP.release-manifest.json` with
the expected size and SHA-256 of every packaged file. The legal files are
product-prefixed to avoid collisions in the shared Revit Addins directory.
The validator also confirms that they exactly match the repository-root
`LICENSE` and `THIRD-PARTY-NOTICES.md` files.

## Security

This add-in can modify a live model and runs with the current user's
permissions. Keep the WebSocket endpoint on loopback, protect the generated
bearer token, use recoverable model backups, and do not publish logs or spill
files containing project data. If `REVIT_MCP_AUTH_TOKEN` is configured
manually, the plugin and MCP server must receive the same secret.

Read [SECURITY.md](SECURITY.md) before enabling script execution or automatic
updates. Report vulnerabilities privately through GitHub Security Advisories.

## Development and contributing

Contributor setup, test commands, confidentiality rules, and the pull request
checklist are in [CONTRIBUTING.md](CONTRIBUTING.md). Command architecture and
AI-first tool contracts are documented in [CLAUDE.md](CLAUDE.md).

## License

Licensed under the [MIT License](LICENSE). Redistributed and runtime-provided
dependencies are documented in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

- Update-notification design informed by
  [RevitLookup](https://github.com/lookup-foundation/RevitLookup).
- CI reference assemblies provided by
  [Nice3point.Revit.Api](https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPI).
- Revit main-thread bridging provided by
  [Revit.Async](https://github.com/KennanChan/Revit.Async).
