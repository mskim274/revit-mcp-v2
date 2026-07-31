[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PluginZip,
    [Parameter(Mandatory = $true)][string]$UpdaterZip,
    [Parameter(Mandatory = $true)][string]$Checksums,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedVersion,
    [string]$ExpectedSourceCommit
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$manifestName = 'RevitMCP.release-manifest.json'
$legalFiles = [ordered]@{
    'RevitMCP.LICENSE.txt' = Join-Path $repoRoot 'LICENSE'
    'RevitMCP.THIRD-PARTY-NOTICES.md' = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'
}
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
}
catch {
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
}

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][IO.Stream]$Stream)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Test-VersionedArchiveEntry {
    param(
        [Parameter(Mandatory = $true)]
        [IO.Compression.ZipArchiveEntry]$Entry,
        [Parameter(Mandatory = $true)][Version]$ExpectedVersion
    )

    $temporaryPath = Join-Path (
        [IO.Path]::GetTempPath()
    ) ("revit-mcp-version-check-{0}.exe" -f [Guid]::NewGuid())
    try {
        $source = $Entry.Open()
        try {
            $destination = [IO.FileStream]::new(
                $temporaryPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None
            )
            try {
                $source.CopyTo($destination)
            }
            finally {
                $destination.Dispose()
            }
        }
        finally {
            $source.Dispose()
        }

        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
            $temporaryPath
        ).FileVersion
        if ($fileVersion -notmatch '^(\d+)\.(\d+)\.(\d+)(?:\.|$)') {
            throw "Archive entry FileVersion is not parseable: $($Entry.Name) ($fileVersion)"
        }
        $actualVersion = [Version]::new(
            [int]$Matches[1],
            [int]$Matches[2],
            [int]$Matches[3]
        )
        if ($actualVersion -ne $ExpectedVersion) {
            throw (
                "Archive entry FileVersion $fileVersion does not match " +
                "release version ${ExpectedVersion}: $($Entry.Name)"
            )
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Test-ReleaseArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$ExpectedProduct,
        [Parameter(Mandatory = $true)][string[]]$RequiredNames,
        [string[]]$ForbiddenNames = @(),
        [string[]]$VersionedBinaryNames = @()
    )

    $resolved = (Resolve-Path -LiteralPath $ArchivePath).Path
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entries = @($archive.Entries)
        $entryNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )

        foreach ($entry in $entries) {
            if ([string]::IsNullOrEmpty($entry.Name) -or $entry.FullName -ne $entry.Name) {
                throw "Archive contains a directory or non-flat path: $($entry.FullName)"
            }
            if (-not $entryNames.Add($entry.Name)) {
                throw "Archive contains duplicate entry: $($entry.Name)"
            }
        }

        foreach ($required in $RequiredNames) {
            if (-not $entryNames.Contains($required)) {
                throw "Archive is missing required entry: $required"
            }
        }
        foreach ($forbidden in $ForbiddenNames) {
            if ($entryNames.Contains($forbidden)) {
                throw "Archive contains forbidden entry: $forbidden"
            }
        }

        foreach ($legalName in $legalFiles.Keys) {
            $entry = $entries | Where-Object Name -eq $legalName
            if ($null -eq $entry) {
                throw "Archive is missing required legal file: $legalName"
            }

            $stream = $entry.Open()
            try {
                $actualLegalHash = Get-StreamSha256 -Stream $stream
            }
            finally {
                $stream.Dispose()
            }
            $expectedLegalHash = (
                Get-FileHash -LiteralPath $legalFiles[$legalName] -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            if ($actualLegalHash -ne $expectedLegalHash) {
                throw "Archive legal file differs from repository root: $legalName"
            }
        }

        $manifestEntry = $entries | Where-Object Name -eq $manifestName
        if ($null -eq $manifestEntry) {
            throw "Archive is missing $manifestName"
        }

        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        if ([int]$manifest.schema_version -ne 1) {
            throw "Unsupported manifest schema in ${ArchivePath}: $($manifest.schema_version)"
        }
        if ([string]$manifest.product -ne $ExpectedProduct) {
            throw "Manifest product mismatch in ${ArchivePath}: $($manifest.product)"
        }
        if ([string]$manifest.version -ne $ExpectedVersion) {
            throw "Manifest version mismatch in ${ArchivePath}: $($manifest.version)"
        }
        if (
            $ExpectedSourceCommit -and
            [string]$manifest.source_commit -ne $ExpectedSourceCommit
        ) {
            throw "Manifest source commit mismatch in ${ArchivePath}: $($manifest.source_commit)"
        }

        $manifestNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase
        )
        foreach ($item in $manifest.files) {
            if (-not $manifestNames.Add([string]$item.name)) {
                throw "Manifest contains duplicate file: $($item.name)"
            }
            $entry = $entries | Where-Object Name -eq $item.name
            if ($null -eq $entry) {
                throw "Manifest references missing file: $($item.name)"
            }
            if ([long]$item.size -ne $entry.Length) {
                throw "Manifest size mismatch for $($item.name)"
            }

            $stream = $entry.Open()
            try {
                $actualHash = Get-StreamSha256 -Stream $stream
            }
            finally {
                $stream.Dispose()
            }
            if ($actualHash -ne ([string]$item.sha256).ToLowerInvariant()) {
                throw "Manifest hash mismatch for $($item.name)"
            }
        }

        foreach ($entry in $entries | Where-Object Name -ne $manifestName) {
            if (-not $manifestNames.Contains($entry.Name)) {
                throw "Archive entry is missing from manifest: $($entry.Name)"
            }
        }

        foreach ($versionedBinaryName in $VersionedBinaryNames) {
            $versionedEntry = $entries |
                Where-Object Name -eq $versionedBinaryName
            if ($null -eq $versionedEntry) {
                throw "Archive is missing versioned binary: $versionedBinaryName"
            }
            Test-VersionedArchiveEntry `
                -Entry $versionedEntry `
                -ExpectedVersion ([Version]::Parse($ExpectedVersion))
        }
    }
    finally {
        $archive.Dispose()
    }
}

