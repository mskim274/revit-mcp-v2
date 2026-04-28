// Pagination utilities for the 3-tier response strategy.
//
// Tier 1: Summary mode — counts and groupings only (default)
// Tier 2: Paginated detail — cursor-based, configurable page size
// Tier 3: Full export — CSV file reference for very large datasets

import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE } from "../constants.js";
import type { PaginatedResult, SummaryResult } from "../types.js";

export function clampPageSize(requested?: number): number {
  if (requested == null || requested <= 0) return DEFAULT_PAGE_SIZE;
  return Math.min(requested, MAX_PAGE_SIZE);
}

// Cursor format: base64-encoded "offset:<number>"
export function parseCursor(cursor?: string | null): number {
  if (!cursor) return 0;
  try {
    const decoded = Buffer.from(cursor, "base64").toString("utf-8");
    const match = decoded.match(/^offset:(\d+)$/);
    return match ? parseInt(match[1], 10) : 0;
  } catch {
    return 0;
  }
}

export function createCursor(offset: number): string {
  return Buffer.from(`offset:${offset}`).toString("base64");
}

// Wrap raw plugin response { total_count, items, offset } in a
// PaginatedResult with cursor for the next page.
export function buildPaginatedResult<T>(
  totalCount: number,
  items: T[],
  offset: number,
  limit: number
): PaginatedResult<T> {
  const nextOffset = offset + items.length;
  const hasMore = nextOffset < totalCount;

  return {
    total_count: totalCount,
    returned_count: items.length,
    has_more: hasMore,
    next_cursor: hasMore ? createCursor(nextOffset) : null,
    items,
  };
}

export function formatSummary(summary: SummaryResult, categoryName: string): string {
  const lines: string[] = [];
  lines.push(`📊 ${categoryName}: ${summary.total} elements total`);

  if (Object.keys(summary.by_type).length > 0) {
    lines.push("\nBy Type:");
    for (const [type, count] of Object.entries(summary.by_type)) {
      lines.push(`  • ${type}: ${count}`);
    }
  }

  if (Object.keys(summary.by_level).length > 0) {
    lines.push("\nBy Level:");
    for (const [level, count] of Object.entries(summary.by_level)) {
      lines.push(`  • ${level}: ${count}`);
    }
  }

  return lines.join("\n");
}

export function formatPaginatedResult<T>(
  result: PaginatedResult<T>,
  formatItem: (item: T) => string
): string {
  const lines: string[] = [];
  lines.push(`Showing ${result.returned_count} of ${result.total_count} elements`);

  if (result.items.length > 0) {
    lines.push("");
    for (const item of result.items) {
      lines.push(formatItem(item));
    }
  }

  if (result.has_more && result.next_cursor) {
    lines.push(`\n--- More results available. Use cursor: "${result.next_cursor}" ---`);
  }

  return lines.join("\n");
}
