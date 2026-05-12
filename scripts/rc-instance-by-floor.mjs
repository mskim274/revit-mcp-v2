// Build a (type × SK_FL) matrix of M_Concrete-Rectangular Beam instances.
// One query per SK_FL value (≈15 queries), then subtract overlapping
// substring matches to get exact counts.
//
// Output: JSON file at C:\Users\and\AppData\Local\Temp\rc_instance_matrix.json
// {
//   floors: [B1F, 1F, 2F, ...],
//   per_type: { typeName: { B1F: 5, 1F: 3, ... }, ... },
//   per_floor_total: { B1F: 100, ... },
// }

import WebSocket from "ws";
import fs from "fs";

function call(cmd, params = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket("ws://127.0.0.1:8181/");
    const id = `q-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
    const t = setTimeout(() => { ws.close(); reject(new Error("timeout")); }, 60000);
    ws.on("open", () => ws.send(JSON.stringify({ id, command: cmd, params, timeout_ms: 55000 })));
    ws.on("message", (d) => {
      clearTimeout(t);
      const r = JSON.parse(d.toString());
      ws.close();
      if (r.status === "success") resolve(r.data);
      else reject(new Error(r.error?.message || JSON.stringify(r)));
    });
    ws.on("error", (e) => { clearTimeout(t); reject(e); });
  });
}

// Test floor values to query. Order matters: query specific (B-prefixed)
// before generic single-digit because parameter_value uses CONTAINS match.
const FLOOR_VALUES = [
  "B1F", "B2F",
  "01F", "02F", "03F", "04F", "05F", "06F",
  "1F", "2F", "3F", "4F", "5F", "6F", "7F",
  "전층",
];

// For each (type_filter), iterate floor values
const CATEGORIES = ["Girder_RC", "Beam_RC"];

const matrix = {}; // { typeName: { skfl: count } }
const floorTotals = {}; // { skfl: total }

for (const cat of CATEGORIES) {
  for (const skfl of FLOOR_VALUES) {
    process.stdout.write(`  ${cat} × ${skfl}... `);
    try {
      const r = await call("query_elements", {
        category: "StructuralFraming",
        type_filter: cat,
        parameter_name: "SK_FL",
        parameter_value: skfl,
        summary_only: true,
      });
      const total = r.total;
      const byType = r.by_type || {};
      console.log(`${total} instances`);
      for (const [tn, cnt] of Object.entries(byType)) {
        if (!matrix[tn]) matrix[tn] = {};
        matrix[tn][skfl] = (matrix[tn][skfl] || 0) + cnt;
      }
      floorTotals[skfl] = (floorTotals[skfl] || 0) + total;
    } catch (e) {
      console.log(`FAIL: ${e.message}`);
    }
  }
}

// Subtract overlap: B-prefixed values are CONTAINED in their digit counterpart.
// E.g. SK_FL="B1F" is matched by both "B1F" and "1F" filters.
// So 1F counts include B1F counts. Subtract to get pure.
const overlapPairs = [
  ["1F", "B1F"],   // 1F filter also catches B1F
  ["2F", "B2F"],
  ["1F", "01F"],   // 1F filter also catches 01F (contains "1F")
  ["2F", "02F"],
  ["3F", "03F"],
  ["4F", "04F"],
  ["5F", "05F"],
  ["6F", "06F"],
];

// Apply: subtract child from parent, per type
for (const [parent, child] of overlapPairs) {
  for (const tn of Object.keys(matrix)) {
    if (matrix[tn][parent] && matrix[tn][child]) {
      matrix[tn][parent] -= matrix[tn][child];
      if (matrix[tn][parent] < 0) matrix[tn][parent] = 0;
    }
  }
  if (floorTotals[parent] && floorTotals[child]) {
    floorTotals[parent] -= floorTotals[child];
    if (floorTotals[parent] < 0) floorTotals[parent] = 0;
  }
}

// Drop zero-count cells
for (const tn of Object.keys(matrix)) {
  for (const sk of Object.keys(matrix[tn])) {
    if (matrix[tn][sk] === 0) delete matrix[tn][sk];
  }
  if (Object.keys(matrix[tn]).length === 0) delete matrix[tn];
}

const out = {
  floors: FLOOR_VALUES,
  per_type: matrix,
  per_floor_total: floorTotals,
  type_count: Object.keys(matrix).length,
};

const outPath = "C:\\Users\\and\\AppData\\Local\\Temp\\rc_instance_matrix.json";
fs.writeFileSync(outPath, JSON.stringify(out, null, 2));

console.log(`\nSaved to: ${outPath}`);
console.log(`Types with instances: ${out.type_count}`);
console.log(`\nFloor totals:`);
for (const [sk, n] of Object.entries(floorTotals).sort((a, b) => b[1] - a[1])) {
  if (n > 0) console.log(`  ${sk.padEnd(6)}: ${n}`);
}
