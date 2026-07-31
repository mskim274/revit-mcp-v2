# Protocol Documentation

Contracts between the MCP TypeScript server and the CAD plugin (Revit /
AutoCAD). Read these before adding new commands or modifying the
WebSocket layer — both ends must agree on the format.

## Index

- [`WIRE_PROTOCOL.md`](./WIRE_PROTOCOL.md) — Message envelope, field
  semantics, error codes, threading model, idempotency, response-size
  protection, and forward-compat rules. The contract.
- [`COMMAND_AUTHORING.md`](./COMMAND_AUTHORING.md) — Step-by-step guide
  for adding a new command (C# class + TypeScript tool + verification
  hook). The how-to.

## Why this folder exists

The protocol is implemented in three places:

| Layer        | Where                                                              |
|--------------|--------------------------------------------------------------------|
| TS types     | [`packages/mcp-cad-core/src/types.ts`](../packages/mcp-cad-core/src/types.ts) |
| TS client    | [`packages/mcp-cad-core/src/services/websocket-client.ts`](../packages/mcp-cad-core/src/services/websocket-client.ts) |
| C# server (Revit) | [`plugin/RevitMCPPlugin/WebSocketServer.cs`](../plugin/RevitMCPPlugin/WebSocketServer.cs) |
| C# server (AutoCAD) | [`autocad/plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs`](../autocad/plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs) |
| C# command interfaces | [`commandset/Interfaces/IRevitCommand.cs`](../commandset/Interfaces/IRevitCommand.cs), [`autocad/commandset/Interfaces/ICadCommand.cs`](../autocad/commandset/Interfaces/ICadCommand.cs) |

Without a written contract, it is far too easy for one side to drift
silently — adding a field that the other side ignores, or changing a
response shape that breaks the client weeks later. These docs are the
single source of truth that all four implementations point back to.
