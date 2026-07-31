# @kimminsub/mcp-cad-core

Shared transport and response-safety primitives used by the Revit MCP and
AutoCAD MCP servers in
[`mskim274/revit-mcp-v2`](https://github.com/mskim274/revit-mcp-v2).

This is a library, not a standalone MCP server. It provides:

- a reconnecting, sequential WebSocket client with authenticated headers;
- timeout handling that distinguishes uncertain side-effect outcomes;
- generated retry keys for side-effect calls that omit one, with the effective
  key returned on timeout or connection-loss errors;
- bounded UTF-8-safe response previews and temporary-file overflow spill;
- strict cursor pagination helpers;
- the shared CAD bridge wire types and constants.

## Requirements

- Node.js 20 or newer
- ESM (`"type": "module"`)

## Usage

```javascript
import {
  CadWebSocketClient,
  createResponseFormatter,
} from "@kimminsub/mcp-cad-core";
```

Product-specific servers supply their loopback URL, bearer-token header
provider, log prefix, and spill directory. See the repository's
[wire protocol](https://github.com/mskim274/revit-mcp-v2/blob/main/protocol/WIRE_PROTOCOL.md)
for authentication, timeout, idempotency, and 64-bit identifier semantics.

## Security and support

The bridge is designed for authenticated loopback connections; do not expose
CAD plugin ports to a LAN or the internet. Follow the repository's
[security policy](https://github.com/mskim274/revit-mcp-v2/blob/main/SECURITY.md)
for private reporting instructions.

Licensed under the MIT License.
