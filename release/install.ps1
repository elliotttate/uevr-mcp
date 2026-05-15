<#
.SYNOPSIS
    First-run installer for the UEVR-MCP release zip.

.DESCRIPTION
    Run this once after unzipping. It will:
      1. Verify the bundled binaries exist (UevrMcpServer.exe, uevr_mcp.dll, dumper7.dll)
      2. Download UEVRBackend.dll from the latest praydog/UEVR GitHub release
         (or use -BackendDll to point at one you already have)
      3. If you pass -GameExe: install uevr_mcp.dll into
         %APPDATA%\UnrealVRMod\<GameName>\plugins\
      4. Optionally write an MCP-client registration file (Claude Code, Cursor)
         that points at the bundled UevrMcpServer.exe

    Safe to re-run. Skips steps that are already done.

.PARAMETER GameExe
    Path to the game .exe you want to dump (e.g. ...\RoboQuest-Win64-Shipping.exe).
    If set, the plugin is copied into the right UEVR per-game plugins folder
    and a dumper_mode sentinel is written so UEVR skips render hooks.

.PARAMETER BackendDll
    Path to an existing UEVRBackend.dll. If omitted, the latest one is
    downloaded from https://github.com/praydog/UEVR/releases/latest.

.PARAMETER UevrZipUrl
    Override the UEVR release zip URL. By default uses the GitHub API to find
    the latest release's UEVR.zip asset.

.PARAMETER McpConfig
    'none' (default), 'claude-code-user', 'claude-code-here', 'cursor-user'.
      none               - don't touch any MCP client config
      claude-code-user   - write %USERPROFILE%\.claude.json so Claude Code picks it up globally
      claude-code-here   - write .mcp.json in the current dir for a workspace-scoped MCP server
      cursor-user        - write %USERPROFILE%\.cursor\mcp.json

.PARAMETER SkipBackendDownload
    Don't download UEVRBackend.dll. Useful if you already have UEVR installed
    and will point at it via the UEVR_BACKEND_DLL environment variable.

.EXAMPLE
    # Just unpack the runtime so the bundled exe can launch
    .\install.ps1

.EXAMPLE
    # Set up for a specific game in one go
    .\install.ps1 -GameExe "E:\SteamLibrary\steamapps\common\RoboQuest\RoboQuest\Binaries\Win64\RoboQuest-Win64-Shipping.exe"

.EXAMPLE
    # Full first-time setup with Claude Code registration
    .\install.ps1 -GameExe "...\MyGame-Win64-Shipping.exe" -McpConfig claude-code-user
#>
[CmdletBinding()]
param(
    [string]$GameExe,
    [string]$BackendDll,
    [string]$UevrZipUrl,
    [ValidateSet('none', 'claude-code-user', 'claude-code-here', 'cursor-user')]
    [string]$McpConfig = 'none',
    [switch]$SkipBackendDownload
)

$ErrorActionPreference = 'Stop'
$Root    = $PSScriptRoot
$BinDir  = Join-Path $Root 'bin'
$Exe     = Join-Path $BinDir 'UevrMcpServer.exe'
$Plugin  = Join-Path $BinDir 'uevr_mcp.dll'
$Dumper7 = Join-Path $BinDir 'dumper7.dll'
$Backend = Join-Path $BinDir 'UEVRBackend.dll'

Write-Host "=== UEVR-MCP install ===" -ForegroundColor Cyan
$verFile = Join-Path $Root 'VERSION'
if (Test-Path $verFile) { Write-Host "Version: $(Get-Content $verFile -Raw)".Trim() }
Write-Host "Root   : $Root"
Write-Host ""

