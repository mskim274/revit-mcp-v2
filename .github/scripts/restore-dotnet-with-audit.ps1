[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Project,

    [string]$TargetFramework,

    [string]$RuntimeIdentifier,

    [string]$MinVerVersionOverride
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repoPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$projectPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $Project))

if (-not $projectPath.StartsWith(
    $repoPrefix,
    [StringComparison]::OrdinalIgnoreCase
)) {
    throw "Project must resolve inside the repository: $projectPath"
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project or solution not found: $projectPath"
}

$restoreArguments = @(
    'restore',
    $projectPath,
    '--force-evaluate',
    '-p:NuGetAudit=true',
    '-p:NuGetAuditMode=all',
    '-p:NuGetAuditLevel=low',
    '-p:WarningsAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904'
)

if ($TargetFramework) {
    $restoreArguments += "-p:TargetFramework=$TargetFramework"
}
if ($RuntimeIdentifier) {
    $restoreArguments += @('-r', $RuntimeIdentifier)
}
if ($MinVerVersionOverride) {
    $restoreArguments += "-p:MinVerVersionOverride=$MinVerVersionOverride"
}

& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) {
    throw "Audited dotnet restore failed with exit code $LASTEXITCODE."
}
