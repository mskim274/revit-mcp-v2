// Resolve "Size 불일치" entries by moving instances to the correct-size
// Excel target type (already created during 신규 생성 phase).
//
// For each Revit type in the Size 불일치 section:
//   1. Get all instances + SK_FL values.
//   2. Look up Excel entries with same base.
//   3. For each instance, pick the best matching Excel target by SK_FL
//      (specific floor > range > 전체).
//   4. Find the corresponding Revit type by exact name match.
//   5. Move instance via change_instance_type.
//   6. Ambiguous instances (multiple equally-specific candidates) → move
//      to a "(확인필요-사이즈)" type for manual review.
//   7. Source type: if empty after moves → delete.
//
// Usage:
//   DRY_RUN=1 node scripts/modify-size-mismatch.mjs
//   ONLY_BASE=B10 node scripts/modify-size-mismatch.mjs
//   MAX=20 node scripts/modify-size-mismatch.mjs

import { exec } from "child_process";
import { promisify } from "util";
import fs from "fs";
import WebSocket from "ws";

const execAsync = promisify(exec);
const REVIT_PORT = process.env.REVIT_MCP_PORT || "8181";
const DRY_RUN = process.env.DRY_RUN === "1";
const MAX = parseInt(process.env.MAX || "200", 10);
const ONLY_BASE = process.env.ONLY_BASE;
const xlsxPath = process.env.XLSX || "C:\\Users\\and\\Desktop\\RC_Beam_Schedule.xlsx";

// ── Read Size 불일치 + all Excel entries ─────────────────────────

async function readPlan() {
  const py = `
import json
from openpyxl import load_workbook
wb = load_workbook(r"${xlsxPath}")
ws = wb["Revit매칭"]

# 1. Read Size 불일치 entries
mismatches = []
in_section = False
for r in range(1, ws.max_row + 1):
    a = ws.cell(r, 1).value
    if a:
        s = str(a)
        if "Size 불일치" in s:
            in_section = True; continue
        if any(t in s for t in ("엑셀에 없음", "일치", "잔여")):
            in_section = False
    if not in_section: continue
    revit_name = ws.cell(r, 2).value
    alts_raw = ws.cell(r, 3).value
    if not revit_name or not alts_raw or "현재" in str(revit_name): continue
    # Parse Revit name: re-join floor commas (after 4th comma)
    rp = str(revit_name).split(",")
    if len(rp) < 5: continue
    revit_floor = ",".join(p.strip() for p in rp[4:]).strip()
    import re as _re
    # Strip Revit's auto-suffix like " 2", " 3" appended to floor on collision
    revit_floor = _re.sub(r"\\s+\\d+$", "", revit_floor)
    # Strip annotations in base like "AB0 (600)" → "AB0"
    revit_base_raw = rp[1].strip()
    revit_base = _re.sub(r"\\s*\\(\\d+\\)\\s*", "", revit_base_raw).strip()
    revit_size = rp[3].strip().replace(" ", "")
    mismatches.append({
        "revit_name": str(revit_name),
        "revit_base": revit_base,
        "revit_size": revit_size,
        "revit_floor": revit_floor,
    })

# 2. Read all 타입정리 (Excel ground truth)
ws2 = wb["타입정리"]
excel_rows = []
for r in range(1, ws2.max_row + 1):
    line = ws2.cell(r, 1).value
    if not line: continue
    parts = str(line).split(",")
    if len(parts) < 5: continue
    floor = ",".join(p.strip() for p in parts[4:]).strip()
    excel_rows.append({
        "name": str(line),
        "category": parts[0].strip(),
        "base": parts[1].strip(),
        "size": parts[3].strip().replace(" ", ""),
        "floor": floor,
    })

import re
print(json.dumps({"mismatches": mismatches, "excel_rows": excel_rows}, ensure_ascii=False))
`.trim();
  const tmp = "C:\\Users\\and\\AppData\\Local\\Temp\\read_size.py";
  fs.writeFileSync(tmp, py);
  const { stdout } = await execAsync(`python "${tmp}"`, { maxBuffer: 50 * 1024 * 1024 });
  return JSON.parse(stdout);
}

// ── Revit calls ──────────────────────────────────────────────────

