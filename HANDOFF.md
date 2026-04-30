# Session Handoff

Rolling notes for continuing work across machines and Claude sessions.
Update this file at the **end of every session** so the next one (on
any machine) can pick up in under a minute.

> **New Claude session on this repo?** Read this file first, then
> `CLAUDE.md` (conventions + architecture), then the latest entry in
> `CHANGELOG.md` (shipped features). That's 3 files, ~5 minutes.

---

## Current state — 2026-04-30 (RC reconciliation: Phase 5 완료)

### 오늘(2026-04-30) 추가 작업

회사 머신에서 Phase 5 진행 — 미반영 127건 분류 + 정합성 보정.

**처리 결과:**
- **14건 비고 정정** (이미 Revit에 있던 type, Excel 비고만 ⚠️→✅)
- **25건 placeholder type 신규 생성**:
  - Case 1: AWG201 Beam→Girder rename
  - Cases 2-13: 단일 후보 12건 (1F를 source로 가까운 floor placeholder)
  - Cases 14-25: 다중 후보 11건 (가장 가까운 floor source)
- **B0 사이즈 보정**: 158 instances (3F:39 + 4F:70 + 5F:49) 500x600→500x700 이동
  - source 3개 (id 609897, 609813, 609437) 삭제
  - 패턴: 일람표 도면대로 모델 정합성 보정
- **30건 type 정리**: 22 orphan + 6 동일사이즈 중복 + 2 already-deleted

**최종 정합성 (Excel 처리내역 시트에 기록):**
- ✅ Excel-Revit 일치: **1,004 / 1,091** (92.0%)
  - 기존 유지: 578 / 신규 생성: 426
- ⚠️ 미반영: **87** (전부 VIOD, 모델 의도적 미반영)
- 동일 (mark,size,floor) 중복: 0건, 사이즈 mismatch: 0건

**잔여 검증 권장:**
- 인스턴스 카운트 30,947 → 30,931 (-16) 발견. Sync to Central 후 재확인 필요.
  825 dependent elements 삭제 중 일부 secondary impact일 수도 (schedule cell, tag 등).
- VIOD 87건 비고 정리 (가장 안전한 batch): 다음 세션

### 새 도구 (오늘 작성)
- `scripts/batch-create-single-candidates.mjs` — 11건 일괄 duplicate
- `scripts/migrate-b0-sizes.mjs` — instance 일괄 type 변경 (batch=10 chunked)

### Workshare 새 발견
- `change_instance_type` batch=50 → "operation canceled" 발생
- batch=10이 안전. HANDOFF 권장사항 재확인됨.

### Last commit
오늘 RC 보 작업 commit 완료 (`9087df6`). 추가로 column 일람표 추출
시작 — 아직 Revit 반영 전, Excel 정리만 됨.

### 추가: RC 기둥 일람표 추출 시작 (오후)

회사 머신에서 AutoCAD 도면 `A71 기둥일람표/...섹터(B,C섹터) RC기둥 일람표-1~13.dwg`
의 첫 시트(RC기둥 일람표-1)를 선택해서 파싱.

**추출 결과:** 4 column types — C0, C0A, C0B-1, C0B-2

**Revit 명명규칙(`Column_RC, [부호], [강도], [B×D], [층범위]`)에 매핑:**
- 6개 type 도출 (VOID 구간 분리 + 주철근 22-D22→22-D19 변경점 분리)
- 모두 ⚠️ Revit 미반영 상태 (다음 세션에 보 작업과 동일 워크플로우로 반영 예정)

**미해결 가정 (모델러 검토 필요):**
- 강도: 일람표에 명시 없어서 40MPa 가정 (기존 RC column 다수가 40MPa)
- C0B-1의 VOID 범위: B1F+1F 둘 다 VOID로 보수 해석 (한 층만일 수도)
- 부호 prefix: `C0` 그대로 vs `BC0`/`CC0` (B/C 섹터 구분)

**파일:** `작업자료/2026-04-30/RC_Column_Schedule.xlsx` (gitignored)
- 6 시트 (RC_Beam_Schedule.xlsx와 동일 구조)
- 도구: `scripts/build-column-schedule.py`

**잔여:** 나머지 12개 시트 (RC기둥 일람표-2~13) 미추출.

### 추가 (오후 늦게): 전체 13시트 추출 시도 → 51개 컬럼 발견

사용자가 도면 전체를 다시 선택해서 877 KB 데이터 추출. **Agent로 위임 시도했지만 컨텍스트 95% 상황에서 시스템이 자동 거부** → 직접 Python 파서로 우회.

