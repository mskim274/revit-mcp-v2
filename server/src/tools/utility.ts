/**
 * Utility Tools — ping, get_project_info
 *
 * Basic tools for connection testing and project information.
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";

export function registerUtilityTools(
  server: McpServer,
  wsClient: RevitWebSocketClient,
): void {
  // ─── revit_ping ───
  server.registerTool(
    "revit_ping",
    {
      title: "Ping Revit",
      description: `Test the connection to Revit. Returns the Revit version, active document name, and connection status.

Use this tool to verify that Revit is running and the MCP plugin is loaded before executing other commands.

Returns:
  - connected: boolean — whether the connection is active
  - revit_version: string — e.g. "2025.1"
  - document_name: string — active document name
  - commandset_hot_reload_ready: boolean — active CommandSet supports generation reload
  - element_count: number — total elements in the document`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => {
      // The session router performs discovery, target selection checks, and
      // on-demand connection itself. Calling connect() first would collapse a
      // registry safety error into an unhelpful generic connection failure.
      const response = await wsClient.sendCommand("ping", {}, 10000);

      if (response.status === "error") {
        return {
          isError: true,
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                connected: false,
                code: response.error?.code,
                error: response.error?.message ?? "Unknown error",
                suggestion: response.error?.suggestion,
              }),
            },
          ],
        };
      }

      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify({
              connected: true,
              ...(response.data as object),
            }),
          },
        ],
      };
    },
  );

  // ─── revit_get_project_info ───
  server.registerTool(
    "revit_get_project_info",
    {
      title: "Get Revit Project Info",
      description: `Get information about the currently open Revit project.

Returns project name, number, address, status, organization, author, and other project-level metadata.

Use this tool at the start of a conversation to understand what project the user is working on.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => {
      const response = await wsClient.sendCommand("get_project_info");

      if (response.status === "error") {
        return {
          isError: true,
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                code: response.error?.code,
                error: response.error?.message,
                suggestion: response.error?.suggestion,
              }),
            },
          ],
        };
      }

      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(response.data, null, 2),
          },
        ],
      };
    },
  );

  // ─── revit_get_commandset_status ──────────────────────────────
  server.registerTool(
    "revit_get_commandset_status",
    {
      title: "Get Revit CommandSet Status",
      description: `Inspect the reloadable C# CommandSet runtime.

Returns the active generation/hash, available staged generations, persistence
state, command count, and whether retired AssemblyLoadContexts were collected.
Revit 2025+ supports hot reload; Revit 2023/2024 reports restart-only mode.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => {
      const response = await wsClient.sendCommand("get_commandset_status");
      if (response.status === "error") {
        return {
          isError: true,
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                code: response.error?.code,
                error: response.error?.message,
                suggestion: response.error?.suggestion,
              }),
            },
          ],
        };
      }
      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(response.data, null, 2),
          },
        ],
      };
    },
  );

  // ─── revit_reload_commandset ──────────────────────────────────
  server.registerTool(
    "revit_reload_commandset",
    {
      title: "Reload Revit CommandSet",
      description: `Activate a staged C# CommandSet generation without restarting Revit 2025+.

First run scripts/stage-commandset.ps1 outside Revit. If generation is omitted,
the newest valid staged generation is selected. The candidate is fully loaded
and its command inventory validated before the active generation is swapped.
Failure leaves the previous generation active. Host, contracts, WebSocket,
Revit.Async, and startup lifecycle changes still require a Revit restart.`,
      inputSchema: {
        generation: z
          .string()
          .trim()
          .min(1)
          .max(128)
          .regex(/^[A-Za-z0-9._-]+$/)
          .optional()
          .describe(
            "Exact staged generation from revit_get_commandset_status; omit for latest.",
          ),
        allow_command_removal: z
          .boolean()
          .optional()
          .default(false)
          .describe(
            "Allow an intentional breaking generation that removes existing commands.",
          ),
        persist: z
          .boolean()
          .optional()
          .default(true)
          .describe("Load this generation automatically on the next Revit start."),
        idempotency_key: z
          .string()
          .trim()
          .min(1)
          .max(512)
          .optional()
          .describe("Stable retry key for this activation request."),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (params) => {
      const payload: Record<string, unknown> = {
        allow_command_removal: params.allow_command_removal,
        persist: params.persist,
      };
      if (params.generation !== undefined) {
        payload.generation = params.generation;
      }
      if (params.idempotency_key !== undefined) {
        payload.idempotency_key = params.idempotency_key;
      }

      const response = await wsClient.sendCommand(
        "reload_commandset",
        payload,
        60_000,
      );
      if (response.status === "error") {
        return {
          isError: true,
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                code: response.error?.code,
                error: response.error?.message,
                suggestion: response.error?.suggestion,
              }),
            },
          ],
        };
      }
      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(response.data, null, 2),
          },
        ],
      };
    },
  );
}
