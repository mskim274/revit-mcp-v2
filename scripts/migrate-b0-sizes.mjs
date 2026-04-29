// Migrate B0 instances from 500x600 to 500x700 on floors 3F/4F/5F.
// Source/target type IDs were verified manually before invoking.
//
// Schedule says these floors should be 500x700 but model has 500x600
// (artifact of earlier reconciliation that created the new types but
// never moved the instances). 158 instances total.
//
// Usage: node scripts/migrate-b0-sizes.mjs

import WebSocket from "ws";
import fs from "node:fs";
import path from "node:path";

const PORT = process.env.MCP_PORT || "8181";

const MIGRATIONS = [
  { floor: "3F", source_id: 609897,  target_id: 5366465, expected: 39 },
  { floor: "4F", source_id: 609813,  target_id: 5366467, expected: 70 },
  { floor: "5F", source_id: 609437,  target_id: 5366469, expected: 49 },
];

const ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
const pending = new Map();
ws.on("message", (data) => {
  const msg = JSON.parse(data.toString());
  const cb = pending.get(msg.id);
  if (cb) { pending.delete(msg.id); cb(msg); }
});

function send(command, params, timeoutMs = 60000) {
  return new Promise((resolve, reject) => {
    const id = `mig-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
    const t = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`timeout: ${command}`));
    }, timeoutMs);
    pending.set(id, (m) => { clearTimeout(t); resolve(m); });
    ws.send(JSON.stringify({ id, command, params, timeout_ms: timeoutMs - 5000 }));
  });
}

async function getInstanceIds(typeFilter) {
  // Page through all instances for the given type filter
  const ids = [];
  let cursor = null;
  for (let page = 0; page < 20; page++) {
    const params = {
      category: "StructuralFraming",
      type_filter: typeFilter,
      summary_only: false,
      limit: 200,
    };
    if (cursor) params.cursor = cursor;
    const resp = await send("query_elements", params);
    if (resp.status !== "success") throw new Error(`query failed: ${resp.error?.message}`);
    const items = resp.data.items || [];
    // Need exact-name match (filter is substring)
    items.forEach(it => ids.push({ id: it.id, name: it.type_name }));
    if (!resp.data.has_more) break;
    cursor = resp.data.next_cursor || null;
    if (!cursor) break;
  }
  return ids;
}

ws.on("open", async () => {
  const summary = [];
  for (const m of MIGRATIONS) {
    process.stdout.write(`\n=== Floor ${m.floor} (source=${m.source_id} → target=${m.target_id}) ===\n`);
    const expectedName = `Beam_RC, B0, 27MPa, 500x600, ${m.floor}`;
    process.stdout.write(`  collecting instance IDs ...`);
    const found = await getInstanceIds(expectedName);
    // Filter to only instances with EXACT matching type name
    const matched = found.filter(it => it.name === expectedName);
    process.stdout.write(` ${matched.length} (expected ${m.expected})\n`);

    if (matched.length === 0) {
      summary.push({ floor: m.floor, moved: 0, skipped: true });
      continue;
    }

    const ids = matched.map(it => it.id);
    // Migrate in small batches — HANDOFF.md notes 10 is workshare-safe.
    // Larger batches hit "operation was canceled" (transaction conflict).
    const BATCH = 10;
    let moved = 0;
    for (let i = 0; i < ids.length; i += BATCH) {
      const chunk = ids.slice(i, i + BATCH);
      process.stdout.write(`  batch ${Math.floor(i/BATCH) + 1}: ${chunk.length} instances ... `);
      const resp = await send("change_instance_type", {
        instance_ids: chunk,
        new_type_id: m.target_id,
        idempotency_key: `mig-b0-${m.floor}-batch-${i}`,
      }, 90000);
      if (resp.status === "success") {
        moved += resp.data.changed_count ?? chunk.length;
        process.stdout.write(`✅ ${resp.data.changed_count ?? chunk.length} moved\n`);
      } else {
        process.stdout.write(`❌ ${resp.error?.message}\n`);
        break;
      }
    }
    summary.push({ floor: m.floor, moved, expected: m.expected });
  }

  console.log("\n=== 완료 요약 ===");
  for (const s of summary) {
    console.log(`  Floor ${s.floor}:  ${s.moved}/${s.expected} 이동`);
  }
  ws.close();
});

ws.on("error", (e) => { console.error("[ws]", e.message); process.exit(1); });
