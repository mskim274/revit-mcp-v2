/**
 * Shared type definitions for Revit MCP Server
 */

// ─── WebSocket Protocol Types ───

export interface CommandRequest {
  id: string;
  command: string;
  params: Record<string, unknown>;
  timeout_ms: number;
}

export interface CommandResponse {
  id: string;
  status: "success" | "error" | "progress";
  data?: unknown;
  error?: CommandError;
  progress?: CommandProgress;
}

export interface CommandError {
  code: ErrorCode;
  message: string;
  recoverable: boolean;
  suggestion?: string;
}

export interface CommandProgress {
  current: number;
  total: number;
  message: string;
}

export type ErrorCode =
  | "CONNECTION_ERROR"
  | "TIMEOUT_ERROR"
  | "REVIT_API_ERROR"
  | "VALIDATION_ERROR"
  | "INTERNAL_ERROR";

// ─── Pagination Types ───

export interface PaginatedResult<T> {
  total_count: number;
  returned_count: number;
  has_more: boolean;
  next_cursor: string | null;
  items: T[];
}

export interface SummaryResult {
  total: number;
  by_type: Record<string, number>;
  by_level: Record<string, number>;
}

// ─── Tool Response Helpers ───

export interface ToolSuccessResponse {
  content: Array<{ type: "text"; text: string }>;
  structuredContent?: unknown;
}
