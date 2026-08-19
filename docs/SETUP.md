# Setup Guide — 새 컴퓨터에서 작업 이어가기

회사/집 두 대 컴퓨터에서 같은 프로젝트를 작업하기 위한 세팅 가이드입니다.
이 문서는 회사 컴퓨터에서 실제로 적용한 절차를 그대로 따라갈 수 있도록
순서대로 정리되어 있습니다.

> **결론 먼저**: 코드는 git에 다 있고, **머신별로 따로 세팅해야 하는 항목들**이
> 핵심입니다. 환경변수, MCP 클라이언트 설정, GitHub 인증, 빌드 산출물 등.

---

## 0. 사전 설치 (Prerequisites)

새 머신에서 처음 한 번만:

| 도구 | 용도 | 설치 |
|---|---|---|
| **Autodesk Revit 2025** | 호스트 애플리케이션 | Autodesk 라이선스 |
| **AutoCAD 2025** (선택) | autocad-mcp 사용 시 | Autodesk 라이선스 |
| **.NET 8 SDK** | C# 플러그인 + 업데이터 빌드 | https://dotnet.microsoft.com/download |
| **Node.js 20+** | TypeScript MCP 서버 빌드 | https://nodejs.org |
| **Git** | 저장소 클론 | https://git-scm.com |
| **Claude Desktop** | MCP 클라이언트 | https://claude.ai/download |
| **Grok CLI** (선택) | Orca / 터미널 MCP 클라이언트 | `grok` 바이너리 (`~/.grok/bin`) |
| **winget** | Windows 패키지 매니저 (Win11 기본 포함) | — |

Windows 10/11 전용 (플러그인이 WPF 사용).

---

## 1. 저장소 클론

```powershell
# 원하는 폴더로 이동 후
git clone https://github.com/mskim274/revit-mcp-v2.git
cd revit-mcp-v2
```

---

## 2. Git 글로벌 설정 (한 번만)

```powershell
git config --global user.name "<your-github-username>"
git config --global user.email "<your-email>"
git config --global core.autocrlf true            # Windows 개행 정규화
git config --global credential.helper manager     # 자격증명 캐싱
git config --global init.defaultBranch main
```

확인:
```powershell
git config --global --list | findstr /R "^user ^core.autocrlf ^credential ^init.default"
```

---

## 3. GitHub CLI 설치 + 인증

```powershell
# 설치
winget install --id GitHub.cli --silent --accept-package-agreements --accept-source-agreements

# 새 터미널 열기 (PATH 갱신용)
gh auth login
# → GitHub.com → HTTPS → "Login with a web browser" 선택
# 표시되는 8자리 코드를 브라우저에서 입력
```

확인:
```powershell
gh auth status
# ✓ Logged in to github.com account <your-github-username>
```

---

## 4. 환경변수 설정 (User 레벨, 영구)

PowerShell에서 한 번 실행하면 영구 적용 (재부팅 불필요, 새 터미널만 열면 됨):

```powershell
[Environment]::SetEnvironmentVariable("REVIT_2025_PATH", "C:\Program Files\Autodesk\Revit 2025", "User")
[Environment]::SetEnvironmentVariable("REVIT_2023_PATH", "C:\Program Files\Autodesk\Revit 2023", "User")
```

**Revit 설치 경로가 다르면** 위 경로만 본인 환경에 맞게 변경.
환경변수가 비어있으면 csproj가 Nice3point NuGet 패키지로 자동 fallback —
CI에서는 이 fallback을 사용하지만 **로컬에서는 정확한 Revit DLL 매칭을 위해
설정 권장**.

확인 (새 터미널에서):
```powershell
echo $env:REVIT_2025_PATH
```

---

## 5. Node 의존성 설치 + TypeScript 빌드

저장소 루트가 npm workspace 구조라서 루트에서 한 번에:

```powershell
cd <repo-root>
npm install                # workspace 전체 (server, autocad/server, packages/*)
npm run build              # 모든 TS 워크스페이스 빌드
```

빌드 결과 확인:
```powershell
dir server\dist\index.js
dir autocad\server\dist\index.js
```

---

## 6. C# 플러그인 빌드 + Revit Addins 배포

> ⚠️ **Revit이 켜져 있으면 DLL 잠금으로 실패합니다. 반드시 Revit 종료 상태에서.**

```powershell
.\scripts\build-and-deploy.ps1 -RevitVersion 2025
```

