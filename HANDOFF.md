# Session Handoff

Rolling notes for continuing work across machines and Claude sessions.
Update this file at the **end of every session** so the next one (on
any machine) can pick up in under a minute.

> **New Claude session on this repo?** Read this file first, then
> `CLAUDE.md` (conventions + architecture), then the latest entry in
> `CHANGELOG.md` (shipped features). That's 3 files, ~5 minutes.

---

## Current state — 2026-04-29 (in-flight: RC schedule reconciliation)

### Active task: 엑셀 일람표 → Revit 타입 자동 동기화

엑셀 `RC_Beam_Schedule.xlsx` (1091 entries 타입정리)에 정리된 RC 보
타입을, Revit 패밀리 `M_Concrete-Rectangular Beam`(811 types)의 실제
타입에 1:1 반영하는 작업.

**진행 누계 (Excel order)**:

| Base | 결과 | Instances 영향 |
|---|---|---|
| ACG2 | ✓ smoke test (B1F-3F 45 + 4F 5) | 50 |
| ACG22 | ✓ 자동 (B1F-3F 4 + 4F 2 + orphan 삭제) | 6 |
| ACG23 | ✓ 변경 불필요 (Option B로 1F,3F 합쳐서 일치) | 0 |
| AG0 | ✓ 자동 (5F 유지 + B1F-2F → B1F-3F 10 + 4F 2) | 12 |
| **AG0A** | ❌ **워크쉐어링 충돌**(다른 사용자 락) | 1개 분실 (43→42) |

총 70 instances 재배정, orphan 2개 삭제.

### 다음 세션에서 재시작

```powershell
git pull                                    # HEAD = 42203d9
.\scripts\build-and-deploy.ps1 -RevitVersion 2025
```

1. **Revit 시작** + 같은 프로젝트 (워크쉐어링 충돌 피하려면 단독 시간대 또는 Detach from Central)
2. **AG0A 재시도** (이전 시도 롤백됨, 새 type 2개 사라짐, 원본 그대로):
   ```bash
   ONLY_BASE=AG0A node scripts/reconcile-revit-types.mjs
   ```
3. **다음 base들** (엑셀 타입정리 순서):
   AG0B → AG0C → AG0D → AG1 → AG10 → ... (총 ~145개 남음)

각 base마다 dry-run → 사용자 확인 → 실행 패턴 유지:
```bash
DRY_RUN=1 ONLY_BASE=<base> node scripts/reconcile-revit-types.mjs
ONLY_BASE=<base> node scripts/reconcile-revit-types.mjs   # 확인 후
```

### 적용된 핵심 결정

- **Option B (원본 라벨 보존)**: `1,3ACG23` 같은 단일 라벨 다중 floor를
  split하지 않고 "1F,3F" 한 type으로 유지. `add_type_summary.py`의
  `convert_floor_label()` 참조.
- **(확인필요) 마커**: SK_FL이 어느 새 타겟에도 안 맞는 instances는
  `<source_name>(확인필요)` 타입으로 이동 → 모델러 검토용.
- **Orphan 자동 삭제**: redistribution 후 비어있는 source type 자동 삭제
  (`PURGE_ORPHANS` 기본값 on, `NO_PURGE=1`로 끔).

### 신규 Revit MCP 명령 (b8109a9에서 추가됨)

| 도구 | 역할 |
|---|---|
| `revit_duplicate_type` | ElementType.Duplicate(name) |
| `revit_rename_type` | ElementType.Name = newName (idempotent) |
| `revit_change_instance_type` | Element.ChangeTypeId, batch 1~1000 |

### 핵심 파일

```
scripts/reconcile-revit-types.mjs   ← 메인 자동화 (HEAD)
scripts/analyze-rc-beams.mjs        ← AutoCAD 일람표 추출
scripts/verify-rc-beams.mjs         ← 일람표 검증
%TEMP%\add_type_summary.py          ← 타입정리 시트 생성기
%TEMP%\compare_revit_excel.py       ← Revit매칭 시트 생성기
~\Desktop\RC_Beam_Schedule.xlsx     ← 작업 입력
```

### 워크쉐어링 주의

- AG0A 실패 원인: 다른 사용자가 element 락
- 권장: 단독 시간대, Detach from Central, 또는 Sync to Central 자주
- 큰 변경 전후로 동기화 필수

### 추가 작업 (Floor 변경 후 단계)

- [ ] AG0A 재시도
- [ ] 엑셀 타입정리 순서로 나머지 ~145개 base
- [ ] **신규 생성 필요 177개** — 현재 스크립트는 Floor 변경 + 일치만 다룸. 별도 처리 필요
- [ ] **Size 불일치 63개** — 사용자 결정 필요 (도면 vs Revit 어느 것이 맞나)
- [ ] 모든 작업 후 (확인필요) 타입들 모델러와 함께 검토

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
