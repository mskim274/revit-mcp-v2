# Session Handoff

Rolling notes for continuing work across machines and Claude sessions.
Update this file at the **end of every session** so the next one (on
any machine) can pick up in under a minute.

> **New Claude session on this repo?** Read this file first, then
> `CLAUDE.md` (conventions + architecture), then the latest entry in
> `CHANGELOG.md` (shipped features). That's 3 files, ~5 minutes.

---

## Current state — 2026-06-10 (P0 버그 4건 + batch/query 개선 — 실사용 페인포인트 해소)

### 배경: 실사용 세션에서 발견된 버그들

Y1P1 지원시설 모델에서 SK_* 파라미터 570건 일괄 입력 작업 중 3가지 페인:
①570번 개별 modify 호출, ②query 페이지네이션 cursor 미작동 (409개 중
200개만 접근 가능), ③SK_SIZE를 50A→250A로 바꾼 뒤에도 "50A" 검색에
385개가 잡힘 (stale 캐시로 오인 — 실제로는 `"250A".IndexOf("50A")==1`
substring false positive). 코드 리뷰 에이전트 2개 + 시장조사로 원인
확정 후 일괄 수정.

### 이번 세션 한 일 (tool inventory 28 → 29)

**P0 버그 수정 4건**
1. `QueryElementsCommand.cs` — parameter_value 매칭이 contains였던 것을
   **exact 기본**으로 변경 + `match_mode` (exact/contains/empty) 추가.
   `empty` = 파라미터 존재하나 값 없음 ("빈 칸 찾기").
2. `QueryElementsCommand.cs` — paginated 응답에 **`next_cursor` 발급**
   (기존엔 파싱 코드만 있고 생성 코드가 없는 write-only 기능이었음)
   + `ParseCursor`에 평문 정수 fallback ("200" → offset 200. 기존엔
   base64 실패를 빈 catch가 삼키고 무성으로 첫 페이지 재반환).
3. `ElementSelector.cs` — category fallback 데드코드 수정. AppliedFilters에
   넣은 `"category=X (post-filter)"` 문자열이 자기 자신의 StartsWith 검사에
   걸려 post-filter가 절대 실행 안 됨 → 비BIC 카테고리명이면 color_filter/
   tag가 전체 모델(MaxCount 10,000) 대상이 되는 위험. bool 플래그로 교체.
4. `WebSocketServer.cs` — `ReceiveAsync` 단일 호출이 `EndOfMessage` 확인
   없이 64KB 초과/분할 프레임을 자르던 것 → MemoryStream 누적 루프 +
   16MB 상한. batch 명령 선행 조건.

**P1 신규 기능**
5. `BatchModifyParametersCommand.cs` (신규) + `revit_batch_modify_parameters`
   — N건을 단일 트랜잭션으로. 입력 A: `modifications` 배열(요소별 다른 값),
   입력 B: `element_ids`+`parameters` 맵(균일 스탬핑). **`only_if_empty=true`**
   = 빈 파라미터만 채움(이번 실사용 "빈 칸만 채우기" 룰 그대로). 실패 항목
   개별 리포팅 + 성공분 일괄 commit. max 5000 set. 트랜잭션 루프 안에서는
   ThrowIfCancellationRequested **안 함** (workshared "operation canceled"
   회피 — 2026-04-30 발견 사항 반영).
   `WebSocketServer.cs`의 idempotency prefix `batch_create_` → `batch_`로
   일반화 (batch_modify_도 캐시 적용).
