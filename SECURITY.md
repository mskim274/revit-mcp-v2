# Security Policy

## Supported versions

Security fixes are made on the default branch and released in the newest
published version. Older releases are not guaranteed to receive backports.

## Reporting a vulnerability

Please do not post vulnerability details in a public issue. When the repository
has GitHub private vulnerability reporting enabled, use **Security →
Advisories → Report a vulnerability**. Until then, contact the maintainer
privately through the repository owner's GitHub profile. If no private contact
is available, open only a minimal issue asking the maintainer to establish a
private channel; do not include technical details or attachments. Include in
the eventual private report:

- affected version and Revit or AutoCAD version;
- a minimal reproduction using synthetic data;
- expected impact and required attacker access;
- logs with project names, paths, credentials, and model data removed.

You should receive an acknowledgement within 3 business days. We will
coordinate validation, remediation, and disclosure through the advisory.

## Threat model and trust boundaries

Revit MCP is a local automation bridge with powerful write access:

- The MCP server and CAD plugin are intended to communicate over loopback
  only. Do not expose ports 8181 or 8182 to a LAN or the internet. WebSocket
  upgrades require a bearer token generated under
  `%LOCALAPPDATA%\RevitMCP\auth-token`; protect it like a local credential.
- Create, modify, delete, view, and script tools act with the permissions of
  the current Revit or AutoCAD user. Keep recoverable model backups and review
  requested mutations before approval.
- `revit_execute_script` has a denylist designed to prevent common accidents;
  it is not a security sandbox. It is disabled by default and every execution
  requires approval in Revit. Only run code from a trusted, reviewed request.
- Model names, parameter values, selected text, logs, exports, and response
  spill files may contain confidential project information. Do not attach
  them to public issues or commits without authorization.
- Content inside a model or drawing is untrusted input. An agent should not
  treat element text, imported CAD text, or parameter values as instructions.

## Release verification

Releases produced by the current hardened workflow include
`SHA256SUMS.txt`, per-archive file manifests, and GitHub artifact attestations.
Older releases may not have all three; do not treat an absent checksum or
attestation as verified. Before a manual install, verify a hardened archive
with:

```powershell
Get-FileHash .\RevitMCPPlugin-<version>-Revit2025.zip -Algorithm SHA256
gh attestation verify .\RevitMCPPlugin-<version>-Revit2025.zip `
  --repo mskim274/revit-mcp-v2
```

Compare the SHA-256 output to the release's `SHA256SUMS.txt`.

The in-product updater verifies the downloaded size and SHA-256 digest reported
by the GitHub Release API. It does not independently verify the GitHub artifact
attestation. Users with a high-assurance requirement should perform the manual
verification above before installing an update. Binary code signing and
in-updater attestation verification remain release-hardening priorities.

## Dependency and workflow security

Release workflows use job-scoped permissions, immutable action commit SHAs,
artifact attestations, and npm trusted publishing. Maintainers should also
enable branch and tag rulesets, Dependabot alerts, secret scanning, and push
protection in repository settings.