function callRevit(command, params = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${REVIT_PORT}/`);
    const id = `mod-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
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

// ── Floor parsing ────────────────────────────────────────────────

function floorLabelToInt(label) {
  label = label.trim();
  let m = label.match(/^B(\d+)F$/);
  if (m) return -parseInt(m[1]);
  m = label.match(/^(\d+)F$/i);
  if (m) return parseInt(m[1]);
  return null;
}

function parseFloorRange(s) {
  if (s === "전체" || s === "ALL") return null;
  const floors = new Set();
  for (const part of s.split(/[,\s]+/)) {
    if (!part) continue;
    if (part.includes("-")) {
      const [a, b] = part.split("-", 2);
      const ai = floorLabelToInt(a), bi = floorLabelToInt(b);
      if (ai !== null && bi !== null) {
        const lo = Math.min(ai, bi), hi = Math.max(ai, bi);
        for (let f = lo; f <= hi; f++) {
          if (f === 0) continue;
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

function specificityScore(floorStr, skflInt) {
  // Specific exact match (single floor) — 3
  // Range match — 2
  // 전체 — 1
  // No match — 0
  if (floorStr === "전체") return 1;
  const set = parseFloorRange(floorStr);
  if (!set || !set.has(skflInt)) return 0;
  if (set.size === 1) return 3;
  return 2;
}

// ── Type cache ───────────────────────────────────────────────────

let typeMap = null;
async function loadTypes() {
  if (typeMap) return typeMap;
  const data = await callRevit("get_family_types", {
    family_name: "M_Concrete-Rectangular Beam",
    include_types: true,
  });
  let types = data?.families?.[0]?.types || [];
  // If response was spilled, read from spill file (handled by harness)
  typeMap = new Map();
  for (const t of types) typeMap.set(t.name, t.id);
  return typeMap;
}

// ── Process one mismatch ─────────────────────────────────────────

async function processOne(m, excelRows, summary) {
  console.log(`\n${m.revit_name}`);
  // Find the Revit type id from the (raw) revit_name
  const tmap = await loadTypes();
  const sourceId = tmap.get(m.revit_name);
  if (!sourceId) {
    console.log(`  ⚠️ source type not found in family — skipping`);
    summary.errors.push({ revit_name: m.revit_name, error: "source not found" });
    return;
  }

  // Get instances of this type
  let instances = [];
  try {
    const data = await callRevit("query_elements", {
      category: "StructuralFraming",
      type_filter: m.revit_name.replace(/^[A-Za-z_]+_RC,\s*/, "").trim(),
      summary_only: false,
      limit: 200,
    });
    instances = data.items || [];
  } catch (e) {
    summary.errors.push({ revit_name: m.revit_name, error: `query: ${e.message}` });
    return;
  }

  if (instances.length === 0) {
    console.log(`  ⚠️ 0 instances — type left intact`);
    return;
  }

  // Read SK_FL for each instance
  const skflById = new Map();
  for (const inst of instances) {
    try {
      const info = await callRevit("get_element_info", { element_id: inst.id });
      const skfl = info.instance_parameters?.SK_FL;
      if (skfl) skflById.set(inst.id, String(skfl));
    } catch (e) { /* ignore */ }
  }

  // Build candidate Excel rows for this base
  const candidates = excelRows.filter(e => e.base === m.revit_base);
  if (candidates.length === 0) {
    console.log(`  ⚠️ no Excel rows for base ${m.revit_base} — skipping`);
    return;
  }

  // Per instance, find best Excel target
  const assignments = new Map(); // target_name → [instance_ids]
  const ambiguous = [];          // {id, skfl, options}
  const unmatched = [];          // {id, skfl}

  for (const [instId, skfl] of skflById) {
    const skflInt = floorLabelToInt(skfl);
    if (skflInt === null) { unmatched.push({ id: instId, skfl }); continue; }

    const scored = candidates.map(c => ({ ...c, score: specificityScore(c.floor, skflInt) }))
      .filter(c => c.score > 0);

    if (scored.length === 0) {
      unmatched.push({ id: instId, skfl });
      continue;
    }

    scored.sort((a, b) => b.score - a.score);
    const top = scored[0];
    const equallySpecific = scored.filter(c => c.score === top.score);
    if (equallySpecific.length > 1) {
      ambiguous.push({ id: instId, skfl, options: equallySpecific.map(c => c.name) });
      continue;
    }

    if (!assignments.has(top.name)) assignments.set(top.name, []);
    assignments.get(top.name).push(instId);
  }

  console.log(`  ${instances.length} instances:`);
  for (const [target, ids] of assignments) {
    console.log(`    → ${target.replace(/^[A-Za-z_]+_RC,\s*/, "")}: ${ids.length}`);
  }
  if (ambiguous.length > 0) console.log(`    → 모호 (확인필요): ${ambiguous.length}`);
  if (unmatched.length > 0) console.log(`    → SK_FL 매칭 없음: ${unmatched.length} [${[...new Set(unmatched.map(u => u.skfl))].join(",")}]`);

  if (DRY_RUN) {
    summary.dryrun.push({ src: m.revit_name, assignments: [...assignments.entries()].map(([n, ids]) => ({ target: n, count: ids.length })), ambiguous: ambiguous.length, unmatched: unmatched.length });
    return;
  }

  // Execute assignments (chunks of 10 to avoid WS timeout)
  for (const [targetName, ids] of assignments) {
    let targetId = tmap.get(targetName);
    if (!targetId) {
      // Auto-create the missing target type
      const tparts = targetName.split(",");
      const tsize = tparts[3]?.trim().replace(" ", "");
      const tm = tsize?.match(/(\d+)x(\d+)/i);
      if (!tm) {
        console.log(`    ⚠️ cannot parse size for "${targetName}" — skipping ${ids.length}`);
        summary.errors.push({ revit_name: m.revit_name, error: `target ${targetName}: unparseable size` });
        continue;
      }
      const tb = parseInt(tm[1]), th = parseInt(tm[2]);
      try {
        const r = await callRevit("duplicate_type", { source_type_id: sourceId, new_name: targetName });
        targetId = r.new_type_id;
        // If source size differs from target size, modify b/h
        const sm = m.revit_size.match(/(\d+)x(\d+)/i);
        if (!sm || parseInt(sm[1]) !== tb || parseInt(sm[2]) !== th) {
          await callRevit("modify_element_parameter", { element_id: targetId, parameter_name: "b", value: tb / 304.8, is_type_param: false });
          await callRevit("modify_element_parameter", { element_id: targetId, parameter_name: "h", value: th / 304.8, is_type_param: false });
        }
        tmap.set(targetName, targetId);
        console.log(`    ✓ auto-created target "${targetName.replace(/^[A-Za-z_]+_RC,\s*/, "")}" (id=${targetId}, b=${tb}, h=${th})`);
      } catch (e) {
        console.log(`    ⚠️ auto-create failed: ${e.message}`);
        summary.errors.push({ revit_name: m.revit_name, error: `auto-create ${targetName}: ${e.message}` });
        continue;
      }
    }
    for (let i = 0; i < ids.length; i += 10) {
      const chunk = ids.slice(i, i + 10);
      try {
        const r = await callRevit("change_instance_type", { instance_ids: chunk, new_type_id: targetId });
        summary.moved += r.changed_count;
      } catch (e) {
        summary.errors.push({ revit_name: m.revit_name, error: `move chunk ${i}: ${e.message}` });
      }
    }
  }

  // Ambiguous + unmatched → (확인필요-사이즈) type
  const reviewIds = [...ambiguous.map(a => a.id), ...unmatched.map(u => u.id)];
  if (reviewIds.length > 0) {
    const checkName = `${m.revit_name}(확인필요-사이즈)`;
    let checkId = tmap.get(checkName);
    if (!checkId) {
      try {
        const r = await callRevit("duplicate_type", { source_type_id: sourceId, new_name: checkName });
        checkId = r.new_type_id;
        tmap.set(checkName, checkId);
        console.log(`    ✓ created (확인필요-사이즈) type id=${checkId}`);
      } catch (e) {
        console.log(`    ⚠️ (확인필요-사이즈) create failed: ${e.message}`);
        return;
      }
    }
    for (let i = 0; i < reviewIds.length; i += 10) {
      const chunk = reviewIds.slice(i, i + 10);
      try {
        await callRevit("change_instance_type", { instance_ids: chunk, new_type_id: checkId });
        summary.checkneeded += chunk.length;
      } catch (e) {
        summary.errors.push({ revit_name: m.revit_name, error: `(확인필요) move: ${e.message}` });
      }
    }
  }

  // Delete source if empty
  try {
    const summary2 = await callRevit("query_elements", {
      category: "StructuralFraming",
      type_filter: m.revit_name.replace(/^[A-Za-z_]+_RC,\s*/, "").trim(),
      summary_only: true,
    });
    if (summary2.total === 0) {
      await callRevit("delete_elements", { element_ids: [sourceId] });
      console.log(`    🗑 deleted empty source`);
      summary.orphans_deleted += 1;
    }
  } catch { /* ignore */ }
}

// ── Main ─────────────────────────────────────────────────────────

(async () => {
  console.log(`Mode: ${DRY_RUN ? "DRY-RUN" : "EXECUTE"}`);
  if (ONLY_BASE) console.log(`Filter: ONLY_BASE=${ONLY_BASE}`);

  const { mismatches, excel_rows } = await readPlan();
  console.log(`Found ${mismatches.length} Size 불일치 entries, ${excel_rows.length} Excel rows`);

  let queue = ONLY_BASE ? mismatches.filter(m => m.revit_base === ONLY_BASE) : mismatches;
  queue = queue.slice(0, MAX);
  console.log(`Processing ${queue.length}\n`);

  try {
    const ping = await callRevit("ping");
    console.log(`Revit ${ping.revit_version} | doc=${ping.document_name}\n`);
  } catch (e) { console.error(`Revit not reachable: ${e.message}`); process.exit(1); }

  const summary = { moved: 0, checkneeded: 0, orphans_deleted: 0, errors: [], dryrun: [] };
  for (const m of queue) {
    try { await processOne(m, excel_rows, summary); }
    catch (e) { summary.errors.push({ revit_name: m.revit_name, error: `unhandled: ${e.message}` }); }
  }

  console.log(`\n${"=".repeat(60)}`);
  console.log(`Summary:`);
  console.log(`  moved              : ${summary.moved}`);
  console.log(`  (확인필요-사이즈)  : ${summary.checkneeded}`);
  console.log(`  orphans deleted    : ${summary.orphans_deleted}`);
  console.log(`  errors             : ${summary.errors.length}`);
  if (summary.errors.length > 0) {
    for (const e of summary.errors.slice(0, 10)) console.log(`  ${e.revit_name}: ${e.error}`);
  }
})();
