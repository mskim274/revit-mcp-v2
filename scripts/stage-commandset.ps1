[CmdletBinding()]
param(
    [ValidateSet('2025')]
    [string]$RevitVersion = '2025',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$framework = 'net8.0-windows'
$revitInstall = "C:\Program Files\Autodesk\Revit $RevitVersion"
$stageRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RevitMCP\CommandSets\staged'))
$stagePrefix = $stageRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

if (Test-Path -LiteralPath $stageRoot) {
    $stageInfo = Get-Item -LiteralPath $stageRoot -Force
    if (($stageInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "CommandSet staging root must not be a reparse point: $stageRoot"
    }
}
else {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
}

$sourceCommit = (& git -C $repoRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    $sourceCommit = 'local'
}
else {
    $sourceCommit = $sourceCommit.Trim().ToLowerInvariant()
}
$shortCommit = if ($sourceCommit -eq 'local') {
    'local'
}
else {
    $sourceCommit.Substring(0, [Math]::Min(8, $sourceCommit.Length))
}
$dirty = -not [string]::IsNullOrWhiteSpace(
    (& git -C $repoRoot status --porcelain --untracked-files=no 2>$null) -join "`n"
)
$dirtyLabel = if ($dirty) { '-dirty' } else { '' }
$generation = '{0}-{1}{2}-{3}' -f `
    ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')), `
    $shortCommit, `
    $dirtyLabel, `
    ([Guid]::NewGuid().ToString('N').Substring(0, 6))

$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $stageRoot ".tmp-$generation"))
$finalDirectory = [IO.Path]::GetFullPath((Join-Path $stageRoot $generation))
foreach ($path in @($temporaryDirectory, $finalDirectory)) {
    if (-not $path.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe CommandSet generation path: $path"
    }
}
if ((Test-Path -LiteralPath $temporaryDirectory) -or
    (Test-Path -LiteralPath $finalDirectory)) {
    throw "CommandSet generation already exists: $generation"
}

try {
    if (Test-Path -LiteralPath (Join-Path $revitInstall 'RevitAPI.dll')) {
        $env:REVIT_2025_PATH = $revitInstall
    }
    else {
        $env:REVIT_2025_PATH = ''
    }

    $commandSetProject = Join-Path $repoRoot 'commandset\CommandSet.csproj'
    if (-not $NoRestore) {
        dotnet restore $commandSetProject -p:TargetFramework=$framework
        if ($LASTEXITCODE -ne 0) {
            throw 'CommandSet restore failed.'
        }
    }

    dotnet build $commandSetProject `
        -c $Configuration `
        -f $framework `
        --no-restore `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'CommandSet build failed.'
    }

    $commandSetOutput = Join-Path $repoRoot "commandset\bin\$Configuration\$framework"
    $contractsOutput = Join-Path $repoRoot "contracts\bin\$Configuration\$framework"
    $commandSetAssembly = Join-Path $commandSetOutput 'RevitMCP.CommandSet.dll'
    $commandSetDeps = Join-Path $commandSetOutput 'RevitMCP.CommandSet.deps.json'
    $contractsAssembly = Join-Path $contractsOutput 'RevitMCP.Contracts.dll'
    foreach ($required in @($commandSetAssembly, $commandSetDeps, $contractsAssembly)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required CommandSet build output is missing: $required"
        }
    }

    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    Get-ChildItem -LiteralPath $commandSetOutput -File |
        Where-Object {
            $_.Name -notmatch '^RevitAPI(UI)?\.dll$' -and
            $_.Name -notmatch '^RevitMCP\.Contracts\.'
        } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $temporaryDirectory
        }

    $stagedAssembly = Join-Path $temporaryDirectory 'RevitMCP.CommandSet.dll'
    $stagedDeps = Join-Path $temporaryDirectory 'RevitMCP.CommandSet.deps.json'
    if (-not (Test-Path -LiteralPath $stagedAssembly -PathType Leaf) -or
        -not (Test-Path -LiteralPath $stagedDeps -PathType Leaf)) {
        throw 'Staged generation is missing its assembly or dependency manifest.'
    }

    $manifest = [ordered]@{
        schema_version     = 1
        generation        = $generation
        created_at_utc    = [DateTimeOffset]::UtcNow.ToString('O')
        target_framework  = $framework
        revit_year        = $RevitVersion
        commandset_assembly = 'RevitMCP.CommandSet.dll'
        commandset_sha256 = (Get-FileHash -LiteralPath $stagedAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
        contracts_sha256  = (Get-FileHash -LiteralPath $contractsAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
        source_commit     = $sourceCommit
        source_dirty      = $dirty
    }
    $manifest |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $temporaryDirectory 'commandset-manifest.json') -Encoding utf8

    Move-Item -LiteralPath $temporaryDirectory -Destination $finalDirectory

    [ordered]@{
        staged             = $true
        generation         = $generation
        directory          = $finalDirectory
        commandset_sha256  = $manifest.commandset_sha256
        contracts_sha256   = $manifest.contracts_sha256
        source_commit      = $sourceCommit
        source_dirty       = $dirty
        next_step          = "Call revit_reload_commandset with generation '$generation'."
    } | ConvertTo-Json -Depth 4
}
catch {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
        if ($resolvedTemporary.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    }
    throw
}
