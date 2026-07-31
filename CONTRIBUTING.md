# Contributing

Thank you for improving Revit MCP. Small, reviewable pull requests with a
clear safety contract are easiest to validate.

## Before you start

- Use Node.js 20 or newer; release automation uses Node.js 24 and npm 11.
- Use the .NET 8 SDK. Revit 2023/2024 compatibility is compiled as `net48`;
  Revit 2025 uses `net8.0-windows`. Do not claim support for a later Revit
  year until it has its own API build, release asset, and CI validation.
- Do not commit client models, drawings, schedules, element IDs, screenshots,
  exports, internal paths, credentials, or company-specific handoff notes.
  Tests and examples must use synthetic data that is safe to publish.

## Build and test

From the repository root:

```powershell
npm ci --workspaces --include-workspace-root
npm test
npm run audit:prod

.\.github\scripts\restore-dotnet-with-audit.ps1 `
  -Project plugin\RevitMCPPlugin\RevitMCPPlugin.csproj `
  -TargetFramework net8.0-windows
dotnet build plugin\RevitMCPPlugin\RevitMCPPlugin.csproj `
  -c Release -f net8.0-windows --no-restore

.\.github\scripts\restore-dotnet-with-audit.ps1 `
  -Project updater\Updater.csproj
dotnet build updater\Updater.csproj -c Release --no-restore
.\scripts\test-updater.ps1

.\.github\scripts\restore-dotnet-with-audit.ps1 `
  -Project autocad\AutoCADMCP.sln
dotnet build autocad\AutoCADMCP.sln -c Release --no-restore
```

When changing shared or compatibility code, also build `net48`. The GitHub CI
workflow builds both Revit target frameworks and the AutoCAD plugin, then runs
updater smoke tests, npm tarball install tests, dependency audit, and release
ZIP validation.

## Command design

New commands must follow the architecture and AI-first contract documented in
`CLAUDE.md`:

- keep Revit API execution in `commandset/`;
- route all Revit API calls through Revit.Async;
- wrap mutations in a descriptive transaction;
- make side effects retry-safe with an idempotency key;
- return actionable suggestions on failure and verification after writes;
- prefer bounded, paginated, batch-composable responses.

## Pull requests

Before requesting review:

- keep unrelated local changes out of the commit;
- add or update tests and public documentation;
- run the commands above from a clean checkout;
- inspect `npm pack --dry-run --json` output;
- update `THIRD-PARTY-NOTICES.md` when a redistributed binary dependency or
  its version changes;
- confirm no confidential or generated project data is included;
- describe safety, compatibility, and rollback implications.

Use a draft pull request while behavior or public API shape is still changing.
Security issues should follow `SECURITY.md`, not the normal issue tracker.

## Releases

Only maintainers cut releases. The release tag must already exist, use strict
`vMAJOR.MINOR.PATCH`, and point to a commit reachable from `main`. The release
workflow builds and verifies all workspaces before publishing the shared core
and Revit server to npm in dependency order. The AutoCAD server remains a
private, CI-tested workspace and is not published. The workflow first uploads
the verified binaries to a draft GitHub Release, attests them, publishes core
and Revit server to npm, verifies both npm versions and the server's exact core
dependency, and only then publishes the draft.

For a manual rerun from an existing tag, use the GitHub CLI so both the
workflow ref and the input are pinned to that tag:

```bash
gh workflow run release.yml --ref vX.Y.Z -f tag=vX.Y.Z
```

This keeps npm provenance, GitHub attestations, the archive manifest, and the
validated source commit aligned. The web UI's branch selector is not a safe
substitute for a tag-pinned rerun.

The npm packages `@kimminsub/mcp-cad-core` and
`@kimminsub/revit-mcp` must each configure `release.yml` as their trusted
GitHub Actions publisher, with the `release` environment. Do not reintroduce a
long-lived npm publish token after trusted publishing is enabled.

Trusted publishers are configured from an existing package's npm settings. If
`@kimminsub/mcp-cad-core` has not been created yet, a maintainer must first
publish a reviewed bootstrap version. Prefer a one-time GitHub-hosted workflow
using a short-lived granular npm token and `npm publish --provenance`; remove
that workflow and secret immediately afterward. A local interactive 2FA
bootstrap is also possible, but it will not carry GitHub Actions provenance.
Then configure the trusted publisher and revoke any temporary publish
credential. The automated workflow can publish later versions only after both
package trust relationships exist.

### First-release repository checklist

Before the first automated release:

- create a GitHub environment named exactly `release` and require a maintainer
  reviewer for deployments;
- bootstrap `@kimminsub/mcp-cad-core` on npm as described above;
- configure npm trusted publishers for both public packages with repository
  `mskim274/revit-mcp-v2`, workflow `release.yml`, and environment `release`;
- protect `main` with required CI checks and protect `v*` tags from movement or
  deletion;
- enable Dependabot alerts, secret scanning, push protection, and private
  vulnerability reporting in the repository security settings.

## License

By contributing, you agree that your contribution is licensed under the MIT
License in `LICENSE` and that you have the right to submit it.