6. `query_elements`에 `ids_only` (페이지 기본 5000/최대 10000 — ID만 반환,
   88KB 스필 해소) + `group_by_parameter` (값별 개수 분포 — "지름별 SK_SIZE
   분포" 한 번에).

**빌드**: TS `npm run build` ✅ / C# net8.0-windows plugin+commandset ✅
(net48은 UpdateChecker HttpClient 기존 이슈로 이전부터 실패 — 이번 변경 무관)

### 미적용 권고 (다음 후보, 코드 리뷰에서 나온 것)

- TS `websocket-client.ts` lazy reconnect (10회 실패 후 영구 포기 → sendCommand에서 재시도)
- `export.ts` EXPORT_TIMEOUT_MS 정의만 되고 미전달 (대형 일람표 30s 타임아웃)
- delete/move/copy zod `.max()` 제약 누락 (설명에만 있음)
- create/modify 도구에 `idempotency_key` 미노출 (tag/batch만 있음)
- workshared 체크아웃 사전 검사 (`WorksharingUtils`) 전무
- `WebSocketServer.cs:267` `.Result` 동기 블로킹 (진짜 async 명령 추가 시 데드락)
- MCP 스펙: structuredContent/outputSchema, 스필 파일 resources 노출, elicitation(execute_script용)
- 시장조사 기반 신규 도구 후보: **create_pipe (CAD 종단도→파이프 파이프라인 완성 — 킬러)**,
  quantity_takeoff, detect_collisions, view snapshot, model health.
  Autodesk Revit 2027 공식 MCP는 read-only 6그룹 → read는 commodity화,
  write 안전성 + 한국 워크플로 + CAD 연계가 차별화 방향.

### 추가 (같은 날 오후): 로드맵 #5 revit_execute_script 출하 (29 → 30 tools)

"이 MCP의 사용자는 Claude" 원칙의 2층 escape hatch. 전용 도구가 없는
요구는 Claude가 Revit API C# 코드를 직접 작성해 실행한다.

- `commandset/Commands/Script/ExecuteScriptCommand.cs` (신규)
  - Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting` 4.9.2, **net8 전용**
    PackageReference — net48은 `#if NETFRAMEWORK` 스텁이 "not supported" 반환)
  - 전역 `ScriptGlobals`: `doc` / `print(object)` / `MmToFt` / `FtToMm`
  - mode=query: 트랜잭션 자체를 안 열음 → Revit API가 변경 시 throw
    (물리적 read-only 강제). mode=modify: 단일 트랜잭션 + 예외 시 롤백.
  - denylist: System.IO/Net/Process/Reflection, new Transaction(도구가 관리),
    Save/SaveAs/SynchronizeWithCentral, unsafe, DllImport. 샌드박스 아님 —
    사고 방지 가드.
  - 컴파일 진단 (line/col + CS코드) 반환 → Claude 자가 수정 루프.
    런타임 에러에 마지막 print 10줄 첨부. 반환값은 JSON-safe 직렬화
    (Element→{id,name,category}, XYZ→{x,y,z}, 컬렉션 1000 cap, depth 4).
- `server/src/tools/script.ts` (신규) + index.ts 등록.
  timeout_ms 노출 (기본 60s, max 300s — sendAndFormat 4번째 인자로 전달).
- `WebSocketServer.cs` IsSideEffectCommand에 `execute_` prefix 추가
  (modify 스크립트 + idempotency_key 재시도 보호).
- 빌드: TS ✅ / commandset+plugin net8 ✅. Roslyn DLL 4개가 plugin 출력에
  자동 포함 확인 (CopyLocalLockFileAssemblies) — deploy 스크립트가 *.dll
  전체를 Addins로 복사하므로 추가 작업 불필요.

알려진 한계 (의도된 v1 트레이드오프):
- 무한루프는 중단 불가 (Revit UI 스레드 점유 — .NET 8에 Thread.Abort 없음).
  WS 타임아웃은 Claude에게 에러를 돌려주지만 Revit은 스크립트가 끝날 때까지
  busy. pyRevit 등 모든 Revit 스크립트 러너의 공통 한계. description에 경고.
- denylist는 문자열 검사 — 우회 가능하나 위협 모델이 "Claude의 실수 방지"라
  충분. 사용자 승인 다이얼로그(elicitation)는 v2 후보.
- Revit 2025 자체 Roslyn(매크로용)과의 어셈블리 버전 충돌 가능성 — 실배포
  후 첫 실행에서 확인 필요. 충돌 시 AssemblyLoadContext 격리 검토.

### 다음 세션 시작점

기존 7-tool 로드맵 중 **#5 execute_script 완료**. 남은 것: #3
multi-instance routing, #4 sheet batch, #6 import_cad_link, #7
load_family. 이번에 추가된 후보들과 합쳐 우선순위 재조정 권장 —
**create_pipe**가 실사용 가치 최상 (당분간은 execute_script로 파이프
생성을 때우면서 패턴이 굳으면 1층 도구로 승격하는 경로도 유효).

⚠️ C# 변경분은 Revit 재시작 + `.\scripts\build-and-deploy.ps1 -RevitVersion
2025` (Revit 종료 상태) 후 반영됨. 이번 세션은 빌드 검증까지만 — 배포는
Revit이 켜져 있어 보류.

---

## Earlier — 2026-05-13 (Sprint 5: Visualize/Export tools 착수)

### 이번 세션 한 일

**Tool inventory 20 → 23.** 7개 신규 도구 로드맵을 잡고 1~2번을 머지했다.
시장 조사 (LuDattilo 124 tools, Demolinator 45, Autodesk Revit 2027 native
MCP) + 우리 RC reconciliation 워크플로우 기반으로 우선순위를 정한 결과.

**빌드 인프라 수정** (`b932aa7`)
- `scripts/build-and-deploy.ps1`이 `*.deps.json`을 Addins 폴더에 안 옮기던
  버그 — Revit 2025(net8.0-windows) plugin이 로드 실패하면서
  `"외부 응용프로그램 'Revit MCP Plugin'을 실행할 수 없습니다"` 다이얼로그가
  뜨고 .addin 매니페스트에 `<SuppressedWarning>UncaughtException</SuppressedWarning>`이
  자동 추가됐다. deploy 단계에 `Copy-Item "$outputDir\*.deps.json" ... -ErrorAction
  SilentlyContinue` 한 줄 추가로 해결.

**#1 revit_export_schedule** (`d43ea52`) — Export 카테고리 신설
- `commandset/Commands/Export/ExportScheduleCommand.cs`
- `server/src/tools/export.ts`
- `ViewSchedule.GetTableData()` 기반. JSON / CSV / both 출력.
- CSV는 **UTF-8 BOM 기본** (Excel 한국어 Windows에서 cp949 오인식 회피).
- 해석: schedule_id 우선 → schedule_name (exact → contains). 모호하면
  후보 나열. 발견 못 하면 `revit_get_views(view_type="Schedule")` 안내.
- 셀 정규화: `\r\n` → `\n`, RFC 4180 quote escape.
- 중복 헤더는 JSON에서 `" (2)"`, `" (3)"` 접미사로 disambiguate.
- 하네스: post-export verification (파일 존재, size, expected vs actual
  line count). overflow spill은 공유 sendAndFormat이 자동 처리.

**#2 revit_apply_color_filter + revit_tag_by_filter** (`a8cec5e`) —
review-aid 도구 묶음
- `commandset/Helpers/ElementSelector.cs` — 공유 selector
  (element_ids | category | type_name contains/starts_with | mark_contains
  | parameter_name+value | level_name | view 스코프). structural framing의
  `참조 레벨` / `Reference Level` 파라미터도 level_name 매칭에 포함.
- `commandset/Commands/View/ApplyColorFilterCommand.cs` — view 단위
  graphic override (line + surface foreground/cut + transparency + halftone).
  컬러: 프리셋 (red/orange/yellow/green/blue/magenta/cyan/gray) 또는 "r,g,b".
  mode=clear는 OverrideGraphicSettings 빈 객체로 초기화.
- `commandset/Commands/Create/TagByFilterCommand.cs` — `IndependentTag.Create`
  bulk. single transaction + per-element skip 리포팅. anchor 휴리스틱:
  LocationPoint → 점, LocationCurve → 중점, fallback → bbox 중심.
- `server/src/tools/visualize.ts` — 둘 묶어서 wrap.
- `plugin/RevitMCPPlugin/WebSocketServer.cs` 의 idempotency-cache
  prefix 목록에 `apply_` + `tag_` 추가 (15분 dedup window).
- 하네스: apply는 post-tx에 첫 element 재읽고 `color_match` 리포트,
  tag는 생성된 tag id를 재조회해 `count_match` 검증.

### 다음 세션 — Task #3 부터

`TaskList`로 확인:

```
#3. [pending] revit_multi_instance_routing + 리본 버튼     ← 다음 시작
#4. [pending] revit_create_sheet_batch + revit_place_views_on_sheet
#5. [pending] revit_execute_script (C# REPL)
#6. [pending] revit_import_cad_link + DWG schedule sync
#7. [pending] revit_load_family + revit_link_revit_model
```

#3는 다른 작업보다 범위가 큼 — plugin UI (RibbonPanel + PushButton),
auto-port 할당 (8181 충돌 시 8182, 8183 ...), `%TEMP%\revit-mcp\<pid>.json`
lockfile, MCP server 측에 multi-instance discovery + 파일명 기반 라우팅
까지. 시작 전에 LinkedIn / Revit ribbon API 한 번 더 검토 권장.

#5 `revit_execute_script`는 보안 요주의 — 임의 C# 코드 실행이라 risk
classification + 사용자 확인 다이얼로그 필수. Harness Tier 2의
risk classifier와 같이 묶는 것도 고려해볼만함.

### 7개 도구 추천 근거 (요약)

시장 비교 (May 2026):
- 우리: 23 tools (Export 1 + Visualize 2 갓 추가)
- Demolinator (pyRevit): 45
- schauh11: 53+
- LuDattilo: 124 (dockable Claude panel, clash detection, model health)
- **Autodesk Revit 2027 공식 MCP** (Tech Preview) — Anthropic MCP 기반,
  6 tool groups. 곧 native 통합이 표준이 되는 흐름이라 우리는 한국 시장
  특화 (cp949 처리, DWG 일람표 sync, SK_FL/SK_ITEM 등 한국 구조 워크플로우)
  로 차별화하는 게 맞다.

### 작업 흐름 (#1 같이 또 다음 작업할 때 패턴)

1. C# command — `commandset/Commands/{Category}/...Command.cs`
   - `IRevitCommand` 구현, `Name` = snake_case 매칭, `Category` 분류
   - Create/Modify는 Transaction 필수, 이름 `"MCP: ..."`
   - post-tx verification 블록 반환 (`verification.performed=true` + 비교)
   - 에러 응답에 `suggestion` 포함
2. TS tool — `server/src/tools/{module}.ts`
   - `server.registerTool("revit_xxx", { ..., inputSchema, annotations }, ...)`
   - `sendAndFormat(wsClient, "xxx", { ... })` — overflow spill 자동
   - read-only면 `readOnlyHint: true`, side-effect면 `false`, 파괴적이면
     `destructiveHint: true`
3. `server/src/index.ts`에 `registerXxxTools` 추가
4. plugin `WebSocketServer.cs` `IsSideEffectCommand` prefix 추가
   (idempotency cache 적용 범위 확장 필요할 때)
5. `CLAUDE.md` Tool Inventory 수 + 섹션 갱신
6. 빌드: `.\scripts\build-and-deploy.ps1 -RevitVersion 2025` (Revit 종료 상태)
7. commit + push

### 무관 변경 (남아있음)

- `package-lock.json` 한 줄 modified — `"peer": true` 제거. column 작업
  무관. 다른 컨텍스트의 흔적이라 그대로 두는 중. 필요 시 별도 정리.

---

## Earlier — 2026-04-30 (RC reconciliation: Phase 5 완료)

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
