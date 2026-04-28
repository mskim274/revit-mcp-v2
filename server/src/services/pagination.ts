// Re-export pagination helpers from the shared core. Kept as a thin shim so
// existing tool imports (`from "../services/pagination.js"`) keep working.

export {
  clampPageSize,
  parseCursor,
  createCursor,
  buildPaginatedResult,
  formatSummary,
  formatPaginatedResult,
} from "@kimminsub/mcp-cad-core";
