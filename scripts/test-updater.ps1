[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$updater = Join-Path $repoRoot 'updater\bin\Release\net8.0-windows\RevitMCPUpdater.exe'
if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) {
    throw "Updater is not built. Run: dotnet build updater/Updater.csproj -c Release"
}

$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempPrefix (
    'revit-mcp-updater-test-' + [guid]::NewGuid().ToString('N')
)
$target = Join-Path $tempRoot 'target'
$script:Passed = 0
$script:Failed = 0
$script:LastLog = ''
New-Item -ItemType Directory -Path $tempRoot | Out-Null

function New-TestZip {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][Collections.IDictionary]$Entries
    )

    $archive = [IO.Compression.ZipFile]::Open(
        $Path,
        [IO.Compression.ZipArchiveMode]::Create
    )
    try {
        foreach ($entryName in $Entries.Keys) {
            $entry = $archive.CreateEntry([string]$entryName)
            $writer = [IO.StreamWriter]::new(
                $entry.Open(),
                [Text.UTF8Encoding]::new($false)
            )
            try {
                $writer.Write([string]$Entries[$entryName])
            }
            finally {
                $writer.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-UpdaterExit {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$Expected,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & $updater @Arguments 2>&1
    $actual = $LASTEXITCODE
    $script:LastLog = ($output | Out-String)
    if ($actual -eq $Expected) {
        Write-Host "[ OK ] $Name (exit $actual)"
        $script:Passed++
    }
    else {
        Write-Host "[FAIL] $Name - wanted exit $Expected, got $actual"
        Write-Host $script:LastLog
        $script:Failed++
    }
}

function Assert-FileContent {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    if (
        (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Content -LiteralPath $Path -Raw).Contains($Expected)
    ) {
        Write-Host "[ OK ] file contains expected text: $Path"
        $script:Passed++
    }
    else {
        Write-Host "[FAIL] file is missing or incorrect: $Path"
        $script:Failed++
    }
}

function Assert-PathAbsent {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "[ OK ] path was not created: $Path"
        $script:Passed++
    }
    else {
        Write-Host "[FAIL] unexpected path exists: $Path"
        $script:Failed++
    }
}

try {
    $initialZip = Join-Path $tempRoot 'initial.zip'
    New-TestZip -Path $initialZip -Entries ([ordered]@{
        'marker.txt' = 'hello-before-replacement'
    })
    Test-UpdaterExit `
        -Name 'initial install' `
        -Expected 0 `
        -Arguments @(
            '--zip', $initialZip,
            '--no-wait',
            '--addins-dir', $target
        )
    Assert-FileContent `
        -Path (Join-Path $target 'marker.txt') `
        -Expected 'hello-before-replacement'

    $traversalZip = Join-Path $tempRoot 'traversal.zip'
    New-TestZip -Path $traversalZip -Entries ([ordered]@{
        '../escaped.txt' = 'must-not-escape'
        'marker.txt'     = 'must-not-replace'
    })
    Test-UpdaterExit `
        -Name 'Zip Slip rejection' `
        -Expected 1 `
        -Arguments @(
            '--zip', $traversalZip,
            '--no-wait',
            '--addins-dir', $target
        )
    if ($script:LastLog -like '*unsafe path segment*') {
        Write-Host '[ OK ] rejection explains the unsafe path'
        $script:Passed++
    }
    else {
        Write-Host '[FAIL] rejection log does not explain the unsafe path'
        $script:Failed++
    }
    Assert-PathAbsent -Path (Join-Path $tempRoot 'escaped.txt')
    Assert-FileContent `
        -Path (Join-Path $target 'marker.txt') `
        -Expected 'hello-before-replacement'

    $replacementZip = Join-Path $tempRoot 'replacement.zip'
    New-TestZip -Path $replacementZip -Entries ([ordered]@{
        'marker.txt' = 'hello-after-replacement'
    })
    Test-UpdaterExit `
        -Name 'normal replacement' `
        -Expected 0 `
        -Arguments @(
            '--zip', $replacementZip,
            '--no-wait',
            '--addins-dir', $target
        )
    Assert-FileContent `
        -Path (Join-Path $target 'marker.txt') `
        -Expected 'hello-after-replacement'
    Assert-FileContent `
        -Path (Join-Path $target 'marker.txt.bak') `
        -Expected 'hello-before-replacement'

    # Product-scoped release manifests must drive stale-file pruning without
    # claiming a generic filename in the shared Revit Addins/<year> folder.
    $manifestName = 'RevitMCP.release-manifest.json'
    $manifestTarget = Join-Path $tempRoot 'manifest-target'
    New-Item -ItemType Directory -Path $manifestTarget | Out-Null
    $utf8NoBom = [Text.UTF8Encoding]::new($false)

    $oldFile = Join-Path $manifestTarget 'old.dll'
    [IO.File]::WriteAllText($oldFile, 'old-binary', $utf8NoBom)
    $oldHash = (
        Get-FileHash -LiteralPath $oldFile -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $oldManifest = [ordered]@{
        schema_version = 1
        product = 'RevitMCP'
        files = @(
            [ordered]@{
                name = 'old.dll'
                size = (Get-Item -LiteralPath $oldFile).Length
                sha256 = $oldHash
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress
    [IO.File]::WriteAllText(
        (Join-Path $manifestTarget $manifestName),
        $oldManifest,
        $utf8NoBom
    )

    $newContent = 'new-binary'
    $newBytes = $utf8NoBom.GetBytes($newContent)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $newHash = (
            [BitConverter]::ToString($sha256.ComputeHash($newBytes))
        ).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    $newManifest = [ordered]@{
        schema_version = 1
        product = 'RevitMCP'
        files = @(
            [ordered]@{
                name = 'new.dll'
                size = $newBytes.Length
                sha256 = $newHash
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress
    $manifestZip = Join-Path $tempRoot 'manifest-upgrade.zip'
    New-TestZip -Path $manifestZip -Entries ([ordered]@{
        'new.dll' = $newContent
        $manifestName = $newManifest
    })
    Test-UpdaterExit `
        -Name 'product-scoped manifest stale prune' `
        -Expected 0 `
        -Arguments @(
            '--zip', $manifestZip,
            '--no-wait',
            '--addins-dir', $manifestTarget
        )
    Assert-PathAbsent -Path $oldFile
    Assert-FileContent `
        -Path ($oldFile + '.bak') `
        -Expected 'old-binary'
    Assert-FileContent `
        -Path (Join-Path $manifestTarget 'new.dll') `
        -Expected $newContent
    Assert-FileContent `
        -Path (Join-Path $manifestTarget $manifestName) `
        -Expected '"name":"new.dll"'
    Assert-PathAbsent `
        -Path (Join-Path $manifestTarget 'release-manifest.json')

    # AutoCAD uses its own manifest name even when --addins-dir overrides the
    # normal ApplicationPlugins target (as these isolated tests do).
    $autoCadManifestName = 'AutoCADMCP.release-manifest.json'
    $autoCadManifestTarget = Join-Path $tempRoot 'autocad-manifest-target'
    New-Item -ItemType Directory -Path $autoCadManifestTarget | Out-Null

    $oldAutoCadFile = Join-Path $autoCadManifestTarget 'old-autocad.dll'
    [IO.File]::WriteAllText(
        $oldAutoCadFile,
        'old-autocad-binary',
        $utf8NoBom
    )
    $oldAutoCadHash = (
        Get-FileHash -LiteralPath $oldAutoCadFile -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $oldAutoCadManifest = [ordered]@{
        schema_version = 1
        product = 'AutoCADMCP'
        files = @(
            [ordered]@{
                name = 'old-autocad.dll'
                size = (Get-Item -LiteralPath $oldAutoCadFile).Length
                sha256 = $oldAutoCadHash
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress
    [IO.File]::WriteAllText(
        (Join-Path $autoCadManifestTarget $autoCadManifestName),
        $oldAutoCadManifest,
        $utf8NoBom
    )

    $newAutoCadContent = 'new-autocad-binary'
    $newAutoCadBytes = $utf8NoBom.GetBytes($newAutoCadContent)
    $autoCadSha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $newAutoCadHash = (
            [BitConverter]::ToString(
                $autoCadSha256.ComputeHash($newAutoCadBytes)
            )
        ).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $autoCadSha256.Dispose()
    }
    $newAutoCadManifest = [ordered]@{
        schema_version = 1
        product = 'AutoCADMCP'
        files = @(
            [ordered]@{
                name = 'new-autocad.dll'
                size = $newAutoCadBytes.Length
                sha256 = $newAutoCadHash
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress
    $autoCadManifestZip = Join-Path $tempRoot 'autocad-manifest-upgrade.zip'
    New-TestZip -Path $autoCadManifestZip -Entries ([ordered]@{
        'new-autocad.dll' = $newAutoCadContent
        $autoCadManifestName = $newAutoCadManifest
    })
    Test-UpdaterExit `
        -Name 'AutoCAD product-scoped manifest stale prune' `
        -Expected 0 `
        -Arguments @(
            '--zip', $autoCadManifestZip,
            '--product', 'autocad',
            '--no-wait',
            '--addins-dir', $autoCadManifestTarget
        )
    Assert-PathAbsent -Path $oldAutoCadFile
    Assert-FileContent `
        -Path ($oldAutoCadFile + '.bak') `
        -Expected 'old-autocad-binary'
    Assert-FileContent `
        -Path (Join-Path $autoCadManifestTarget 'new-autocad.dll') `
        -Expected $newAutoCadContent
    Assert-FileContent `
        -Path (Join-Path $autoCadManifestTarget $autoCadManifestName) `
        -Expected '"name":"new-autocad.dll"'
    Assert-PathAbsent `
        -Path (
            Join-Path $autoCadManifestTarget 'RevitMCP.release-manifest.json'
        )

    # A malformed manifest with A+A entries must not pass merely because its
    # item count equals the staged A+B file count. Otherwise B can be mistaken
    # for a stale file and removed after its new version was installed.
    $duplicateTarget = Join-Path $tempRoot 'duplicate-manifest-target'
    New-Item -ItemType Directory -Path $duplicateTarget | Out-Null
    $duplicateOldA = Join-Path $duplicateTarget 'duplicate-a.dll'
    $duplicateOldB = Join-Path $duplicateTarget 'duplicate-b.dll'
    [IO.File]::WriteAllText($duplicateOldA, 'old-a', $utf8NoBom)
    [IO.File]::WriteAllText($duplicateOldB, 'old-b', $utf8NoBom)
    $duplicateOldManifest = [ordered]@{
        schema_version = 1
        product = 'RevitMCP'
        files = @(
            [ordered]@{
                name = 'duplicate-a.dll'
                size = (Get-Item -LiteralPath $duplicateOldA).Length
                sha256 = (
                    Get-FileHash -LiteralPath $duplicateOldA -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            },
            [ordered]@{
                name = 'duplicate-b.dll'
                size = (Get-Item -LiteralPath $duplicateOldB).Length
                sha256 = (
                    Get-FileHash -LiteralPath $duplicateOldB -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress
    [IO.File]::WriteAllText(
        (Join-Path $duplicateTarget $manifestName),
        $duplicateOldManifest,
        $utf8NoBom
    )

    $duplicateNewA = 'new-a'
    $duplicateNewB = 'new-b'
    $duplicateHash = [Security.Cryptography.SHA256]::Create()
    try {
        $duplicateNewABytes = $utf8NoBom.GetBytes($duplicateNewA)
        $duplicateNewAHash = (
            [BitConverter]::ToString(
                $duplicateHash.ComputeHash($duplicateNewABytes)
            )
        ).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $duplicateHash.Dispose()
    }
    $duplicateNewManifest = [ordered]@{
        schema_version = 1
        product = 'RevitMCP'
        files = @(
            [ordered]@{
                name = 'duplicate-a.dll'
                size = $duplicateNewABytes.Length
                sha256 = $duplicateNewAHash
            },
            [ordered]@{
                name = 'DUPLICATE-A.DLL'
                size = $duplicateNewABytes.Length
                sha256 = $duplicateNewAHash
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress
    $duplicateManifestZip = Join-Path $tempRoot 'duplicate-manifest.zip'
    New-TestZip -Path $duplicateManifestZip -Entries ([ordered]@{
        'duplicate-a.dll' = $duplicateNewA
        'duplicate-b.dll' = $duplicateNewB
        $manifestName = $duplicateNewManifest
    })
    Test-UpdaterExit `
        -Name 'case-insensitive duplicate manifest paths cannot prune' `
        -Expected 0 `
        -Arguments @(
            '--zip', $duplicateManifestZip,
            '--no-wait',
            '--addins-dir', $duplicateTarget
        )
    Assert-FileContent `
        -Path $duplicateOldB `
        -Expected $duplicateNewB
    if ($script:LastLog -like '*does not match the staged archive*') {
        Write-Host '[ OK ] duplicate manifest disabled stale pruning'
        $script:Passed++
    }
    else {
        Write-Host '[FAIL] duplicate manifest was not rejected for pruning'
        $script:Failed++
    }
}
finally {
    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    if (
        $resolvedTempRoot.StartsWith(
            $tempPrefix,
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        [IO.Path]::GetFileName($resolvedTempRoot).StartsWith(
            'revit-mcp-updater-test-',
            [StringComparison]::Ordinal
        )
    ) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
    else {
        throw "Refusing to clean unexpected test directory: $resolvedTempRoot"
    }
}

Write-Host
Write-Host "passed: $script:Passed  failed: $script:Failed"
if ($script:Failed -ne 0) {
    exit 1
}
