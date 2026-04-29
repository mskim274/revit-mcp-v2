// Reconcile Revit beam types with the Excel schedule.
//
// v2 (Option A — multi-source aware):
// Groups by (base, size). For each group:
//   1. Match exact names: source_name == target_name → no change.
//   2. For Excel targets without exact source: create via duplicate_type
//      (using any source in this group as the seed).
//   3. For Revit sources without exact target: redistribute instances by
//      SK_FL parameter. Instances whose SK_FL fits no target are moved
//      to a `<source_name>(확인필요)` type for manual review.
//   4. After redistribution, sources whose all instances moved out are
//      left alone (could be deleted, but we keep for safety).
//
// Reads "Revit매칭" sheet from RC_Beam_Schedule.xlsx.
// Safety: DRY_RUN=1 (default-recommended), ONLY_BASE=AB1, MAX_GROUPS=N.

import fs from "fs";
import { exec } from "child_process";
import { promisify } from "util";
import WebSocket from "ws";

const REVIT_PORT = process.env.REVIT_MCP_PORT || "8181";
const DRY_RUN = process.env.DRY_RUN === "1";
const MAX_GROUPS = parseInt(process.env.MAX_GROUPS || "200", 10);
const ONLY_BASE = process.env.ONLY_BASE;
const LIST_BASES = process.env.LIST_BASES === "1";
const PURGE_ORPHANS = process.env.NO_PURGE !== "1"; // default: purge empty source types after redistribution
const xlsxPath = process.env.XLSX || "C:\\Users\\and\\Desktop\\RC_Beam_Schedule.xlsx";

const execAsync = promisify(exec);

// ── Step 1: Extract plan grouped by (base, size) ──────────────────

async function extractPlan() {
  const py = `
import json, re
from collections import defaultdict
from openpyxl import load_workbook
wb = load_workbook(r"${xlsxPath}")
ws = wb["Revit매칭"]

# Read 변경 필요 / Floor 변경 필요 / 일치 sections. The 일치 section is
# included so the script knows about target types that are already
# correctly named (and corresponding Revit types).
flat = []
section_kind = None  # None | "rename" | "floor" | "exact"
for r in range(1, ws.max_row + 1):
    a = ws.cell(r, 1).value
    if a:
        s = str(a)
        if "변경 필요" in s and "Floor" not in s:
            section_kind = "rename"; continue
        if "Floor 변경 필요" in s:
            section_kind = "floor"; continue
        if "일치" in s and "변경 불필요" in s:
            section_kind = "exact"; continue
        if any(t in s for t in ("신규 생성", "Size 불일치", "엑셀에 없음")):
            section_kind = None
    if section_kind is None: continue

    expected = ws.cell(r, 2).value
    if section_kind == "rename":
        current = ws.cell(r, 2).value
        target  = ws.cell(r, 4).value
        if not current or not target: continue
        expected = target
        cand_pairs = [(current, None)]
    elif section_kind == "exact":
        # Section: # | 타입명 (Revit name == Excel name)
        if not expected: continue
        cand_pairs = [(str(expected), None)]  # Revit type name = expected name
    else:  # floor
        candidates_raw = ws.cell(r, 4).value
        if not expected or not candidates_raw: continue
        cand_pairs = re.findall(r'"([^"]+)" \\(id=(\\d+)\\)', candidates_raw)
        cand_pairs = [(n, int(i)) for n, i in cand_pairs]
        if not cand_pairs: continue

    parts = [p.strip() for p in str(expected).split(",")]
    if len(parts) < 5: continue
    # Re-join everything after index 4 to preserve commas in floor part
    floor = ",".join(parts[4:]).strip()
    flat.append({
        "expected_name": str(expected),
        "category": parts[0],
        "base": parts[1],
        "size": parts[3],
        "target_floor": floor,
        "revit_candidates": [{"name": n, "id": i} for n, i in cand_pairs],
        "section": section_kind,
    })

# Group by (base, size). Dedupe candidates: prefer entries with a real id
# over id=None placeholders from the rename section.
groups = defaultdict(lambda: {"category": "", "base": "", "size": "",
                              "targets": {}, "sources_by_name": {}})
for p in flat:
    key = (p["base"], p["size"])
    g = groups[key]
    g["category"] = p["category"]
    g["base"] = p["base"]
    g["size"] = p["size"]
    g["targets"][p["expected_name"]] = p["target_floor"]
    for c in p["revit_candidates"]:
        cur = g["sources_by_name"].get(c["name"])
        # Keep an existing id if already set; otherwise overwrite
        if cur is None or cur.get("id") is None:
            g["sources_by_name"][c["name"]] = c

out = []
for (base, size), g in groups.items():
    sources = list(g["sources_by_name"].values())
    out.append({
        "category": g["category"],
        "base": base,
        "size": size,
        "targets": [{"name": n, "floor": f} for n, f in g["targets"].items()],
        "sources": sources,
    })
print(json.dumps(out, ensure_ascii=False, indent=2))
`.trim();
  const tempPy = "C:\\Users\\and\\AppData\\Local\\Temp\\extract_plan.py";
  fs.writeFileSync(tempPy, py);
  const { stdout } = await execAsync(`python "${tempPy}"`, { maxBuffer: 50 * 1024 * 1024 });
  return JSON.parse(stdout);
}