**결과 (`scripts/parse-column-schedule.py`):**
- 전체 entities: 11,742
- 텍스트 entities: 1,911
- 컬럼 부호 발견: **51개 (모두 unique)**
- 파싱 성공: 50/51 (1개 floor row 못 찾음)

**51개 부호 목록:**
C0, C0A, C0B-1, C0B-2, C1, C1-1, C10, C10-2, C11, C11A-1, C11B,
C11C, C11C-1, C11CA, C12A-1, C13A, C14, C15, C15-1, C15-2, C15A,
C15B, C16, C1A, C1A-1, C2, C21, C21-1, C21A, C22, C22-1, C22-2,
C22-3, C22A, C22B, C22C, C22C-1, C23, C23A, C23C, C24, C2B, C3,
C3A, C3A-1, C4, C4(R), C4-1, C4-2, C4A, C4B

**발견된 패턴:**
- 강합성 컬럼 다수 발견 — H형강 매립 (예: `100H 428x407x20x35`,
  `<PC11C>`, `<P2>`, `1-M19@200(FLG)`)
- 사이즈/철근 floor별 변경 빈번
- VOID 셀 존재
- 좌동 인식 정상 작동

**저장 파일 (둘 다 gitignored 폴더):**
- `작업자료/2026-04-30/column-schedule-raw.json` — Python 파서 결과 (50 컬럼 raw)
- `작업자료/2026-04-30/RC_Column_Schedule.xlsx` — 4 컬럼 시트 (이전, 부족)

**다음 세션 작업 (필수):**
1. raw JSON을 깨끗한 Excel 시트로 변환 (50 컬럼 × 8 row 매트릭스)
2. row 라벨 정규화 (사이즈/주철근/주근/띠근중앙부/띠근상하부/TIE BAR)
3. 사이즈/철근 변경점 자동 검출 → Revit type 분리
4. Revit 명명규칙 매핑: `Column_RC, [부호], [강도], [B×D], [층범위]`
5. 강합성 컬럼 별도 처리 방안 (RC family 외 별도?)
6. 모델러 검토 항목 (강도 / VOID 범위 / 부호 prefix)

**도구:**
- `scripts/parse-column-schedule.py` — 877 KB JSON 직접 파싱 (Agent 우회)

---

## Earlier — 2026-04-29 저녁 (RC reconciliation: 거의 완료)

### Active task: 엑셀 일람표 → Revit 타입 자동 동기화

엑셀 `RC_Beam_Schedule.xlsx` (1091 entries 타입정리)에 정리된 RC 보
타입을, Revit 패밀리 `M_Concrete-Rectangular Beam`에 1:1 반영.

### 처리 완료 단계

**1. Floor 변경 필요 (143 base)** — `reconcile-auto.mjs` 자동 batch
- ~1,612 instances 재배정, 94 orphan 삭제
- 1개 (G0) + 1개 (B0B) 사이즈 불일치 케이스는 **수동으로
  `(확인필요-실제500x700)` rename**으로 표시 (G0 73개, B0B 29개 instances)

**2. 신규 생성 필요 (177)** — `create-missing-types.mjs`
- 177 타입 모두 신규 생성, b/h 자동 보정 (mm → ft 변환)
- 2건 "already exists" → rename trick으로 우회

**3. Size 불일치 (61)** — `modify-size-mismatch.mjs`
- ~1,269 instances Excel 기준 사이즈로 이동
- 59 orphan 삭제 (사이즈 잘못된 타입)
- Auto-create missing target with b/h modify

**4. 깨진 한글 정정 (14)** — `fix-broken-korean.mjs`
- "전체" → "전층" 일괄 변경 (Revit 14 + Excel 29 cells)
- 원인: Python execAsync 출력이 cp949로 잘못 디코딩

**총 ~2,881 instances 정렬 완료, 184+ orphan 삭제.**

### 최종 매칭 상태 (compare 결과)

| 분류 | 시작 | 종료 | 변화 |
|---|---|---|---|
| ✅ 일치 | 577 | **965** | +388 (88.5%) |
| ❌ 신규 생성 | 177 | **0** | -177 ✅ |
| 🔄 Floor 변경 | 247 | 37 | -210 |
| ⚡ Size 불일치 | 63 | (해결) | -63 ✅ |
| ⚠️ 미반영 | - | 127 | (잔여) |

### 적용된 핵심 결정

