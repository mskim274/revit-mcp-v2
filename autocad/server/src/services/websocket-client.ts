// AutoCAD-specific wiring layer over the generic CadWebSocketClient.

import { CadWebSocketClient } from "@kimminsub/mcp-cad-core";
import { WS_URL, LOG_PREFIX } from "../constants.js";

export class AcadWebSocketClient extends CadWebSocketClient {
  constructor() {
    super({
      url: WS_URL,
      logPrefix: LOG_PREFIX,
      notConnectedSuggestion:
        "Ensure AutoCAD is running with the AutoCADMCP plugin loaded (NETLOAD or autoloader bundle).",
    });
  }
}
