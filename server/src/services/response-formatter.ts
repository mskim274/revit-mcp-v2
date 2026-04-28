// Revit-specific wiring layer over the generic response formatter.
// Implementation (overflow spill etc.) lives in @kimminsub/mcp-cad-core.

import { createResponseFormatter } from "@kimminsub/mcp-cad-core";
import { RESPONSE_SPILL_DIR } from "../constants.js";

const formatter = createResponseFormatter({ spillDirName: RESPONSE_SPILL_DIR });

export const sendAndFormat = formatter.sendAndFormat;
export const protectAgainstOverflow = formatter.protectAgainstOverflow;
