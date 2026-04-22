# Revit MCP V2 — Agent Instructions

## Architecture

3-layer architecture: **MCP Server (TypeScript)** ↔ **WebSocket** ↔ **Revit Plugin (C#)** + **CommandSet (C#)**.

```
Claude Desktop ──stdio──▶ MCP Server (TS)
                              │
                         WebSocket :8181
                              │
                        Revit Plugin (C#) ──Revit.Async──▶ Revit API
                              │
                        CommandSet (C#) ← reflection auto-discovery
```

### Layer Responsibilities

- `server/` — TypeScript MCP server. Handles MCP protocol (stdio), tool definitions, input validation (Zod), pagination formatting, error wrapping, response-size overflow spill. NO Revit logic here.
- `plugin/` — C# Revit Add-in. WebSocket server, command dispatching, JsonElement→native conversion, Revit.Async for thread safety, idempotency cache, WPF update notification. NO MCP logic here.
- `commandset/` — C# pure Revit API execution. Each command is one file implementing `IRevitCommand`. Auto-discovered via Assembly reflection. Create/Modify commands also perform post-transaction verification. NO networking or protocol code here.
- `updater/` — Standalone net8 console app that runs OUTSIDE Revit. Waits for Revit to exit, then extracts a downloaded plugin zip into the user's Addins folder. Ships as a single-file `.exe` published per release.

### Key Design Decisions

- **Revit.Async is mandatory**: All Revit API calls run on Revit's main thread. WebSocket handlers are on worker threads. Revit.Async bridges this safely.
- **JsonElement conversion**: System.Text.Json deserializes `Dictionary<string, object>` values as `JsonElement`. The plugin's `ConvertJsonElements()` converts to native .NET types before passing to commands.
- **Single-pass counting**: For large models (396K+ elements), always iterate once rather than per-category collectors.
- **3-Tier pagination**: Summary (counts) → Paginated detail (cursor, 50/page) → Export (future CSV). Always default to summary for safety.
- **Transactions**: Create/Modify commands MUST wrap Revit API mutations in `Transaction`. Query commands are read-only (no transaction needed).
- **UIDocument Action Descriptors**: Commands needing UIDocument access (view switching, element selection, isolation) cannot use UIDocument directly from CommandSet (only `Document` is passed). Instead, they return an `action` field in `CommandResult.Data`. The plugin's `WebSocketServer.cs` post-processes these actions after command execution:
  - `action: "activate_view"` + `view_id` → `_uiApp.ActiveUIDocument.ActiveView = view`
  - `action: "select_elements"` + `element_ids` → `_uiApp.ActiveUIDocument.Selection.SetElementIds(ids)`
  - `action: "isolate_in_view"` + `element_ids` → `activeView.IsolateElementsTemporary(ids)`

## Project Structure

```
revit-mcp-v2/
├── CLAUDE.md                          ← You are here (agent instructions)
├── README.md                          ← User-facing intro
├── CHANGELOG.md                       ← Release history
├── RevitMCP.sln                       ← Solution file
├── .github/workflows/
│   └── release.yml                    ← Tag-triggered CI/CD (P1)
├── server/                            ← TypeScript MCP Server
│   ├── src/
│   │   ├── index.ts                   ← Entry point, tool registration
│   │   ├── constants.ts               ← Config (ports, timeouts, overflow limits)
│   │   ├── types.ts                   ← WebSocket protocol types
│   │   ├── services/
│   │   │   ├── websocket-client.ts    ← WS client with reconnection
│   │   │   ├── pagination.ts          ← Cursor-based pagination helpers
│   │   │   └── response-formatter.ts  ← Shared sendAndFormat + overflow spill
│   │   └── tools/
│   │       ├── utility.ts             ← ping, get_project_info
│   │       ├── query.ts               ← 8 query tools (Sprint 2)
│   │       ├── create.ts              ← 2 create tools (Sprint 3)
│   │       ├── modify.ts              ← 4 modify tools (Sprint 3)
│   │       └── view.ts                ← 4 view tools (Sprint 4)
│   └── package.json
├── commandset/                        ← C# CommandSet library
│   ├── CommandSet.csproj              ← MinVer + Nice3point.Revit.Api (CI fallback)
│   ├── Interfaces/
│   │   └── IRevitCommand.cs           ← Core interface + CommandResult
│   └── Commands/
│       ├── Utility/                   ← PingCommand, GetProjectInfoCommand
│       ├── Query/                     ← 8 query commands (Sprint 2)
│       ├── Create/                    ← 2 create commands (Sprint 3)
│       │   ├── CreateWallCommand.cs   ← + post-tx verification
│       │   └── CreateFloorCommand.cs  ← + rect→polygon auto-fallback
│       ├── Modify/                    ← 4 modify commands (Sprint 3)
│       └── View/                      ← 4 view commands (Sprint 4)
├── plugin/                            ← C# Revit Plugin
│   ├── RevitMCPPlugin/
│   │   ├── RevitMCPPlugin.csproj      ← UseWPF, MinVer, Nice3point fallback
│   │   ├── Application.cs             ← IExternalApplication, update-check hook
│   │   ├── WebSocketServer.cs         ← WS + dispatch + idempotency cache
│   │   ├── CommandDispatcher.cs       ← Reflection-based command registry
│   │   ├── Services/
│   │   │   ├── GitHubRelease.cs       ← /releases/latest DTO
│   │   │   └── UpdateChecker.cs       ← Polls GitHub, snooze cache
│   │   └── UI/
│   │       ├── UpdateNotificationWindow.xaml     ← WPF dialog
│   │       └── UpdateNotificationWindow.xaml.cs  ← async download + install
│   └── revit-mcp.addin                ← Revit add-in manifest
├── updater/                           ← External DLL replacer (P1.3)
│   ├── Updater.csproj                 ← net8 single-file .exe
│   └── Program.cs                     ← Waits for Revit exit, extracts zip
├── scripts/
│   ├── build-and-deploy.ps1           ← Build + copy to Revit Addins folder
│   └── test-ws.js                     ← Direct WebSocket probe (bypasses MCP)
└── protocol/                          ← WebSocket protocol docs (future)
```

## Adding a New Command (Step-by-Step)

### 1. C# Command (commandset)

Create `commandset/Commands/{Category}/{CommandName}Command.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.{Category}
{
    public class {CommandName}Command : IRevitCommand
    {
        public string Name => "command_name";      // matches tool name without "revit_" prefix
        public string Category => "{Category}";    // "Query", "Create", "Modify", "View", "Export"

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // For CREATE/MODIFY: wrap in Transaction
                using (var tx = new Transaction(doc, "MCP: Description"))
                {
                    tx.Start();
                    // ... Revit API calls ...
                    tx.Commit();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["result"] = "value"
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed: {ex.Message}",
                    "Helpful suggestion for recovery."));
            }
        }
    }
}
```

### 2. TypeScript Tool (server)

Add tool registration in `server/src/tools/{category}.ts`:

```typescript
server.registerTool(
    "revit_command_name",
    {
        title: "Human Readable Title",
        description: `Description for Claude to understand when to use this tool.`,
        inputSchema: {
            param_name: z.string().describe("Description"),
        },
        annotations: {
            readOnlyHint: false,        // true for Query, false for Create/Modify
            destructiveHint: true,       // true if modifies/deletes elements
            idempotentHint: false,
            openWorldHint: false,
        },
    },
    async (params) => {
        return sendAndFormat(wsClient, "command_name", {
            param_name: params.param_name,
        });
    }
);
```

### 3. Register in index.ts

```typescript
import { registerCreateTools } from "./tools/create.js";
registerCreateTools(server, wsClient);
```

### 4. That's it — CommandDispatcher auto-discovers the C# command via reflection.

## Build & Deploy

### Local development

```powershell
# Full build + deploy to %APPDATA%\Autodesk\Revit\Addins\2025\
.\scripts\build-and-deploy.ps1 -RevitVersion 2025

# TypeScript only (no Revit restart needed, Claude Desktop restart enough)
cd server && npm run build

# C# only — requires Revit to be CLOSED (DLL file lock)
$env:REVIT_2025_PATH = "C:\Program Files\Autodesk\Revit 2025"
dotnet build RevitMCP.sln -c Release
```

### Cutting a public release

```bash
# Tag + push triggers .github/workflows/release.yml
git tag v0.4.0
git push origin v0.4.0
# GitHub Actions builds both zips + creates the GitHub Release (~2 min).
# End users get the notification dialog on their next Revit start.
```

### Testing the update dialog locally

```powershell
# Build with a forced lower version than the latest GitHub release
dotnet build plugin\RevitMCPPlugin\RevitMCPPlugin.csproj `
  -c Release -f net8.0-windows -p:MinVerVersionOverride=0.1.0

# Copy the resulting DLLs to the Addins folder (Revit must be closed).
# On next Revit start the dialog shows "v0.1.0 → v<latest>".
```

### Direct WebSocket testing (bypass MCP client)

```powershell
# Useful when the current Claude session's MCP has lost its connection.
# Sends raw WebSocket commands straight to the plugin on :8181.
node scripts\test-ws.js ping
node scripts\test-ws.js query_elements '{"category":"Walls","summary_only":true}'
```

**Important**:
- C# changes require Revit restart (DLL locking).
- TypeScript changes only need Claude Desktop restart.
- End users: C# DLL updates via the auto-update dialog (Plan A zip flow).

### Environment Variables

```
REVIT_2025_PATH=C:\Program Files\Autodesk\Revit 2025   # Revit API DLL location (local dev)
REVIT_2023_PATH=C:\Program Files\Autodesk\Revit 2023   # For .NET 4.8 target (local dev)
REVIT_MCP_PORT=8181                                      # WebSocket port (optional override)
```

When these env vars are empty (e.g., on the GitHub runner), the csproj
automatically falls back to the Nice3point.Revit.Api NuGet packages.

## Conventions & Rules

### Naming
- Tool names: `revit_` prefix + snake_case (e.g., `revit_query_elements`)
- Command names: matching without prefix (e.g., `query_elements`)
- C# files: PascalCase matching class name (e.g., `QueryElementsCommand.cs`)

### Safety
- All Revit API calls MUST go through Revit.Async — never call Revit API from WebSocket thread
- Create/Modify commands MUST use Transaction with descriptive name `"MCP: {action}"`
- Always include `cancellationToken.ThrowIfCancellationRequested()` in loops
- Errors always include `suggestion` field for Claude auto-recovery

### Performance
- Default to summary mode for queries — never return raw element lists by default
- Use FilteredElementCollector filters before LINQ — push filtering to Revit API
- Single-pass iteration for counting — avoid per-category collectors on large models
- Clamp page sizes (max 200) to prevent response bloat

### Multi-target Build
- `net48` → Revit 2023/2024 (.NET Framework 4.8)
- `net8.0-windows` → Revit 2025+ (.NET 8.0)
- Use `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` in plugin .csproj for NuGet DLL deployment

## Sprint Status

- [x] Sprint 1: Infrastructure (MCP Server, WebSocket, Plugin, CommandSet)
- [x] Sprint 2: Query Tools (8 tools — tested on 396K-element model)
- [x] Sprint 3: Create/Modify Tools (6 tools)
- [x] Sprint 4: View Tools (4 tools)
- [x] **Harness Tier 1** (v0.2.0+): idempotency cache, post-tx verification, response overflow spill
- [x] **Phase P0** (v0.2.0+): GitHub-Releases-based update notification dialog
- [x] **Phase P1** (v0.3.0+): tag-triggered CI release + one-click auto-install
- [ ] Phase P2: npm publish the TS server as `@mskim274/revit-mcp`
- [ ] Phase P3: WiX MSI installer + code signing
- [ ] Sprint 5: Advanced (worksharing, linked models, family loading, export)

## Tool Inventory (20 tools)

### Utility (2)
- `revit_ping` — Connection health check
- `revit_get_project_info` — Project metadata

### Query (8)
- `revit_get_levels`, `revit_get_views`, `revit_get_grids` — Project structure
- `revit_query_elements` — Element search with filters + pagination
- `revit_get_element_info` — Single element detail
- `revit_get_types_by_category`, `revit_get_family_types` — Type catalog
- `revit_get_all_categories` — Available categories

### Create (2)
- `revit_create_wall` — Straight wall between two points
- `revit_create_floor` — Floor from rectangle or polygon

### Modify (4)
- `revit_modify_element_parameter` — Set parameter value
- `revit_delete_elements` — Delete by ID (max 100, destructive)
- `revit_move_elements` — Move by vector (max 500)
- `revit_copy_elements` — Copy by vector (max 100)

### View (4)
- `revit_set_active_view` — Switch view (partial name match)
- `revit_isolate_elements` — Isolate or hide in view
- `revit_reset_view_isolation` — Reset temporary isolation
- `revit_select_elements` — UI selection/highlight

## Tested Models

### 대형 프로젝트 (Sprint 2 검증)
- Project: Y1P1_PH1_ST_지원시설-Central_분리됨
- Revit 2025 (build 25.3.0.46), 396,118 elements
- Query 8개 전체 검증, 31,347 structural framing 페이지네이션 테스트 완료
- select_elements, isolate_elements, set_active_view 실전 검증

### 빈 프로젝트 (Sprint 3 검증)
- Project: 프로젝트1 (Revit 2025 기본 템플릿)
- create_wall, create_floor, set_active_view 검증
- 2층 주택 모델 생성 테스트 (외벽 8개, 내벽 5개, 바닥 2개)

## Harness Engineering — Tier 1 Safeguards

The plugin ships with three production-safety patterns wrapping every
agent-driven operation. These are invisible to Claude in the happy path
but provide critical recovery signals on failure.

### 1. Idempotency cache (`WebSocketServer.cs`)

- **Where**: `_idempotencyCache` dictionary in `RevitWebSocketServer`.
- **Keyed by**: caller-supplied `idempotency_key` param, falling back to `request.Id` (UUID).
- **TTL**: 15 minutes. Opportunistic pruning every 50 writes.
- **Scoped to side-effect commands only**: `create_*`, `modify_*`, `delete_*`, `move_*`, `copy_*`, `mirror_*`, `rotate_*`, `array_*`, `rename_*`, `place_*`, `load_*`, `purge_*`, `set_*`, `batch_create_*`, `fix_*`. Read-only queries are never cached.
- **What it prevents**: duplicate element creation when a WebSocket response is lost mid-flight and the client retries. The second call returns the cached result verbatim — same `id` field, no Revit API call.

### 2. Post-transaction verification (Create/Modify commands)

After `tx.Commit()`, the command re-queries the element and compares actual
geometry/parameters against the request. Returns a `verification` block in
`CommandResult.Data`:

```json
"verification": {
  "performed": true,
  "geometry_match": false,
  "actual_start": { "x": ..., "y": ..., "z": ... },
  "start_offset_feet": 0.003,
  "issues": ["Start point offset by 1mm from request."]
}
```

- **Tolerances**: 0.01 ft (≈3mm) for position, 5% for area.
- **Z-axis note**: walls are placed at `level.Elevation`, not Z=0 from the request. The verification builds expected points with `level.Elevation` for Z — do NOT compare raw request coordinates.
- **Auto-fallback**: `CreateFloorCommand` retries rectangle → polygon boundary on "Invalid boundary" errors. The response reports `auto_fallback_applied: true` + `mode: "rectangle→polygon(auto)"` when this happens.

### 3. Response size overflow spill (`services/response-formatter.ts`)

- **Soft limit**: 25 KB (`RESPONSE_SIZE_SOFT_LIMIT`). Above this, the full
  JSON is written to `%TEMP%\revit-mcp-spill\<command>-<ts>-<uuid>.json`
  and the inline response is replaced with a ~12 KB preview + file path.
- **Hard limit**: 500 KB (`RESPONSE_SIZE_HARD_LIMIT`). Above this, same
  spill behavior plus an explicit "exceeds hard limit" marker.
- **Used by**: every tool via the shared `sendAndFormat` helper in
  `response-formatter.ts`. Do NOT reintroduce per-tool `sendAndFormat`
  copies — the duplicated versions the project had before the shared
  helper was extracted bypass overflow protection.

## Auto-Update System

### User-facing flow (v0.3.0+)

1. Plugin loads on Revit startup. Fire-and-forget HTTP call hits
   `/repos/mskim274/revit-mcp-v2/releases/latest`.
2. If a newer published (non-draft, non-prerelease) tag is found, a
   WPF dialog shows on the first `Idling` event (so the UI thread is
   ready). The Idling handler unsubscribes after firing once — no
   repeat prompts in the same session.
3. Clicking "⬇ 다운로드 및 설치" streams both release zips to
   `%LOCALAPPDATA%\RevitMCP\Updates\v<ver>\` (plugin + updater).
4. `RevitMCPUpdater.exe` is extracted and launched with
   `--wait --revit-year 2025 --zip <plugin.zip>`.
5. The updater detaches from Revit (via `UseShellExecute=true`),
   polls for revit.exe to exit (5-minute timeout), then extracts the
   plugin zip into `%APPDATA%\Autodesk\Revit\Addins\<year>\` with
   `.bak` backups of any pre-existing file.
6. User restarts Revit manually — new version loads.

### Developer-facing flow (cutting a release)

```bash
# 1. Make sure main is green and committed
git status

# 2. Tag with leading 'v' — MinVer's TagPrefix is 'v'
git tag v0.4.0
git push origin v0.4.0

# 3. GitHub Actions takes ~2 min to build + publish
#    https://github.com/mskim274/revit-mcp-v2/actions
#    Artifacts uploaded to the release:
#      - RevitMCPPlugin-<ver>-Revit2025.zip
#      - RevitMCPUpdater-<ver>.zip
```

### Version injection (MinVer)

- `<MinVerTagPrefix>v</MinVerTagPrefix>` in both csprojs — without this,
  MinVer silently ignores tags like `v0.2.0` and falls back to
  `0.0.0-alpha.N`, which shipped a broken v0.2.0 that reported itself
  as 0.0.0 and re-prompted on every Revit start.
- `FileVersion` is read at runtime by `Application.GetCurrentPluginVersion()`
  using `FileVersionInfo` — not `Assembly.GetName().Version`, which
  MinVer pins to `major.0.0.0` for 0.x versions. Do NOT change this back.

### Snooze state

- File: `%LOCALAPPDATA%\RevitMCP\update-cache.json`
- Updated when the user checks "하루 동안 알리지 않음" before closing
  the dialog. The next check skips silently until `snooze_until_utc` has
  passed.

### CI build constraints

- GitHub runners don't have Revit installed, so the csprojs fall back to
  `Nice3point.Revit.Api.RevitAPI` / `.RevitAPIUI` NuGet packages when
  `REVIT_202x_PATH` is empty. Local dev still uses the installed DLLs
  (exact runtime match).
- Revit 2023 (net48) build is currently disabled in CI — Nice3point 2023.*
  packages don't provide net48 reference assemblies. Contributors with a
  local Revit 2023 install can still build net48 by setting
  `REVIT_2023_PATH`.

## Known Pitfalls (실전에서 발견된 문제들)

### Namespace Conflict: `Commands.View` vs `Autodesk.Revit.DB.View`
`commandset/Commands/View/` 네임스페이스가 Revit의 `View` 클래스를 가림. View 폴더 내 모든 C# 파일에서 반드시 `global::Autodesk.Revit.DB.View`로 참조해야 함. `GetViewsCommand.cs`도 동일 문제 있었음.

### JsonElement Array/Object 처리
`System.Text.Json`이 `Dictionary<string, object>` 값을 `JsonElement`로 역직렬화함. `ConvertJsonElement()`에서 `JsonValueKind.Array` → `List<object>`, `JsonValueKind.Object` → `Dictionary<string,object>`로 재귀 변환 필수. 누락 시 배열이 문자열 `"[1,2,3]"`으로 전달됨.

### Structural Framing의 LevelId = -1
구조 프레이밍(보) 요소는 `LevelId` 프로퍼티가 `-1`(InvalidElementId). 실제 레벨 정보는 "참조 레벨" 파라미터(ElementId) 또는 커스텀 파라미터("SK_FL" 등)에 있음. `level_filter`로 필터링 불가 — `parameter_name`/`parameter_value` 필터 사용해야 함.

### 대형 모델(396K+)에서 전체 요소 순회 타임아웃
`FilteredElementCollector`로 전체 모델 요소를 수집 후 개별 처리하면 30초 타임아웃 발생. 예: isolate 시 "나머지 전부 hide" 접근법 실패. 해결: Revit 네이티브 API(`IsolateElementsTemporary`) 사용하거나, plugin 레이어에서 UIDocument 통해 처리.

### IsolateElementsTemporary API 호환성
`View.IsolateElementsTemporary()`는 Revit 2024+에서 사용 가능. Revit 2023(net48)에서 컴파일/런타임 호환성 미검증 상태.

### create_floor 직사각형 모드
`min_x/min_y/max_x/max_y` 파라미터로 직사각형 바닥 생성 시 "Invalid boundary" 에러 발생할 수 있음. 폴리곤 모드(`points` 배열)를 사용하면 안정적.

### MCP 응답 크기와 토큰 소모
대형 모델에서 query 응답이 매우 큼 (예: 500+ 타입 목록, 98개 뷰 상세). 항상 `include_properties: false` 기본 사용, 필요시 `limit` 파라미터로 제한. **v0.2.0부터 자동 완화**: 25KB 초과 응답은 자동으로 `%TEMP%\revit-mcp-spill\`에 스필되고 12KB 프리뷰만 반환됨 (`services/response-formatter.ts`).

### MinVer `TagPrefix` 누락 시 무한 업데이트 루프 (v0.2.0 사고)
태그를 `v0.2.0` 형식으로 쓰는데 MinVer 기본 설정은 prefix 없음 (`0.2.0`). 양쪽 csproj에 `<MinVerTagPrefix>v</MinVerTagPrefix>` 명시 안 하면 CI 빌드가 태그를 못 찾아 `0.0.0-alpha.N`으로 떨어짐. 결과: 릴리스된 DLL이 자기 자신을 v0.0.0으로 인식 → 매번 업데이트 다이얼로그 표시. **v0.3.0에서 수정됨**. 현재 두 csproj에 명시돼 있음 — 제거하지 말 것.

### `Assembly.GetName().Version` ≠ FileVersion (MinVer 0.x pinning)
MinVer는 바인딩 redirect 호환성 때문에 `AssemblyVersion`을 `major.0.0.0`으로 고정함. 0.x 릴리스에서는 항상 `0.0.0.0`이 됨. **올바른 버전 읽기**는 `FileVersionInfo.GetVersionInfo(asm.Location).FileVersion` 사용. `Application.GetCurrentPluginVersion()` 참조.

### Revit Addin ID 충돌 (v0.1 초기 GUID 사고)
초기에 사용한 placeholder GUID `A1B2C3D4-E5F6-7890-ABCD-EF1234567890`는 **AutoCAD MCP 번들**에서도 같은 값 사용 중이었음. Autodesk의 `ApplicationPlugins/` 폴더는 AutoCAD/Revit 모두 스캔해서 GUID 충돌 감지 → "중복된 애드인 ID" 대화상자로 로드 거부. 현재 GUID: `DFFD689E-FEF6-4B62-8D7C-DA6C3AB4EFD4` (유일). 신규 GUID는 `powershell -Command "[guid]::NewGuid()"`로 생성.

### DocumentCreated vs DocumentOpened (v0.3 수정)
`DocumentOpened` 이벤트는 **기존 .rvt 파일**을 열 때만 발동. New Project로 빈 프로젝트 생성하면 `DocumentCreated`가 발동. 초기 `Application.cs`는 `DocumentOpened`만 구독해서 빈 프로젝트에서 WebSocket 서버가 시작 안 됨. 현재는 두 이벤트 모두 구독하고 공통 `StartWebSocketServerIfNeeded()` 로 분기.

### Revit 실행 중 DLL 배포 불가 (파일 잠금)
Revit이 켜져 있으면 `.dll` 파일이 잠기므로 `build-and-deploy.ps1`로 덮어쓰기 불가. 자동 업데이트 시스템은 이 문제를 외부 `updater.exe`로 해결 (Revit 종료를 기다린 후 extract). 수동 배포 시에도 반드시 Revit 종료 후 실행.

### CI에서 Revit API DLL 참조 (Nice3point 패키지)
GitHub runner에는 Revit이 설치돼있지 않음. csproj는 `REVIT_202x_PATH`가 비어있을 때 `Nice3point.Revit.Api.RevitAPI` / `.RevitAPIUI` NuGet 패키지로 fallback. **주의**: Nice3point는 Revit 2023용 net48 타겟을 제공하지 않음 → CI에서 Revit 2023 빌드 비활성화 상태. 로컬에선 `REVIT_2023_PATH` 설정 시 정상 빌드 가능.

## Debugging

- Revit Debug Output: `System.Diagnostics.Debug.WriteLine("[RevitMCP] ...")`
- MCP Server stderr: `console.error("[revit-mcp] ...")`
- Check plugin load: Revit → File → Options → Transfer → Add-ins tab
- WebSocket health: HTTP GET `http://127.0.0.1:8181/` returns `{"status":"ok"}`
