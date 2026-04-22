# @kimminsub/revit-mcp

[![npm version](https://img.shields.io/npm/v/@kimminsub/revit-mcp.svg)](https://www.npmjs.com/package/@kimminsub/revit-mcp)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/mskim274/revit-mcp-v2/blob/main/LICENSE)

Model Context Protocol server for Autodesk Revit. Lets Claude (and other
MCP clients) read, query, create, and modify elements in a running Revit
session through natural language. 20 tools across Utility / Query /
Create / Modify / View.

> **This is the TypeScript MCP server half.** The C# Revit plugin that
> it talks to lives in the same repo and is installed separately inside
> Revit itself. See the full repo for the plugin install instructions.

---

## Install for Claude Desktop

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "revit": {
      "command": "npx",
      "args": ["-y", "@kimminsub/revit-mcp"]
    }
  }
}
```

Restart Claude Desktop. `npx` will download and run the server the first
time — no manual clone, install, or build needed for this half.

### Optional environment variables

```json
{
  "mcpServers": {
    "revit": {
      "command": "npx",
      "args": ["-y", "@kimminsub/revit-mcp"],
      "env": {
        "REVIT_MCP_HOST": "127.0.0.1",
        "REVIT_MCP_PORT": "8181"
      }
    }
  }
}
```

---

## Prerequisites: Revit plugin

This server talks to a companion C# add-in that runs *inside* Revit.
Install it once per machine:

1. Download the latest `RevitMCPPlugin-<version>-Revit2025.zip` from
   [GitHub Releases](https://github.com/mskim274/revit-mcp-v2/releases/latest).
2. Close Revit.
3. Extract the four files to `%APPDATA%\Autodesk\Revit\Addins\2025\`.
4. Start Revit and open a project. The plugin starts a WebSocket server
   on port 8181; the MCP server connects to it automatically.

After the first install, future plugin updates arrive via a one-click
dialog inside Revit. No manual re-downloading.

---

## Verify

Ask Claude: *"Call revit_ping."* You should see the Revit version,
document name, and element count come back.

---

## Tool inventory (v0.1)

| Category | Tools |
|---|---|
| Utility | `revit_ping`, `revit_get_project_info` |
| Query | `revit_get_levels`, `revit_get_views`, `revit_get_grids`, `revit_query_elements`, `revit_get_element_info`, `revit_get_types_by_category`, `revit_get_family_types`, `revit_get_all_categories` |
| Create | `revit_create_wall`, `revit_create_floor` |
| Modify | `revit_modify_element_parameter`, `revit_delete_elements`, `revit_move_elements`, `revit_copy_elements` |
| View | `revit_set_active_view`, `revit_isolate_elements`, `revit_reset_view_isolation`, `revit_select_elements` |

Full tool docs: [CLAUDE.md](https://github.com/mskim274/revit-mcp-v2/blob/main/CLAUDE.md)

---

## Links

- **Main repo**: https://github.com/mskim274/revit-mcp-v2
- **Changelog**: https://github.com/mskim274/revit-mcp-v2/blob/main/CHANGELOG.md
- **Issues**: https://github.com/mskim274/revit-mcp-v2/issues

---

## License

MIT
