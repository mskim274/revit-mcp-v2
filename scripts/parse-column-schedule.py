"""
Parse the full RC 기둥 일람표 (51 columns) from AutoCAD selection JSON.

Algorithm:
1. Load all 1,911 text entities.
2. Find 51 column marks (layer=AS-GNRL_TEXT, height=120).
3. For each column mark, identify its "section" (rectangular area in the
   drawing where its floor headers + data cells live):
   - Section spans roughly (mark_x ± 12000, mark_y - 6000 → mark_y + 12000)
   - Floor headers are within the section, height=90, AA-GNRL_TEXT,
     text matches floor pattern (B?\d+F or 4F~5F-style ranges).
4. Detect 좌동 cells by cp949-garbled byte pattern.
5. Detect VOID by height=360 large text.
6. Apply 좌동 inheritance left→right within each row.
7. Group cells by Y-row (data row label position).
8. For each (column mark, floor) cell, build the schedule row.
9. Detect type splits (size change OR rebar change) per column.
10. Output JSON for downstream Excel building.
"""

import json
import io
import re
import sys
from collections import defaultdict

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

SRC = (
    r"C:\Users\ms.kim\.claude\projects\C--Users-ms-kim-Desktop-revit-mcp-v2"
    r"\2f377a06-c04b-4012-8959-38f4fbdfbd33\tool-results"
    r"\toolu_01Whc9hLWEgZiurcCYu51pED.json"
)
OUT = r"C:\Users\ms.kim\AppData\Local\Temp\column-schedule-full.json"


# ─── Load ────────────────────────────────────────────────────────────────
with open(SRC, "r", encoding="utf-8") as f:
    raw = f.read()
outer = json.loads(raw)
data = json.loads(outer[0]["text"])
texts = data["texts"]
print(f"Loaded {len(texts)} text entities")


# ─── Helpers ─────────────────────────────────────────────────────────────
FLOOR_PATTERN = re.compile(
    r"^(B?\d+F)$|"        # B2F, B1F, 1F, 2F, 5F
    r"^(\d+F~\d+F)$|"     # 4F~5F
    r"^(B?\d+F-B?\d+F)$"  # B2F-B1F, 1F-2F
)


def is_floor_label(t):
    txt = t.get("text", "").strip()
    return (
        t.get("layer") == "AA-GNRL_TEXT"
        and t.get("height") == 90.0
        and FLOOR_PATTERN.match(txt) is not None
    )


def is_jwadong(t):
    """좌동 = same-as-left. cp949 bytes \xc1\xc2\xb5\xbf displayed as garbled.
    Detect by: short text (~4 char), height=90, AA-GNRL_TEXT, NOT a floor
    label, NOT a regular ASCII data cell (no digits/letters that match
    typical schedule values like '800x800' or '22-D22').
    """
    if t.get("layer") != "AA-GNRL_TEXT" or t.get("height") != 90.0:
        return False
    txt = t.get("text", "").strip()
    # 좌동 garbled is exactly 4 chars (or sometimes shows as "?�?" patterns)
    if not txt:
        return False
    # If it looks like a real value (digits, mm, x, D, @), it's data not 좌동.
    if re.search(r"[\dx@-]", txt):
        return False
    # If it's just a single dash, it's '-' meaning N/A — keep as data
    if txt == "-":
        return False
    # Korean ASCII labels at left margin (X<2500) are headers, not 좌동.
    if t.get("position", {}).get("x", 0) < 2500:
        return False
    # Otherwise, treat as 좌동
    return True


def is_void(t):
    """VOID labels are tall (height=360) on schedule layer."""
    return t.get("text", "").strip() == "VOID" and t.get("height", 0) >= 200


def is_data_cell(t):
    """Real data values: text contains digits or D-spec or x or - or @."""
    if t.get("layer") != "AA-GNRL_TEXT" or t.get("height") != 90.0:
        return False
    txt = t.get("text", "").strip()
    if not txt:
        return False
    # Skip floor labels (handled separately)
    if FLOOR_PATTERN.match(txt):
        return False
    # Skip 좌동 pattern
    if is_jwadong(t):
        return False
    # Skip left-margin headers
    if t.get("position", {}).get("x", 0) < 2500:
        return False
    # Skip lone "B"/"C" sub-position markers (they sit at X near 23000+
    # offset from each section, often standalone single letter)
    if txt in ("B", "C") and t.get("width", 0) < 2000:
        # heuristic: keep them — they may be sub-position labels
        # but most are not data. Treat as data only if surrounded by data.
        # For now, exclude.
        return False
    # Has digit or D-spec or x or @ — data
    if re.search(r"[\dxD@]", txt):
        return True
    # Single dash = N/A
    if txt == "-":
        return True
    return False