# ── 1. Verify bundled bits ───────────────────────────────────────────
Write-Host "--- Verifying bundle ---" -ForegroundColor Yellow
$missing = @()
foreach ($p in @($Exe, $Plugin, $Dumper7)) {
    if (Test-Path $p) {
        Write-Host "  OK  $(Split-Path -Leaf $p)" -ForegroundColor Green
    } else {
        Write-Host "  ??  $(Split-Path -Leaf $p) missing" -ForegroundColor Red
        $missing += $p
    }
}
if ($missing.Count -gt 0) {
    throw "Bundle is incomplete. Re-download UevrMcp-vX.Y.Z.zip."
}

# ── 2. UEVRBackend.dll ───────────────────────────────────────────────
Write-Host ""
Write-Host "--- UEVRBackend.dll ---" -ForegroundColor Yellow

if ($BackendDll) {
    if (-not (Test-Path $BackendDll)) { throw "BackendDll not found: $BackendDll" }
    Copy-Item $BackendDll $Backend -Force
    Write-Host "  Copied from $BackendDll" -ForegroundColor Green
}
elseif ($SkipBackendDownload) {
    Write-Host "  -SkipBackendDownload: not installing UEVRBackend.dll." -ForegroundColor Yellow
    Write-Host "  Set the UEVR_BACKEND_DLL environment variable, or pass -BackendDll on a later run."
}
elseif (Test-Path $Backend) {
    $size = [int]((Get-Item $Backend).Length / 1KB)
    Write-Host "  Already present at bin\UEVRBackend.dll ($size KB) — keeping it." -ForegroundColor Green
    Write-Host "  (Re-run with -BackendDll to replace, or delete the file to force redownload.)"
}
else {
    try {
        if (-not $UevrZipUrl) {
            Write-Host "  Querying github.com/praydog/UEVR for latest release..."
            $headers = @{ 'User-Agent' = 'uevr-mcp-installer' }
            $latest = Invoke-RestMethod -Uri 'https://api.github.com/repos/praydog/UEVR/releases/latest' -Headers $headers -TimeoutSec 30
            $asset = $latest.assets | Where-Object { $_.name -like 'UEVR*.zip' } | Select-Object -First 1
            if (-not $asset) { throw "No UEVR*.zip asset on the latest release." }
            $UevrZipUrl = $asset.browser_download_url
            Write-Host "  Latest: $($latest.tag_name) -> $($asset.name)"
        }
        $tmpZip = Join-Path $env:TEMP "uevr_release_$(Get-Random).zip"
        $tmpDir = Join-Path $env:TEMP "uevr_release_$(Get-Random)"
        Write-Host "  Downloading $UevrZipUrl ..."
        Invoke-WebRequest -Uri $UevrZipUrl -OutFile $tmpZip -UseBasicParsing
        Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force
        $found = Get-ChildItem -Path $tmpDir -Recurse -Filter 'UEVRBackend.dll' | Select-Object -First 1
        if (-not $found) { throw "UEVRBackend.dll not present in the UEVR zip." }
        Copy-Item $found.FullName $Backend -Force
        Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
        Remove-Item -Force         $tmpZip -ErrorAction SilentlyContinue
        $size = [int]((Get-Item $Backend).Length / 1KB)
        Write-Host "  OK  UEVRBackend.dll installed ($size KB)" -ForegroundColor Green
    } catch {
        Write-Host "  Failed to fetch UEVRBackend.dll: $_" -ForegroundColor Red
        Write-Host "  You can install UEVR manually from https://github.com/praydog/UEVR/releases"
        Write-Host "  then re-run:  .\install.ps1 -BackendDll <path-to-UEVRBackend.dll>"
        Write-Host "  or set UEVR_BACKEND_DLL in your environment."
    }
}

