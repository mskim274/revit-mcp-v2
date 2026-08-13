# @kimminsub/revit-mcp

[![npm version](https://img.shields.io/npm/v/@kimminsub/revit-mcp.svg)](https://www.npmjs.com/package/@kimminsub/revit-mcp)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/mskim274/revit-mcp-v2/blob/main/LICENSE)

The TypeScript MCP server for Autodesk Revit. It exposes 37 tools for session
selection, CommandSet hot reload, query, creation, modification, view control,
export, visualization, and controlled C# scripting.

This package is one half of the system. The companion C# add-in must be
installed inside Revit before the MCP server can connect.

## Install for an MCP client

Add this entry to the client's MCP configuration:

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

Restart the client after updating its configuration.

## Install the Revit add-in

1. Download `RevitMCPPlugin-<version>-Revit2025.zip` and
   `SHA256SUMS.txt` from
   [GitHub Releases](https://github.com/mskim274/revit-mcp-v2/releases/latest).
2. Verify the archive hash and close Revit.
3. Extract **all files** into
   `%APPDATA%\Autodesk\Revit\Addins\2025\`.
4. Start Revit and open or create a project.

The first plugin listens on `127.0.0.1:8181`. Additional Revit processes
without an explicit `REVIT_MCP_PORT` use 8183 through 8199 automatically; 8182
is reserved for AutoCAD. The MCP server discovers these local sessions and
reconnects automatically.

Automatic selection requires `REVIT_MCP_PORT` to be unset in the environment
that launches Revit. If it is defined globally, each Revit process treats it as
an exact operator override and does not scan for a free fallback port.

The published add-in supports Revit 2025 exactly. Revit 2026 and later require
a year-specific API build, release asset, and validation and are not currently
supported.

## Verify

Call `revit_ping`. A successful response includes the Revit build and current
document information.

When multiple Revit processes are running, call `revit_list_sessions`, then
`revit_set_target` with an exact `session_id`. The selected active document is
pinned by fingerprint; switching its Revit document tab blocks subsequent
commands until the session is selected again.

When no registry record exists, the configured legacy endpoint is probed before
use. Only a pre-session plugin whose ping has no session identity receives the
requested command; a new plugin with a missing record fails closed.

## Tool inventory

| Category | Count |
|---|---:|
| Session | 3 |
| Utility | 4 |
| Query | 10 |
| Create | 3 |
| Modify | 8 |
| View | 5 |
| Export | 1 |
| Visualize / Review | 2 |
| Script | 1 |
| **Total** | **37** |

Full tool and safety documentation is maintained in the
[repository README](https://github.com/mskim274/revit-mcp-v2#readme) and
[CLAUDE.md](https://github.com/mskim274/revit-mcp-v2/blob/main/CLAUDE.md).

## Security

Keep `REVIT_MCP_HOST` on loopback. The server can invoke model mutations, and
`revit_execute_script` is not a security sandbox. Review the
[security policy](https://github.com/mskim274/revit-mcp-v2/blob/main/SECURITY.md)
before use and never attach confidential model output to a public issue.

## Links

- [Source and plugin releases](https://github.com/mskim274/revit-mcp-v2)
- [Changelog](https://github.com/mskim274/revit-mcp-v2/blob/main/CHANGELOG.md)
- [Issue tracker](https://github.com/mskim274/revit-mcp-v2/issues)

## License

[MIT](LICENSE)