# ─── Find all column marks ───────────────────────────────────────────────
col_marks = []
for t in texts:
    if t.get("layer") == "AS-GNRL_TEXT" and t.get("height") == 120.0:
        col_marks.append(
            {
                "mark": t["text"].strip(),
                "x": t["position"]["x"],
                "y": t["position"]["y"],
            }
        )

print(f"Found {len(col_marks)} column marks")


# ─── For each column mark, find its section ─────────────────────────────
# Section heuristic: based on the C0 example, the column mark sits BELOW its
# table block. The table extends roughly:
#   X: mark_x - 1000 → mark_x + 18000  (width of one column section)
#   Y: mark_y + 1000 → mark_y + 16000  (data above the mark)
# Let me verify by looking at C0: mark at (1930, -140141), the table data
# at Y=-138828 (header row "층") through Y=-142253 (last row).
# So section_y_top = mark_y + 13000, section_y_bot = mark_y - 1000
# section_x_lo = mark_x - 200, section_x_hi = mark_x + 22000

SECTION_X_LO = -500
SECTION_X_HI = 22000  # one section is about 18000 wide
SECTION_Y_BOT = -2000  # bottom slightly below mark
SECTION_Y_TOP = 14000  # top way above mark


def find_section_texts(mark, all_texts):
    """Return texts that belong to this column mark's table section."""
    mx, my = mark["x"], mark["y"]
    return [
        t
        for t in all_texts
        if (mx + SECTION_X_LO) <= t["position"]["x"] <= (mx + SECTION_X_HI)
        and (my + SECTION_Y_BOT) <= t["position"]["y"] <= (my + SECTION_Y_TOP)
    ]


# ─── Y-row clustering ────────────────────────────────────────────────────
# Cells in the same row share a Y within ~50 units
def cluster_by_y(texts_in, tol=50):
    """Return list of rows, each row = list of texts sharing similar Y."""
    sorted_texts = sorted(texts_in, key=lambda t: -t["position"]["y"])
    rows = []
    current = []
    current_y = None
    for t in sorted_texts:
        y = t["position"]["y"]
        if current_y is None or abs(y - current_y) <= tol:
            current.append(t)
            current_y = y if current_y is None else current_y
        else:
            rows.append((current_y, current))
            current = [t]
            current_y = y
    if current:
        rows.append((current_y, current))
    # sort each row by X
    rows = [(y, sorted(row, key=lambda t: t["position"]["x"])) for y, row in rows]
    return rows


# ─── Process each column ─────────────────────────────────────────────────
# Schedule structure inferred from C0 example:
#   Row 1 (top): 층 label header
#   Row 2: floor labels (B2F, B1F, 1F, ...)
#   Row 3: 사이즈 row + size values
#   Row 4: 주철근 row + main rebar values
#   Row 5: 띠근 (TIE BAR sub-section starts) — main label text "TIE BAR"
#   Row 6: 중앙부 + tie center spacing
#   Row 7: 상,하부 + tie top/bottom spacing
#   Row 8: 주근 + sub-rebar count

# Y offsets from mark (positive = above mark):
# C0 example: mark_y = -140141
#   Row at -138828: floor labels (offset +1313)
#   Row at -141453: size (offset -1312 from mark = below) — wait, mark is below data?
# Re-check: mark "C0" at y=-140141. Data above at y=-138828? That's ABOVE the mark
# (less negative). So y_data > y_mark.
#
# Actually the section convention seems to be: COLUMN MARK at the bottom-left
# of its table, table extends UP and RIGHT. Confirmed.
#
# So for each column we look UP from the mark, find rows at increasing Y
# (less negative), and decode them.

print("\n=== Processing each column ===")