이 스크립트가 하는 일:
- TypeScript 서버 빌드
- NuGet 패키지 복원
- C# 솔루션 빌드 (Release, net8.0-windows)
- DLL을 `%APPDATA%\Autodesk\Revit\Addins\2025\`에 복사

배포 결과 확인:
```powershell
dir "$env:APPDATA\Autodesk\Revit\Addins\2025" | findstr /I "RevitMCP revit-mcp"
# RevitMCPPlugin.dll
# RevitMCP.CommandSet.dll
# revit-mcp.addin
```

---

## 7. MCP 클라이언트 설정 (Grok CLI / Orca / Claude)

CAD와 Revit 모두 **이 저장소의 TypeScript stdio 서버**를 가리켜야 합니다.

| 서버 | 진입점 | 플러그인 포트 |
|---|---|---|
| `revit` | `server/dist/index.js` | 8181 (추가 Revit은 8183–8199) |
| `cad` | `autocad/server/dist/index.js` | 8182 |

`CadMCPServer.exe` (`%LOCALAPPDATA%\RevitMCP\Server\cad\`)는 쓰지 마세요.
`tools/list` JSON이 깨져 있어서 Grok/Orca가 65초 타임아웃으로 연결에 실패합니다.

사전 조건: 저장소 루트에서 `npm run build`가 되어 있고, Revit/AutoCAD 플러그인이
로드된 문서가 열려 있어야 합니다.

### 7.1 Grok CLI / Orca (권장)

이 저장소의 `.grok/config.toml`이 Grok 프로젝트 설정입니다. Orca에서 Grok을
띄우면 작업 폴더가 저장소(또는 동일 구조의 worktree) 루트이므로 이 파일이
그대로 적용됩니다.

```toml
[mcp_servers.cad]
command = "node"
args = ["autocad/server/dist/index.js"]
enabled = true
startup_timeout_sec = 45