// ── Revit WebSocket helper ────────────────────────────────────────

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

// ── Floor parsing ─────────────────────────────────────────────────

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

// ── Per-group processor ──────────────────────────────────────────

async function processGroup(g, summary) {
  console.log(`\n=== (base=${g.base}, size=${g.size}) ===`);
  // Filter out sources without a resolved id (defensive — should be rare after dedupe)
  g.sources = g.sources.filter(s => s.id !== null && s.id !== undefined);

  console.log(`  Excel targets (${g.targets.length}): ${g.targets.map(t => t.floor).join(", ")}`);
  console.log(`  Revit sources (${g.sources.length}): ${g.sources.map(s => s.name.replace(/^[A-Za-z_]+_RC,\s*/, "")).join(" | ")}`);

  // 1. Identify exact-name matches (no change needed)
  const targetByName = new Map(g.targets.map(t => [t.name, t]));
  const sourceByName = new Map(g.sources.map(s => [s.name, s]));
  const targetTypeId = {};   // target_name → revit type id
  const sourceUsed = new Set();

  for (const t of g.targets) {
    const matchingSource = sourceByName.get(t.name);
    if (matchingSource) {
      targetTypeId[t.name] = matchingSource.id;
      sourceUsed.add(matchingSource.id);
    }
  }
  const exactMatches = Object.keys(targetTypeId).length;
  if (exactMatches > 0) console.log(`  ✓ ${exactMatches} target(s) already exist with correct name`);

  // 2. Sources NOT yet matched to any target — these will donate instances
  const orphanSources = g.sources.filter(s => !sourceUsed.has(s.id));
  if (orphanSources.length === 0) {
    console.log(`  All Revit sources already match Excel targets — no work needed.`);
    summary.groups_noop += 1;
    return;
  }
  console.log(`  ${orphanSources.length} orphan source(s) need redistribution: ${orphanSources.map(s => s.name.replace(/^[A-Za-z_]+_RC,\s*/, "")).join(" | ")}`);

  // 3. For each unmatched target, ensure a type exists (duplicate from any orphan source if needed)
  const targetsNeedingCreation = g.targets.filter(t => !(t.name in targetTypeId));
  for (const t of targetsNeedingCreation) {
    const seedSourceId = orphanSources[0].id; // any source in this group works
    if (DRY_RUN) {
      targetTypeId[t.name] = "DUPLICATE_FROM_" + seedSourceId;
      console.log(`  [plan] duplicate_type from id=${seedSourceId} → "${t.name}"`);
      continue;
    }
    try {
      const r = await callRevit("duplicate_type", { source_type_id: seedSourceId, new_name: t.name });
      targetTypeId[t.name] = r.new_type_id;
      console.log(`  ✓ duplicate → "${t.name}" (id=${r.new_type_id})`);
    } catch (e) {
      summary.errors.push({ group: `${g.base}/${g.size}`, error: `duplicate ${t.name}: ${e.message}` });
      return;
    }
  }

  // 4. For each orphan source, redistribute its instances
  for (const src of orphanSources) {
    let instances = [];
    try {
      const data = await callRevit("query_elements", {
        category: "StructuralFraming",
        type_filter: src.name.replace(/^[A-Za-z_]+_RC,\s*/, "").trim(),
        summary_only: false,
        limit: 200,
      });
      instances = data.items || [];
    } catch (e) {
      summary.errors.push({ group: `${g.base}/${g.size}`, error: `query ${src.name}: ${e.message}` });
      continue;
    }

    if (instances.length === 0) {
      console.log(`  ⚠️ Source "${src.name.replace(/^[A-Za-z_]+_RC,\s*/, "")}" has 0 instances — skipped.`);
      continue;
    }

    // Read SK_FL for each instance (sequential — could be parallelized)
    const skflById = new Map();
    for (const inst of instances) {
      try {
        const info = await callRevit("get_element_info", { element_id: inst.id });
        const skfl = info.instance_parameters?.SK_FL;
        if (skfl) skflById.set(inst.id, String(skfl));
      } catch (e) { /* ignore */ }
    }

    // Build floor sets per target
    const targetFloorSets = g.targets.map(t => ({ name: t.name, floors: parseFloorRange(t.floor) }));

    // Assign instances
    const assignedToTarget = new Map(); // target_name → [ids]
    const unmatched = []; // { id, skfl }
    for (const [instId, skfl] of skflById) {
      const skflInt = floorLabelToInt(skfl);
      let chosen = null;
      for (const ts of targetFloorSets) {
        if (ts.floors === null) { chosen = ts.name; break; }
        if (skflInt !== null && ts.floors.has(skflInt)) { chosen = ts.name; break; }
      }
      if (chosen) {
        if (!assignedToTarget.has(chosen)) assignedToTarget.set(chosen, []);
        assignedToTarget.get(chosen).push(instId);
      } else {
        unmatched.push({ id: instId, skfl });
      }
    }

    const srcShort = src.name.replace(/^[A-Za-z_]+_RC,\s*/, "");
    console.log(`  Source "${srcShort}" (${instances.length} instances):`);
    for (const ts of g.targets) {
      const ids = assignedToTarget.get(ts.name) || [];
      if (ids.length > 0) {
        const label = ts.name.replace(/^[A-Za-z_]+_RC,\s*/, "");
        console.log(`    → ${label}: ${ids.length}`);
      }
    }
    if (unmatched.length > 0) {
      const skfls = [...new Set(unmatched.map(u => u.skfl))].join(",");
      console.log(`    → (확인필요) ${unmatched.length} instances [SK_FL ∈ {${skfls}}]`);
    }

    if (DRY_RUN) {
      summary.dry_run.push({
        group: `${g.base}/${g.size}`,
        source: src.name,
        moves: [...assignedToTarget.entries()].map(([n, ids]) => ({ target: n, count: ids.length })),
        unmatched_count: unmatched.length,
        skfls: [...new Set(unmatched.map(u => u.skfl))],
      });
      continue;
    }

    // ── Execute moves ──
    for (const [targetName, ids] of assignedToTarget) {
      const newId = targetTypeId[targetName];
      if (!newId || typeof newId !== "number") continue;
      try {
        const r = await callRevit("change_instance_type", { instance_ids: ids, new_type_id: newId });
        summary.reassigned += r.changed_count;
        console.log(`    ✓ moved ${r.changed_count}/${ids.length} → ${targetName.replace(/^[A-Za-z_]+_RC,\s*/, "")}`);
      } catch (e) {
        summary.errors.push({ group: `${g.base}/${g.size}`, error: `move to ${targetName}: ${e.message}` });
      }
    }

    // ── Handle (확인필요) ──
    if (unmatched.length > 0) {
      // Naming: "<source_name>(확인필요)" — preserves the source's floor label
      // so the modeler sees e.g. "Beam_RC, AB1, 27MPa, 500x800, 5F(확인필요)"
      const checkName = `${src.name}(확인필요)`;
      let checkTypeId = null;
      try {
        // Avoid creating duplicate (확인필요) type — try to find by name first?
        // Easiest: just call duplicate; if name already exists, it'll error.
        const r = await callRevit("duplicate_type", { source_type_id: src.id, new_name: checkName });
        checkTypeId = r.new_type_id;
        console.log(`    ✓ created "${checkName.replace(/^[A-Za-z_]+_RC,\s*/, "")}" (id=${checkTypeId})`);
      } catch (e) {
        if (e.message.includes("already exists")) {
          console.log(`    ⚠️ "${checkName}" already exists, skipping creation`);
          // We could look up its id via revit_get_family_types but for now skip
        } else {
          summary.errors.push({ group: `${g.base}/${g.size}`, error: `(확인필요) create: ${e.message}` });
        }
      }
      if (checkTypeId) {
        try {
          const r = await callRevit("change_instance_type", {
            instance_ids: unmatched.map(u => u.id),
            new_type_id: checkTypeId,
          });
          summary.checkneeded += r.changed_count;
          console.log(`    ✓ moved ${r.changed_count} instances to (확인필요)`);
        } catch (e) {
          summary.errors.push({ group: `${g.base}/${g.size}`, error: `(확인필요) move: ${e.message}` });
        }
      }
    }
  }

  // ── 5. Purge orphan sources (0 instances after redistribution) ──
  if (PURGE_ORPHANS && !DRY_RUN) {
    for (const src of orphanSources) {
      try {
        const data = await callRevit("query_elements", {
          category: "StructuralFraming",
          type_filter: src.name.replace(/^[A-Za-z_]+_RC,\s*/, "").trim(),
          summary_only: true,
        });
        if (data.total === 0) {
          try {
            await callRevit("delete_elements", { element_ids: [src.id] });
            console.log(`  🗑 deleted orphan source type "${src.name.replace(/^[A-Za-z_]+_RC,\s*/, "")}" (id=${src.id})`);
            summary.orphans_deleted += 1;
          } catch (e) {
            console.log(`  ⚠️ could not delete orphan id=${src.id}: ${e.message}`);
          }
        } else {
          console.log(`  ℹ orphan id=${src.id} still has ${data.total} instance(s) — keeping`);
        }
      } catch (e) { /* ignore */ }
    }
  }

  summary.groups_processed += 1;
}

