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
  wsClient: RevitWebSocketClient
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
      if (!wsClient.isConnected) {
        // Try to connect first
        try {
          await wsClient.connect();
        } catch {
          return {
            content: [
              {
                type: "text" as const,
                text: JSON.stringify({
                  connected: false,
                  error: "Cannot connect to Revit. Ensure Revit is running with the MCP plugin.",
                }),
              },
            ],
          };
        }
      }

      const response = await wsClient.sendCommand("ping", {}, 10000);

      if (response.status === "error") {
        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                connected: false,
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
            text: JSON.stringify({ connected: true, ...response.data as object }),
          },
        ],
      };
    }
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
          content: [
            {
              type: "text" as const,
              text: `Error: ${response.error?.message}. ${response.error?.suggestion ?? ""}`,
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
    }
  );
}