[mcp_servers.revit]
command = "node"
args = ["server/dist/index.js"]
enabled = true
startup_timeout_sec = 45
```

확인:

```powershell
cd <repo-root>
grok mcp list
grok mcp doctor cad
grok mcp doctor revit
```

`cad (project)` / `revit (project)`로 보이면 성공입니다. 이미 열린 Grok 세션은
`/mcps`에서 `r`로 새로고침하거나 세션을 다시 시작하세요.

### 7.2 Claude Code / Codex (`.mcp.json`)

저장소 루트 `.mcp.json`도 같은 Node 진입점을 등록합니다. Claude Code와 Codex가
프로젝트 MCP를 읽을 때 사용합니다.

### 7.3 Claude Desktop / `~/.claude.json`

Grok은 Claude MCP 설정도 읽어 옵니다. 이름이 겹치면 `.grok/config.toml`이 이깁니다.
Desktop 설정은 `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": ["C:\\Users\\<사용자명>\\<경로>\\revit-mcp-v2\\server\\dist\\index.js"]
    },
    "cad": {
      "command": "node",
      "args": ["C:\\Users\\<사용자명>\\<경로>\\revit-mcp-v2\\autocad\\server\\dist\\index.js"]
    }
  }
}
```

> **JSON에서는 백슬래시를 두 번** (`\\`) 써야 합니다.

```powershell
$cfg = "$env:APPDATA\Claude\claude_desktop_config.json"
Copy-Item $cfg "$cfg.bak.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
notepad $cfg
```

Claude Desktop은 트레이 아이콘에서 Quit 후 재시작하세요.

---

## 8. 검증 (Verification)

### Revit 플러그인 동작 확인
1. Revit 2025 시작 → 아무 프로젝트 열기 (또는 New Project)
   플러그인이 `%LOCALAPPDATA%\RevitMCP\auth-token`을 생성하고 MCP
   서버가 이 토큰을 자동으로 읽습니다.
2. PowerShell에서:
   ```powershell
   $token = Get-Content "$env:LOCALAPPDATA\RevitMCP\auth-token" -Raw
   curl.exe -H "Authorization: Bearer $($token.Trim())" http://127.0.0.1:8181/
   # {"status":"ok","server":"revit-mcp-plugin"}
   ```

### Claude / Grok MCP 연결 확인
1. 클라이언트를 재시작하거나 Grok TUI에서 `/mcps` → `r`
2. 새 대화에서 `revit_ping`, AutoCAD가 열려 있으면 `cad_ping`
3. Revit은 빌드 + 문서명 + 요소 수, AutoCAD는 버전 + 도면명 + 엔티티 수가 오면 성공
4. Grok 도구 이름은 `revit__revit_ping`, `cad__cad_ping` 형태입니다

### Revit 여러 개 실행

업데이트된 플러그인을 설치한 뒤에는 Revit을 평소처럼 여러 개 실행해도
됩니다. 첫 프로세스는 8181, 추가 프로세스는 8183~8199 중 빈 포트를
자동 사용합니다. 8182는 AutoCAD MCP용으로 예약됩니다.

> 자동 포트 선택을 쓰려면 Revit이 상속하는 전역/User 환경변수
> `REVIT_MCP_PORT`가 없어야 합니다. 전역으로 `8181`이 지정되어 있으면 모든
> Revit이 8181만 사용하려 하므로 첫 프로세스만 연결됩니다. 포트를 수동으로
> 고정할 때만 각 Revit 실행 바로가기에 서로 다른 값을 따로 지정하세요.

1. `revit_list_sessions`로 실행 중인 Revit과 활성 문서를 확인합니다.
2. 원하는 항목의 정확한 `session_id`로 `revit_set_target`을 호출합니다.
3. `revit_get_target` 또는 `revit_ping`으로 대상 문서를 재확인합니다.
4. 이후 일반 조회·작성 명령을 실행합니다.

선택 후 해당 Revit에서 문서 탭을 바꾸면 문서 fingerprint가 달라져 명령이
차단됩니다. 새 활성 문서를 의도한 경우 세션 목록을 갱신하고 다시
`revit_set_target`을 호출합니다. 세션 등록 파일은
`%LOCALAPPDATA%\RevitMCP\instances\`에 있으며, 인증정보가 아닌 PID·포트·
활성 문서 식별정보만 담습니다.

### 트러블슈팅
- **WebSocket 응답 없음**: Revit에 프로젝트가 열려 있는지 확인 (플러그인은
  `DocumentOpened`/`DocumentCreated` 이벤트에서 시작)
- **대상 세션 필요 오류**: `revit_list_sessions` 후 정확한 `session_id`로
  `revit_set_target` 호출
- **Revit 하나만 목록에 표시됨**: 전역/User `REVIT_MCP_PORT`가 설정되어
  있는지 확인하고, 자동 선택을 쓸 경우 제거한 뒤 Revit을 모두 재시작
- **문서 fingerprint 불일치**: Revit의 활성 문서 탭이 바뀌었으므로 목록을
  새로 읽고 의도한 세션을 다시 선택
- **HTTP 401**: 플러그인과 MCP 서버가 같은
  `%LOCALAPPDATA%\RevitMCP\auth-token` 또는 같은
  `REVIT_MCP_AUTH_TOKEN`을 사용 중인지 확인
- **MCP 서버 못 찾음**: JSON 경로의 백슬래시가 두 번인지, `npm run build` 후
  `server/dist/index.js`와 `autocad/server/dist/index.js`가 있는지 확인
- **CAD MCP 65초 타임아웃**: `cad`가 `CadMCPServer.exe`를 가리키고 있으면
  `.grok/config.toml` / `.mcp.json` / `~/.claude.json`을 이 저장소의
  `autocad/server/dist/index.js`로 바꾼 뒤 세션을 재시작
- **DLL 로드 실패**: Revit → File → Options → Add-ins 탭에서 "RevitMCPPlugin"
  활성 여부 확인

---

## 9. 일일 작업 흐름 (Daily Workflow)

### 작업 시작
```powershell
git pull
# package.json 변경 있었으면:
npm install
# C# 변경 있었으면 (Revit 종료 후):
.\scripts\build-and-deploy.ps1 -RevitVersion 2025
```

### 작업 종료
```powershell
git status
git add <files>
git commit -m "..."
git push
```

### 머신 전환 시 작업 중인 변경사항 이동
`git stash`는 머신 간 이동이 안 됩니다. 대신:
```powershell
# 떠나는 머신에서
git checkout -b wip/<설명>
git add -A && git commit -m "WIP: <설명>"
git push -u origin wip/<설명>

