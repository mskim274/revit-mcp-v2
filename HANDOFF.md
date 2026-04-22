# Session Handoff

Rolling notes for continuing work across machines and Claude sessions.
Update this file at the **end of every session** so the next one (on
any machine) can pick up in under a minute.

> **New Claude session on this repo?** Read this file first, then
> `CLAUDE.md` (conventions + architecture), then the latest entry in
> `CHANGELOG.md` (shipped features). That's 3 files, ~5 minutes.

---

## Current state — 2026-04-22

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
