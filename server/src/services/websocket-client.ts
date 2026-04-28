// Revit-specific wiring layer over the generic CadWebSocketClient.
// Implementation lives in @kimminsub/mcp-cad-core.

import { CadWebSocketClient } from "@kimminsub/mcp-cad-core";
import { WS_URL, LOG_PREFIX } from "../constants.js";

export class RevitWebSocketClient extends CadWebSocketClient {
  constructor() {
    super({
      url: WS_URL,
      logPrefix: LOG_PREFIX,
      notConnectedSuggestion:
        "Ensure Revit is running with the MCP plugin loaded, then retry.",
    });
  }
}