# ── 3. Game-specific install ─────────────────────────────────────────
if ($GameExe) {
    Write-Host ""
    Write-Host "--- Game install: $GameExe ---" -ForegroundColor Yellow
    if (-not (Test-Path $GameExe)) { throw "GameExe not found: $GameExe" }
    $gameName = [System.IO.Path]::GetFileNameWithoutExtension($GameExe)
    $gameDir  = Join-Path $env:APPDATA "UnrealVRMod\$gameName"
    $pluginDest = Join-Path $gameDir 'plugins\uevr_mcp.dll'
    $sentinel   = Join-Path $gameDir 'dumper_mode'
    New-Item -ItemType Directory -Path (Split-Path $pluginDest -Parent) -Force | Out-Null
    Copy-Item $Plugin $pluginDest -Force
    New-Item -ItemType File -Path $sentinel -Force | Out-Null
    Write-Host "  Plugin   -> $pluginDest" -ForegroundColor Green
    Write-Host "  Sentinel -> $sentinel (dumper-mode: skips UEVR render hooks)" -ForegroundColor Green
}

# ── 4. MCP client registration ───────────────────────────────────────
if ($McpConfig -ne 'none') {
    Write-Host ""
    Write-Host "--- MCP client registration: $McpConfig ---" -ForegroundColor Yellow

    $exeForJson = $Exe.Replace('\', '\\')
    $serverEntry = @{
        type    = 'stdio'
        command = $Exe
    }

    switch ($McpConfig) {
        'claude-code-here' {
            $target = Join-Path (Get-Location) '.mcp.json'
            $json = @{ mcpServers = @{ uevr = $serverEntry } }
            $json | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 $target
            Write-Host "  Wrote $target" -ForegroundColor Green
            Write-Host "  Restart Claude Code in this directory to pick it up."
        }
        'claude-code-user' {
            $target = Join-Path $env:USERPROFILE '.claude.json'
            $existing = if (Test-Path $target) { Get-Content $target -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
            if (-not $existing.mcpServers) { $existing.mcpServers = @{} }
            $existing.mcpServers.uevr = $serverEntry
            $existing | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $target
            Write-Host "  Merged 'uevr' MCP server into $target" -ForegroundColor Green
        }
        'cursor-user' {
            $cursorDir = Join-Path $env:USERPROFILE '.cursor'
            $target = Join-Path $cursorDir 'mcp.json'
            New-Item -ItemType Directory -Path $cursorDir -Force | Out-Null
            $existing = if (Test-Path $target) { Get-Content $target -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
            if (-not $existing.mcpServers) { $existing.mcpServers = @{} }
            $existing.mcpServers.uevr = $serverEntry
            $existing | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $target
            Write-Host "  Merged 'uevr' MCP server into $target" -ForegroundColor Green
            Write-Host "  Restart Cursor to pick it up."
        }
    }
}

# ── 5. Smoke test ────────────────────────────────────────────────────
Write-Host ""
Write-Host "--- Smoke test ---" -ForegroundColor Yellow
try {
    $out = & $Exe wait-plugin 500 2>&1
    if ($out -match '"ok"') {
        Write-Host "  UevrMcpServer.exe runs and emits JSON." -ForegroundColor Green
    } else {
        Write-Host "  UevrMcpServer.exe ran but output was unexpected:" -ForegroundColor Yellow
        $out | Select-Object -First 3 | ForEach-Object { Write-Host "    $_" }
    }
} catch {
    Write-Host "  Smoke test failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
Write-Host "Next steps:"
if (-not $GameExe) {
    Write-Host "  1. Launch your UE4/UE5 game via Steam / Epic."
    Write-Host "  2. Run:  .\install.ps1 -GameExe <full-path-to-game.exe>"
    Write-Host "  3. Run:  .\tools\quick-dump.ps1 -GameExe <full-path-to-game.exe>"
} else {
    Write-Host "  1. Launch '$([System.IO.Path]::GetFileNameWithoutExtension($GameExe))' via Steam / Epic and wait for the main menu."
    Write-Host "  2. Run:  .\tools\quick-dump.ps1 -GameExe `"$GameExe`""
}
Write-Host ""
Write-Host "Or, if you registered with an MCP client, ask your agent things like:"
Write-Host "  'uevr_setup_game' / 'uevr_dump_usmap' / 'uevr_dump_ue_project'"
