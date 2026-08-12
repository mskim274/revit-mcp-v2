import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { ErrorCode } from "@kimminsub/mcp-cad-core";
import { z } from "zod";
import {
  RevitWebSocketClient,
  TargetSelectionError,
} from "../services/websocket-client.js";

const READ_ONLY_ANNOTATIONS = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
} as const;

const SET_TARGET_INPUT = {
  session_id: z
    .string()
    .trim()
    .min(1)
    .max(128)
    .optional()
    .describe(
      "Exact session_id returned by revit_list_sessions. Required unless clear=true.",
    ),
  clear: z
    .boolean()
    .optional()
    .default(false)
    .describe("Clear the current target instead of selecting a session."),
};

export function registerSessionTools(
  server: McpServer,
  wsClient: RevitWebSocketClient,
): void {
  server.registerTool(
    "revit_list_sessions",
    {
      title: "List Live Revit Sessions",
      description: `List live Revit processes registered on this computer.

Returns each session's stable session_id, Revit PID/version, WebSocket port,
active document title/path, heartbeat time, and whether it is the selected
target. Stale, dead-process, malformed, oversized, or non-regular registry
entries are ignored and counted.

When more than one session is live, inspect the full document path and call
revit_set_target before any model command.`,
      inputSchema: {},
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async () => {
      try {
        const discovery = await wsClient.getLiveSessions();
        const target = wsClient.getSelectedTarget();
        const selectedLiveSession = target
          ? discovery.sessions.find(
              (session) => session.session_id === target.session_id,
            )
          : undefined;
        const selectedDocumentMatch =
          target !== null && selectedLiveSession !== undefined
            ? target.document_fingerprint.toLowerCase() ===
              selectedLiveSession.document_fingerprint.toLowerCase()
            : false;
        const targetValid =
          target !== null &&
          selectedLiveSession !== undefined &&
          selectedDocumentMatch;
        const sessions = discovery.sessions.map((session) => ({
          session_id: session.session_id,
          pid: session.pid,
          port: session.port,
          revit_version: session.revit_version,
          revit_build: session.revit_build,
          active_document_title: session.active_document_title,
          active_document_path: session.active_document_path,
          document_fingerprint: session.document_fingerprint,
          started_at_utc: session.started_at_utc,
          last_seen_utc: session.last_seen_utc,
          selected: target?.session_id === session.session_id,
          selected_document_match:
            target?.session_id === session.session_id
              ? target.document_fingerprint.toLowerCase() ===
                session.document_fingerprint.toLowerCase()
              : null,
        }));
        return jsonResult({
          live_count: sessions.length,
          selection_required:
            (sessions.length > 1 && !targetValid) ||
            (target !== null && !targetValid),
          selected_session_id: target?.session_id ?? null,
          selected_target_valid: targetValid,
          sessions,
          ignored_entry_count:
            discovery.ignored_entry_count ?? discovery.ignored_entries.length,
          ignored_entries: discovery.ignored_entries,
          legacy_fallback:
            sessions.length === 0
              ? "No live registry entries. The router will probe REVIT_MCP_PORT (default 8181) and use it only when the endpoint proves it is a pre-session legacy plugin."
              : null,
        });
      } catch (error) {
        return errorResult(
          "VALIDATION_ERROR",
          `Cannot safely read the Revit instance registry: ${toMessage(error)}`,
          "Restore %LOCALAPPDATA%\\RevitMCP\\instances as a normal local directory, then retry.",
        );
      }
    },
  );

  server.registerTool(
    "revit_set_target",
    {
      title: "Set Target Revit Session",
      description: `Select the exact Revit process and active document for all subsequent tools.

Pass an exact session_id from revit_list_sessions. Selection pins both the
process session and its current document fingerprint. If the user changes the
active Revit document tab later, writes and reads fail closed until this tool is
called again after reviewing the new document.

Use clear=true to remove the selection. With multiple live sessions, other
Revit commands will then return TARGET_SELECTION_REQUIRED.`,
      inputSchema: SET_TARGET_INPUT,
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (params) => {
      if (params.clear && params.session_id !== undefined) {
        return errorResult(
          "VALIDATION_ERROR",
          "Omit session_id when clear=true.",
          "Call revit_set_target with either an exact session_id or clear=true, not both.",
        );
      }
      if (!params.clear && params.session_id === undefined) {
        return errorResult(
          "VALIDATION_ERROR",
          "session_id is required unless clear=true.",
          "Call revit_list_sessions, then pass one exact returned session_id.",
        );
      }

      if (params.clear) {
        wsClient.clearTarget();
        return jsonResult({
          selected: false,
          target: null,
          suggestion:
            "Call revit_list_sessions and revit_set_target before model commands when multiple Revit sessions are live.",
        });
      }

      try {
        const target = await wsClient.selectTarget(params.session_id!);
        return jsonResult({
          selected: true,
          target,
          guard_policy:
            "Every routed request carries target_session_id and expected_document_fingerprint.",
        });
      } catch (error) {
        if (error instanceof TargetSelectionError) {
          return errorResult(error.code, error.message, error.suggestion);
        }
        return errorResult(
          "INTERNAL_ERROR",
          `Could not select the Revit target: ${toMessage(error)}`,
          "Call revit_list_sessions to verify the registry, then retry with an exact live session_id.",
        );
      }
    },
  );

  server.registerTool(
    "revit_get_target",
    {
      title: "Get Target Revit Session",
      description: `Return the locally selected Revit process and pinned document.

Also checks the live registry and reports whether the process is still live and
whether its current document still matches the pinned fingerprint. This tool
does not automatically follow active-document changes.`,
      inputSchema: {},
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async () => {
      const target = wsClient.getSelectedTarget();
      if (!target) {
        return jsonResult({
          selected: false,
          target: null,
          suggestion:
            "Call revit_list_sessions, then revit_set_target when more than one Revit session is live.",
        });
      }

      try {
        const discovery = await wsClient.getLiveSessions();
        const live = discovery.sessions.find(
          (session) => session.session_id === target.session_id,
        );
        return jsonResult({
          selected: true,
          target,
          session_live: live !== undefined,
          document_match:
            live !== undefined &&
            live.document_fingerprint.toLowerCase() ===
              target.document_fingerprint.toLowerCase(),
          current_document: live
            ? {
                title: live.active_document_title,
                path: live.active_document_path,
                fingerprint: live.document_fingerprint,
                last_seen_utc: live.last_seen_utc,
              }
            : null,
          suggestion:
            live === undefined
              ? "Call revit_list_sessions and select another live session."
              : live.document_fingerprint.toLowerCase() !==
                  target.document_fingerprint.toLowerCase()
                ? "The active document changed. Review it and call revit_set_target again only if it is intended."
                : null,
        });
      } catch (error) {
        return errorResult(
          "VALIDATION_ERROR",
          `Cannot safely read the Revit instance registry: ${toMessage(error)}`,
          "Restore the registry directory, then call revit_list_sessions.",
        );
      }
    },
  );
}

function jsonResult(value: Record<string, unknown>) {
  return {
    structuredContent: value,
    content: [{ type: "text" as const, text: JSON.stringify(value, null, 2) }],
  };
}

function errorResult(code: ErrorCode, message: string, suggestion: string) {
  const error = { code, message, recoverable: true, suggestion };
  return {
    isError: true,
    structuredContent: { error },
    content: [
      { type: "text" as const, text: JSON.stringify({ error }, null, 2) },
    ],
  };
}

function toMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
