# @kimminsub/autocad-mcp

Source-only TypeScript MCP server for the AutoCAD 2025 bridge in
[`mskim274/revit-mcp-v2`](https://github.com/mskim274/revit-mcp-v2).

This workspace is currently marked private and is not published to npm.
Build and run it from the repository:

```powershell
npm ci --workspaces --include-workspace-root
npm run build:autocad
node autocad\server\dist\index.js
```

The AutoCAD plugin must be loaded separately with `NETLOAD`. See the
[AutoCAD guide](https://github.com/mskim274/revit-mcp-v2/blob/main/autocad/README.md)
for supported tools, authentication, and installation details.
