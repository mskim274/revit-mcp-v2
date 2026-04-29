// Batch-create the 11 single-candidate types via duplicate_type.
// Reads the case plan from %TEMP%\case-plan.json and writes new type IDs
// back to a result file so the Excel-update step can pick them up.
//
// Usage: node scripts/batch-create-single-candidates.mjs

import WebSocket from "ws";
import fs from "node:fs";
import path from "node:path";

const PORT = process.env.MCP_PORT || "8181";
const PLAN_PATH = path.join(process.env.TEMP || "/tmp", "case-plan.json");
const RESULT_PATH = path.join(process.env.TEMP || "/tmp", "case-results.json");

const plan = JSON.parse(fs.readFileSync(PLAN_PATH, "utf8"));
const cases = plan.single;
console.log(`[plan] ${cases.length} single-candidate cases to process\n`);

// One persistent WS connection so we don't reconnect 11 times
const ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
const pending = new Map();

ws.on("message", (data) => {
  const msg = JSON.parse(data.toString());
  const cb = pending.get(msg.id);
  if (cb) { pending.delete(msg.id); cb(msg); }
});

function send(command, params, timeoutMs = 25000) {
  return new Promise((resolve, reject) => {
    const id = `batch-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const t = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`timeout: ${command}`));
    }, timeoutMs);
    pending.set(id, (msg) => { clearTimeout(t); resolve(msg); });
    ws.send(JSON.stringify({ id, command, params, timeout_ms: timeoutMs - 1000 }));
  });
}

ws.on("open", async () => {
  const results = [];
  for (let i = 0; i < cases.length; i++) {
    const c = cases[i];
    const cand = c.candidates[0];
    const newName = c.excel_name;
    const idemKey = `batch-single-${c.mark}-${c.size}-${c.excel_floor}`.replace(/\s/g, "");

    process.stdout.write(`[${i + 1}/${cases.length}] ${newName}\n  source: ${cand.name} (id=${cand.id})\n  → `);
    try {
      const resp = await send("duplicate_type", {
        source_type_id: cand.id,
        new_name: newName,
        idempotency_key: idemKey,
      });
      if (resp.status === "success") {
        results.push({ excel_name: newName, new_id: resp.data.new_type_id, source_floor: cand.floor, status: "ok" });
        console.log(`✅ new_id=${resp.data.new_type_id}`);
      } else {
        results.push({ excel_name: newName, status: "error", error: resp.error });
        console.log(`❌ ${resp.error?.message}`);
      }
    } catch (e) {
      results.push({ excel_name: newName, status: "error", error: { message: e.message } });
      console.log(`❌ ${e.message}`);
    }
  }

  fs.writeFileSync(RESULT_PATH, JSON.stringify(results, null, 2), "utf8");
  console.log(`\n[done] results written to ${RESULT_PATH}`);
  console.log(`[summary] ok=${results.filter(r => r.status === "ok").length} error=${results.filter(r => r.status !== "ok").length}`);
  ws.close();
});

ws.on("error", (e) => { console.error("[ws error]", e.message); process.exit(1); });
