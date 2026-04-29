// Auto-reconcile wrapper: processes bases in Excel order, dry-runs each,
// and auto-executes if the plan is clean. Stops & reports when:
//   - dry-run has errors > 0
//   - (확인필요) > 0  — SK_FL doesn't fit any target
//   - multi-source case with more than 1 orphan source (manual review)
//   - unhandled exception
//
// Usage:
//   START=AG100B MAX=20 node scripts/reconcile-auto.mjs
//
//   START : base name to start from (default: from beginning of Excel order)
//   MAX   : max number of bases to process this run (default: 50)
//   SKIP  : comma-separated bases to skip (e.g. "AG12,AG14")

import { exec } from "child_process";
import { promisify } from "util";
import fs from "fs";

const execAsync = promisify(exec);

const START = process.env.START;
const MAX = parseInt(process.env.MAX || "50", 10);
const SKIP = (process.env.SKIP || "").split(",").map(s => s.trim()).filter(Boolean);

// Load bases in Excel 타입정리 order (those needing reconciliation)
const py = `
import sys
sys.stdout.reconfigure(encoding='utf-8')
from openpyxl import load_workbook
wb = load_workbook(r'C:\\\\Users\\\\and\\\\Desktop\\\\RC_Beam_Schedule.xlsx')
ws_type = wb['타입정리']
seen = set(); order = []
for r in range(1, ws_type.max_row + 1):
    line = ws_type.cell(r, 1).value
    if not line: continue
    parts = [p.strip() for p in str(line).split(',')]
    if len(parts) < 2: continue
    base = parts[1]
    if base not in seen:
        seen.add(base); order.append(base)

ws_match = wb['Revit매칭']
floor_change_bases = set()
section = None
for r in range(1, ws_match.max_row + 1):
    a = ws_match.cell(r, 1).value
    if a:
        s = str(a)
        if 'Floor' in s and '필요' in s:
            section = 'floor'; continue
        if any(t in s for t in ('신규', 'Size', '없음', '일치', '변경 필요')):
            section = None
    if section != 'floor': continue
    expected = ws_match.cell(r, 2).value
    if expected:
        parts = [p.strip() for p in str(expected).split(',')]
        if len(parts) >= 2:
            floor_change_bases.add(parts[1])

bases = [b for b in order if b in floor_change_bases]
import json
print(json.dumps(bases))
`.trim();

const tempPy = "C:\\Users\\and\\AppData\\Local\\Temp\\list_bases.py";
fs.writeFileSync(tempPy, py);
const { stdout: basesJson } = await execAsync(`python "${tempPy}"`);
const allBases = JSON.parse(basesJson);
console.log(`Loaded ${allBases.length} bases needing reconciliation\n`);

let startIdx = 0;
if (START) {
  startIdx = allBases.indexOf(START);
  if (startIdx < 0) {
    console.error(`START=${START} not found in base list`);
    process.exit(1);
  }
}

const queue = allBases.slice(startIdx).filter(b => !SKIP.includes(b));
const todo = queue.slice(0, MAX);
console.log(`Processing ${todo.length} bases starting at "${todo[0]}"\n`);

async function runReconcile(base, dryRun) {
  const env = { ...process.env, ONLY_BASE: base };
  if (dryRun) env.DRY_RUN = "1";
  else delete env.DRY_RUN;
  const cmd = `node scripts/reconcile-revit-types.mjs`;
  try {
    const { stdout, stderr } = await execAsync(cmd, { env, maxBuffer: 50 * 1024 * 1024 });
    return stdout + stderr;
  } catch (e) {
    return (e.stdout || "") + (e.stderr || "") + "\n[EXEC ERROR] " + e.message;
  }
}

function parseSummary(output) {
  const m = output.match(/Summary:\s+groups processed\s*:\s*(\d+)\s+groups no-op[^:]*:\s*(\d+)\s+reassigned\s*:\s*(\d+)\s+\(확인필요\)\s*:\s*(\d+)\s+orphans deleted\s*:\s*(\d+)\s+errors\s*:\s*(\d+)/);
  if (!m) return null;
  return {
    groups_processed: +m[1],
    groups_noop: +m[2],
    reassigned: +m[3],
    checkneeded: +m[4],
    orphans_deleted: +m[5],
    errors: +m[6],
  };
}

function checkProblems(output, summary) {
  const problems = [];
  if (!summary) {
    problems.push("Could not parse summary block");
    return problems;
  }
  if (summary.errors > 0) problems.push(`errors=${summary.errors}`);
  if (summary.checkneeded > 0) problems.push(`(확인필요)=${summary.checkneeded}`);
  // Multi-orphan-source: more than 1 orphan to redistribute → manual review
  const orphanCount = (output.match(/orphan source\(s\) need redistribution/g) || []).length;
  if (orphanCount > 0) {
    const m = output.match(/(\d+) orphan source\(s\) need redistribution/g) || [];
    for (const line of m) {
      const n = parseInt(line.match(/\d+/)[0]);
      if (n >= 3) problems.push(`${n} orphan sources (manual review recommended)`);
    }
  }
  return problems;
}

const totals = { processed: 0, reassigned: 0, orphans: 0, noop: 0, paused: [] };

for (const base of todo) {
  console.log(`\n${"=".repeat(70)}\n[${base}]`);
  // Dry-run
  const dryOut = await runReconcile(base, true);
  const drySummary = parseSummary(dryOut);
  const problems = checkProblems(dryOut, drySummary);

  if (problems.length > 0) {
    console.log(`⏸ Pausing on ${base}: ${problems.join(", ")}`);
    console.log(`  Last lines of dry-run output:`);
    console.log(dryOut.split("\n").slice(-30).join("\n").split("\n").map(l => "    " + l).join("\n"));
    totals.paused.push({ base, problems });
    continue; // skip; user will handle this base manually later
  }

  // Auto-execute
  console.log(`  ✓ Dry-run clean — executing...`);
  const execOut = await runReconcile(base, false);
  const execSummary = parseSummary(execOut);
  if (!execSummary) {
    console.log(`  ⚠️ Could not parse exec summary for ${base}`);
    totals.paused.push({ base, problems: ["unparseable exec output"] });
    continue;
  }
  console.log(`  ✓ ${base}: groups ${execSummary.groups_processed} processed (${execSummary.groups_noop} no-op), reassigned ${execSummary.reassigned}, orphans deleted ${execSummary.orphans_deleted}${execSummary.errors ? `, errors ${execSummary.errors}` : ""}`);
  totals.processed += 1;
  totals.reassigned += execSummary.reassigned;
  totals.orphans += execSummary.orphans_deleted;
  totals.noop += execSummary.groups_noop;
}

console.log(`\n${"=".repeat(70)}`);
console.log(`Auto-batch summary:`);
console.log(`  bases processed   : ${totals.processed}`);
console.log(`  groups no-op      : ${totals.noop}`);
console.log(`  total reassigned  : ${totals.reassigned}`);
console.log(`  orphans deleted   : ${totals.orphans}`);
console.log(`  paused (manual)   : ${totals.paused.length}`);
if (totals.paused.length > 0) {
  console.log(`\n=== Paused bases (need manual review) ===`);
  for (const p of totals.paused) {
    console.log(`  ${p.base}: ${p.problems.join(", ")}`);
  }
}
