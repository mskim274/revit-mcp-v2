# Revit MCP V2 — Build and Deploy Script
# Usage: .\scripts\build-and-deploy.ps1 [-RevitVersion 2023|2025|all]

param(
    [ValidateSet("2023", "2025", "all")]
    [string]$RevitVersion = "all"
)

$ErrorActionPreference = "Stop"
$SrcDir = Split-Path -Parent $PSScriptRoot

# ─── Revit paths ───
$RevitPaths = @{
    "2023" = @{
        Framework = "net48"
        AddinFolder = "$env:APPDATA\Autodesk\Revit\Addins\2023"
        RevitPath = "C:\Program Files\Autodesk\Revit 2023"
    }
    "2025" = @{
        Framework = "net8.0-windows"
        AddinFolder = "$env:APPDATA\Autodesk\Revit\Addins\2025"
        RevitPath = "C:\Program Files\Autodesk\Revit 2025"
    }
}

function Build-Version {
    param([string]$Version)

    $config = $RevitPaths[$Version]
    Write-Host "`n=== Building for Revit $Version ($($config.Framework)) ===" -ForegroundColor Cyan

    # Set Revit API path
    $env:REVIT_2023_PATH = $RevitPaths["2023"].RevitPath
    $env:REVIT_2025_PATH = $RevitPaths["2025"].RevitPath

    # Build C# projects
    dotnet build "$SrcDir\RevitMCP.sln" `
        -c Release `
        -f $config.Framework `
        --no-restore

    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED for Revit $Version" -ForegroundColor Red
        exit 1
    }

    # Deploy to Revit Add-in folder
    $outputDir = "$SrcDir\plugin\RevitMCPPlugin\bin\Release\$($config.Framework)"
    $addinDir = $config.AddinFolder

    if (-not (Test-Path $addinDir)) {
        New-Item -ItemType Directory -Path $addinDir -Force | Out-Null
    }

    # Copy DLLs
    Copy-Item "$outputDir\*.dll" $addinDir -Force
    Copy-Item "$SrcDir\plugin\revit-mcp.addin" $addinDir -Force

    Write-Host "Deployed to: $addinDir" -ForegroundColor Green
}

# ─── Build TypeScript Server ───
Write-Host "`n=== Building TypeScript MCP Server ===" -ForegroundColor Cyan
Push-Location "$SrcDir\server"
npm install
npm run build
Pop-Location
Write-Host "TypeScript build complete" -ForegroundColor Green

# ─── Restore NuGet packages ───
Write-Host "`n=== Restoring NuGet packages ===" -ForegroundColor Cyan
dotnet restore "$SrcDir\RevitMCP.sln"

# ─── Build C# ───
if ($RevitVersion -eq "all") {
    Build-Version "2023"
    Build-Version "2025"
} else {
    Build-Version $RevitVersion
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host "TypeScript server: $SrcDir\server\dist\index.js"
Write-Host "Configure Claude Desktop with:"
Write-Host @"

{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": ["$($SrcDir -replace '\\', '/')/server/dist/index.js"],
      "env": {
        "REVIT_MCP_PORT": "8181"
      }
    }
  }
}
"@
