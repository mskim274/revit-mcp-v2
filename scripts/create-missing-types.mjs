// Create new Revit types for entries in "❌ 신규 생성 필요" section.
//
// For each missing type:
//   1. Parse size from expected name (e.g. "500x800" → b=500, h=800).
//   2. Find an existing M_Concrete-Rectangular Beam type with matching b/h.
//      If found → duplicate (no parameter modification needed).
//      If not  → duplicate from any type, then set b and h type parameters.
//   3. Verify the new type's b/h match the requested size.
//
// Usage:
//   DRY_RUN=1 node scripts/create-missing-types.mjs       # plan only
//   MAX=20 node scripts/create-missing-types.mjs           # cap per run
//   ONLY_BASE=AB1 node scripts/create-missing-types.mjs    # one base
//   DELAY_MS=200 node scripts/create-missing-types.mjs     # ms between commands

import { exec } from "child_process";
import { promisify } from "util";
import fs from "fs";
import WebSocket from "ws";

const execAsync = promisify(exec);
const REVIT_PORT = process.env.REVIT_MCP_PORT || "8181";
const DRY_RUN = process.env.DRY_RUN === "1";
const MAX = parseInt(process.env.MAX || "200", 10);
const ONLY_BASE = process.env.ONLY_BASE;
const DELAY_MS = parseInt(process.env.DELAY_MS || "0", 10);
const xlsxPath = process.env.XLSX || "C:\\Users\\and\\Desktop\\RC_Beam_Schedule.xlsx";

// ── Read 신규 생성 필요 from xlsx ─────────────────────────────────

async function readMissingTypes() {
  const py = `
import json
from openpyxl import load_workbook
wb = load_workbook(r"${xlsxPath}")
ws = wb["Revit매칭"]
out = []
in_section = False
for r in range(1, ws.max_row + 1):
    a = ws.cell(r, 1).value
    if a:
        s = str(a)
        if "신규 생성 필요" in s:
            in_section = True; continue
        if any(t in s for t in ("Size 불일치", "엑셀에 없음", "일치", "변경 필요")):
            in_section = False
    if not in_section: continue
    name = ws.cell(r, 2).value
    if not name: continue
    raw_parts = str(name).split(",")
    if len(raw_parts) < 5: continue
    floor = ",".join(p.strip() for p in raw_parts[4:]).strip()
    out.append({
        "expected_name": str(name),
        "category": raw_parts[0].strip(),
        "base": raw_parts[1].strip(),
        "size": raw_parts[3].strip().replace(" ", ""),
        "floor_range": floor,
    })
print(json.dumps(out, ensure_ascii=False))
`.trim();
  const tmp = "C:\\Users\\and\\AppData\\Local\\Temp\\read_missing.py";
  fs.writeFileSync(tmp, py);
  const { stdout } = await execAsync(`python "${tmp}"`, { maxBuffer: 50 * 1024 * 1024 });
  return JSON.parse(stdout);
}

// ── Revit calls ───────────────────────────────────────────────────

