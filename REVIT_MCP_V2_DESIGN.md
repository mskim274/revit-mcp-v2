# Revit MCP V2 — Architecture Design Document

**Author:** kimminsub (and.ms.kim@gmail.com)
**Date:** 2026-03-29
**Status:** Draft — Review Required
**Version:** 0.1.0

---

## 1. Problem Statement

기존 REVIT_MCP (v1)은 다음과 같은 구조적 문제를 갖고 있다.

**성능 문제:** query_elements, get_family_types 등의 조회 명령이 전체 결과를 한 번에 직렬화하여 반환한다. 부재가 많은 대형 Revit 모델(보 500개+)에서 요소 전체 추출 시 타임아웃 또는 메모리 초과로 멈춤 현상이 발생한다.

**개발 속도 문제:** 새 명령어 하나를 추가하려면 3곳(Command 클래스, Application.cs RegisterCommands(), ToolRegistry.cs)을 수정하고, 빌드 → Revit 재시작 → Claude 재시작 사이클을 반복해야 한다. 이 과정이 기능 하나당 10~20분의 로스를 만든다.

**안정성 문제:** 에러 핸들링이 일관되지 않아 Revit API 예외가 그대로 전파되어 크래시하거나, Claude에게 의미 없는 에러 메시지가 전달된다.

---

## 2. Goals & Non-Goals

### Goals
- 대형 Revit 모델(요소 10,000개+)에서도 안정적으로 동작
- 새 명령어 추가 시 수정 포인트를 1곳으로 줄임
- 페이지네이션/요약 모드로 대용량 데이터를 점진적으로 전달
- Revit 2023 (.NET Framework 4.8) + 2025 (.NET 8.0) 동시 지원
- 모든 명령에 일관된 에러 핸들링 및 타임아웃 보호
- gstack 워크플로우(Think → Plan → Build → Review → Test → Ship)로 체계적 개발

### Non-Goals
- 클라우드 기반 헤드리스 Revit 자동화 (Autodesk APS 방식은 범위 밖)
- Revit 2022 이하 버전 지원
- 실시간 양방향 모델 동기화 (이벤트 스트림)
- 멀티 유저 동시 접속

---

## 3. Architecture Overview

```
┌──────────┐     stdio      ┌──────────────────┐    WebSocket    ┌──────────────────┐
│  Claude   │ ◄─────────────► │   MCP Server      │ ◄──────────────► │   Revit Plugin    │
│  Desktop  │                │   (TypeScript)    │                │   (C# Add-in)    │
│           │                │                  │                │                  │
│  MCP Host │                │  server/          │                │  plugin/          │
│           │                │  - 도구 정의       │                │  - WS 브릿지       │
│           │                │  - 페이지네이션     │                │  - 커맨드 디스패치   │
│           │                │  - 에러 래핑       │                │                  │
│           │                │  - 응답 포맷팅     │                │  commandset/      │
│           │                │                  │                │  - Revit API 실행  │
│           │                │                  │                │  - 순수 로직       │
└──────────┘                └──────────────────┘                └──────────────────┘
```

### 3.1 Three-Layer Separation

**Layer 1: MCP Server (TypeScript, server/)**
- MCP 프로토콜 처리 (stdio transport)
- 도구(tool) 정의 및 입력 검증 (Zod)
- 페이지네이션, 요약 모드, CSV 포맷 변환
- WebSocket 클라이언트로 Plugin과 통신
- 에러 래핑 및 사용자 친화적 메시지 생성