# 도착한 머신에서
git fetch && git checkout wip/<설명>
git reset --soft HEAD~1   # 커밋 풀기 (변경사항은 유지)
```

> ⚠️ **두 머신에서 동시에 같은 브랜치에 푸시하면 충돌**합니다.
> 항상 한쪽 작업 → push → 다른 쪽 pull 순서를 지키세요.

---

## 10. 머신별 / 공유 항목 정리

### ✅ Git 동기화 (자동)
- 소스 코드, 빌드 스크립트, CI 워크플로우
- `CLAUDE.md` — 에이전트 컨벤션 (Claude Code가 자동 로드)
- GitHub Releases zip — 어느 머신에서든 다운로드 가능

### ⚠️ 머신별 따로 세팅 (이 가이드 대상)
| 항목 | 위치 |
|---|---|
| Git 글로벌 설정 | `~/.gitconfig` |
| GitHub 인증 | Windows 자격증명 관리자 (keyring) |
| `REVIT_*_PATH` 환경변수 | 시스템 User 환경변수 |
| Claude Desktop MCP 설정 | `%APPDATA%\Claude\claude_desktop_config.json` |
| Grok CLI / Orca MCP 설정 | 저장소 `.grok/config.toml` (프로젝트) |
| Claude Code / Codex MCP 설정 | 저장소 `.mcp.json` |
| Revit Addins 폴더 DLL | `%APPDATA%\Autodesk\Revit\Addins\<year>\` |
| node_modules / bin / obj | 각 머신에서 빌드 |
| Revit 라이선스 | 각 머신에서 활성화 |

### ❌ 의도적으로 동기화 안 됨
- Claude Code / Claude Desktop **대화 기록** — 세션은 로컬
- `.claude/settings.local.json` — gitignored
- `scratch/`, `작업자료/`, `*.pbix` 등 로컬 작업 파일
- Claude Code 메모리 (`~/.claude/projects/.../memory/`)

> **대화 컨텍스트 인계 팁**: 작업 종료 시 커밋 메시지나 `CHANGELOG.md`에
> 어디서 멈췄는지 한 줄 남겨두면, 다른 머신에서 Claude Code 새 세션이
> 그 내용을 읽고 자연스럽게 이어갑니다. AI 세션 동기화에 의존하지 마세요.

---

## 11. 주요 경로 레퍼런스

```
저장소:                          C:\Users\<user>\<path>\revit-mcp-v2
플러그인 DLL 배포 위치:          %APPDATA%\Autodesk\Revit\Addins\2025
TS 서버 진입점:                  server\dist\index.js
AutoCAD TS 서버 진입점:          autocad\server\dist\index.js
Grok / Orca 프로젝트 MCP:        .grok\config.toml
Claude Code / Codex 프로젝트 MCP: .mcp.json
Claude Desktop 설정:             %APPDATA%\Claude\claude_desktop_config.json
플러그인 업데이트 캐시:          %LOCALAPPDATA%\RevitMCP\update-cache.json
다운로드된 플러그인 zip:         %LOCALAPPDATA%\RevitMCP\Updates\v<ver>\
응답 오버플로우 spill:           %TEMP%\revit-mcp-spill\
Revit 저널 (디버깅):             %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2025\Journals
```

---

## 12. 한 번에 실행하는 부트스트랩 (참고)

처음 세팅할 때 7~8단계까지 한 번에 실행하고 싶으면 (Revit 종료 상태에서):

```powershell
# 1. Clone & enter
git clone https://github.com/mskim274/revit-mcp-v2.git
cd revit-mcp-v2

# 2. Git config
git config --global user.name "<your-github-username>"
git config --global user.email "<your-email>"
git config --global core.autocrlf true
git config --global credential.helper manager
git config --global init.defaultBranch main

# 3. gh CLI
winget install --id GitHub.cli --silent --accept-package-agreements --accept-source-agreements
# (새 터미널 열고 gh auth login)

# 4. Env vars
[Environment]::SetEnvironmentVariable("REVIT_2025_PATH", "C:\Program Files\Autodesk\Revit 2025", "User")
[Environment]::SetEnvironmentVariable("REVIT_2023_PATH", "C:\Program Files\Autodesk\Revit 2023", "User")

# 5. Node deps + TS build
npm install
npm run build

# 6. C# plugin build + deploy
.\scripts\build-and-deploy.ps1 -RevitVersion 2025

# 7. Claude Desktop config — 수동 편집 필요 (위 7번 참고)
notepad $env:APPDATA\Claude\claude_desktop_config.json
```

이후 Claude Desktop / Revit 재시작하면 끝.
