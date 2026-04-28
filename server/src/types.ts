// Re-export protocol/pagination types from the shared core. Existing
// imports (`from "../types.js"`) keep working.

export type {
  CommandRequest,
  CommandResponse,
  CommandError,
  CommandProgress,
  ErrorCode,
  PaginatedResult,
  SummaryResult,
  ToolSuccessResponse,
} from "@kimminsub/mcp-cad-core";
