# Revit script approval preference

Revit 2025+ scripts still require `REVIT_MCP_ENABLE_SCRIPT=1` in the Revit
process. Approval is a separate, opt-in **Windows current-user** preference:

```powershell
# Explicitly authorize trusted query AND modify scripts without the dialog.
.\scripts\set-script-approval.ps1 -Mode auto

# Restore the per-script dialog immediately for subsequent requests.
.\scripts\set-script-approval.ps1 -Mode prompt
```

The persistent User environment variable `REVIT_MCP_SCRIPT_APPROVAL` accepts
exactly `auto` or `prompt`. Unset/empty defaults to `prompt`. An invalid value
fails with an actionable error; it never enables automatic approval. Only the
Windows User value is read, on every request. Process/Machine values and RPC
parameters do not override it. This avoids stale inherited values preventing
revocation after a restart. The setting persists across restarts and applies
to all updated Revit sessions under the same Windows account, not just one
document or agent. Reverting affects subsequent requests, not scripts already
authorized and executing.

Use `auto` only after the user explicitly authorizes it and trusts all clients
using the local authenticated bridge. The agent must still review the request,
scope, document identity and script, and summarize modifications before sending
them. This preference does not authorize unrelated model changes, weaken bearer
authentication, alter coordination reservations, allow forbidden code, remove
deadline checks, or change transaction rollback/commit verification. Query mode
is not a security sandbox. Automatic approval reduces the per-request human
checkpoint; the denylist is only best-effort, not an isolation boundary.

Successful script responses include `approval.policy`, `approval.source`, and
`approval.dialog_result`. Automatic runs report `auto`,
`windows_user_environment`, and `not_shown`; they do not claim a human clicked
Yes. Prompt refusals distinguish No from cancellation/closing when Revit
provides different TaskDialog results. Scripts cannot use the obvious
`SetEnvironmentVariable` API to change this setting themselves.

## Deployment and checks

Only CommandSet C# changes are needed for runtime behavior. Stage against the
running host's exact contracts and hot reload on Revit 2025+, then issue a harmless
query and check `approval` in its response. The TypeScript change updates tool
descriptions; already-connected clients receive those descriptions on refresh.
Do not deploy host/contracts changes merely to activate this preference.

```powershell
dotnet run --project tests/ScriptApprovalSmoke -c Release
```

Tests cover default prompts, explicit automatic approval without invoking the
dialog, invalid settings, revocation, result metadata, and No/cancel distinction.
