# Changelog

All notable changes to this project are documented here. Versions follow
[Semantic Versioning](https://semver.org/): while we're on 0.x, breaking
changes are still fair game.

---

## [Unreleased]

### Fixed
- Updated compatible transitive `fast-uri`, `hono`, and `qs` versions to
  resolve the production dependency advisories detected by CI.
- Isolated AutoCAD script compiler dependencies from AutoCAD's older Roslyn
  assemblies. A Roslyn-free command boundary checks opt-in before loading;
  generated scripts share the exact API/globals assemblies. Deploy the new
  `script-engine` folder with the matching Host/CommandSet and restart AutoCAD.
- Fixed AutoCAD host transaction cleanup accessing a disposed native transaction
  after command/cancellation/serialization exceptions. One transaction boundary
  preserves commit state, never retries cleanup, and logs original exceptions
  before disposal. Added 16 host-independent lifecycle regression scenarios.
  Requires AutoCAD restart; Revit is unaffected.

### Added
- Clarified reservation compatibility for older running Revit hosts. Work-scope
  status now reports confirmed unsupported functionality explicitly; approved
  legacy work does not need a reservation exception. Real coordination, target
  and transport failures remain errors and never trigger a silent downgrade.
- Added explicit Windows User `REVIT_MCP_SCRIPT_APPROVAL=auto/prompt` for
  Revit query/modify scripts. Default remains per-script confirmation; user
  changes apply on the next request without restart. Responses identify
  automatic approval, and prompt cancellation is distinguished from No.
- Added `revit_work_scope` for host-enforced multi-agent assignments, overlap
  rejection, lease expiry and stale model evidence checks. Disjoint element
  scopes permit built-in instance Comments/Mark edits; other side effects need
  exclusive document scope. Reservations enable fail-closed coordination until
  explicitly disabled by a document-scope holder. Requires host restart.
- Added project MCP configs for Grok CLI / Orca (`.grok/config.toml`) and
  Claude Code / Codex (`.mcp.json`), both pointing `cad` and `revit` at the
  TypeScript stdio servers. Documented why `CadMCPServer.exe` must not be used
  (invalid `tools/list` JSON causes a Grok startup timeout).
- Added Revit 2025+ CommandSet hot reload with a stable contracts assembly,
  collectible `AssemblyLoadContext`, immutable hash-verified generations,
  atomic pre-validated swaps, persisted activation with baseline fallback,
  unload-leak limits, and `revit_get_commandset_status` /
  `revit_reload_commandset` tools.
- Added multi-Revit session discovery and targeting: normally launched Revit
  processes auto-bind to 8181 or 8183–8199, publish short-lived local registry
  records, and can be selected with `revit_list_sessions`,
  `revit_set_target`, and `revit_get_target`.
- Added process/session and active-document fingerprint guards to the
  WebSocket request envelope so a selected target fails closed if the Revit
  process or active document changes before execution.
- Expanded the Revit MCP surface to 43 tools, including session targeting, exact-match and
  batch-query improvements, batch parameter modification, type management,
  schedule export, review overlays and tagging, survey-coordinate pipe runs,
  live C# script execution, batch view duplication, linked-model discovery,
  raster view export, loaded-family placement, sheet discovery, and batched
  view placement on existing sheets.
- Added read-only `revit_get_linked_models`, returning link instance/type IDs,
  names, paths, load state, and host worksets without loading, reloading, or
  unloading references. Added `revit_export_view` for verified PNG/JPG export
  of one active or ID/name-targeted non-template view, with exact-first name matching,
  `overwrite=false`, and idempotent retry support.
- Added `revit_get_sheets`, returning sheet number/name/ID and viewport view
  IDs, plus `revit_place_family` and `revit_place_views_on_sheet`. Both new
  placement tools accept batches of up to 50, use one transaction with
  per-item outcomes, support idempotent retries, and verify the first created
  result after commit; family/type names resolve by case-insensitive exact match.
- Registered `revit_modify_wall_height_to_linked_soffit` with
  `dry_run=true` by default. It measures up to 50 host walls against floors in
  loaded links and requires an explicit non-dry run before writing verified
  unconnected heights.
- Expanded the AutoCAD MCP surface to 15 tools. `cad_create_entities` creates
  up to 200 line/polyline/circle/arc/text items in one transaction, and
  `cad_modify_entities` moves, copies, erases, or rotates up to 500 handles in
  one transaction; both return per-item outcomes and accept idempotency keys.
- Added `cad_blocks`: list block names/reference counts or insert up to 50
  exact-name references in one transaction with per-item outcomes,
  idempotency, and verification. Added `cad_plot_pdf` for verified plotting of
  the current or an exact-name layout, defaulting to
  `%TEMP%\cad-mcp-exports` with `overwrite=false`.
- Added `cad_execute_script` as the AutoCAD long-tail escape hatch and added
  read-only xref metadata to `cad_get_drawing_info`.
- Added PR CI for npm workspace builds, clean-consumer tarball tests,
  production dependency audit, both Revit target frameworks, updater smoke
  tests, and release-package validation.
- Added product-specific release manifests, SHA-256 checksums, GitHub artifact attestations,
  npm trusted publishing support, and strict tag validation.
- Added authenticated loopback WebSocket bridges with a shared local bearer
  token, strict message and timeout limits, bounded document-scoped
  idempotency caches, and explicit unknown-outcome retry guidance.
- Added product-prefixed project license and version-specific third-party
  notices to both binary release archives, with validation against the
  repository copies and generic-name collision guards.
- Added `SECURITY.md`, `CONTRIBUTING.md`, CODEOWNERS, Dependabot configuration,
  and issue and pull request templates.

### Changed
- Split the long-lived Revit host/contracts from ordinary CommandSet code.
  CommandSet-only changes can now be staged with
  `scripts/stage-commandset.ps1` and activated without restarting Revit 2025+;
  host and contract changes still require restarting the affected process.
- npm publishing now builds and publishes `@kimminsub/mcp-cad-core` before
  `@kimminsub/revit-mcp`; the server depends on the exact matching core
  version.
- Revit release archives are assembled from staged runtime output and include
  Roslyn scripting dependencies, legal notices, and
  `RevitMCPPlugin.deps.json`, while excluding Revit API reference assemblies.
- Public binary support is documented as Revit 2025 only; later Revit years
  require an API-specific build, release asset, and validation before support
  is claimed.
- Release publication is gated on all npm, .NET, updater, and archive
  preflight checks. GitHub Actions are pinned to immutable commit SHAs with
  job-scoped permissions; every downstream checkout uses the validated commit
  SHA and publish/finalize steps re-check the remote tag target.
- Verified archives are uploaded to a draft release before npm publication;
  the release becomes public only after both exact-version npm packages and
  their dependency relationship are confirmed.
- Plugin, commandset, and updater binaries now derive the same version from
  `v`-prefixed tags. Packaging independently verifies both the plugin DLL and
  updater EXE file versions before a release can proceed.
- Revit and AutoCAD write commands now check transaction outcomes, serialize
  verifiable mutation results, and preserve committed-success responses when a
  later UI or command-context follow-up fails.
- Tool schemas now enforce cross-field contracts, bounded batch sizes,
  64-bit-safe element identifiers, strict cursors, and stable
  `idempotency_key` forwarding.
- `revit_query_elements` can now opt into loaded linked documents with
  `include_links=true` (default remains false), identifies linked detail/ID
  rows, and reports linked summary counts with the per-link list capped at 50
  plus an explicit truncation flag. It also accepts a case-insensitive exact
  `workset_filter` for host elements; Structural Framing still requires
  parameter filtering when `LevelId` is unavailable.
- `revit_get_project_info` now reports workset count and workset metadata only
  when the document is workshared.
- `revit_select_elements` now accepts `zoom=true` to zoom the active view to
  the new selection; the default remains false.
- Documented `Rooms` as the `revit_query_elements` category for room-finish
  workflows, with `revit_export_schedule` as the path for existing room-finish
  schedules.
- `cad_query_entities` now accepts `space="current"` (default), `model`, or
  `paper`; paper mode scans every paper-space layout. Documented
  `AUTOCAD_MCP_PORT` for overriding the default AutoCAD bridge port.
- Clarified that the current AutoCAD 2025 plugin targets .NET 8 and does not
  claim AutoCAD 2027 support; Autodesk's 2027 managed API requires a separate
  .NET 10 host build and SDK validation.

### Fixed
- Stabilized multi-Revit active-document fingerprints by using Revit's
  document-instance hash instead of the transient managed-wrapper hash. This
  keeps one open document pinned across API wrapper refreshes while still
  invalidating the target when the file is closed and reopened.

### Security
- Updated production JavaScript dependencies and added an audit gate.
- Hardened updater extraction against path traversal, path collisions, links,
  oversized archives, and partial replacement; added malicious-ZIP and backup
  preservation regression tests.
- Automatic updates now require the exact GitHub asset name, size, and
  GitHub-provided SHA-256 digest before extraction, then recheck the archive
  immediately before installation.
- `revit_execute_script` is disabled by default, requires explicit enablement
  and Revit UI approval for every run, and is documented as an escape hatch
  rather than a security sandbox.
- `cad_execute_script` is independently disabled unless AutoCAD starts with
  `AUTOCAD_MCP_ENABLE_SCRIPT=1`. Query mode aborts its transaction and modify
  mode commits it; its denylist is not a sandbox and it has no UI approval
  dialog.
- Documented the local trust boundary, script-execution risk, confidential
  model-data handling, and manual release verification process.

---

## [0.5.0] — 2026-04-29

### Added
- Added Revit selection-aware queries:
  `revit_get_selected_elements` and `revit_get_element_geometry`.
- Added AutoCAD selection primitives and schedule parsing tools backed by the
  shared `@kimminsub/mcp-cad-core` workspace.

### Known issue
- The Revit GitHub artifacts were published, but the parallel npm job failed
  while building the newly extracted core workspace. npm therefore remained
  on `@kimminsub/revit-mcp@0.4.0`.

---

## [0.4.0] — 2026-04-22

### Added
- Published the TypeScript server to npm for the first time as
  `@kimminsub/revit-mcp`.
- Made the npm release job workspace-aware.

---

## [0.3.0] — 2026-04-22

### Added
- **One-click auto-install** in the update notification dialog. Clicking
  **"⬇ 다운로드 및 설치"** now downloads the plugin zip and the bundled
  `RevitMCPUpdater.exe`, launches the updater with `--wait`, and shows a
  "Close Revit to finish" confirmation. The updater detaches from Revit,
  waits up to 5 minutes for `revit.exe` to exit, then extracts the zip
  into `%APPDATA%\Autodesk\Revit\Addins\<year>\` with `.bak` backups.
- `UpdateChecker.PluginZipUrl` / `UpdaterZipUrl` — release-asset
  discovery now tracks both artifacts separately, picking the plugin
  zip matching the running Revit year via `RevitYearTag` (compile-time
  constant driven by target framework).
- Browser-fallback path: if the updater asset is missing or auto-install
  throws, the dialog opens the release HTML URL so the user can proceed
  manually.

### Fixed
- **Infinite update-loop shipped in v0.2.0.** MinVer's default tag
  pattern doesn't include a prefix, so our `v0.2.0` tag was silently
  ignored and CI produced DLLs reporting `0.0.0-alpha.N`. Those DLLs
  then saw their own release as newer and re-prompted on every Revit
  start.
  - Added `<MinVerTagPrefix>v</MinVerTagPrefix>` to both csprojs.
  - `Application.GetCurrentPluginVersion()` now reads `FileVersionInfo.FileVersion`
    (full semver), not `AssemblyVersion` (which MinVer pins to
    `major.0.0.0` for any 0.x release).
  - Verified: v0.3.0 DLLs report `FileVersion 0.3.0.0` correctly.

### Changed
- Primary dialog button label: *"⬇ 다운로드"* → *"⬇ 다운로드 및 설치"*.
- Added a status line under the snooze checkbox that reports live
  download / extraction progress and success / error state.

---

## [0.2.0] — 2026-04-22

> ⚠️ **Known-broken release.** This build reported its own version as
> `0.0.0.0` due to a MinVer misconfiguration, causing the update dialog
> to reappear on every Revit start. Upgrade to v0.3.0. Left in the
> release list for archival; no action needed from end users.

### Added — Harness Engineering Tier 1
- **Idempotency cache** (`WebSocketServer.cs`) — 15-minute TTL dictionary
  keyed by `idempotency_key` param (or request UUID). Scoped to
  side-effect commands (`create_*`, `modify_*`, `delete_*`, `move_*`,
  `copy_*`, etc.); read-only queries are never cached. Prevents
  duplicate mutations on retry-after-timeout.
- **Post-transaction verification** on `create_wall` and `create_floor`.
  After `tx.Commit()`, the command re-queries the element and compares
  actual geometry to the request (3 mm position tolerance, 5% area
  tolerance). Returns `verification.geometry_match` + `issues[]` so
  agents can self-correct.
- **Rectangle → polygon auto-fallback** for `create_floor`. The known
  "Invalid boundary" failure in rectangle mode now transparently
  retries with a polygon boundary built from the same four corners.
  Response reports `auto_fallback_applied: true`.
- **Response-size overflow spill** (`services/response-formatter.ts`).
  Payloads above 25 KB are written to `%TEMP%\revit-mcp-spill\` and
  the inline response is replaced with a ~12 KB preview + file path.
  500 KB is the hard cap. Consolidated four duplicate `sendAndFormat`
  helpers into a single shared service.

### Added — Phase P0: Update notification
- `Services/GitHubRelease.cs` — DTO for the `/releases/latest` response.
- `Services/UpdateChecker.cs` — fire-and-forget GitHub poll on plugin
  startup. Compares `FileVersionInfo.FileVersion` to the latest
  published (non-draft, non-prerelease) tag. Persists a
  "don't show today" state to `%LOCALAPPDATA%\RevitMCP\update-cache.json`.
- `UI/UpdateNotificationWindow.xaml` — dark-themed WPF dialog styled
  after SMART MEP's update notification, with a snooze checkbox.
- `Application.OnStartup` hook: subscribes to the `Idling` event;
  renders the dialog on the first tick once Revit's UI thread is safe
  to use, then unsubscribes.
- `DocumentCreated` event handler — WebSocket now starts for new/blank
  projects, not just opened `.rvt` files.

### Added — Phase P1: CI/CD
- `.github/workflows/release.yml` — tag-triggered (`v*.*.*`) build
  pipeline that packages `RevitMCPPlugin-<ver>-Revit2025.zip` and
  `RevitMCPUpdater-<ver>.zip` and creates a GitHub Release via
  `softprops/action-gh-release`.
- `MinVer` in both csprojs — `AssemblyVersion` / `FileVersion` derived
  from the nearest git tag. (See v0.3.0 for the fix to this.)
- `Nice3point.Revit.Api` NuGet fallback in csprojs for CI builds that
  don't have Revit installed. Local dev still uses the installed DLLs
  when `REVIT_202x_PATH` is set.
- `updater/` project — framework-dependent single-file `.exe` that
  waits for Revit to exit then extracts a downloaded zip.
- `scripts/test-ws.js` — direct WebSocket probe for bypassing the MCP
  client during debugging.

### Fixed
- **Revit add-in GUID collision**. The initial placeholder GUID
  (`A1B2C3D4-E5F6-7890-ABCD-EF1234567890`) matched a GUID in an
  unrelated AutoCadMCP bundle. Revit's add-in manager refused to load
  our plugin with a "duplicate add-in ID" dialog. Replaced with a
  freshly-generated GUID.
- **Z-axis handling in `create_wall` verification**. Walls are placed
  at `level.Elevation`, not Z=0. The verification previously compared
  actual geometry to raw request points (Z=0) and reported spurious
  offsets. Now builds expected points with `level.Elevation` for Z.

### Changed
- CI workflow disables the `net48` (Revit 2023) build — the
  Nice3point 2023.* packages don't publish a net48 target. Local
  multi-target builds still work with `REVIT_2023_PATH` set.

---

## [0.1.0] — 2026-03-30 (initial commit)

First public tag. 20 tools across Utility / Query / Create / Modify /
View. No auto-update or Harness Tier 1 scaffolding yet. Tested on an
anonymized structural model with hundreds of thousands of elements and on
Revit's blank template for create/modify flows.

### Initial tool set
- Utility (2): `revit_ping`, `revit_get_project_info`
- Query (8): `revit_get_levels`, `revit_get_views`, `revit_get_grids`,
  `revit_query_elements`, `revit_get_element_info`,
  `revit_get_types_by_category`, `revit_get_family_types`,
  `revit_get_all_categories`
- Create (2): `revit_create_wall`, `revit_create_floor`
- Modify (4): `revit_modify_element_parameter`, `revit_delete_elements`,
  `revit_move_elements`, `revit_copy_elements`
- View (4): `revit_set_active_view`, `revit_isolate_elements`,
  `revit_reset_view_isolation`, `revit_select_elements`

[Unreleased]: https://github.com/mskim274/revit-mcp-v2/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/mskim274/revit-mcp-v2/releases/tag/v0.5.0
[0.4.0]: https://github.com/mskim274/revit-mcp-v2/releases/tag/v0.4.0
[0.3.0]: https://github.com/mskim274/revit-mcp-v2/releases/tag/v0.3.0
[0.2.0]: https://github.com/mskim274/revit-mcp-v2/releases/tag/v0.2.0
[0.1.0]: https://github.com/mskim274/revit-mcp-v2/commit/ea036ec