results = []
for mark in col_marks:
    section = find_section_texts(mark, texts)
    rows = cluster_by_y(section, tol=80)

    # Find floor header row (the one with multiple floor labels)
    floor_row = None
    floor_row_idx = None
    for i, (y, row) in enumerate(rows):
        floor_cells = [t for t in row if is_floor_label(t)]
        if len(floor_cells) >= 1:
            floor_row = floor_cells
            floor_row_idx = i
            break

    if not floor_row:
        results.append(
            {"mark": mark["mark"], "x": mark["x"], "y": mark["y"], "error": "no floor row"}
        )
        continue

    # Floor X positions
    floors = [(t["text"].strip(), t["position"]["x"]) for t in floor_row]

    # Now build a 2D grid: row_label_y → {floor: value}
    # Data rows are BELOW the floor row (Y less than floor row's Y)
    floor_y = rows[floor_row_idx][0]

    # Data rows have Y < floor_y (more negative)
    data_rows = [r for r in rows if r[0] < floor_y - 50]

    # For each data row, find values per floor (by X proximity to floor X)
    grid = []  # list of {label_text, values: {floor_name: value}}
    for ry, row in data_rows[:10]:  # limit to ~6 expected rows
        # Left-margin header label (X < 2500 from mark X) — actually need
        # to use absolute X since marks may differ. Let's use X < mark.x + 1000
        label_text = None
        values = {}
        # Sort cells in this row by X
        for t in row:
            x = t["position"]["x"]
            if x < mark["x"] + 800:
                # Left header label
                if label_text is None:
                    label_text = t["text"].strip()
            else:
                # Data cell — match to nearest floor by X
                best_floor = None
                best_dx = 99999
                for fname, fx in floors:
                    dx = abs(x - fx)
                    if dx < best_dx:
                        best_dx = dx
                        best_floor = fname
                if best_floor and best_dx < 1500:
                    if is_void(t):
                        values[best_floor] = "VOID"
                    elif is_jwadong(t):
                        values[best_floor] = "_LEFT_"  # placeholder for inheritance
                    elif is_data_cell(t):
                        values[best_floor] = t["text"].strip()
        grid.append({"row_y": ry, "label": label_text, "values": values})

    # Apply 좌동 inheritance (left to right)
    floor_order = [f for f, _ in floors]
    for entry in grid:
        prev_val = None
        for fname in floor_order:
            v = entry["values"].get(fname)
            if v == "_LEFT_":
                entry["values"][fname] = prev_val if prev_val else "좌동(원본없음)"
            elif v is not None:
                prev_val = v

    # Map grid rows to schedule fields by ROW INDEX (positional)
    # Standard order in this schedule template:
    #   [0] 사이즈
    #   [1] 주철근
    #   [2] 주근(?) — sub bar spec
    #   [3] 띠근크기 area or 중앙부
    #   [4] 상,하부
    #   [5] TIE BAR
    # But labels are garbled so let's just keep all rows with their values.
    schedule_per_floor = {}
    for fname, _ in floors:
        schedule_per_floor[fname] = {}
    for i, entry in enumerate(grid):
        for fname in floor_order:
            v = entry["values"].get(fname)
            if v is not None:
                schedule_per_floor[fname][f"row_{i}"] = v

    results.append(
        {
            "mark": mark["mark"],
            "x": mark["x"],
            "y": mark["y"],
            "floors_in_order": floor_order,
            "schedule_per_floor": schedule_per_floor,
            "grid_row_count": len(grid),
        }
    )


# ─── Save ────────────────────────────────────────────────────────────────
with open(OUT, "w", encoding="utf-8") as f:
    json.dump(
        {
            "summary": {
                "total_text_entities": len(texts),
                "total_column_marks": len(col_marks),
                "unique_marks": sorted(set(c["mark"] for c in col_marks)),
            },
            "columns": results,
        },
        f,
        ensure_ascii=False,
        indent=2,
    )

print(f"\nSaved: {OUT}")
print(f"Columns processed: {len(results)}")
errs = [r for r in results if "error" in r]
ok = [r for r in results if "error" not in r]
print(f"  ok:    {len(ok)}")
print(f"  errs:  {len(errs)}")

# Print sample 5
print("\n=== Sample (first 5 columns) ===")
for r in ok[:5]:
    print(f"\n[{r['mark']}]  pos=({r['x']:.0f}, {r['y']:.0f})  floors={r['floors_in_order']}")
    for fname in r["floors_in_order"]:
        sched = r["schedule_per_floor"].get(fname, {})
        vals = " | ".join(f"{k}={v}" for k, v in sched.items())
        print(f"    {fname}: {vals}")
