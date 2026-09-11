import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";
import { elementIdSchema } from "./shared.js";

export const WORK_SCOPE_INPUT = {
  op: z.enum(["status", "acquire", "renew", "release", "disable"]),
  scope: z.enum(["elements", "document"]).optional(),
  element_ids: z.array(elementIdSchema()).min(1).max(5000).optional()
    .describe("Exact host IDs, never linked IDs; elements scope only. Resolve floor/zone/category assignments to IDs first."),
  label: z.string().trim().min(1).max(120).optional().describe("Human-readable assignment, required for acquire."),
  ttl_seconds: z.number().int().min(30).max(1800).optional().describe("Acquire/renew lifetime, default 300 seconds. No automatic renewal."),
  lease_token: z.string().regex(/^[a-f0-9]{32}$/).optional().describe("Renew/release/disable only; omit to use this MCP process's current assignment."),
  idempotency_key: z.string().trim().min(1).max(512).optional().describe("Required stable key for acquire; reuse only for retries of that assignment."),
};

export function registerWorkScopeTools(server: McpServer, client: RevitWebSocketClient): void {
  server.registerTool("revit_work_scope", {
    title: "Coordinate Revit Work Scopes",
    description: `Assign exclusive work ranges across MCP processes connected to one Revit host.
First check op=status on the running host; tool catalog presence is not proof of support.
An older host's exact unknown-command response returns supported=false and
reservation_required=false. For already-authorized work, use the existing workflow
with one model writer, pinned target, fresh reads and post-write verification;
do not ask for an exception approval solely because reservations are unavailable.
When supported but coordination_enabled=false, existing single-writer work is allowed;
acquire is opt-in to coordinated work. When enabled, obey reservation requirements.
Timeout, target mismatch, stale/conflicting/expired scopes are not unsupported-feature
signals; resolve those errors without bypassing protection. Never claim a reservation
was acquired when the host reports WORK_SCOPE_UNSUPPORTED.
op=status reports up to 50 active assignments (20-ID previews); no model mutation.
When using supported coordination, acquire BEFORE querying data used to calculate edits. scope=elements reserves exact
host IDs (1..5000) and permits only built-in instance Comments/Mark parameter writes,
including batch_modify_parameters. All geometry, type, create/delete, script, UI and
export commands require an exclusive scope=document assignment. Ordinary query tools
remain available. Disjoint element assignments may coexist; overlaps fail immediately.
The MCP process remembers its own token and attaches it to subsequent commands.
Manual edits, Undo/Redo and changes to tracked element/type/level dependencies make
an assignment stale. Release, reacquire with a NEW key and re-query before editing.
renew extends TTL without acknowledging changes. Expired tokens are rejected.
Acquiring enables coordination for this document in this host lifetime: unreserved
writes are then rejected even after the last assignment expires or is released.
op=disable requires the active document-scope holder and restores single-agent usage.
One active assignment per MCP process/document. This is cooperative MCP coordination,
not Revit worksharing ownership or a lock against manual editing/other add-ins.
Host restart required when installing this feature; CommandSet hot reload is insufficient.`,
    inputSchema: WORK_SCOPE_INPUT,
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async (params) => {
    const acquire = params.op === "acquire";
    const scopeFields = params.scope !== undefined || params.element_ids !== undefined || params.label !== undefined;
    const invalid = (acquire && (!params.scope || !params.label || !params.idempotency_key || params.lease_token !== undefined ||
      (params.scope === "elements" ? !params.element_ids : params.element_ids !== undefined))) ||
      (!acquire && scopeFields) ||
      (params.ttl_seconds !== undefined && !acquire && params.op !== "renew") ||
      (params.op === "status" && (params.lease_token !== undefined || params.idempotency_key !== undefined));
    if (invalid) return {
      isError: true,
      content: [{ type: "text" as const, text: JSON.stringify({ code: "VALIDATION_ERROR",
        error: "Fields do not match the work scope operation.",
        suggestion: "Acquire needs scope, label and idempotency_key; elements scope also needs element_ids. Renew accepts ttl_seconds; release/disable accept an optional lease_token. Status needs only op." }) }],
    };
    return sendAndFormat(client, "work_scope", params, 30_000, { sideEffect: params.op !== "status" });
  });
}