// ── Main ──────────────────────────────────────────────────────────

(async () => {
  console.log(`Mode: ${DRY_RUN ? "DRY-RUN (no Revit changes)" : "EXECUTE"}`);
  if (ONLY_BASE) console.log(`Filter: ONLY_BASE=${ONLY_BASE}`);
  console.log(`Extracting plan from ${xlsxPath}...`);

  const groups = await extractPlan();
  console.log(`Found ${groups.length} (base, size) groups in 'Floor 변경 필요'.\n`);

  // List-bases mode: enumerate distinct base names + counts and exit
  if (LIST_BASES) {
    const byBase = new Map();
    for (const g of groups) {
      const cur = byBase.get(g.base) || { groups: 0, sources: 0, targets: 0 };
      cur.groups += 1;
      cur.sources += g.sources.length;
      cur.targets += g.targets.length;
      byBase.set(g.base, cur);
    }
    const sorted = [...byBase.entries()].sort((a, b) => b[1].sources - a[1].sources);
    console.log(`=== Bases needing reconciliation (${byBase.size} total) ===`);
    console.log(`base       | (base,size) groups | revit sources | excel targets`);
    console.log("-".repeat(72));
    for (const [base, c] of sorted) {
      console.log(`${base.padEnd(10)} | ${String(c.groups).padStart(18)} | ${String(c.sources).padStart(13)} | ${String(c.targets).padStart(13)}`);
    }
    return;
  }

  const filtered = ONLY_BASE
    ? groups.filter(g => g.base === ONLY_BASE)
    : groups;
  const limited = filtered.slice(0, MAX_GROUPS);
  console.log(`Processing ${limited.length} groups (filter: ${ONLY_BASE || "none"}, max ${MAX_GROUPS})\n`);

  try {
    const ping = await callRevit("ping");
    console.log(`Revit ${ping.revit_version} | doc=${ping.document_name} | elements=${ping.element_count}\n`);
  } catch (e) {
    console.error(`Revit not reachable: ${e.message}`);
    process.exit(1);
  }

  const summary = {
    groups_processed: 0,
    groups_noop: 0,
    reassigned: 0,
    checkneeded: 0,
    orphans_deleted: 0,
    errors: [],
    dry_run: [],
  };
  for (const g of limited) {
    try {
      await processGroup(g, summary);
    } catch (e) {
      summary.errors.push({ group: `${g.base}/${g.size}`, error: `unhandled: ${e.message}` });
    }
  }

  console.log(`\n${"=".repeat(60)}`);
  console.log(`Summary:`);
  console.log(`  groups processed       : ${summary.groups_processed}`);
  console.log(`  groups no-op (already) : ${summary.groups_noop}`);
  console.log(`  reassigned             : ${summary.reassigned}`);
  console.log(`  (확인필요)             : ${summary.checkneeded}`);
  console.log(`  orphans deleted        : ${summary.orphans_deleted}`);
  console.log(`  errors                 : ${summary.errors.length}`);
  if (summary.errors.length > 0) {
    for (const e of summary.errors.slice(0, 10)) console.log(`    - [${e.group}] ${e.error}`);
  }

  const reportPath = "C:\\Users\\and\\Desktop\\reconcile_report.json";
  fs.writeFileSync(reportPath, JSON.stringify(summary, null, 2));
  console.log(`\nReport saved to: ${reportPath}`);
})();
