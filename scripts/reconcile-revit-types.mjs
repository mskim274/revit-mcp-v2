// Reconcile Revit beam types with the Excel schedule.
//
// Reads the "Revit매칭" sheet from RC_Beam_Schedule.xlsx and applies the
// "Floor 변경 필요" entries to Revit via the new MCP commands:
//   - duplicate_type
//   - rename_type
//   - change_instance_type
//
// For each Revit source type that needs to be split, it:
//   1. Determines which target floor ranges the schedule wants.
//   2. Reads each instance's SK_FL and assigns it to a target.
//   3. Renames the source to the FIRST target, duplicates for the rest.
//   4. Reassigns instances to their target type.
//   5. Marks instances whose SK_FL fits no target with a "(확인필요)" type.
//
// Safety:
//   - DRY_RUN=1 → only print plan, no Revit modifications.
//   - Limits: max 100 source types per run, max 1000 instances per change call.

import fs from "fs";
import { exec } from "child_process";
import { promisify } from "util";

const REVIT_PORT = process.env.REVIT_MCP_PORT || "8181";
const DRY_RUN = process.env.DRY_RUN === "1";
const MAX_TYPES = parseInt(process.env.MAX_TYPES || "100", 10);
const ONLY_BASE = process.env.ONLY_BASE; // e.g. "ACG2" — process only one base for testing

// Read Revit매칭 sheet by extracting from xlsx via Python (already installed).
const xlsxPath = process.env.XLSX || "C:\\Users\\and\\Desktop\\RC_Beam_Schedule.xlsx";
const planFile = "C:\\Users\\and\\AppData\\Local\\Temp\\reconcile_plan.json";

const execAsync = promisify(exec);

// ── Step 1: Extract floor_mismatch entries from xlsx ──────────────

async function extractPlan() {
  const py = `
import json, re
from collections import defaultdict
from openpyxl import load_workbook
wb = load_workbook(r"${xlsxPath}")
ws = wb["Revit매칭"]

plan = []  # one entry per source Revit type to split
in_section = False
for r in range(1, ws.max_row + 1):
    a = ws.cell(r, 1).value
    if a and "Floor 변경 필요" in str(a):
        in_section = True; continue
    if a and ("신규 생성" in str(a) or "Size 불일치" in str(a) or "엑셀에 없음" in str(a) or "일치" in str(a)):
        in_section = False
    if not in_section: continue
    # Row format: # | 엑셀 기준 (목표) | ← | Revit 후보 (변경 대상)
    expected = ws.cell(r, 2).value
    candidates_raw = ws.cell(r, 4).value
    if not expected or not candidates_raw: continue
    # Parse Revit candidates: 'name (id=N); name (id=N); ...'
    cands = re.findall(r'"([^"]+)" \\(id=(\\d+)\\)', candidates_raw)
    if not cands: continue
    # Parse expected name: "Beam_RC, AB1, 27MPa, 500x800, B1F-3F"
    parts = [p.strip() for p in expected.split(",")]
    if len(parts) < 5: continue
    plan.append({
        "expected_name": expected,
        "category": parts[0],
        "base": parts[1],
        "size": parts[3],
        "target_floor": parts[4],
        "revit_candidates": [{"name": n, "id": int(i)} for n, i in cands],
    })

# Group by source Revit type id (the candidate type to be split).
# Multiple expected entries may share same source type.
by_source = defaultdict(lambda: {"source_id": None, "source_name": None, "targets": []})
for p in plan:
    # Use the FIRST candidate as canonical source (they're usually the same set)
    src_id = p["revit_candidates"][0]["id"]
    src_name = p["revit_candidates"][0]["name"]
    by_source[src_id]["source_id"] = src_id
    by_source[src_id]["source_name"] = src_name
    by_source[src_id]["targets"].append({
        "expected_name": p["expected_name"],
        "base": p["base"],
        "size": p["size"],
        "target_floor": p["target_floor"],
    })

print(json.dumps(list(by_source.values()), ensure_ascii=False, indent=2))
`.trim();
  const tempPy = "C:\\Users\\and\\AppData\\Local\\Temp\\extract_plan.py";
  fs.writeFileSync(tempPy, py);
  const { stdout } = await execAsync(`python "${tempPy}"`, { maxBuffer: 50 * 1024 * 1024 });
  fs.writeFileSync(planFile, stdout);
  return JSON.parse(stdout);
}

// ── Step 2: WebSocket call helper ─────────────────────────────────

import WebSocket from "ws";