**Layer 2: Revit Plugin (C#, plugin/)**
- Revit Add-in으로 Revit 프로세스 안에서 실행
- WebSocket 서버 역할 (Plugin이 서버, MCP Server가 클라이언트)
- 수신된 명령을 CommandSet으로 디스패치
- **Revit.Async** 라이브러리를 통한 Revit API 스레드 안전한 실행 (TAP 패턴)

**Layer 3: CommandSet (C#, commandset/)**
- 순수 Revit API 실행 로직만 포함
- Plugin과 분리되어 독립 단위 테스트 가능
- 리플렉션 기반 자동 등록 — 새 Command 추가 시 여기에 파일 하나만 추가
- 결과를 직렬화 가능한 DTO로 반환

---

## 4. Repository Structure (Mono-repo)

```
revit-mcp-v2/
├── .github/
│   └── workflows/          # CI/CD
├── server/                  # TypeScript MCP Server
│   ├── src/
│   │   ├── index.ts         # 진입점, McpServer 초기화
│   │   ├── tools/           # 도구 정의 (도메인별 분리)
│   │   │   ├── query.ts     # query_elements, get_element_info...
│   │   │   ├── create.ts    # create_wall, create_floor...
│   │   │   ├── modify.ts    # modify_element_parameter, move...
│   │   │   ├── view.ts      # get_views, set_active_view...
│   │   │   ├── export.ts    # export_to_pdf, export_to_dwg...
│   │   │   └── utility.ts   # ping, get_project_info...
│   │   ├── services/
│   │   │   ├── websocket-client.ts  # Plugin과 WebSocket 통신
│   │   │   └── pagination.ts        # 페이지네이션 유틸리티
│   │   ├── schemas/         # Zod 스키마
│   │   ├── types.ts         # 공유 타입 정의
│   │   └── constants.ts     # 상수 (포트, 타임아웃 등)
│   ├── package.json
│   ├── tsconfig.json
│   └── README.md
├── plugin/                  # C# Revit Plugin
│   ├── RevitMCPPlugin/
│   │   ├── Application.cs           # Revit Add-in 진입점
│   │   ├── WebSocketServer.cs       # WS 서버
│   │   ├── CommandDispatcher.cs     # 명령 디스패치 + 리플렉션 등록
│   │   └── RevitMCPPlugin.csproj
│   ├── RevitMCPPlugin.Tests/
│   └── revit-mcp.addin             # Revit Add-in 매니페스트
├── commandset/              # C# CommandSet (순수 Revit API)
│   ├── Commands/
│   │   ├── Query/
│   │   │   ├── QueryElementsCommand.cs
│   │   │   ├── GetElementInfoCommand.cs
│   │   │   ├── GetLevelsCommand.cs
│   │   │   └── ...
│   │   ├── Create/
│   │   │   ├── CreateWallCommand.cs
│   │   │   ├── CreateFloorCommand.cs
│   │   │   └── ...
│   │   ├── Modify/
│   │   └── Export/
│   ├── Models/               # DTO 정의
│   ├── Interfaces/
│   │   └── IRevitCommand.cs  # 모든 명령이 구현하는 인터페이스
│   ├── CommandSet.csproj
│   └── CommandSet.Tests/
├── protocol/                # 공유 프로토콜 정의
│   └── messages.md          # WebSocket 메시지 스키마 문서
├── scripts/
│   ├── build-and-deploy.ps1
│   └── dev-setup.ps1
├── docs/
│   ├── ARCHITECTURE.md
│   └── COMMANDS.md
├── RevitMCP.sln             # C# 솔루션 (plugin + commandset + tests)
├── CLAUDE.md
├── AGENTS.md
├── package.json             # 루트 (워크스페이스 설정)
└── README.md
```

---

## 5. Thread Safety: Revit.Async Pattern

### 5.1 Problem

Revit API는 **반드시 메인 스레드에서만** 실행되어야 한다. WebSocket 메시지는 별도 Worker Thread에서 수신되므로, 여기서 직접 Revit API를 호출하면 데드락 또는 크래시가 발생한다. 이것이 v1에서 "멈춤 현상"의 근본 원인 중 하나다.

### 5.2 Solution: Revit.Async

**Revit.Async** (https://github.com/KennanChan/Revit.Async) 라이브러리를 사용한다. 이 라이브러리는 Task-based Async Pattern(TAP)으로 ExternalEvent를 래핑하여, 어떤 스레드에서든 Revit API를 안전하게 호출할 수 있게 해준다.

선택 근거: "boring by default" 원칙. 직접 ExternalEvent + Queue 패턴을 구현하면 데드락 함정에 빠지기 쉽다. Revit.Async는 검증된 라이브러리이며, Revit 커뮤니티에서 널리 사용된다.

### 5.3 Execution Flow

```
WebSocket 메시지 수신 (Worker Thread)
    │
    ├── JSON 파싱 → command, params 추출
    │
    ├── RevitTask.RunAsync(async () => {
    │       // 이 블록은 Revit 메인 스레드에서 실행됨
    │       var command = _dispatcher.GetCommand(commandName);
    │       return await command.ExecuteAsync(doc, params, cancellationToken);
    │   });
    │
    ├── Revit.Async 내부:
    │   ├── IExternalEventHandler에 작업 래핑
    │   ├── ExternalEvent.Raise() 호출
    │   ├── Revit 메인 스레드에서 Execute() 실행
    │   └── TaskCompletionSource로 결과 반환
    │
    └── Worker Thread에서 결과 수신 → WebSocket 응답 전송
```

### 5.4 Plugin Code Pattern

```csharp
// plugin/WebSocketServer.cs
private async Task HandleMessage(string message)
{
    var request = JsonSerializer.Deserialize<CommandRequest>(message);

    try
    {
        // Revit.Async가 메인 스레드 실행을 보장
        var result = await RevitTask.RunAsync(async () =>
        {
            var doc = _application.ActiveUIDocument.Document;
            var command = _dispatcher.GetCommand(request.Command);
            return await command.ExecuteAsync(doc, request.Params, _cts.Token);
        });

        await SendResponse(request.Id, "success", result);
    }
    catch (OperationCanceledException)
    {
        await SendError(request.Id, "TIMEOUT_ERROR",
            "Command execution timed out",
            "Try reducing the scope with limit parameter");
    }
    catch (Exception ex)
    {
        await SendError(request.Id, "REVIT_API_ERROR",
            ex.Message,
            _dispatcher.GetSuggestion(request.Command, ex));
    }
}
```

### 5.5 Initialization

```csharp
// plugin/Application.cs
public Result OnStartup(UIControlledApplication application)
{
    // Revit.Async 초기화 — 반드시 OnStartup에서 호출
    RevitTask.Initialize(application);

    // WebSocket 서버 시작
    _wsServer = new WebSocketServer(application.ControlledApplication);
    _wsServer.Start();

    return Result.Succeeded;
}
```

---

## 6. Communication Protocol

### 5.1 WebSocket Message Format

MCP Server ↔ Plugin 간 WebSocket 통신은 JSON 메시지를 사용한다.

**Request (Server → Plugin):**
```json
{
  "id": "req-uuid-001",
  "command": "query_elements",
  "params": {
    "category": "StructuralFraming",
    "limit": 50,
    "cursor": null,
    "summary_only": false
  },
  "timeout_ms": 30000
}
```

**Response (Plugin → Server):**
```json
{
  "id": "req-uuid-001",
  "status": "success",
  "data": {
    "total_count": 523,
    "returned_count": 50,
    "has_more": true,
    "next_cursor": "eyJvZmZzZXQiOjUwfQ==",
    "items": [ ... ]
  }
}
```

**Error Response:**
```json
{
  "id": "req-uuid-001",
  "status": "error",
  "error": {
    "code": "REVIT_API_ERROR",
    "message": "FilteredElementCollector failed: category not found",
    "recoverable": true,
    "suggestion": "Use revit_get_all_categories to find valid category names"
  }
}
```

**Progress (Plugin → Server, optional):**
```json
{
  "id": "req-uuid-001",
  "status": "progress",
  "progress": {
    "current": 200,
    "total": 523,
    "message": "Processing elements..."
  }
}
```

### 5.2 Connection Lifecycle

```
MCP Server 시작
    │
    ├── WebSocket 연결 시도 (ws://127.0.0.1:8181)
    │
    ├── 연결 실패 → 5초 후 재시도 (최대 10회)
    │
    ├── 연결 성공 → ping/pong 헬스체크 (30초 간격)
    │
    ├── 명령 수신 → Plugin으로 전달 → 응답 대기 (타임아웃 포함)
    │
    └── 연결 끊김 → 자동 재연결
```

---

## 7. Pagination Strategy (3-Tier Response)

### Tier 1: Summary Mode (기본)
```
요청: revit_query_elements(category="StructuralFraming", summary_only=true)

응답:
  total: 523
  by_type: { "RC Beam 400x600": 200, "Steel H-300": 323 }
  by_level: { "1F": 120, "2F": 180, "3F": 223 }
```

Claude가 대부분의 경우 이것만으로 사용자 질문에 답할 수 있다. "프로젝트에 보가 몇 개야?" → 523개.

### Tier 2: Paginated Detail
```
요청: revit_query_elements(category="StructuralFraming", limit=50)

응답:
  total_count: 523
  returned_count: 50
  has_more: true
  next_cursor: "eyJvZmZzZXQiOjUwfQ=="
  items: [
    { id: 12345, type: "RC Beam 400x600", level: "1F", length: 6.0 },
    ...
  ]
```

Claude가 "다음 페이지"를 요청하면 cursor를 넘겨서 다음 50개를 받는다.

### Tier 3: Full Export
```
요청: revit_export_query_results(
  category="StructuralFraming",
  format="csv",
  output_path="C:/Export/beams.csv"
)

응답:
  file_path: "C:/Export/beams.csv"
  row_count: 523
  columns: ["Id", "Type", "Level", "Length", "Section"]
```

전체 데이터가 필요할 때 파일로 내보내기. Claude 컨텍스트 윈도우를 오염시키지 않는다.

---

## 8. Command Registration (Reflection-based)

### 7.1 IRevitCommand Interface

```csharp
// commandset/Interfaces/IRevitCommand.cs
public interface IRevitCommand
{
    string Name { get; }           // "query_elements"
    string Category { get; }       // "Query"
    Task<CommandResult> ExecuteAsync(
        Document doc,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken
    );
}
```

### 7.2 Attribute-based Registration

```csharp
// commandset/Commands/Query/QueryElementsCommand.cs
[RevitCommand("query_elements", Category = "Query")]
public class QueryElementsCommand : IRevitCommand
{
    public async Task<CommandResult> ExecuteAsync(
        Document doc,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        // Revit API logic here
    }
}
```

### 7.3 Auto-Discovery

```csharp
// plugin/CommandDispatcher.cs
public class CommandDispatcher
{
    private readonly Dictionary<string, IRevitCommand> _commands;

    public CommandDispatcher()
    {
        // commandset 어셈블리에서 IRevitCommand 구현체를 자동 검색
        _commands = Assembly.GetAssembly(typeof(IRevitCommand))
            .GetTypes()
            .Where(t => typeof(IRevitCommand).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (IRevitCommand)Activator.CreateInstance(t))
            .ToDictionary(c => c.Name);
    }
}
```

이 구조에서 **새 명령어 추가 = commandset/Commands/에 C# 파일 하나 추가**. 등록, 디스패치, MCP 도구 정의 모두 자동.

---

## 9. Error Handling Strategy

### 8.1 Error Categories

| Code | 의미 | 복구 가능 | 예시 |
|------|------|----------|------|
| CONNECTION_ERROR | Plugin 연결 끊김 | 자동 재시도 | WebSocket 끊김 |
| TIMEOUT_ERROR | 명령 실행 시간 초과 | 재시도 가능 | 대용량 조회 30초 초과 |
| REVIT_API_ERROR | Revit API 예외 | 파라미터 수정 | 잘못된 카테고리명 |
| VALIDATION_ERROR | 입력값 검증 실패 | 파라미터 수정 | 필수 파라미터 누락 |
| INTERNAL_ERROR | 예기치 못한 오류 | 불가 | 널 참조, 캐스팅 오류 |

### 8.2 Error Response Pattern

모든 에러에는 `suggestion` 필드를 포함하여 Claude가 자동으로 복구를 시도할 수 있게 한다.

```json
{
  "status": "error",
  "error": {
    "code": "REVIT_API_ERROR",
    "message": "Category 'Beam' not found",
    "recoverable": true,
    "suggestion": "Valid category name is 'StructuralFraming'. Use revit_get_all_categories to list all categories."
  }
}
```

### 8.3 Timeout Protection

모든 명령에 기본 타임아웃 30초. 대용량 작업(export, batch)은 120초.

```
요청 수신 → 타이머 시작 → Revit API 실행 → 타임아웃 전 완료?
    YES → 정상 응답
    NO  → CancellationToken 발동 → TIMEOUT_ERROR + "limit 파라미터로 조회 범위를 줄이세요"
```

---

## 10. Multi-Version Support (Revit 2023 + 2025)

### 9.1 Build Configuration

```xml
<!-- plugin/RevitMCPPlugin.csproj -->
<PropertyGroup>
  <TargetFrameworks>net48;net8.0-windows</TargetFrameworks>
</PropertyGroup>

<ItemGroup Condition="'$(TargetFramework)' == 'net48'">
  <!-- Revit 2023/2024 references -->
  <Reference Include="RevitAPI" />
  <Reference Include="RevitAPIUI" />
</ItemGroup>

<ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
  <!-- Revit 2025/2026 references -->
  <PackageReference Include="Autodesk.Revit.SDK" Version="2025.*" />
</ItemGroup>
```

### 9.2 Version-Specific Code

```csharp
#if NET48
    // Revit 2023/2024 specific code
#else
    // Revit 2025+ specific code
#endif
```

### 9.3 Deploy Script

```powershell
# scripts/build-and-deploy.ps1
dotnet build -c Release -f net48       # → Revit 2023 addin 폴더
dotnet build -c Release -f net8.0-windows  # → Revit 2025 addin 폴더
```

---

## 11. Tool Definitions (MCP Server Side)

### 10.1 Naming Convention

MCP best practice에 따라 `revit_` prefix + snake_case.

```
revit_query_elements       (not query_elements)
revit_create_wall          (not create_wall)
revit_get_project_info     (not get_project_info)
```

### 10.2 Tool Categories & Priority

**Tier 1 — Core (Sprint 1~2)**

| Tool | Description | Annotations |
|------|-------------|-------------|
| revit_ping | 연결 상태 확인 | readOnly: true |
| revit_get_project_info | 프로젝트 기본 정보 | readOnly: true |
| revit_query_elements | 카테고리별 요소 검색 (페이지네이션) | readOnly: true |
| revit_get_element_info | 요소 상세 정보 | readOnly: true |
| revit_get_levels | 레벨 목록 | readOnly: true |
| revit_get_grids | 그리드 목록 | readOnly: true |
| revit_get_views | 뷰 목록 | readOnly: true |
| revit_get_family_types | 패밀리 타입 조회 | readOnly: true |
| revit_get_all_categories | 카테고리 목록 | readOnly: true |
| revit_get_types_by_category | 카테고리별 타입 | readOnly: true |

**Tier 2 — Creation & Modification (Sprint 3)**

| Tool | Description | Annotations |
|------|-------------|-------------|
| revit_create_wall | 벽 생성 | destructive: false |
| revit_create_floor | 바닥 생성 | destructive: false |
| revit_create_column | 기둥 생성 | destructive: false |
| revit_create_beam | 보 생성 | destructive: false |
| revit_modify_element_parameter | 파라미터 수정 | destructive: false |
| revit_move_elements | 요소 이동 | destructive: false |
| revit_copy_elements | 요소 복사 | destructive: false |
| revit_delete_elements | 요소 삭제 | destructive: true |
| revit_get_selected_elements | 선택된 요소 | readOnly: true |
| revit_select_elements | 요소 선택 | destructive: false |

**Tier 3 — Views & Export (Sprint 4)**

| Tool | Description | Annotations |
|------|-------------|-------------|
| revit_create_level | 레벨 생성 | destructive: false |
| revit_create_grid | 그리드 생성 | destructive: false |
| revit_create_view | 뷰 생성 | destructive: false |
| revit_create_sheet | 시트 생성 | destructive: false |
| revit_place_view_on_sheet | 시트에 뷰 배치 | destructive: false |
| revit_export_to_pdf | PDF 내보내기 | readOnly: true |
| revit_export_to_dwg | DWG 내보내기 | readOnly: true |
| revit_export_schedule | 일람표 CSV 내보내기 | readOnly: true |
| revit_set_active_view | 활성 뷰 변경 | destructive: false |
| revit_zoom_to_elements | 요소로 줌 | destructive: false |

**Tier 4 — Advanced (Sprint 5+)**

| Tool | Description | Annotations |
|------|-------------|-------------|
| revit_detect_collisions | 충돌 검사 | readOnly: true |
| revit_calculate_volume | 볼륨 계산 | readOnly: true |
| revit_array_elements | 배열 복사 | destructive: false |
| revit_mirror_elements | 대칭 복사 | destructive: false |
| revit_rotate_elements | 요소 회전 | destructive: false |
| revit_tag_elements | 태그 배치 | destructive: false |
| revit_isolate_in_view | 뷰 격리/숨기기 | destructive: false |
| revit_batch_create_family_types | 일괄 타입 생성 | destructive: false |
| revit_purge_unused | 미사용 요소 정리 | destructive: true |

---

## 12. Development Workflow (gstack)

### 11.1 Sprint Plan

```
Phase 1: Think (완료)
  └── /office-hours → 이 설계 문서

Phase 2: Plan
  ├── /plan-ceo-review → 스코프 확정 (Tier 1~4 중 어디까지?)
  └── /plan-eng-review → 아키텍처 최종 확정

Phase 3: Build
  ├── Sprint 1: 인프라 (server/ + plugin/ + WebSocket 통신 + ping)
  ├── Sprint 2: Tier 1 조회 명령 (페이지네이션 포함)
  ├── Sprint 3: Tier 2 생성/수정 명령
  ├── Sprint 4: Tier 3 뷰/내보내기
  └── Sprint 5+: Tier 4 고급 기능

Phase 4: Review & Test (매 스프린트)
  ├── /review → 코드 리뷰
  ├── /qa → 통합 테스트
  └── /cso → 보안 리뷰

Phase 5: Ship
  ├── /ship → PR 생성
  ├── /land-and-deploy → 배포
  └── /canary → 모니터링

Phase 6: Reflect
  ├── /retro → 스프린트 회고
  └── /document-release → 문서 업데이트
```

### 11.2 Development Environment

| 영역 | 환경 | 도구 |
|------|------|------|
| TypeScript (server/) | code-server (Ubuntu) 또는 로컬 Windows | Claude Code + gstack |
| C# (plugin/ + commandset/) | 로컬 Windows | Visual Studio / VS Code |
| 통합 테스트 | 로컬 Windows + Revit | Revit 2023/2025 실행 |
| Git | 모노레포 | GitHub |

---

## 13. Technology Stack

| Component | Technology | Version |
|-----------|-----------|---------|
| MCP Server | TypeScript + Node.js | Node 20+ |
| MCP SDK | @modelcontextprotocol/sdk | Latest |
| Schema Validation | Zod | 3.x |
| WebSocket (Server) | ws | 8.x |
| Transport | stdio (MCP) + WebSocket (internal) | — |
| Revit Plugin | C# | .NET FW 4.8 / .NET 8.0 |
| WebSocket (Client) | System.Net.WebSockets | Built-in |
| Thread Safety | Revit.Async | Latest |
| Build | dotnet CLI + npm | — |
| CI/CD | GitHub Actions | — |

---

## 14. Migration from V1

### 13.1 Reusable Components
- commandset C# 로직 (Revit API 호출 코어): 60~70% 재활용
- build-and-deploy.ps1 빌드 스크립트: 멀티타겟 로직 재활용
- directives/ 문서: 명령어 스펙 참조용

### 13.2 To Be Replaced
- HTTP 통신 → WebSocket
- C# MCP Server (RevitMCPServer) → TypeScript MCP Server
- ToolRegistry.cs → server/tools/*.ts (Zod 스키마)
- Application.cs RegisterCommands() → 리플렉션 기반 자동 등록
- 에러 핸들링 → 일관된 에러 카테고리 시스템

---

## 15. References

### Revit MCP Projects
- [revit-mcp by Sparx](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) — WebSocket + CommandSet 패턴 참조
- [oakplank/RevitMCP](https://github.com/oakplank/RevitMCP) — pyRevit 핫리로드 참조
- [PiggyAndrew/revit_mcp](https://github.com/piggyandrew/revit_mcp) — TypeScript MCP 서버 참조
- [Autodesk APS Sample](https://github.com/autodesk-platform-services/aps-sample-mcp-server-revit-automation/) — 클라우드 자동화 참조

### MCP Specification
- [MCP Pagination](https://modelcontextprotocol.io/specification/2025-03-26/server/utilities/pagination)
- [Axiom: Designing MCP Servers for Large Result Sets](https://axiom.co/blog/designing-mcp-servers-for-wide-events)

### Articles
- [ArchiLabs: Revit MCP Overview](https://archilabs.ai/posts/revit-model-context-protocol)
- [Autodesk Blog: Talk to Your BIM](https://aps.autodesk.com/blog/talk-your-bim-exploring-aec-data-model-mcp-server-claude)

---

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 0 | — | — |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 0 | — | — |
| Design Review | `/plan-design-review` | UI/UX gaps | 0 | — | — |

**VERDICT:** DESIGN DOC READY — Run `/plan-eng-review` to lock architecture, then start Sprint 1.
