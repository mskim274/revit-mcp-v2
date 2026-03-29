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

- `server/` — TypeScript MCP server. Handles MCP protocol (stdio), tool definitions, input validation (Zod), pagination formatting, error wrapping. NO Revit logic here.
- `plugin/` — C# Revit Add-in. WebSocket server, command dispatching, JsonElement→native conversion, Revit.Async for thread safety. NO MCP logic here.
- `commandset/` — C# pure Revit API execution. Each command is one file implementing `IRevitCommand`. Auto-discovered via Assembly reflection. NO networking or protocol code here.

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
├── CLAUDE.md                          ← You are here
├── RevitMCP.sln                       ← Solution file
├── server/                            ← TypeScript MCP Server
│   ├── src/
│   │   ├── index.ts                   ← Entry point, tool registration
│   │   ├── constants.ts               ← Config (ports, timeouts, pagination)
│   │   ├── types.ts                   ← WebSocket protocol types
│   │   ├── services/
│   │   │   ├── websocket-client.ts    ← WS client with reconnection
│   │   │   └── pagination.ts          ← Cursor-based pagination helpers
│   │   └── tools/
│   │       ├── utility.ts             ← ping, get_project_info
│   │       ├── query.ts               ← 8 query tools (Sprint 2)
│   │       ├── create.ts              ← 2 create tools (Sprint 3)
│   │       ├── modify.ts              ← 4 modify tools (Sprint 3)
│   │       └── view.ts               ← 4 view tools (Sprint 4)
│   └── package.json
├── commandset/                        ← C# CommandSet library
│   ├── CommandSet.csproj
│   ├── Interfaces/
│   │   └── IRevitCommand.cs           ← Core interface + CommandResult
│   └── Commands/
│       ├── Utility/                   ← PingCommand, GetProjectInfoCommand
│       ├── Query/                     ← 8 query commands (Sprint 2)
│       ├── Create/                    ← 2 create commands (Sprint 3)
│       ├── Modify/                    ← 4 modify commands (Sprint 3)
│       └── View/                      ← 4 view commands (Sprint 4)
├── plugin/                            ← C# Revit Plugin
│   ├── RevitMCPPlugin/
│   │   ├── RevitMCPPlugin.csproj
│   │   ├── Application.cs             ← IExternalApplication entry point
│   │   ├── WebSocketServer.cs         ← WS server + dispatch + JsonElement conversion
│   │   └── CommandDispatcher.cs       ← Reflection-based command registry
│   └── revit-mcp.addin               ← Revit add-in manifest
├── scripts/
│   └── build-and-deploy.ps1          ← Build + copy to Revit Addins folder
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

```powershell
# Full build + deploy (Windows PowerShell)
.\scripts\build-and-deploy.ps1

# TypeScript only (no Revit restart needed!)
cd server && npm run build

# C# only (requires Revit restart)
dotnet build RevitMCP.sln -c Release
```

**Important**: C# changes require Revit restart (DLL locking). TypeScript changes only need Claude Desktop restart.

### Environment Variables

```
REVIT_2025_PATH=C:\Program Files\Autodesk\Revit 2025   # Revit API DLL location
REVIT_2023_PATH=C:\Program Files\Autodesk\Revit 2023   # For .NET 4.8 target
REVIT_MCP_PORT=8181                                      # WebSocket port (optional)
```

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
- [x] Sprint 3: Create/Modify Tools (6 tools: create_wall, create_floor, modify_element_parameter, delete_elements, move_elements, copy_elements)
- [x] Sprint 4: View Tools (4 tools: set_active_view, isolate_elements, reset_view_isolation, select_elements)
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
대형 모델에서 query 응답이 매우 큼 (예: 500+ 타입 목록, 98개 뷰 상세). 항상 `include_properties: false` 기본 사용, 필요시 `limit` 파라미터로 제한. 미개선 항목: 카테고리 상위 N개 제한, 타입 목록 truncate.

## Debugging

- Revit Debug Output: `System.Diagnostics.Debug.WriteLine("[RevitMCP] ...")`
- MCP Server stderr: `console.error("[revit-mcp] ...")`
- Check plugin load: Revit → File → Options → Transfer → Add-ins tab
- WebSocket health: HTTP GET `http://127.0.0.1:8181/` returns `{"status":"ok"}`
