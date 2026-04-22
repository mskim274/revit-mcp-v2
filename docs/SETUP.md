# Setup Guide — Continuing Work on a New Machine

You want to pick up revit-mcp-v2 development on another computer. Everything
that matters is already in git, so the flow is: install prerequisites →
clone → build → configure Claude Desktop.

---

## 1. Prerequisites

Install these on the new machine (skip anything already there):

| Tool | Why | Install |
|---|---|---|
| **Autodesk Revit 2025** | The host application | License from Autodesk |
| **.NET 8 SDK** | Build C# plugin + updater | https://dotnet.microsoft.com/download |
| **Node.js 18+** | Build TypeScript MCP server | https://nodejs.org |
| **Git** | Clone the repo | https://git-scm.com |
| **Claude Desktop** | MCP client that talks to the server | https://claude.ai/download |
| **VS Code / Cursor / Claude Code** | Editor (any is fine) | Optional |

Windows 10 / 11 only — the plugin uses WPF.

---

## 2. Clone + build

```powershell
# Clone the repo to any folder you like
git clone https://github.com/mskim274/revit-mcp-v2.git
cd revit-mcp-v2

# Build the TypeScript server
cd server
npm install
npm run build
cd ..

# Build + deploy the Revit plugin (Revit must be CLOSED)
$env:REVIT_2025_PATH = "C:\Program Files\Autodesk\Revit 2025"
.\scripts\build-and-deploy.ps1 -RevitVersion 2025
```

After the deploy script finishes, the plugin DLLs are at
`%APPDATA%\Autodesk\Revit\Addins\2025\`.

---

## 3. Configure Claude Desktop

Open `%APPDATA%\Claude\claude_desktop_config.json` and add the `revit`
server block. Merge it with any other MCP servers you already have:

```json
{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": [
        "C:\\Users\\YOU\\revit-mcp-v2\\server\\dist\\index.js"
      ],
      "env": {
        "REVIT_MCP_PORT": "8181"
      }
    }
  }
}
```

Replace the path with wherever you cloned the repo. Use double
backslashes in JSON.

**Alternative (once Phase P2 ships)**: install via npm and drop the
absolute path entirely:

```json
{
  "mcpServers": {
    "revit": { "command": "npx", "args": ["-y", "@kimminsub/revit-mcp"] }
  }
}
```

Restart Claude Desktop so it picks up the new config.

---

## 4. Verify

1. Start Revit 2025 and open any project.
2. In Claude Desktop, ask: *"Call revit_ping."*
3. You should see the project name, Revit build number, and element
   count come back.

If the ping fails:

- Make sure Revit has a project open — the WebSocket starts on
  `DocumentOpened` / `DocumentCreated`, not at plugin-load time.
- `curl http://127.0.0.1:8181/` from PowerShell should return
  `{"status":"ok","server":"revit-mcp-plugin"}`.
- Check `scripts/test-ws.js` for direct WebSocket probes that bypass
  Claude's MCP client entirely.

---

## 5. What transfers vs. what doesn't

### ✅ Fully portable (in git)

- Source code, build scripts, CI workflows.
- `CLAUDE.md` — all the agent-level conventions. When you open Claude
  Code in this folder on a new machine, it reads `CLAUDE.md`
  automatically and picks up project context.
- Release zips on GitHub — the same v0.3.0 you're running here is
  available for download on the new machine.

### ⚠️ Needs reconfiguration per machine

- `claude_desktop_config.json` — absolute paths differ per machine.
- `REVIT_2025_PATH` environment variable — only needed for local
  C# builds; CI uses the Nice3point NuGet fallback.
- Revit license — must be activated on the new machine.

### ❌ Does NOT transfer (by design)

- Claude Code / Claude Desktop **conversation history** — sessions
  are local. To continue a long-running thread, either (a) keep
  working on the original machine, or (b) summarize the relevant
  context into a prompt you paste into the new session. `CLAUDE.md`
  intentionally captures the durable project knowledge so chat
  history loss doesn't block progress.
- Per-user `.claude/settings.local.json` — gitignored on purpose.
- Any untracked scratch files (`scratch/`, `*.pbix`, etc.).

---

## 6. Typical "two-machine" workflows

### a) Continue on a second laptop tonight

```powershell
# On the original machine — push any uncommitted work first
git push

# On the second laptop
cd path\to\revit-mcp-v2
git pull
npm --prefix server run build
# (C# plugin only needs a rebuild if you changed C# since last deploy)
```

If you also want Claude Code's session history to mirror over,
just... don't expect that to work. Instead, end each session on the
first machine with a clean commit + a note in `CLAUDE.md` or
`CHANGELOG.md` describing where you stopped. Pick up on the new
machine by reading those. This is the sanest way to hand off work
between machines regardless of the AI tooling.

### b) You bought a new dev machine and migrating

Do the full bootstrap once (above). Then:

1. Install whatever editor you prefer.
2. `git clone` the repo.
3. Build + deploy.
4. Point Claude Desktop at the new path.
5. The old machine is now redundant — delete the local checkout when
   you're sure everything works on the new one.

### c) You're testing end-to-end auto-update on a new machine

Great test case. The new machine is a true "first-time user":

1. Install prereqs.
2. Download `RevitMCPPlugin-<latest>-Revit2025.zip` from
   [Releases](https://github.com/mskim274/revit-mcp-v2/releases/latest).
3. Extract the 4 files to `%APPDATA%\Autodesk\Revit\Addins\2025\`.
4. Start Revit + any project. Plugin loads, checks GitHub, sees
   itself as latest, no dialog.
5. (Optional) Cut a v0.X.Y bump on the main machine. The new
   machine's Revit startup will detect it and show the auto-install
   dialog on next launch.

---

## 7. Quick reference — paths

```
Repository:                     C:\Users\YOU\path\to\revit-mcp-v2
Plugin DLLs (deployed):         %APPDATA%\Autodesk\Revit\Addins\2025
TS server entry (Claude uses):  server\dist\index.js
Plugin update cache:            %LOCALAPPDATA%\RevitMCP\update-cache.json
Downloaded plugin zips:         %LOCALAPPDATA%\RevitMCP\Updates\v<ver>\
Response overflow spill:        %TEMP%\revit-mcp-spill\
Claude Desktop config:          %APPDATA%\Claude\claude_desktop_config.json
Revit journal (debugging):      %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2025\Journals
```
