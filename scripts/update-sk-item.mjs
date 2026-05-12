// Batch update SK_ITEM parameter for all PSRC DC* instances.
// Usage: node scripts/update-sk-item.mjs [--dry-run]
//
// Connects to Revit plugin WebSocket on :8181 and:
//   1. Paginates through all "Column_PSRC, DC*" instances
//   2. For each, sets SK_ITEM = "Column_PSRC"
//   3. Reports progress every 50 updates
//
// Concurrency: 10 parallel requests (configurable via PARALLEL env).

import WebSocket from "ws";

const PORT = process.env.MCP_PORT || "8181";
const PARALLEL = parseInt(process.env.PARALLEL || "10", 10);
const DRY_RUN = process.argv.includes("--dry-run");
const TARGET_VALUE = "Column_PSRC";

// Single-shot WebSocket call.
function call(command, params, timeoutMs = 25000) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
    const id = `sk-item-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const timer = setTimeout(() => {
      try { ws.close(); } catch {}
      reject(new Error(`timeout after ${timeoutMs}ms`));
    }, timeoutMs);

    ws.on("open", () => {
      ws.send(JSON.stringify({ id, command, params, timeout_ms: timeoutMs - 2000 }));
    });
    ws.on("message", (data) => {
      clearTimeout(timer);
      let response;
      try {
        response = JSON.parse(data.toString());
      } catch (e) {
        ws.close();
        reject(new Error(`bad JSON: ${e.message}`));
        return;
      }
      ws.close();
      if (response.error) {
        reject(new Error(response.error.message || JSON.stringify(response.error)));
      } else {
        resolve(response.data || response.result || response);
      }
    });
    ws.on("error", (e) => {
      clearTimeout(timer);
      reject(e);
    });
  });
}

function makeCursor(offset) {
  return Buffer.from(`offset:${offset}`).toString("base64");
}

async function collectAllIds() {
  console.log(`[1/2] Collecting PSRC DC* instance IDs (page size 200)...`);
  const ids = [];
  let offset = 0;
  while (true) {
    const params = {
      category: "StructuralColumns",
      type_filter: "Column_PSRC, DC",
      summary_only: false,
      limit: 200,
    };
    if (offset > 0) params.cursor = makeCursor(offset);
    const result = await call("query_elements", params);
    for (const item of result.items) ids.push(item.id);
    process.stdout.write(`  page offset=${offset}: +${result.items.length} (total ${ids.length}/${result.total_count})\n`);
    if (!result.has_more) break;
    offset += result.items.length;
    if (offset > 5000) {
      throw new Error("Sanity check: offset exceeded 5000, breaking");
    }
  }
  return ids;
}

async function updateOne(elementId) {
  return call("modify_element_parameter", {
    element_id: elementId,
    parameter_name: "SK_ITEM",
    value: TARGET_VALUE,
  });
}

async function runBatch(ids) {
  console.log(`[2/2] Updating SK_ITEM = "${TARGET_VALUE}" for ${ids.length} instances (parallel=${PARALLEL})...`);
  let done = 0;
  let failed = 0;
  const failures = [];
  let lastReport = Date.now();

  // Worker-pool pattern.
  let cursor = 0;
  const workers = Array.from({ length: PARALLEL }, async () => {
    while (true) {
      const idx = cursor++;
      if (idx >= ids.length) return;
      const id = ids[idx];
      try {
        await updateOne(id);
        done++;
      } catch (e) {
        failed++;
        failures.push({ id, error: e.message });
      }
      const total = done + failed;
      if (total % 50 === 0 || Date.now() - lastReport > 5000) {
        const pct = ((total / ids.length) * 100).toFixed(1);
        console.log(`  progress: ${total}/${ids.length} (${pct}%) — ok=${done} failed=${failed}`);
        lastReport = Date.now();
      }
    }
  });
  await Promise.all(workers);
  return { done, failed, failures };
}

async function main() {
  const startTime = Date.now();
  console.log(`SK_ITEM batch updater — port ${PORT}, dry-run=${DRY_RUN}`);
  console.log(`Target: SK_ITEM = "${TARGET_VALUE}" on all PSRC DC* instances\n`);

  // Sanity ping.
  try {
    const ping = await call("ping", {});
    console.log(`[ping] connected: doc=${ping.document_name}, elements=${ping.element_count}\n`);
  } catch (e) {
    console.error(`[ping] FAILED: ${e.message}`);
    process.exit(1);
  }

  const ids = await collectAllIds();
  console.log(`\nCollected ${ids.length} PSRC DC* instance IDs.\n`);

  if (DRY_RUN) {
    console.log("[dry-run] Skipping update phase. Would have updated:");
    console.log(`  ${ids.length} instances → SK_ITEM = "${TARGET_VALUE}"`);
    console.log(`  First 10 IDs: ${ids.slice(0, 10).join(", ")}`);
    console.log(`  Last  10 IDs: ${ids.slice(-10).join(", ")}`);
    return;
  }

  const result = await runBatch(ids);
  const elapsed = ((Date.now() - startTime) / 1000).toFixed(1);

  console.log(`\n=== DONE in ${elapsed}s ===`);
  console.log(`  Total:  ${ids.length}`);
  console.log(`  OK:     ${result.done}`);
  console.log(`  Failed: ${result.failed}`);
  if (result.failures.length > 0) {
    console.log(`\nFirst 10 failures:`);
    for (const f of result.failures.slice(0, 10)) {
      console.log(`  ${f.id}: ${f.error}`);
    }
  }
}

main().catch((e) => {
  console.error(`[FATAL] ${e.message}`);
  console.error(e.stack);
  process.exit(1);
});