function callRevit(command, params = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${REVIT_PORT}/`);
    const id = `recon-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
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

// ── Step 3: Floor parsing (matches add_type_summary.py) ───────────

function parseFloorRange(s) {
  if (s === "전체" || s === "ALL") return null; // wildcard
  const floors = new Set();
  for (const part of s.split(/[,\s]+/)) {
    if (!part) continue;
    if (part.includes("-")) {
      const [a, b] = part.split("-", 2);
      const ai = floorLabelToInt(a), bi = floorLabelToInt(b);
      if (ai !== null && bi !== null) {
        const lo = Math.min(ai, bi), hi = Math.max(ai, bi);
        for (let f = lo; f <= hi; f++) {
          if (f === 0) continue; // skip non-existent floor 0
          floors.add(f);
        }
      }
    } else {
      const i = floorLabelToInt(part);
      if (i !== null) floors.add(i);
    }
  }
  return floors;
}

function floorLabelToInt(label) {
  label = label.trim();
  let m = label.match(/^B(\d+)F$/);
  if (m) return -parseInt(m[1]);
  m = label.match(/^(\d+)F$/);
  if (m) return parseInt(m[1]);
  return null;
}

function skflToInt(skfl) {
  return floorLabelToInt(skfl);
}

// ── Step 4: Process one source type ───────────────────────────────

async function processSourceType(group, summary) {
  const { source_id, source_name, targets } = group;
  console.log(`\n=== ${source_name} (id=${source_id}) ===`);
  console.log(`  Targets (${targets.length}):`);
  for (const t of targets) console.log(`    → ${t.expected_name}`);

  // Get all instances of this source type
  let instances;
  try {
    const data = await callRevit("query_elements", {
      category: "StructuralFraming",
      type_filter: source_name.replace(/^[A-Za-z_]+_RC,\s*/, "").trim(),
      summary_only: false,
      limit: 200,
    });
    instances = data.items || [];
  } catch (e) {
    summary.errors.push({ source_name, error: `query_elements: ${e.message}` });
    return;
  }

  if (instances.length === 0) {
    console.log(`  ⚠️ 인스턴스 0개 — type만 변경, instance 재할당 없음`);
  } else {
    console.log(`  인스턴스 ${instances.length}개 — 각각 SK_FL 확인 중...`);
  }

  // For each instance, read SK_FL via get_element_info (we don't have batch)
  const instanceFloors = new Map(); // id → SK_FL string
  for (const inst of instances) {
    try {
      const info = await callRevit("get_element_info", { element_id: inst.id });
      const skfl = info.instance_parameters?.SK_FL;
      if (skfl) instanceFloors.set(inst.id, String(skfl));
    } catch (e) {
      console.log(`    ⚠️ ${inst.id} SK_FL 읽기 실패: ${e.message}`);
    }
  }

  // Plan: for each instance, find target whose floor set contains its SK_FL
  const targetSets = targets.map(t => ({ ...t, floors: parseFloorRange(t.target_floor) }));
  const targetAssignment = new Map(); // target_index → [instance_ids]
  const unmatched = []; // SK_FL doesn't fit any target → (확인필요)

  for (const [instId, skfl] of instanceFloors) {
    const skflInt = skflToInt(skfl);
    let bestTargetIdx = -1;
    for (let i = 0; i < targetSets.length; i++) {
      const ts = targetSets[i];
      if (ts.floors === null) { bestTargetIdx = i; break; } // wildcard
      if (skflInt !== null && ts.floors.has(skflInt)) { bestTargetIdx = i; break; }
    }
    if (bestTargetIdx >= 0) {
      if (!targetAssignment.has(bestTargetIdx)) targetAssignment.set(bestTargetIdx, []);
      targetAssignment.get(bestTargetIdx).push(instId);
    } else {
      unmatched.push({ id: instId, skfl });
    }
  }

  console.log(`  배정 결과:`);
  for (let i = 0; i < targets.length; i++) {
    const ids = targetAssignment.get(i) || [];
    console.log(`    [${i}] ${targets[i].expected_name}  → ${ids.length} instances`);
  }
  if (unmatched.length > 0) {
    console.log(`    [확인필요] ${unmatched.length} instances: SK_FL ∈ {${[...new Set(unmatched.map(u => u.skfl))].join(",")}}`);
  }

  if (DRY_RUN) {
    summary.dry_run_plans.push({ source_name, targets, assignment: Array.from(targetAssignment.entries()), unmatched });
    return;
  }

  // ── Execute ──
  // Strategy: rename source to first target, duplicate for rest.
  // Then reassign instances of remaining targets.
  const firstTarget = targets[0];
  let firstTypeId = source_id;

  // Rename source to first target (only if name differs)
  if (source_name !== firstTarget.expected_name) {
    try {
      await callRevit("rename_type", { type_id: source_id, new_name: firstTarget.expected_name });
      console.log(`  ✓ rename: ${source_name} → ${firstTarget.expected_name}`);
    } catch (e) {
      summary.errors.push({ source_name, error: `rename: ${e.message}` });
      return;
    }
  }

  // Duplicate for additional targets
  const targetTypeIds = [firstTypeId];
  for (let i = 1; i < targets.length; i++) {
    try {
      const r = await callRevit("duplicate_type", {
        source_type_id: source_id,
        new_name: targets[i].expected_name,
      });
      targetTypeIds.push(r.new_type_id);
      console.log(`  ✓ duplicate: → ${targets[i].expected_name} (id=${r.new_type_id})`);
    } catch (e) {
      summary.errors.push({ source_name, error: `duplicate ${targets[i].expected_name}: ${e.message}` });
      targetTypeIds.push(null);
    }
  }

  // Reassign instances of targets[1..N] (target[0] keeps source's renamed type)
  for (let i = 1; i < targets.length; i++) {
    const newTypeId = targetTypeIds[i];
    const ids = targetAssignment.get(i) || [];
    if (!newTypeId || ids.length === 0) continue;
    try {
      const r = await callRevit("change_instance_type", { instance_ids: ids, new_type_id: newTypeId });
      console.log(`  ✓ reassign ${ids.length} → ${targets[i].expected_name}: ${r.changed_count} ok, ${r.failed_count} failed`);
      summary.reassigned += r.changed_count;
    } catch (e) {
      summary.errors.push({ source_name, error: `reassign ${targets[i].expected_name}: ${e.message}` });
    }
  }

  // (확인필요): create a marker type and move unmatched instances
  if (unmatched.length > 0) {
    const checkName = `${firstTarget.expected_name} (확인필요)`;
    let checkTypeId = null;
    try {
      const r = await callRevit("duplicate_type", { source_type_id: source_id, new_name: checkName });
      checkTypeId = r.new_type_id;
      console.log(`  ✓ create (확인필요) type: id=${checkTypeId}`);
    } catch (e) {
      console.log(`  ⚠️ (확인필요) type creation failed: ${e.message}`);
    }
    if (checkTypeId) {
      const ids = unmatched.map(u => u.id);
      try {
        const r = await callRevit("change_instance_type", { instance_ids: ids, new_type_id: checkTypeId });
        console.log(`  ✓ moved ${r.changed_count} instances to (확인필요)`);
        summary.checkneeded += r.changed_count;
      } catch (e) {
        summary.errors.push({ source_name, error: `(확인필요) reassign: ${e.message}` });
      }
    }
  }

  summary.processed_types += 1;
}

// ── Main ──────────────────────────────────────────────────────────

(async () => {
  console.log(`Mode: ${DRY_RUN ? "DRY-RUN (no Revit changes)" : "EXECUTE"}`);
  if (ONLY_BASE) console.log(`Filter: ONLY_BASE=${ONLY_BASE}`);
  console.log(`Extracting plan from ${xlsxPath}...`);

  const groups = await extractPlan();
  console.log(`Found ${groups.length} source Revit types to split.\n`);

  const filtered = ONLY_BASE
    ? groups.filter(g => g.targets.some(t => t.base === ONLY_BASE))
    : groups;
  const limited = filtered.slice(0, MAX_TYPES);
  console.log(`Processing ${limited.length} (filter: ${ONLY_BASE || "none"}, max ${MAX_TYPES})\n`);

  // Verify Revit ping
  try {
    const ping = await callRevit("ping");
    console.log(`Revit ${ping.revit_version} | doc=${ping.document_name} | elements=${ping.element_count}\n`);
  } catch (e) {
    console.error(`Revit not reachable: ${e.message}`);
    process.exit(1);
  }

  const summary = { processed_types: 0, reassigned: 0, checkneeded: 0, errors: [], dry_run_plans: [] };
  for (const g of limited) {
    try {
      await processSourceType(g, summary);
    } catch (e) {
      summary.errors.push({ source_name: g.source_name, error: `unhandled: ${e.message}` });
    }
  }

  console.log(`\n${"=".repeat(60)}`);
  console.log(`Summary:`);
  console.log(`  processed types : ${summary.processed_types}`);
  console.log(`  reassigned      : ${summary.reassigned}`);
  console.log(`  (확인필요)      : ${summary.checkneeded}`);
  console.log(`  errors          : ${summary.errors.length}`);
  if (summary.errors.length > 0) {
    for (const e of summary.errors.slice(0, 10)) console.log(`    - ${e.source_name}: ${e.error}`);
  }

  // Save summary
  const reportPath = "C:\\Users\\and\\Desktop\\reconcile_report.json";
  fs.writeFileSync(reportPath, JSON.stringify(summary, null, 2));
  console.log(`\nReport saved to: ${reportPath}`);
})();
