import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { AcadWebSocketClient } from "../services/websocket-client.js";

export function registerUtilityTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_ping",
    {
      title: "Ping AutoCAD",
      description: `Test the connection to AutoCAD. Returns the AutoCAD version, active drawing name, and entity count in model space.

Use this to verify AutoCAD is running and the AutoCADMCP plugin is loaded before trying other commands.`,
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
        try {
          await wsClient.connect();
        } catch {
          return {
            content: [
              {
                type: "text" as const,
                text: JSON.stringify({
                  connected: false,
                  error:
                    "Cannot connect to AutoCAD. Ensure AutoCAD is running with the AutoCADMCP plugin (NETLOAD or autoloader bundle).",
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
            text: JSON.stringify({ connected: true, ...(response.data as object) }),
          },
        ],
      };
    }
  );
}