- **Option B (원본 라벨 보존)**: `1,3ACG23` 같은 단일 라벨 다중 floor →
  split하지 않고 "1F,3F" 한 type으로 유지.
- **(확인필요-실제size)**: Excel과 Revit 사이즈 불일치 케이스 marker로 표시.
- **(확인필요-사이즈)**: 자동 매칭 안 되는 ambiguous 케이스 marker.
- **Orphan 자동 삭제**: redistribution 후 비어있는 source type 자동 삭제.
- **전층** (not 전체): "ALL" 접두사 부호의 한국어 표기.

### 신규 Revit MCP 명령 (b8109a9에서 추가)

| 도구 | 역할 |
|---|---|
| `revit_duplicate_type` | ElementType.Duplicate(name) |
| `revit_rename_type` | ElementType.Name = newName |
| `revit_change_instance_type` | Element.ChangeTypeId, batch 1~1000 |

### 작업 도구 (scripts/)

```
reconcile-revit-types.mjs    ← Floor 변경 batch (multi-source aware)
reconcile-auto.mjs           ← 자동 진행 wrapper (dry-run + execute)
create-missing-types.mjs     ← 신규 타입 생성 + b/h 자동 보정
modify-size-mismatch.mjs     ← Size 불일치 처리 (auto-create missing targets)
fix-broken-korean.mjs        ← 한글 인코딩 깨짐 일괄 수정
analyze-rc-beams.mjs         ← AutoCAD 일람표 추출
verify-rc-beams.mjs          ← 일람표 검증
%TEMP%\add_type_summary.py   ← 타입정리 시트 생성기
%TEMP%\compare_revit_excel.py ← Revit매칭 시트 생성기
%TEMP%\add_action_log.py     ← 비고 컬럼 + 처리내역 시트 추가
```

### 엑셀 시트 구조 (RC_Beam_Schedule.xlsx)

```
처리내역      ← NEW: 단계별 결과 + 사용 도구/스크립트 요약
타입정리       ← 1091 entries + 비고 컬럼 (✅ 기존 유지 / ✅ 신규 생성 / ⚠️ 미반영)
Revit매칭     ← 5 카테고리별 비교 결과
RC Beam Schedule ← 원본 1091 entries (시트별)
시트별 요약    ← 50 시트 카운트
출처          ← 메타데이터
```

### 잔여 작업

- [ ] **127 미반영** entries 검토 — 형식 차이 또는 잔여 floor 변경 (37건)
- [ ] **(확인필요) 타입들** 모델러와 함께 도면 직접 검토
  - G0: 73 instances 사이즈 변경 필요
  - B0B: 29 instances 사이즈 변경 필요
- [ ] Sync to Central 권장
- [ ] 다른 패밀리 (M_Concrete-Rectangular Column 등)에 동일 워크플로우 적용 가능

### Workshare 주의

- Workshared 모델에서 다른 사용자 락 시 트랜잭션 cancel
- WS timeout (25s) 회피 위해 batch size 10으로 chunked
- 권장: 단독 시간대, Sync 자주

### Last commit
`42203d9` — Reconcile script v2 — multi-source aware + 일치 section + comma-floor

---

## Earlier states

### What just shipped

- **v0.4.0** released end-to-end. Both artifacts live:
  - GitHub Release: https://github.com/mskim274/revit-mcp-v2/releases/tag/v0.4.0
  - npm package: https://www.npmjs.com/package/@kimminsub/revit-mcp
- Phase P2 (TS server → npm publish) complete. Claude Desktop users can
  install the MCP half with a single `npx -y @kimminsub/revit-mcp` line.