$checksumMap = @{}
foreach ($line in Get-Content -LiteralPath $Checksums) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  ([^\\/]+)$') {
        throw "Malformed checksum line: $line"
    }
    if ($checksumMap.ContainsKey($Matches[2])) {
        throw "Duplicate checksum entry: $($Matches[2])"
    }
    $checksumMap[$Matches[2]] = $Matches[1].ToLowerInvariant()
}

$expectedChecksumNames = @(
    [IO.Path]::GetFileName($PluginZip),
    [IO.Path]::GetFileName($UpdaterZip)
)
if ($checksumMap.Count -ne $expectedChecksumNames.Count) {
    throw "SHA256SUMS.txt must contain exactly $($expectedChecksumNames.Count) release archives."
}

foreach ($archivePath in @($PluginZip, $UpdaterZip)) {
    $name = [IO.Path]::GetFileName($archivePath)
    if (-not $checksumMap.ContainsKey($name)) {
        throw "SHA256SUMS.txt does not contain $name"
    }
    $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $checksumMap[$name]) {
        throw "SHA256SUMS mismatch for $name"
    }
}

Test-ReleaseArchive `
    -ArchivePath $PluginZip `
    -ExpectedProduct 'revit-plugin-2025' `
    -RequiredNames @(
        'RevitMCPPlugin.dll',
        'RevitMCP.CommandSet.dll',
        'Revit.Async.dll',
        'RevitMCPPlugin.deps.json',
        'Microsoft.CodeAnalysis.dll',
        'Microsoft.CodeAnalysis.CSharp.dll',
        'Microsoft.CodeAnalysis.Scripting.dll',
        'Microsoft.CodeAnalysis.CSharp.Scripting.dll',
        'revit-mcp.addin',
        'RevitMCP.LICENSE.txt',
        'RevitMCP.THIRD-PARTY-NOTICES.md',
        $manifestName
    ) `
    -ForbiddenNames @(
        'RevitAPI.dll',
        'RevitAPIUI.dll',
        'LICENSE',
        'THIRD-PARTY-NOTICES.md'
    ) `
    -VersionedBinaryNames @(
        'RevitMCPPlugin.dll',
        'RevitMCP.CommandSet.dll'
    )

Test-ReleaseArchive `
    -ArchivePath $UpdaterZip `
    -ExpectedProduct 'revit-mcp-updater' `
    -RequiredNames @(
        'RevitMCPUpdater.exe',
        'RevitMCP.LICENSE.txt',
        'RevitMCP.THIRD-PARTY-NOTICES.md',
        $manifestName
    ) `
    -ForbiddenNames @('LICENSE', 'THIRD-PARTY-NOTICES.md') `
    -VersionedBinaryNames @('RevitMCPUpdater.exe')

Write-Host 'Release package validation passed.'
