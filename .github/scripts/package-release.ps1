[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Configuration = 'Release',
    [string]$OutputRoot = '.artifacts/release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repoPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$manifestName = 'RevitMCP.release-manifest.json'
$legalFiles = @(
    [pscustomobject]@{
        Source = Join-Path $repoRoot 'LICENSE'
        Name   = 'RevitMCP.LICENSE.txt'
    },
    [pscustomobject]@{
        Source = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'
        Name   = 'RevitMCP.THIRD-PARTY-NOTICES.md'
    }
)

if (-not $artifactRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must resolve inside the repository: $artifactRoot"
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactRoot | Out-Null

function New-ReleaseArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Product,
        [Parameter(Mandatory = $true)][string]$ArchiveName,
        [Parameter(Mandatory = $true)][object[]]$Files,
        [Parameter(Mandatory = $true)][string[]]$RequiredNames
    )

    $stageRoot = Join-Path $artifactRoot "stage-$Product"
    New-Item -ItemType Directory -Path $stageRoot | Out-Null

    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )

    foreach ($file in $Files) {
        $source = [IO.Path]::GetFullPath($file.Source)
        $name = [IO.Path]::GetFileName($file.Name)

        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Missing release input for ${Product}: $source"
        }
        if ($name -ne $file.Name) {
            throw "Release entries must be flat file names: $($file.Name)"
        }
        if (-not $seen.Add($name)) {
            throw "Duplicate release entry for ${Product}: $name"
        }

        Copy-Item -LiteralPath $source -Destination (Join-Path $stageRoot $name)
    }

    foreach ($required in $RequiredNames) {
        if (-not $seen.Contains($required)) {
            throw "${Product} staging is missing required file: $required"
        }
    }

    $manifestFiles = @(
        Get-ChildItem -LiteralPath $stageRoot -File |
            Sort-Object Name |
            ForEach-Object {
                [ordered]@{
                    name   = $_.Name
                    size   = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )

    $manifest = [ordered]@{
        schema_version = 1
        product        = $Product
        version        = $Version
        source_commit  = if ($env:RELEASE_SOURCE_COMMIT) {
            $env:RELEASE_SOURCE_COMMIT
        }
        elseif ($env:GITHUB_SHA) {
            $env:GITHUB_SHA
        }
        else {
            'local'
        }
        files          = $manifestFiles
    }
    $manifest |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $stageRoot $manifestName) -Encoding utf8

    $archivePath = Join-Path $artifactRoot $ArchiveName
    Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
    return $archivePath
}

function Assert-BinaryFileVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][Version]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing ${DisplayName}: $Path"
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    if ($fileVersion -notmatch '^(\d+)\.(\d+)\.(\d+)(?:\.|$)') {
        throw "${DisplayName} FileVersion is not parseable: $fileVersion"
    }
    $actualVersion = [Version]::new(
        [int]$Matches[1],
        [int]$Matches[2],
        [int]$Matches[3]
    )
    if ($actualVersion -ne $ExpectedVersion) {
        throw (
            "${DisplayName} FileVersion $fileVersion does not match " +
            "release version ${ExpectedVersion}."
        )
    }
}

$expectedVersion = [Version]::Parse($Version)
$pluginOutput = Join-Path $repoRoot "plugin\RevitMCPPlugin\bin\$Configuration\net8.0-windows"
$pluginAssembly = Join-Path $pluginOutput 'RevitMCPPlugin.dll'
$commandSetAssembly = Join-Path $pluginOutput 'RevitMCP.CommandSet.dll'
$contractsAssembly = Join-Path $pluginOutput 'RevitMCP.Contracts.dll'
Assert-BinaryFileVersion `
    -Path $pluginAssembly `
    -DisplayName 'Plugin assembly' `
    -ExpectedVersion $expectedVersion
Assert-BinaryFileVersion `
    -Path $commandSetAssembly `
    -DisplayName 'CommandSet assembly' `
    -ExpectedVersion $expectedVersion
Assert-BinaryFileVersion `
    -Path $contractsAssembly `
    -DisplayName 'Contracts assembly' `
    -ExpectedVersion $expectedVersion

$pluginFiles = @(
    Get-ChildItem -LiteralPath $pluginOutput -File |
        Where-Object {
            ($_.Extension -eq '.dll' -or $_.Name -in @(
                'RevitMCPPlugin.deps.json',
                'RevitMCP.CommandSet.deps.json'
            )) -and
            $_.Name -notmatch '^RevitAPI(UI)?\.dll$'
        } |
        ForEach-Object {
            [pscustomobject]@{ Source = $_.FullName; Name = $_.Name }
        }
)
$pluginFiles += [pscustomobject]@{
    Source = Join-Path $repoRoot 'plugin\revit-mcp.addin'
    Name   = 'revit-mcp.addin'
}
$pluginFiles += $legalFiles

$pluginRequired = @(
    'RevitMCPPlugin.dll',
    'RevitMCP.Contracts.dll',
    'RevitMCP.CommandSet.dll',
    'RevitMCP.CommandSet.deps.json',
    'Revit.Async.dll',
    'RevitMCPPlugin.deps.json',
    'Microsoft.CodeAnalysis.dll',
    'Microsoft.CodeAnalysis.CSharp.dll',
    'Microsoft.CodeAnalysis.Scripting.dll',
    'Microsoft.CodeAnalysis.CSharp.Scripting.dll',
    'revit-mcp.addin',
    'RevitMCP.LICENSE.txt',
    'RevitMCP.THIRD-PARTY-NOTICES.md'
)

$pluginZip = New-ReleaseArchive `
    -Product 'revit-plugin-2025' `
    -ArchiveName "RevitMCPPlugin-$Version-Revit2025.zip" `
    -Files $pluginFiles `
    -RequiredNames $pluginRequired

$updaterOutput = Join-Path $repoRoot 'updater\publish'
$updaterAssembly = Join-Path $updaterOutput 'RevitMCPUpdater.exe'
Assert-BinaryFileVersion `
    -Path $updaterAssembly `
    -DisplayName 'Updater executable' `
    -ExpectedVersion $expectedVersion

$updaterFiles = @(
    Get-ChildItem -LiteralPath $updaterOutput -File |
        Where-Object {
            $_.Extension -in @('.exe', '.dll', '.json') -and
            $_.Extension -ne '.pdb'
        } |
        ForEach-Object {
            [pscustomobject]@{ Source = $_.FullName; Name = $_.Name }
        }
)
$updaterFiles += $legalFiles

$updaterZip = New-ReleaseArchive `
    -Product 'revit-mcp-updater' `
    -ArchiveName "RevitMCPUpdater-$Version.zip" `
    -Files $updaterFiles `
    -RequiredNames @(
        'RevitMCPUpdater.exe',
        'RevitMCP.LICENSE.txt',
        'RevitMCP.THIRD-PARTY-NOTICES.md'
    )

$checksumPath = Join-Path $artifactRoot 'SHA256SUMS.txt'
@($pluginZip, $updaterZip) |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($_))"
    } |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Get-ChildItem -LiteralPath $artifactRoot -Directory | Remove-Item -Recurse -Force
Write-Host "Release artifacts created in $artifactRoot"
