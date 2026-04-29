// Fix Revit types with corrupted Korean characters in their names.
// Pattern observed: "전층" got corrupted to "��ü" (U+FFFD U+FFFD ü).

import WebSocket from "ws";

function callRevit(command, params = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket("ws://127.0.0.1:8181/");
    const id = `fix-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
    const timer = setTimeout(() => { ws.close(); reject(new Error("WS timeout")); }, 30000);
    ws.on("open", () => ws.send(JSON.stringify({ id, command, params, timeout_ms: 25000 })));
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

const broken = [
  { id: 5366483, name: "Beam_RC, B1, 27MPa, 400x500, 전층" },
  { id: 5366495, name: "Beam_RC, B2, 27MPa, 500x600, 전층" },
  { id: 5366289, name: "Girder_RC, G1, 27MPa, 400x500, 전층" },
  { id: 5366405, name: "Girder_RC, RAWG1, 27MPa, 400x600, 전층" },
  { id: 5366407, name: "Girder_RC, RAWG1A, 27MPa, 900x600, 전층" },
  { id: 5366409, name: "Girder_RC, RAWG1B, 27MPa, 600x600, 전층" },
  { id: 5366411, name: "Girder_RC, RAWG1C, 27MPa, 1100x600, 전층" },
  { id: 5366413, name: "Girder_RC, WG0, 27MPa, 500x500, 전층" },
  { id: 5366417, name: "Girder_RC, WG1, 27MPa, 500x500, 전층" },
  { id: 5366415, name: "Girder_RC, WG1, 27MPa, 500x800, 전층" },
  { id: 5366421, name: "Girder_RC, WG1A, 27MPa, 800x800, 전층" },
  { id: 5366423, name: "Girder_RC, WG2, 27MPa, 600x1300, 전층" },
  { id: 5366425, name: "Girder_RC, WG20, 27MPa, 600x1100, 전층" },
  { id: 5366427, name: "Girder_RC, WG600, 27MPa, 600x500, 전층" },
];

const ping = await callRevit("ping");
console.log(`Revit connected: ${ping.document_name}\n`);

let success = 0, failed = 0;
for (const item of broken) {
  try {
    const r = await callRevit("rename_type", { type_id: item.id, new_name: item.name });
    console.log(`✓ id=${item.id} → "${item.name}"`);
    success++;
  } catch (e) {
    console.log(`✗ id=${item.id}: ${e.message}`);
    failed++;
  }
}
console.log(`\nFixed ${success}/${broken.length}, failed ${failed}`);
