// AutoCAD-specific wiring layer over the generic response formatter.

import { createResponseFormatter } from "@kimminsub/mcp-cad-core";
import { RESPONSE_SPILL_DIR } from "../constants.js";

const formatter = createResponseFormatter({ spillDirName: RESPONSE_SPILL_DIR });
export const sendAndFormat = formatter.sendAndFormat;
