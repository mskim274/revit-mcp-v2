import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

const DEFAULT_SCRIPT_TIMEOUT_MS = 60_000;
const MAX_SCRIPT_TIMEOUT_MS = 300_000;

export function registerScriptTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_execute_script",
    {
      title: "Execute C# Script in AutoCAD",
      description: `Run a C# script against the active AutoCAD 2025 drawing as an escape hatch when no dedicated cad_* tool fits.

Globals: db (Database), doc (Document), tr (the dispatcher-owned Transaction), and print(object). Auto-imports: System, System.Collections.Generic, System.Linq, Autodesk.AutoCAD.DatabaseServices, Autodesk.AutoCAD.Geometry. The last expression without a trailing semicolon becomes return_value; database objects, ObjectIds, Handles, points, and collections are converted to bounded JSON-safe data.

mode='query' (default) runs inside the dispatcher's transaction and deliberately ABORTS it after a successful script, so writes made through tr are not persisted. mode='modify' commits that one dispatcher-owned transaction; a runtime error aborts all changes. Scripts must never create, commit, or abort nested transactions.

This tool is disabled unless AutoCAD starts with AUTOCAD_MCP_ENABLE_SCRIPT=1. A best-effort denylist blocks obvious file, network, process, reflection, document Save/SaveAs, nested transaction, async/threading, and infinite-loop patterns, but this is NOT a security sandbox. There is no UI approval dialog. Compile errors include line/column diagnostics and runtime errors include the last print() lines.`,
      inputSchema: {
        code: z.string().min(1).max(50_000)
          .describe("C# script body. The last expression without a semicolon becomes return_value."),
        mode: z.enum(["query", "modify"]).optional().default("query"),
        timeout_ms: z.number().int().min(5_000).max(MAX_SCRIPT_TIMEOUT_MS).optional()
          .describe(`Execution timeout in milliseconds (default ${DEFAULT_SCRIPT_TIMEOUT_MS}, max ${MAX_SCRIPT_TIMEOUT_MS}).`),
        idempotency_key: z.string().trim().min(1).max(512).optional()
          .describe("Stable deduplication key for an identical retry after an uncertain result."),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(
      wsClient,
      "execute_script",
      {
        code: params.code,
        mode: params.mode ?? "query",
        idempotency_key: params.idempotency_key,
      },
      Math.min(params.timeout_ms ?? DEFAULT_SCRIPT_TIMEOUT_MS, MAX_SCRIPT_TIMEOUT_MS),
      { sideEffect: true }
    )
  );
}
