# CommandSet hot reload

Revit MCP uses a long-lived host and a reloadable CommandSet on Revit 2025+.
This keeps WebSocket, session routing, `Revit.Async`, and startup lifecycle code
stable while ordinary Revit command implementations can be rebuilt and
activated without restarting Revit.

## Why this architecture

- Revit keeps an `IExternalApplication` instance for the whole Revit session,
  so replacing the startup host in place is not a safe general-purpose update
  mechanism: [Autodesk External Application lifetime](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_External_Applications_html.html).
- .NET 8 supports isolated plugin dependencies through
  `AssemblyDependencyResolver` and a custom `AssemblyLoadContext`:
  [Microsoft plugin sample](https://learn.microsoft.com/en-us/samples/dotnet/samples/appwithplugin-demo/).
- Unloading is cooperative. It only completes after all external references,
  threads, event handlers, and stack frames release the collectible context:
  [Microsoft unloadability guidance](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability).
- RevitAddInManager adopted collectible contexts for Revit 2025 in
  [PR #54](https://github.com/chuongmep/RevitAddInManager/pull/54), then fixed
  leaked command references, undisposed streams, and premature GC checks in
  [PR #70](https://github.com/chuongmep/RevitAddInManager/pull/70).
- pyRevit's current loader similarly separates bootstrap, runtime, and engines;
  its test notes still identify loader/engine changes that require a restart:
  [pyRevit loader testing #2995](https://github.com/pyrevitlabs/pyRevit/issues/2995).

Revit 2026 adds manifest-level add-in dependency isolation. It helps avoid
dependency conflicts but does not replace this generation swap mechanism:
[Autodesk add-in dependency isolation](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Add_In_Integration/Revit_API_Revit_API_Developers_Guide_Introduction_Add_In_Integration_Add_in_Dependency_Isolation_html.html).

## Runtime boundaries

| Component | Lifetime | Update behavior |
|---|---|---|
| `RevitMCPPlugin.dll` | Revit process | Restart required |
| `RevitMCP.Contracts.dll` | Revit process | Restart required |
| `RevitMCP.CommandSet.dll` | One collectible generation | Hot reload |
| TypeScript MCP server | MCP client process | Restart MCP server/client only |

The contracts assembly contains only `IRevitCommand`, `CommandResult`, the
selection snapshot, and ElementId compatibility helpers. Reloadable code must
not reference `RevitMCPPlugin.dll`.

## Development workflow

1. Edit files under `commandset/` only.
2. Build an immutable staged generation:

   ```powershell
   .\scripts\stage-commandset.ps1 -RevitVersion 2025
   ```

3. Call `revit_get_commandset_status` and review the generation/hash.
4. Call `revit_reload_commandset`, optionally passing the exact generation.
5. Verify the returned `verification` block and active hash.

Successful activation persists by default in
`%LOCALAPPDATA%\RevitMCP\CommandSets\active-2025.json`. On the next Revit
start, an incompatible or corrupt persisted generation is rejected and the
installed baseline CommandSet is loaded.

## Safety properties

- Generation directories are immutable and atomically renamed into place.
- Only the fixed local staging root is loadable; arbitrary assembly paths are
  not accepted over MCP.
- Reparse-point directories/files, malformed manifests, wrong Revit targets,
  contract hash mismatches, and CommandSet hash mismatches are rejected.
- The candidate is loaded and every command instantiated before the active
  generation is swapped.
- Duplicate/reserved command names are rejected.
- Command removal is rejected unless `allow_command_removal=true` is explicit.
- A failed candidate leaves the previous generation active.
- Source DLLs are loaded from streams with delete sharing, avoiding build-file
  locks even while a generation is active.
- Retired contexts are held only by weak references. At eight pending contexts,
  the host performs a final two-pass collection; if all eight are still alive,
  further reloads stop and request a Revit restart. This prevents an unbounded
  leak from static events, threads, or third-party caches without forcing GC on
  every normal reload.

Hot reload is intentionally limited to Revit 2025+/.NET 8. Revit 2023/2024
uses the load-once .NET Framework path.

## Updating the stable host while Revit is running

The stable host still activates only when a Revit process starts, but it can be
installed without closing every running Revit process:

```powershell
.\scripts\deploy-host-side-by-side.ps1 -RevitVersion 2025
```

This writes the host into a new immutable version directory and atomically
updates the `.addin` manifest. Existing Revit processes continue with their
already-loaded host; each process adopts the new host the next time that
individual process restarts. This is useful when ports 8181 and 8183 are both
active and only one process should be restarted at a time.