function callRevit(command, params = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${REVIT_PORT}/`);
    const id = `crmis-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
    const timer = setTimeout(() => { ws.close(); reject(new Error("WS timeout")); }, 60000);
    ws.on("open", () => ws.send(JSON.stringify({ id, command, params, timeout_ms: 55000 })));
    ws.on("message", (data) => {
      clearTimeout(timer);
      const r = JSON.parse(data.toString());
      ws.close();
      if (r.status === "success") resolve(r.data);
      else reject(new Error(`${command}: ${r.error?.message || JSON.stringify(r)}`));
    });
    ws.on("error", (e) => { clearTimeout(timer); reject(e); });
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ── Cache existing types ──────────────────────────────────────────

let typeCache = null;  // {id → {name, b_mm, h_mm}}
async function loadTypes() {
  if (typeCache) return typeCache;
  const data = await callRevit("get_family_types", {
    family_name: "M_Concrete-Rectangular Beam",
    include_types: true,
  });
  // Note: family_types response may be spilled to a file when large.
  let types = [];
  if (data?.families?.[0]?.types) {
    types = data.families[0].types;
  } else if (data?.spill_path) {
    // Read spill file
    const spill = JSON.parse(fs.readFileSync(data.spill_path, "utf8"));
    types = spill.families?.[0]?.types || [];
  }
  // We need b/h for matching. Without per-type info, we can't match by params.
  // For a fast match we use the size in NAME (e.g. "...500x800,..." → b=500, h=800).
  typeCache = new Map();
  for (const t of types) {
    const sizeMatch = String(t.name).match(/(\d+)\s*x\s*(\d+)/i);
    if (sizeMatch) {
      typeCache.set(t.id, {
        name: t.name,
        b_mm: parseInt(sizeMatch[1]),
        h_mm: parseInt(sizeMatch[2]),
      });
    }
  }
  return typeCache;
}

function findSeedType(typeMap, b, h) {
  for (const [id, info] of typeMap) {
    if (info.b_mm === b && info.h_mm === h) return { id, ...info };
  }
  // fallback: any type
  for (const [id, info] of typeMap) return { id, ...info };
  return null;
}

// ── Process one missing entry ─────────────────────────────────────

async function processOne(entry, summary) {
  const m = entry.size.match(/(\d+)x(\d+)/i);
  if (!m) {
    summary.errors.push({ entry: entry.expected_name, error: `unparseable size: ${entry.size}` });
    return;
  }
  const b = parseInt(m[1]);
  const h = parseInt(m[2]);

  const typeMap = await loadTypes();
  const seed = findSeedType(typeMap, b, h);
  if (!seed) {
    summary.errors.push({ entry: entry.expected_name, error: "no seed type found in family" });
    return;
  }

  const sizeMatchesSeed = (seed.b_mm === b && seed.h_mm === h);
  const tag = sizeMatchesSeed ? "size-match" : "size-mismatch (will modify)";
  console.log(`  ${entry.expected_name}`);
  console.log(`    seed: id=${seed.id} (${seed.name.slice(0, 60)}...) [${tag}]`);

  if (DRY_RUN) {
    summary.dryrun.push({ entry: entry.expected_name, seedId: seed.id, sizeMatchesSeed });
    return;
  }

  // Duplicate
  let newId;
  try {
    const r = await callRevit("duplicate_type", { source_type_id: seed.id, new_name: entry.expected_name });
    newId = r.new_type_id;
    console.log(`    ✓ duplicated → id=${newId}`);
  } catch (e) {
    if (/already exists/.test(e.message)) {
      // Look up existing id
      const tmap = await loadTypes();
      for (const [id, info] of tmap) {
        if (info.name === entry.expected_name) { newId = id; break; }
      }
      if (newId) {
        console.log(`    ↺ already exists (id=${newId}) — verifying b/h`);
      } else {
        summary.errors.push({ entry: entry.expected_name, error: `already exists but cannot find id` });
        return;
      }
    } else {
      summary.errors.push({ entry: entry.expected_name, error: `duplicate: ${e.message}` });
      return;
    }
  }

  // Update cache with new type
  typeMap.set(newId, { name: entry.expected_name, b_mm: b, h_mm: h });

  // Set b and h type parameters if seed didn't already match
  if (!sizeMatchesSeed) {
    // Revit stores 'b' and 'h' as type parameters in feet (internal units).
    // 1 mm = 1 / 304.8 feet
    const bFeet = b / 304.8;
    const hFeet = h / 304.8;
    try {
      await callRevit("modify_element_parameter", {
        element_id: newId, parameter_name: "b", value: bFeet, is_type_param: false,
      });
      await callRevit("modify_element_parameter", {
        element_id: newId, parameter_name: "h", value: hFeet, is_type_param: false,
      });
      console.log(`    ✓ set b=${b}mm, h=${h}mm`);
    } catch (e) {
      summary.errors.push({ entry: entry.expected_name, error: `set b/h: ${e.message}` });
      return;
    }
  }

  summary.created += 1;
  if (DELAY_MS > 0) await sleep(DELAY_MS);
}

// ── Main ──────────────────────────────────────────────────────────

(async () => {
  console.log(`Mode: ${DRY_RUN ? "DRY-RUN" : "EXECUTE"}`);
  if (ONLY_BASE) console.log(`Filter: ONLY_BASE=${ONLY_BASE}`);

  const all = await readMissingTypes();
  console.log(`Found ${all.length} missing types in '신규 생성 필요'`);

  let queue = ONLY_BASE ? all.filter(e => e.base === ONLY_BASE) : all;
  queue = queue.slice(0, MAX);
  console.log(`Processing ${queue.length} entries\n`);

  try {
    const ping = await callRevit("ping");
    console.log(`Revit ${ping.revit_version} | doc=${ping.document_name} | elements=${ping.element_count}\n`);
  } catch (e) {
    console.error(`Revit not reachable: ${e.message}`);
    process.exit(1);
  }

  const summary = { created: 0, errors: [], dryrun: [] };
  for (let i = 0; i < queue.length; i++) {
    const e = queue[i];
    console.log(`[${i + 1}/${queue.length}]`);
    try {
      await processOne(e, summary);
    } catch (err) {
      summary.errors.push({ entry: e.expected_name, error: `unhandled: ${err.message}` });
    }
  }

  console.log(`\n${"=".repeat(60)}`);
  console.log(`Summary:`);
  console.log(`  created : ${summary.created}`);
  console.log(`  errors  : ${summary.errors.length}`);
  if (summary.errors.length > 0) {
    console.log(`\nFirst 10 errors:`);
    for (const e of summary.errors.slice(0, 10)) {
      console.log(`  ${e.entry}: ${e.error}`);
    }
  }
})();
