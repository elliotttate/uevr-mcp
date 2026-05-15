<#
.SYNOPSIS
    Build a UEVR-MCP release zip that an end user can unpack and run.

.DESCRIPTION
    Produces dist/UevrMcp-vX.Y.Z.zip containing:
      bin/
        UevrMcpServer.exe   (self-contained .NET 9 single-file publish)
        uevr_mcp.dll        (the UEVR plugin)
        dumper7.dll         (Dumper-7 fallback dumper)
      tools/                (PowerShell wrappers: quick-dump, dumper-mode, etc.)
      examples/
        .mcp.json           (template MCP client registration)
      install.ps1           (first-run installer: pulls UEVRBackend.dll, installs plugin,
                             optionally writes MCP client config)
      README.md             (release-side quickstart)

    UEVRBackend.dll is NOT bundled. install.ps1 downloads it from praydog/UEVR
    on the user's machine. This keeps the zip free of forked / large binaries
    and means users always pick up the latest UEVR release.

.PARAMETER Version
    Version string for the zip name (e.g. "1.0.0"). Defaults to "dev".

.PARAMETER OutDir
    Where to drop the zip + staging tree. Defaults to <repo>/dist.

.PARAMETER NoZip
    Stage the layout but skip the zip step (useful for local testing).

.EXAMPLE
    .\tools\package-release.ps1 -Version 1.0.0

.EXAMPLE
    .\tools\package-release.ps1 -Version dev -NoZip
#>
[CmdletBinding()]
param(
    [string]$Version = 'dev',
    [string]$OutDir,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }

$StageName = "UevrMcp-v$Version"
$StageDir  = Join-Path $OutDir $StageName
$ZipPath   = Join-Path $OutDir "$StageName.zip"

Write-Host "=== UEVR-MCP release packager ===" -ForegroundColor Cyan
Write-Host "Version : $Version"
Write-Host "Stage   : $StageDir"
Write-Host "Zip     : $ZipPath"
Write-Host ""

# Clean stage
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $StageDir 'bin')      -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $StageDir 'tools')    -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $StageDir 'examples') -Force | Out-Null

# ── 1. Build the plugin ──────────────────────────────────────────────
Write-Host "--- Building plugin ---" -ForegroundColor Yellow
$PluginDir = Join-Path $RepoRoot 'plugin'
$BuildDir  = Join-Path $PluginDir 'build'
if (-not (Test-Path (Join-Path $BuildDir 'CMakeCache.txt'))) {
    Write-Host "  configuring..."
    & cmake -S $PluginDir -B $BuildDir | Out-Host
}
Write-Host "  building uevr_mcp + dumper7 (Release)..."
& cmake --build $BuildDir --config Release --target uevr_mcp dumper7 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

$PluginDll  = Join-Path $BuildDir 'Release\uevr_mcp.dll'
$Dumper7Dll = Join-Path $BuildDir 'Release\dumper7.dll'
if (-not (Test-Path $PluginDll))  { throw "uevr_mcp.dll missing at $PluginDll"  }
if (-not (Test-Path $Dumper7Dll)) { throw "dumper7.dll missing at $Dumper7Dll" }
Copy-Item $PluginDll  (Join-Path $StageDir 'bin\uevr_mcp.dll')
Copy-Item $Dumper7Dll (Join-Path $StageDir 'bin\dumper7.dll')
Write-Host "  OK plugin DLLs copied." -ForegroundColor Green

# ── 2. Publish the MCP server (self-contained single-file) ───────────
Write-Host ""
Write-Host "--- Publishing MCP server (self-contained) ---" -ForegroundColor Yellow
$ServerProj = Join-Path $RepoRoot 'mcp-server\UevrMcpServer.csproj'
$PublishDir = Join-Path $RepoRoot 'mcp-server\bin\Publish'
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

& dotnet publish $ServerProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:InvariantGlobalization=true `
    -p:DebugType=embedded `
    -o $PublishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$PublishedExe = Join-Path $PublishDir 'UevrMcpServer.exe'
if (-not (Test-Path $PublishedExe)) { throw "Published exe missing at $PublishedExe" }
Copy-Item $PublishedExe (Join-Path $StageDir 'bin\UevrMcpServer.exe')
# Copy the .pdb if present so crash stack traces are useful.
$PublishedPdb = Join-Path $PublishDir 'UevrMcpServer.pdb'
if (Test-Path $PublishedPdb) { Copy-Item $PublishedPdb (Join-Path $StageDir 'bin\UevrMcpServer.pdb') }
$exeSize = [int]((Get-Item (Join-Path $StageDir 'bin\UevrMcpServer.exe')).Length / 1MB)
Write-Host "  OK UevrMcpServer.exe ($exeSize MB)" -ForegroundColor Green

# ── 3. Stage tools and examples ──────────────────────────────────────
Write-Host ""
Write-Host "--- Staging tools/ + examples/ ---" -ForegroundColor Yellow
$toolNames = @(
    'quick-dump.ps1'
    'enable-dumper-mode.ps1'
    'disable-dumper-mode.ps1'
    'enable-wer-dumps.ps1'
    'stop-game.ps1'
    'cli-cheatsheet.md'
    'dumper-mode-recipe.md'
    'README.md'
)
foreach ($n in $toolNames) {
    $src = Join-Path $PSScriptRoot $n
    if (Test-Path $src) { Copy-Item $src (Join-Path $StageDir "tools\$n") }
}

# AGENT.md — release users get the navigation guide too (uevr_help reads it).
$agentMd = Join-Path $RepoRoot 'AGENT.md'
if (Test-Path $agentMd) { Copy-Item $agentMd (Join-Path $StageDir 'AGENT.md') }

# .mcp.json template — points at the bundled exe rather than `dotnet run`.
$mcpTemplate = @'
{
  "mcpServers": {
    "uevr": {
      "type": "stdio",
      "command": "REPLACE_WITH_FULL_PATH_TO\\bin\\UevrMcpServer.exe"
    }
  }
}
'@
$mcpTemplate | Set-Content -Encoding UTF8 (Join-Path $StageDir 'examples\.mcp.json')

# ── 4. Drop the release-side install.ps1 + README ───────────────────
Write-Host ""
Write-Host "--- Writing install.ps1 + README.md ---" -ForegroundColor Yellow
$ReleaseSrc = Join-Path $RepoRoot 'release'
Copy-Item (Join-Path $ReleaseSrc 'install.ps1') (Join-Path $StageDir 'install.ps1')
Copy-Item (Join-Path $ReleaseSrc 'README.md')   (Join-Path $StageDir 'README.md')

# Drop a VERSION sentinel so install.ps1 / users can see what they have.
"$Version`n" | Set-Content -Encoding UTF8 (Join-Path $StageDir 'VERSION')

# ── 5. Zip it ────────────────────────────────────────────────────────
if (-not $NoZip) {
    Write-Host ""
    Write-Host "--- Zipping ---" -ForegroundColor Yellow
    if (Test-Path $ZipPath) { Remove-Item $ZipPath }
    Compress-Archive -Path "$StageDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal
    $zipMb = [int]((Get-Item $ZipPath).Length / 1MB)
    Write-Host "  OK $ZipPath ($zipMb MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Release ready ===" -ForegroundColor Cyan
Write-Host "  Stage: $StageDir"
if (-not $NoZip) { Write-Host "  Zip:   $ZipPath" }
Write-Host ""
Write-Host "Smoke test:"
Write-Host "  cd $StageDir"
Write-Host "  .\bin\UevrMcpServer.exe wait-plugin 1000   # should print JSON"
