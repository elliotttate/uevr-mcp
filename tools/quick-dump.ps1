<#
.SYNOPSIS
    Full reflection dump pipeline for a single UE game.

.DESCRIPTION
    Runs the complete A+B+C dump suite on a target UE4/UE5 game:
      A. USMAP         (FModel / CUE4Parse / UAssetAPI mappings)
      B. UE project    (.uproject + Source/<Module>/{Public,Private}/*.h)
      C. RE bundle     (jmap + hpp + Binary Ninja + IDA import scripts)

    By default does:
      - attach to already-running game (you launch via Steam / Epic)
      - methods=true so UFUNCTION bodies + Kismet bytecode previews are emitted
      - gameContent=true so /Game/ BP classes are included with bytecode

    Outputs go to <OutDir>/:
      <GameStem>.usmap
      MirrorProject/
      REBundle/
      dump.log (full MCP server output)

.PARAMETER GameExe
    Game exe path. Used to infer UnrealVRMod folder + plugin install + attach.

.PARAMETER OutDir
    Base output directory. Defaults to C:\dumps\<GameStem>.

.PARAMETER Launch
    If set, launch the game via setup-game (install plugin + CreateProcess
    + inject). Default behaviour is to attach to an already-running process
    (safer for launcher-stub games like Hogwarts Legacy).

.PARAMETER SkipDumperMode
    Don't enable dumper mode. Use when you specifically want VR-mode UEVR.

.PARAMETER Methods
    Include UFUNCTION bodies + Kismet previews (default: on).
    Set -Methods:$false to skip for a faster dump.

.PARAMETER GameContent
    Include /Game/ BP-generated classes (default: on).
    Set -GameContent:$false to emit only /Script/ native modules.

.PARAMETER EngineAssociation
    UE version for the .uproject (default 4.26).

.EXAMPLE
    .\tools\quick-dump.ps1 -GameExe "E:\SteamLibrary\steamapps\common\RoboQuest\RoboQuest\Binaries\Win64\RoboQuest-Win64-Shipping.exe"

.EXAMPLE
    .\tools\quick-dump.ps1 -GameExe "D:\Epic\Fortnite\FortniteClient.exe" -OutDir D:\dumps\Fortnite -Methods:$false
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GameExe,

    [string]$OutDir,

    [switch]$Launch,

    [switch]$SkipDumperMode,

    [bool]$Methods      = $true,

    [bool]$GameContent  = $true,

    [string]$EngineAssociation = '4.26'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Resolve UevrMcpServer.exe — release zip layout (bin/) or repo dev layout.
$McpCandidates = @(
    (Join-Path $RepoRoot 'bin\UevrMcpServer.exe')
    (Join-Path $RepoRoot 'mcp-server\bin\Release\net9.0\UevrMcpServer.exe')
    (Join-Path $RepoRoot 'mcp-server\bin\Publish\UevrMcpServer.exe')
)
$Mcp = $McpCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Mcp) {
    throw "UevrMcpServer.exe not found. Searched:`n  $($McpCandidates -join "`n  ")`nRun tools\setup.ps1 (repo) or .\install.ps1 (release)."
}
if (-not (Test-Path $GameExe)) {
    throw "GameExe $GameExe not found."
}

$GameStem = [System.IO.Path]::GetFileNameWithoutExtension($GameExe)
if (-not $OutDir) { $OutDir = "C:\dumps\$GameStem" }

# Paths
$UsmapPath    = Join-Path $OutDir "$GameStem.usmap"
$ProjectDir   = Join-Path $OutDir 'MirrorProject'
$BundleDir    = Join-Path $OutDir 'REBundle'
$DumpLog      = Join-Path $OutDir 'dump.log'

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
"$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  quick-dump.ps1 start  GameExe=$GameExe  OutDir=$OutDir  Methods=$Methods  GameContent=$GameContent" | Set-Content $DumpLog

function Run-Mcp {
    param([string]$Verb, [string[]]$Args, [string]$Label)
    Write-Host "[$Label]" -ForegroundColor Cyan
    "---- $Label ----" | Add-Content $DumpLog
    $allArgs = @($Verb) + $Args
    $out = & $Mcp @allArgs 2>&1 | Out-String
    $out | Add-Content $DumpLog
    return $out
}

# Step 0: enable dumper mode unless explicitly skipped
if (-not $SkipDumperMode) {
    & (Join-Path $PSScriptRoot 'enable-dumper-mode.ps1') -GameExe $GameExe | Out-Null
}

# Step 1: attach or launch
if ($Launch) {
    Write-Host "[setup-game]" -ForegroundColor Cyan
    $out = Run-Mcp 'setup-game' @($GameExe) 'setup-game'
    if ($out -match '"ok": false') {
        Write-Warning "setup-game reported error; trying attach"
        Run-Mcp 'attach' @($GameExe) 'attach (fallback)' | Out-Null
    }
    Start-Sleep -Seconds 8
} else {
    Write-Host "[attach]" -ForegroundColor Cyan
    Write-Host "  Assuming $GameStem is already running (launch via Steam / Epic / shortcut)."
    $out = Run-Mcp 'attach' @($GameExe) 'attach'
    if ($out -match '"ok": false') {
        throw "attach failed. Launch the game first or pass -Launch."
    }
    Start-Sleep -Seconds 5
}

# Step 2: verify plugin HTTP up
Write-Host "[wait-plugin]" -ForegroundColor Cyan
try {
    $status = Invoke-RestMethod -Uri 'http://127.0.0.1:8899/api/status' -TimeoutSec 10 -ErrorAction Stop
    Write-Host "  tick_count=$($status.tick_count)  uptime=$($status.uptime_seconds)s"
    "status OK: tick=$($status.tick_count) uptime=$($status.uptime_seconds)" | Add-Content $DumpLog
} catch {
    throw "Plugin HTTP not responding at 127.0.0.1:8899 — plugin may not have initialized yet. Wait 10s and retry, or check $env:APPDATA\UnrealVRMod\$GameStem\log.txt"
}

# Step 3: USMAP
Run-Mcp 'dump-usmap' @($UsmapPath) 'USMAP' | Out-Null
if (Test-Path $UsmapPath) {
    $size = [int]((Get-Item $UsmapPath).Length / 1KB)
    Write-Host "  USMAP:  $UsmapPath ($size KB)" -ForegroundColor Green
} else {
    Write-Warning "USMAP output missing at $UsmapPath"
}

# Step 4: UE project
$methodsFlag = if ($Methods)     { '1' } else { '0' }
$gameFlag    = if ($GameContent) { '1' } else { '0' }
Remove-Item -Recurse -Force $ProjectDir -ErrorAction SilentlyContinue
Run-Mcp 'dump-ue-project' @($ProjectDir, "$GameStem-Mirror", '-', $EngineAssociation, $methodsFlag, $gameFlag) 'UE project' | Out-Null
$hdrCount = (Get-ChildItem -Path $ProjectDir -Filter *.h -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host "  UE project:  $ProjectDir ($hdrCount headers)" -ForegroundColor Green

# Step 5: BN/IDA bundle
Remove-Item -Recurse -Force $BundleDir -ErrorAction SilentlyContinue
Run-Mcp 'dump-bn-bundle' @($BundleDir) 'RE bundle' | Out-Null
if (Test-Path (Join-Path $BundleDir 'uevr_types.jmap')) {
    Write-Host "  RE bundle: $BundleDir" -ForegroundColor Green
    Get-ChildItem $BundleDir | ForEach-Object { Write-Host "    $($_.Name) ($([int]($_.Length/1024)) KB)" }
}

# Summary
Write-Host ""
Write-Host "=== Dump complete ===" -ForegroundColor Cyan
Write-Host "  Output dir:  $OutDir"
Write-Host "  USMAP:       $UsmapPath"
Write-Host "  UE project:  $ProjectDir  ($hdrCount headers)"
Write-Host "  RE bundle:   $BundleDir"
Write-Host "  Log:         $DumpLog"
Write-Host ""
Write-Host "Game is still running. Use tools\stop-game.ps1 to close it, or leave it up for more exploration."
