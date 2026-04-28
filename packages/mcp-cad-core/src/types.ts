// WebSocket protocol types shared between MCP TypeScript server and the
// CAD plugin (Revit / AutoCAD / etc.). Stable wire format — change with care.

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

// Pagination types

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

// MCP tool response shape

export interface ToolSuccessResponse {
  content: Array<{ type: "text"; text: string }>;
  structuredContent?: unknown;
}
