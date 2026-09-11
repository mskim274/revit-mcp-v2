[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('prompt', 'auto')]
    [string]$Mode
)

$ErrorActionPreference = 'Stop'
$settingName = 'REVIT_MCP_SCRIPT_APPROVAL'
$Mode = $Mode.ToLowerInvariant()
$previous = [Environment]::GetEnvironmentVariable($settingName, 'User')
[Environment]::SetEnvironmentVariable($settingName, $Mode, 'User')
$actual = [Environment]::GetEnvironmentVariable($settingName, 'User')
if ($actual -cne $Mode) { throw 'Script approval preference verification failed.' }
[ordered]@{
    setting = $settingName
    scope = 'Windows current user; all supported Revit sessions for this account'
    previous = $previous
    actual = $actual
    verified = $true
    applies = 'Next script on updated CommandSet; no Revit restart required'
    script_enable_gate_unchanged = $true
} | ConvertTo-Json