- Plugin (C#) is at v0.4.0 deployed locally in `%APPDATA%\Autodesk\Revit\Addins\2025\`.

### Active development target

Nothing in-flight. Last commit (`fafe86f`) is a clean CI fix; the repo
is release-green on `main`.

### Decision pending (user to pick)

Next phase is one of:

1. **Phase P3 — WiX MSI installer** (~1 day)
   - Windows-standard `.msi` with "Close Revit" prompt.
   - Sets up enterprise deployment path.
   - Code signing is a separate future item (P4).
2. **Harness Tier 2** (~half day)
   - Command risk classification (Read / Modify / Destructive attributes).
   - WebSocket circuit breaker.
   - Loop fingerprinting.
3. **Sprint 5 — real features** (~1–2 weeks)
   - Worksharing / central model operations.
   - Linked models.
   - Family loading.
   - Export (schedule → CSV, PDF, DWG).
4. **Pause for real-world feedback** — exercise v0.4.0 on an actual
   project, collect pain points, let findings drive priorities.

---

## Environment / infra state (non-portable)

These live on the original dev machine and need one-time setup on
another machine (see `docs/SETUP.md`):

- `REVIT_2025_PATH` env var (local builds only; CI uses Nice3point).
- Revit 2025 license activated.
- Claude Desktop config: `claude_desktop_config.json` points at either
  the local `dist/index.js` OR `npx @kimminsub/revit-mcp` (post-v0.4.0
  recommended).

### Secrets that exist but aren't in git

- `NPM_TOKEN` — granular access token, "Read and write" on all
  packages, 90-day expiry. Stored in GitHub Actions secrets for the
  `mskim274/revit-mcp-v2` repo. Rotates by **~2026-07-21**.
- No other secrets needed for CI today.

### Known one-off machine state

- `%APPDATA%\Autodesk\Revit\Addins\2025\*.bak` files remain from the
  v0.2 → v0.3 auto-install test. Harmless. Can be deleted at any time.
- `%LOCALAPPDATA%\RevitMCP\Updates\v0.2.0\` + `v0.3.0\` folders left
  over from update flow verification. Only grow when new updates are
  downloaded; safe to delete to reclaim disk.

---

## Known issues / tech debt

- CI skips the Revit 2023 (`net48`) build — Nice3point doesn't publish
  net48 reference assemblies. Contributors with local Revit 2023 can
  still build by setting `REVIT_2023_PATH`.
- No automated tests yet. `scripts/test-ws.js` is a manual probe.
- Plugin ID (`dffd689e-fef6-4b62-8d7c-da6c3ab4efd4`) was generated to
  avoid collision with AutoCadMCP — don't regenerate casually.

---

## Copy-paste resume prompt

To seed a fresh Claude Code session (any machine), paste this after
`git pull`:

```
Continuing work on revit-mcp-v2. Read HANDOFF.md, CLAUDE.md, and the
top entry of CHANGELOG.md.

Most recent shipped: v0.4.0 (2026-04-22) — npm publish of the TS server
as @kimminsub/revit-mcp. GitHub Actions release pipeline is working end
to end. Plugin auto-update flow is verified.

Decision pending: Phase P3 (WiX MSI) vs Harness Tier 2 vs Sprint 5 vs
pause for feedback. Which should we do?
```

---

## Session log — most recent on top

Each entry: date, short summary, what's next.

### 2026-04-22 — Phase P2 + documentation
- Shipped v0.4.0: TS server published to npm (`@kimminsub/revit-mcp`).
  GitHub Actions now builds + releases the plugin zip, updater zip,
  and npm package in parallel on every `v*.*.*` tag.
- Fixed npm workspace issue in release workflow — `npm ci` now runs at
  the repo root with `--workspaces --include-workspace-root`.
- Added `docs/SETUP.md` — how to bootstrap the project on a new
  machine. Covers prereqs, clone+build, Claude Desktop config, what's
  portable vs not.
- Added root `README.md` + `CHANGELOG.md` + `server/README.md`.
  Refreshed `CLAUDE.md` for the Harness Tier 1 + auto-update
  subsystems and the new known pitfalls (MinVer TagPrefix, GUID
  collision, DocumentCreated event, Nice3point net48 gap).
- **Next**: user to choose between P3 / Tier 2 / Sprint 5 / pause.

### 2026-04-22 — Phase P1 + auto-install
- Shipped v0.3.0 (auto-install) and v0.2.0 (Harness Tier 1 + P0 dialog,
  now-deprecated due to MinVer bug).
- Tag-triggered CI release pipeline (`.github/workflows/release.yml`).
- One-click auto-install in update dialog: downloads plugin zip +
  updater zip, launches detached updater, waits for Revit exit,
  extracts new DLLs with `.bak` backups.
- Fixed MinVer tag-prefix bug that caused v0.2.0 to report itself as
  0.0.0 and infinitely re-prompt the update dialog.
- **Next → done this session**: P2 (npm publish).

### Initial commit → 2026-04-22 — Sprints 1-4 + Harness Tier 1 + P0
- Baseline 20 tools across Utility / Query / Create / Modify / View.
- Harness Tier 1: idempotency cache, post-tx verification + rect→polygon
  fallback, response-overflow spill.
- P0 update-notification dialog (GitHub Releases poll on Revit startup).
- See `CHANGELOG.md` for the fine-grained history.
