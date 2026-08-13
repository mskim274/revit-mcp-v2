[CmdletBinding()]
param(
    [ValidateSet('2025')]
    [string]$RevitVersion = '2025',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$framework = 'net8.0-windows'
$revitInstall = "C:\Program Files\Autodesk\Revit $RevitVersion"
$addinRoot = [IO.Path]::GetFullPath((Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"))
$hostsRoot = [IO.Path]::GetFullPath((Join-Path $addinRoot 'RevitMCP\hosts'))
$hostsPrefix = $hostsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $addinRoot 'revit-mcp.addin'

if (-not $NoBuild) {
    if (Test-Path -LiteralPath (Join-Path $revitInstall 'RevitAPI.dll')) {
        $env:REVIT_2025_PATH = $revitInstall
    }
    else {
        $env:REVIT_2025_PATH = ''
    }

    $pluginProject = Join-Path $repoRoot 'plugin\RevitMCPPlugin\RevitMCPPlugin.csproj'
    dotnet restore $pluginProject -p:TargetFramework=$framework
    if ($LASTEXITCODE -ne 0) { throw 'Revit MCP host restore failed.' }
    dotnet build $pluginProject `
        -c $Configuration `
        -f $framework `
        --no-restore `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Revit MCP host build failed.' }
}

$outputDirectory = Join-Path $repoRoot "plugin\RevitMCPPlugin\bin\$Configuration\$framework"
$hostAssembly = Join-Path $outputDirectory 'RevitMCPPlugin.dll'
if (-not (Test-Path -LiteralPath $hostAssembly -PathType Leaf)) {
    throw "Revit MCP host output is missing: $hostAssembly"
}

$hostHash = (Get-FileHash -LiteralPath $hostAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostAssembly).FileVersion
$safeVersion = ($fileVersion -replace '[^A-Za-z0-9._-]', '_')
$generation = '{0}-{1}-{2}' -f `
    $safeVersion, `
    ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')), `
    $hostHash.Substring(0, 12)

New-Item -ItemType Directory -Path $hostsRoot -Force | Out-Null
$hostsInfo = Get-Item -LiteralPath $hostsRoot -Force
if (($hostsInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Host deployment root must not be a reparse point: $hostsRoot"
}

$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $hostsRoot ".tmp-$generation"))
$finalDirectory = [IO.Path]::GetFullPath((Join-Path $hostsRoot $generation))
foreach ($path in @($temporaryDirectory, $finalDirectory)) {
    if (-not $path.StartsWith($hostsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe host generation path: $path"
    }
}
if ((Test-Path -LiteralPath $temporaryDirectory) -or
    (Test-Path -LiteralPath $finalDirectory)) {
    throw "Host generation already exists: $generation"
}

$requiredNames = @(
    'RevitMCPPlugin.dll',
    'RevitMCPPlugin.deps.json',
    'RevitMCP.Contracts.dll',
    'RevitMCP.CommandSet.dll',
    'RevitMCP.CommandSet.deps.json',
    'Revit.Async.dll',
    'Microsoft.CodeAnalysis.dll',
    'Microsoft.CodeAnalysis.CSharp.dll',
    'Microsoft.CodeAnalysis.Scripting.dll',
    'Microsoft.CodeAnalysis.CSharp.Scripting.dll'
)

try {
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    Get-ChildItem -LiteralPath $outputDirectory -File |
        Where-Object {
            ($_.Extension -in @('.dll', '.pdb') -or
             $_.Name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase)) -and
            $_.Name -notmatch '^RevitAPI(UI)?\.dll$'
        } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $temporaryDirectory
        }

    foreach ($requiredName in $requiredNames) {
        $requiredPath = Join-Path $temporaryDirectory $requiredName
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Host generation is missing required file: $requiredName"
        }
    }

    $hostFiles = @(
        Get-ChildItem -LiteralPath $temporaryDirectory -File |
            Sort-Object Name |
            ForEach-Object {
                [ordered]@{
                    name = $_.Name
                    size = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    [ordered]@{
        schema_version = 1
        generation = $generation
        revit_year = $RevitVersion
        created_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
        host_sha256 = $hostHash
        files = $hostFiles
    } |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $temporaryDirectory 'host-manifest.json') -Encoding utf8

    Move-Item -LiteralPath $temporaryDirectory -Destination $finalDirectory

    New-Item -ItemType Directory -Path $addinRoot -Force | Out-Null
    $relativeAssembly = "RevitMCP\hosts\$generation\RevitMCPPlugin.dll"
    $manifestXml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Revit MCP Plugin</Name>
    <Assembly>$relativeAssembly</Assembly>
    <FullClassName>RevitMCP.Plugin.Application</FullClassName>
    <AddInId>DFFD689E-FEF6-4B62-8D7C-DA6C3AB4EFD4</AddInId>
    <VendorId>andlab</VendorId>
    <VendorDescription>andlab - Revit MCP</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    $temporaryManifest = Join-Path $addinRoot ".revit-mcp.$([Guid]::NewGuid().ToString('N')).tmp"
    # File.Replace creates the backup itself. Give every deployment a unique
    # name so a second deployment cannot collide with an earlier backup.
    $backupManifest = Join-Path $addinRoot (
        'revit-mcp.addin.previous.{0}.{1}' -f `
            ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')), `
            ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
    try {
        [IO.File]::WriteAllText(
            $temporaryManifest,
            $manifestXml,
            [Text.UTF8Encoding]::new($false)
        )
        if (Test-Path -LiteralPath $manifestPath) {
            [IO.File]::Replace(
                $temporaryManifest,
                $manifestPath,
                $backupManifest,
                $true
            )
        }
        else {
            [IO.File]::Move($temporaryManifest, $manifestPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryManifest) {
            Remove-Item -LiteralPath $temporaryManifest -Force
        }
    }

    $deployedHash = (Get-FileHash -LiteralPath (Join-Path $finalDirectory 'RevitMCPPlugin.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($deployedHash -ne $hostHash) {
        throw 'Deployed host hash verification failed.'
    }

    [ordered]@{
        deployed = $true
        generation = $generation
        host_directory = $finalDirectory
        manifest = $manifestPath
        previous_manifest_backup = if (Test-Path -LiteralPath $backupManifest) {
            $backupManifest
        } else {
            ''
        }
        assembly = $relativeAssembly
        host_sha256 = $hostHash
        activation = 'The next Revit process start loads this host. Running Revit processes continue safely on their already-loaded generation.'
    } | ConvertTo-Json -Depth 4
}
catch {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
        if ($resolvedTemporary.StartsWith($hostsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    }
    throw
}
