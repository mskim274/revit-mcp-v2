/**
 * Script Tools — the "escape hatch" layer.
 *
 * Tools:
 *   revit_execute_script — Run arbitrary C# against the live Revit document.
 *
 * Design (CLAUDE.md "AI-First Tool Design Principles" §7):
 *   Tier 1 = ~30 solid primitives (80% of requests)
 *   Tier 2 = THIS TOOL (the remaining 20% long tail)
 *   Recurring script patterns get promoted to Tier 1 tools.
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";
import { idempotencyKeySchema } from "./shared.js";

const DEFAULT_SCRIPT_TIMEOUT_MS = 60_000;
const MAX_SCRIPT_TIMEOUT_MS = 300_000;

export function registerScriptTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  server.registerTool(
    "revit_execute_script",
    {
      title: "Execute C# Script in Revit",
      description: `Run an arbitrary C# script against the live Revit document. This is the escape hatch for requests that have no dedicated tool — write Revit API code directly instead of giving up.

**Script environment (Revit 2025+ only):**
- Globals: \`doc\` (Autodesk.Revit.DB.Document), \`print(object)\` (appends to prints[] in the response), \`MmToFt(double)\`, \`FtToMm(double)\`
- Auto-imports: System, System.Collections.Generic, System.Linq, Autodesk.Revit.DB
- The LAST EXPRESSION (no trailing semicolon) becomes \`return_value\` — Element/ElementId/XYZ/collections are auto-serialized (capped at 1000 items)
- All Revit API lengths are in FEET (use MmToFt/FtToMm)

**mode (transaction safety):**
- \`"query"\` (default): opens no Revit transaction, so normal model edits fail. This is not a security sandbox: some APIs can still perform non-model side effects such as export, and every script requires approval in Revit.
- \`"modify"\`: wrapped in ONE transaction; runtime exceptions roll back model changes. Every script requires approval in Revit; show the user a summary before submitting it.

**Self-repair loop:** compile errors return line-numbered diagnostics — fix the code and call again. Runtime errors include the last 10 print() lines for debugging.

**Safety:** disabled unless the Revit process starts with \`REVIT_MCP_ENABLE_SCRIPT=1\`. A best-effort denylist blocks obvious file/network/process/reflection access, manual Transaction creation, save, and synchronize calls, but it is not a complete sandbox. Long-running loops block Revit's UI thread until done — keep element sets narrow.

Examples:
  Query — slope check on pipes:
    code: "var pipes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().Cast<Pipe>().ToList(); print($\\"total {pipes.Count}\\"); pipes.Count"
  Modify — stamp a parameter:
    mode: "modify", code: "var n = 0; foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType()) { var p = e.LookupParameter(\\"Comments\\"); if (p != null && !p.IsReadOnly) { p.Set(\\"Reviewed by MCP\\"); n++; } } n"`,
      inputSchema: {
        code: z
          .string()
          .min(1)
          .max(50_000)
          .describe("C# script body. Last expression (no trailing semicolon) is returned as return_value."),
        mode: z
          .enum(["query", "modify"])
          .optional()
          .default("query")
          .describe('"query" = no model transaction, but not a complete side-effect sandbox. "modify" = single transaction with rollback on runtime error. Both require Revit UI approval.'),
        timeout_ms: z
          .number()
          .int()
          .min(5_000)
          .max(MAX_SCRIPT_TIMEOUT_MS)
          .optional()
          .describe(`Execution timeout in ms (default ${DEFAULT_SCRIPT_TIMEOUT_MS}, max ${MAX_SCRIPT_TIMEOUT_MS}).`),
        idempotency_key: idempotencyKeySchema(
          "Dedup key for safe retries of any script after an uncertain timeout (15-minute window)."
        ),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true, // modify-mode scripts can delete elements
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (params) => {
      const timeout = Math.min(
        params.timeout_ms ?? DEFAULT_SCRIPT_TIMEOUT_MS,
        MAX_SCRIPT_TIMEOUT_MS
      );
      const mode = params.mode ?? "query";
      return sendAndFormat(
        wsClient,
        "execute_script",
        {
          code: params.code,
          mode,
          idempotency_key: params.idempotency_key,
        },
        timeout,
        { sideEffect: true }
      );
    }
  );
}
