# UEVR-MCP Server

An [MCP (Model Context Protocol)](https://modelcontextprotocol.io) server that gives AI agents live, programmatic access to any Unreal Engine game running with [UEVR](https://github.com/praydog/UEVR) — from flat-screen games injected into VR to native UE titles.

- **Inspect everything.** Every UObject, every field, every UFunction — searchable and navigable from a running game. Call any method, chain multi-step queries across the object graph, batch operations in a single tick.
- **Read and write live state.** Player health, enemy AI, physics, materials, animations, transforms — the agent sees what the game sees, in real time.
- **Full VR control.** HMD and controller poses, haptic feedback, snap turn, aim method, motion controller attachment — complete VR subsystem access.
- **Live Lua scripting.** Execute Lua code in the game process with persistent state, frame callbacks, timers, async coroutines, and a module system. Hot-reload scripts without losing state. Write and deploy scripts to the UEVR autorun folder.
- **Screenshot from the GPU.** Capture the game's D3D11 or D3D12 backbuffer as JPEG — works even when the game window is behind other windows. Handles R10G10B10A2, FP16 HDR, BGRA8, and RGBA8 formats with high-quality WIC scaling.
- **Crash diagnostics built in.** Pull a single snapshot with callback health counters, breadcrumb state, plugin inventory, latest crash dump, UEVR log tail, runtime map/controller/pawn, and live render metadata.
- **Reverse-engineer anything.** Snapshot an object's state, perform an action, diff to see what changed. Hook any UFunction to log calls, block execution, or run Lua callbacks with argument inspection. Watch properties with Lua triggers for reactive scripting.
- **Real-time event streaming.** Poll for hook fires, watch changes, and Lua output in real time via long-polling.
- **ProcessEvent listener.** Start/stop a global ProcessEvent hook to capture every Blueprint and native function call in real time — filter by name, ignore noisy ticks, establish baselines, and discover what the game does when you take an action. Equivalent to UEVR's Developer tab.
- **Motion controller attachment.** Attach any actor or component to a VR controller hand with position/rotation offsets — the core mechanism for making VR mods feel native.
- **Timer/scheduler.** Create one-shot or repeating timers that execute Lua code, with full lifecycle management (list, cancel, clear).
- **Dump a buildable UE project from a running game.** One call produces a `.uproject` + per-module `Source/<Module>/{Public,Private}/*.h` tree with real `UCLASS(Config=Game) / UPROPERTY(EditAnywhere, Replicated) / AActor* Owner` headers, real property offsets, typed object refs, enum entries, interface inheritance, **CDO default values**, and **Kismet bytecode previews** on Blueprint-hosted functions. Falls back to [Dumper-7](https://github.com/Encryqed/Dumper-7) + USMAP conversion for games where UEVR's render hooks are fragile.
- **Byte-compatible USMAP output.** Drop the file next to FModel / CUE4Parse / UAssetAPI to parse unversioned cooked assets for the game. Validated by round-tripping through jmap's own USMAP parser.
- **Self-discovering reflection probes.** Property-subclass fields UEVR's public API doesn't expose (FObjectProperty::PropertyClass, FMapProperty::KeyProp, UClass::ClassFlags / ClassConfigName / Interfaces[], delegate signatures, UFunction::Script) get located at runtime via SEH-guarded memory probes with UObjectHook-backed validation — works across **UE4.22–UE5.5** without a hardcoded offset table. An `engineVersionHint` is reported alongside probe offsets so agents see which UE family they're on.
- **Auto-compile-check emitter output.** `uevr_compile_check` copies the emitted UHT headers into any host `.uproject`, runs UnrealBuildTool against the game's reported engine association, parses MSVC + UHT diagnostics, and reports per-header pass/fail. Auto-filters headers whose name or declared type collides with the installed engine's own types (~4500 filtered on a typical AAA cooked game) via a two-pass macro scan of Engine/Source + Plugins. Validates the emitter against real UHT + Clang/MSVC instead of regex gut-checks.
- **Native RenderDoc captures from MCP.** Launch through UEVRJ's suspended RenderDoc launcher, request `.rdc` captures, validate them with `renderdoccmd index-capture`, and extract thumbnails without hand-running scripts.
- **Works across games.** Same 178 tools work on any Unreal Engine game that UEVR supports (UE4.22–UE5.5 verified), plus a Dumper-7 fallback path for games too fragile for UEVR's render hooks.

## Setup

### Quickest path: download the release zip (no build, no SDKs)

Grab `UevrMcp-vX.Y.Z.zip` from the [Releases page](https://github.com/elliotttate/uevr-mcp/releases), unzip it, and run the bundled installer:

```powershell
cd C:\Tools\UevrMcp-v1.0.0
.\install.ps1 -GameExe "E:\SteamLibrary\steamapps\common\MyGame\...\MyGame-Win64-Shipping.exe"
.\tools\quick-dump.ps1 -GameExe "...MyGame-Win64-Shipping.exe" -OutDir C:\dumps\MyGame
```

The installer downloads `UEVRBackend.dll` from praydog/UEVR, copies `uevr_mcp.dll` into `%APPDATA%\UnrealVRMod\<Game>\plugins\`, and (optionally with `-McpConfig claude-code-user|cursor-user|claude-code-here`) wires up your MCP client. **No .NET SDK, CMake, or Visual Studio install required** — `UevrMcpServer.exe` is a self-contained single-file publish. See [`release/README.md`](release/README.md) for the full release-side quickstart.

### Build from source

Use this path if you want to hack on the plugin or MCP server itself. Prereqs:

- [UEVR](https://github.com/praydog/UEVR) installed and configured for your game
- [.NET 9+ SDK](https://dotnet.microsoft.com/download/dotnet/9.0) for the MCP server
- [CMake 3.21+](https://cmake.org) and Visual Studio 2022 Build Tools to compile the plugin

### 1. Build and install the plugin

```bash
cd plugin
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

Copy `build/Release/uevr_mcp.dll` into your UEVR plugins directory for the target game:

```
%APPDATA%\UnrealVRMod\<GameExeName>\plugins\uevr_mcp.dll
```

For example: `%APPDATA%\UnrealVRMod\MyGame-Win64-Shipping\plugins\uevr_mcp.dll`

### 2. Connect an MCP client

The repo root contains `.mcp.json` — agents that support workspace-level MCP config (Claude Code, Cursor, etc.) will detect it automatically.

To register manually:

```json
{
  "mcpServers": {
    "uevr": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/mcp-server"],
      "env": {
        "UEVR_MCP_API_URL": "http://localhost:8899"
      }
    }
  }
}
```

### 3. Launch the game

Start the game, inject UEVR, and the plugin starts automatically. The HTTP server listens on `localhost:8899`. The MCP server connects to it on first tool call.

Prefer the one-call flow below — `uevr_setup_game` handles plugin install + process launch + backend injection + verification in a single call.

### CLI / script-first mode (no MCP client required)

`UevrMcpServer.exe` has positional subcommands that bypass the MCP stdio transport — shell scripts, CI jobs, and "I just want the dump, no AI agent" flows. Everything's documented in:

- [`tools/cli-cheatsheet.md`](tools/cli-cheatsheet.md) — every verb with concrete examples
- [`tools/dumper-mode-recipe.md`](tools/dumper-mode-recipe.md) — from zero to a full dump, copy-paste walkthrough
- [`docs/dumper-mode.md`](docs/dumper-mode.md) — deep dive on the dumper-mode UEVR architecture

Ready-made PowerShell wrappers in [`tools/`](tools/):

| Script | What it does |
|---|---|
| `setup.ps1` | Verify deps, build plugin + MCP server, optional per-game WER registration. One-shot, idempotent. |
| `enable-dumper-mode.ps1` / `disable-dumper-mode.ps1` | Toggle the `dumper_mode` sentinel for a specific game |
| `quick-dump.ps1` | Full pipeline for a single game: attach → USMAP + UE project + BN/IDA bundle |
| `enable-wer-dumps.ps1` | Register per-exe Windows Error Reporting full-memory dumps for crash diagnostics |
| `stop-game.ps1` | Graceful close of an injected game |

**Shortest path from zero to dump:**

```powershell
# 1. One-time setup
.\tools\setup.ps1

# 2. Enable dumper mode (skips UEVR render hooks — reflection-only plugins don't need them)
.\tools\enable-dumper-mode.ps1 -GameExe "E:\SteamLibrary\steamapps\common\MyGame\...\MyGame-Win64-Shipping.exe"

# 3. Launch game via Steam, wait for main menu, then:
.\tools\quick-dump.ps1 -GameExe "E:\SteamLibrary\steamapps\common\MyGame\...\MyGame-Win64-Shipping.exe" -OutDir C:\dumps\MyGame
```

Output: `MyGame.usmap` + `MirrorProject/` (buildable UE4/UE5 project) + `REBundle/` (Binary Ninja / IDA import bundle).

**Raw CLI verbs** (for custom pipelines):

```
# Lifecycle
UevrMcpServer.exe setup-game     <gameExe>              # install+launch+inject
UevrMcpServer.exe attach         <gameExe>              # attach to running game (launcher-stub friendly)
UevrMcpServer.exe stop-game      <gameExe>
UevrMcpServer.exe plugin-info    <gameExe>
UevrMcpServer.exe plugin-rebuild

# Dumps
UevrMcpServer.exe dump-usmap       <outPath> [filter] [compression]
UevrMcpServer.exe dump-ue-project  <outDir> [proj] [modules] [engineAssoc] [methods:0|1] [gameContent:0|1]
UevrMcpServer.exe dump-bn-bundle   <outDir> [filter]
UevrMcpServer.exe emit-from-usmap  <usmap> <outDir> <proj> <module> <engineAssoc> [gobjPath]
UevrMcpServer.exe compile-check    <emittedDir> <hostUproject> [module] [maxHeaders] [headerFilter]

# Patternsleuth wrappers
UevrMcpServer.exe ps-resolve   <exePath> [resolvers]
UevrMcpServer.exe ps-xref      <exePath> <address>
UevrMcpServer.exe ps-diff      <exePath> [baselineJson] [outputJson]
UevrMcpServer.exe ps-disasm    <exePath> <resolverOrRange>
UevrMcpServer.exe ps-symbols   <exePath> <regex>
```

Each command prints the same JSON the MCP tool would return and exits. When the first arg doesn't match any known subcommand, the server falls through to normal MCP stdio mode.

## Launching and lifecycle

The `SetupTools` + `ReadinessTools` surface turns "point the MCP at a game exe" into a one-call flow. Behind the scenes the tool: copies `uevr_mcp.dll` into `%APPDATA%\UnrealVRMod\<GameName>\plugins\`, launches the game with `-windowed` (or your launch args), waits for the main window, injects `UEVRBackend.dll` via `CreateRemoteThread` + `LoadLibraryA`, and polls `Process.Modules` until both modules show up.

```
uevr_setup_paths                          # verify plugin + backend DLLs are found
uevr_find_steam_game { query: "robo" }    # locate exe under any Steam library
uevr_setup_game { gameExe: "E:\\...\\MyGame-Win64-Shipping.exe" }
uevr_wait_for_plugin { timeoutMs: 30000 } # block until HTTP :8899 responds
uevr_is_ready                             # single green/red: process+backend+plugin+HTTP
```

`uevr_setup_game` is idempotent against an already-running game — it reuses the PID and just injects. `uevr_stop_game` does a graceful `WM_CLOSE` then falls back to `Kill` after `gracefulMs`, with `force=true` for immediate termination. `uevr_uevr_log` tails `%APPDATA%\UnrealVRMod\<GameName>\log.txt` (the UEVR host log, distinct from `uevr_get_log` which is the plugin's own ring buffer).

**Fragile-game stability mode.** Some games crash UEVR's stereo render hook on the first frame after injection. For those, `uevr_write_stability_config` writes a conservative `config.txt` (ExtremeCompatibilityMode, 2DScreenMode, skip PostInitProperties, alternating render method) before launch, and `uevr_suppress_d3d_monitor` suspends UEVR's worker threads after injection — this breaks the 10-second rehook retry cycle that tears down D3D mid-render. See the [Troubleshooting](#troubleshooting--game-specific-notes) section for what works on which game class.

## Dumping a UE game to a buildable project

This is the headline workflow. The MCP produces jmap-equivalent output: a full `.uproject` tree you can drop into the UE4/UE5 editor and compile with UnrealHeaderTool.

### Quick start (~30 seconds end-to-end on a Steam indie):

```
# 1. Set up + inject
uevr_setup_game      { gameExe: "E:\\SteamLibrary\\...\\MyGame-Win64-Shipping.exe" }
uevr_wait_for_plugin { timeoutMs: 30000 }

# 2. Dump
uevr_dump_usmap      { outPath: "E:\\out\\mygame.usmap" }
uevr_dump_ue_project { outDir:  "E:\\out\\MyGameMirror",
                       projectName: "MyGameMirror",
                       engineAssociation: "4.26" }

# 3. Validate
uevr_validate_usmap  { usmapPath: "E:\\out\\mygame.usmap" }
```

Output: a real UE4 project with `Source/<Module>/{Public,Private}/*.h` files like:

```cpp
// Source/Phoenix/Public/SpellData.h
USTRUCT(BlueprintType)
struct PHOENIX_API FSpellData {
    GENERATED_BODY()
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly)
    FName SpellName;          // +0x8

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly)
    AActor* Instigator;       // +0x10  ← typed, A-prefix resolved

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly)
    FHitResult Hit;           // +0x18  ← nested struct
};
```

### What you get

| Output | Size on a ~big AAA UE4.27 (Hogwarts Legacy) | Size on a small indie UE4.26 (Severed Steel) |
|---|---|---|
| USMAP (jmap-compatible, byte-verified) | 2.5 MB, 16407 structs + 2626 enums | 1.1 MB, 6194 structs + 1261 enums |
| UE project | 141 modules, 12460 headers | 30 modules, 1649 headers |
| Dump time | ~20 s | ~4 s |

### Available dump tools

| Tool | Output |
|---|---|
| `uevr_dump_usmap` | `.usmap` binary — FModel / CUE4Parse / UAssetAPI compatible. Version 4 (ExplicitEnumValues). Optional Brotli compression. |
| `uevr_dump_sdk_cpp` | Cast-style C++ headers with byte offsets baked into comments. For runtime workflows (trainers, VR mods, patches) that `reinterpret_cast` game-memory addresses directly. |
| `uevr_dump_uht_sdk` | UHT-style single-dir headers with `UCLASS / USTRUCT / UPROPERTY` macros and forward decls. Use as input to your own editor project. |
| `uevr_dump_ue_project` | **Full UE project** — `.uproject`, `Target.cs`, per-module `Build.cs`, `Public/*.h`, `Private/<Module>.cpp`. The jmap-equivalent. |
| `uevr_dump_reflection_json` | Raw structured JSON of every class, struct, enum with fields, offsets, property flags, tags. Feed it into your own generator. |
| `uevr_dump_bn_ida_bundle` | Reverse-engineering bundle for Binary Ninja + IDA Pro: emits a Binary Ninja-friendly `.jmap`, a parser-friendly local-type header, import scripts for both tools, and a README with usage notes. |
| `uevr_dump_usmap_selftest` | Emit a synthetic fixture USMAP covering every tag variant — no game required. Pair with `uevr_validate_usmap` for round-trip testing. |
| `uevr_dump_cache_status` / `uevr_dump_cache_clear` | Inspect / invalidate the per-session reflection-walk cache. Consecutive dump calls with the same filter share one walk. |

### When the live pipeline doesn't work: Dumper-7 fallback

A handful of games trip UEVR's `FFakeStereoRenderingHook` on first frame — the process dies before we can dump. For those, use the vendored [Dumper-7](https://github.com/Encryqed/Dumper-7) (MIT, built as `plugin/build/Release/dumper7.dll` alongside `uevr_mcp.dll`). Dumper-7 never hooks D3D — it reads reflection without touching the render pipeline, so it survives on games where UEVR can't.

```
# 1. Launch game, wait for main menu (memory stable above a threshold)
# 2. Inject Dumper-7 — it auto-dumps into C:\Dumper-7\<GameName-Version>\
uevr_dumper7_run { pid: 12345, gameName: "MyGame-Win64-Shipping" }

# 3. Convert Dumper-7's USMAP into the same UE project we'd produce live
uevr_dump_uht_from_usmap {
    usmapPath: "C:\\Dumper-7\\MyGame-4.26.2-...\\Mappings\\4.26.2-0+++UE4+Release-4.26-MyGame.usmap",
    outDir:    "E:\\out\\MyGameMirror",
    projectName: "MyGameMirror",
    moduleName:  "MyGame",
    engineAssociation: "4.26"
}
```

Output matches `uevr_dump_ue_project` in structure but omits fields USMAP doesn't carry: real property offsets (all `+0x0`), `UCLASS(Abstract, Config=X)` class-flag specifiers, `UFUNCTION` method stubs, interface inheritance lists, per-module package origin (collapses into a single flat module). Everything else — class hierarchy, `UPROPERTY` types with typed object refs, enum entries, forward decls, A/U prefixing — is the same.

### Validating a USMAP

```
uevr_validate_usmap { usmapPath: "..." }
```

Two-stage check: (a) local header parse (magic, version, compression, names-section count), (b) optional round-trip through jmap's `usmap to-json` CLI (set `$USMAP_CLI` or drop `usmap.exe` on PATH; the local `E:\Github\jmap\target\release\usmap.exe` is tried automatically). Reports struct / enum counts post-parse so you can cross-check.

### Probe diagnostics

The reflection pipeline uses runtime probes to find FProperty / UClass / UFunction field offsets that UEVR's public API doesn't expose. Probes self-discover on first use via SEH-guarded memory reads validated with `UObjectHook::exists` (for UObject-valued fields) or executable-memory / FName plausibility checks (for native / FField-valued fields). Check status with:

```
uevr_probe_status
→ {
    probes: [
      { field: "FObjectPropertyBase::PropertyClass", offset: 0x78, status: "resolved", hits: 45210, ... },
      { field: "UClass::ClassWithin",                offset: 0xD8, status: "resolved", hits:  3920, ... },
      { field: "UFunction::Script",                  offset: 0xB8, status: "resolved", hits:  7811, ... },
      ...
    ],
    engineVersionHint: "UE5"   // inferred from presence of DoubleProperty
  }
```

Output lists each probe with `status` (`resolved` / `failed` / `undiscovered`), discovered `offset`, and `hits` count. On a new game, expect all 10 probes to resolve on the first heavy dump. If any stay `undiscovered`, that game's layout is outside our candidate offset range — open an issue or extend `plugin/src/reflection/property_probes.cpp`'s candidate list.

Probes covered:
- `FObjectPropertyBase::PropertyClass` (and its SoftObject / WeakObject / LazyObject / AssetObject variants)
- `FClassProperty::MetaClass`
- `FInterfaceProperty::InterfaceClass`
- `FMapProperty::KeyProp` / `ValueProp`
- `FSetProperty::ElementProp`
- `F*DelegateProperty::SignatureFunction`
- `UClass::ClassWithin` (anchor — ClassFlags is at offset-0xC, ClassConfigName at offset+0x10)
- `UClass::Interfaces` (TArray<FImplementedInterface>)
- `UFunction::Script` (Kismet bytecode TArray<uint8>, anchored on Func pointer in executable memory)

### Kismet bytecode previews

For Blueprint-hosted UFunctions (those with non-empty `Script` buffers), the emitter renders an opcode mnemonic preview comment above each method signature:

```cpp
// @kismet 47 bytes: LetBool, Let, Context, LocalVariable, True, Return, EndOfScript
UFUNCTION(BlueprintCallable)
void SetVisible(bool bNewVisible);
```

The disassembler walks up to 32 opcodes per function, stopping cleanly on unknown opcodes (rather than misaligning into operand bytes). Native-only UFUNCTIONs skip the comment (no Script buffer). Opcode names mirror UE's `EExprToken` enum from `Engine/Source/Runtime/CoreUObject/Public/UObject/Script.h` — enough for triage without reimplementing the full EX_* interpreter. For deeper analysis, pair with [KismetKompiler](https://github.com/TGE-TJ/KismetKompiler) or [Kismet Analyzer](https://github.com/trumank/kismet-analyzer).

### Validating the emitter with UnrealBuildTool

`uevr_compile_check` is the ground-truth check for emitter output — it runs the real UHT + MSVC / Clang toolchain against the generated headers and surfaces concrete per-file errors. Use it any time you suspect the emitter produced something UE won't accept.

```
uevr_compile_check {
    emittedDir:    "E:\\out\\MyGameMirror",
    hostUproject:  "C:\\Users\\me\\Documents\\Unreal Projects\\Scratch\\Scratch.uproject",
    moduleName:    "SDK"
}
→ {
    ok: true,
    data: {
      exitCode: 0,
      elapsedSec: 11.5,
      moduleName: "SDK",
      srcHeaders: 4805,               // copied into the host project
      skippedEngineConflicts: 1988,   // filtered by engine-type scan
      totalErrors: 0,
      totalWarnings: 0,
      perFile: [],
      engineRoot: "E:\\UnrealEngine\\UE_5.5",
      logTail: "...UHT manifest + UBT output..."
    }
  }
```

**What it does:**

1. Resolves the installed engine from `hostUproject`'s `EngineAssociation` via `LauncherInstalled.dat` (falls back to common install paths).
2. Copies `<emittedDir>/Source/<module>/Public/*.h` into `<hostUproject>/Source/<moduleName>/Public/`, wiping the destination first.
3. **Engine-type filter** — skips headers whose basename matches any `.h` file in `Engine/Source` or `Engine/Plugins`, AND whose declared type name (stripped of `A/U/F/E/I` prefix, `DEPRECATED_` prefix, case-insensitive) matches any `UCLASS` / `USTRUCT` / `UENUM` / `UINTERFACE` declared in engine headers. The scan uses a two-pass macro walker that handles nested parens in `meta=(...)`, `UE_DEPRECATED(5.4, "...")` attributes, and `UENUM()` followed by comment-then-`enum X : int`. Cached on disk per engine install (~15s first time, ~100ms cached).
4. Generates a minimal `Build.cs` + module-stub `.cpp` in the host project, patches the host `.uproject` to list the new module.
5. Invokes `Engine\Build\BatchFiles\Build.bat <HostEditor> Win64 Development -Project=<uproject>`.
6. Parses MSVC (`path(line,col): error C1234: msg`) and UHT (`path(line): Error: msg`) diagnostics from the log, groups by filename, returns top-30 failing files with 3 error samples each.

**What the filters mean:**

- `srcHeaders` — headers that actually made it into the build.
- `skippedEngineConflicts` — headers excluded pre-build because they collide with engine types. On a typical cooked AAA UE4.27 game this is ~70% of the emit (the game's cooked USMAP includes every engine type it references).
- `totalErrors` / `totalWarnings` — actual UE compile failures on the filtered subset. This is the emitter-defect budget: if this is > 0, the generator has a real bug.
- `perFile[]` — ranked worst-to-least-bad, with sample diagnostics. Usually surfaces a pattern (all failures are "Unable to find parent class X" → the emitter is misclassifying struct vs class) rather than 500 unrelated issues.

**Typical smoke-test flow** with a narrow filter before committing to the full compile:

```
# Quick 20-header smoke test to confirm the UHT path works
uevr_compile_check {
    emittedDir:   "E:\\out\\MyGameMirror",
    hostUproject: "C:\\...\\Scratch.uproject",
    maxHeaders:   20,
    headerFilter: "EBool"           // substring match on filename
}

# If smoke is green, run the full emit
uevr_compile_check {
    emittedDir:   "E:\\out\\MyGameMirror",
    hostUproject: "C:\\...\\Scratch.uproject"
}
```

**Real-world numbers** from RoboQuest USMAP → UE 5.5 compile-check:

- 6927 UHT headers emitted from USMAP + Dumper-7 offset backfill
- 4489 filtered as engine-name collisions
- 2438 copied → UHT parses **2402 clean** (98.5% of the non-engine subset)
- 36 emitter defects surfaced as `"Unable to find parent class type X"` (struct-vs-class super misclassification — a real gap for future work)

A host `.uproject` is required because UBT + UHT need a project context to resolve engine dependencies. Any empty UE5 C++ scratch project works — create one in 60 seconds with File → New → Games → Blank → C++.

### Anatomy of an emitted header

A single generated header combines four orthogonal data sources:

```cpp
// Source/SDK/Public/WeaponData.h

// Auto-generated UHT-style header. Source: ScriptStruct /Script/RoboQuest.WeaponData
#pragma once

#include "CoreMinimal.h"
#include "UObject/NoExportTypes.h"
#include "UObject/ObjectMacros.h"

class AProjectile;              //         ← Forward-decls derived from the
struct FDamageProfile;          //           property tags' propertyClass /
enum class EWeaponSlot : uint8; //           structName / enumName fields

#include "WeaponData.generated.h"

USTRUCT(BlueprintType)                     // ← UCLASS/USTRUCT flags decoded from
struct SDK_API FWeaponData {               //   CLASS_* / STRUCT_* bit sets via
    GENERATED_BODY()                       //   UClassSpecifier() / reflection

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Replicated)   // ← CPF_* flags
    float FireRate = 0.25f;               // +0x10   ← offset from Dumper-7 GObjects
                                          //          or live FProperty::get_offset
    UPROPERTY(EditAnywhere, BlueprintReadOnly)
    AProjectile* ProjectileClass;         // +0x18   ← propertyClass probe +
                                          //          Actor-chain detection → A prefix

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly)
    FDamageProfile Damage;                // +0x20

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly)
    EWeaponSlot PreferredSlot = EWeaponSlot::Primary;   // ← CDO default value

    UPROPERTY(BlueprintReadOnly)
    TArray<AProjectile*> AltFireSequence; // +0x28   ← array with typed inner

    // @kismet 92 bytes: Let, InstanceVariable, Context, FinalFunction,
    //         IntConst, Return, EndOfScript, ...             ← Kismet preview
    UFUNCTION(BlueprintCallable, BlueprintImplementableEvent)
    float CalculateDamage(AActor* Target, int32 Hits);

    // Native-only C++ function — no Script buffer, no @kismet comment.
    UFUNCTION(BlueprintCallable)
    void ApplyDamage(AActor* Target, float Amount);
};
```

| Element | Source |
|---|---|
| `USTRUCT(BlueprintType)` / `UCLASS(Abstract, Config=Engine, ...)` | `UClass::ClassFlags` (CLASS_* bits) decoded via `UClassSpecifier()` |
| `UPROPERTY(EditAnywhere, BlueprintReadWrite, Replicated, Transient, Config, ...)` | `FProperty::PropertyFlags` (CPF_* bits) decoded via `UPropertySpecifier()` |
| `UFUNCTION(BlueprintCallable, Server, Reliable, BlueprintImplementableEvent, ...)` | `UFunction::FunctionFlags` (FUNC_* bits) decoded via `UFunctionSpecifier()` |
| Typed field references (`AProjectile*`, `FHitResult`, `EWeaponSlot`) | Live FProperty subclass probes — PropertyClass, MetaClass, InterfaceClass, MapKey/Value, SetElement, SignatureFunction |
| A/U/F/E/I prefix resolution | Actor-chain detection via super-walk + known Actor base list |
| `+0x10` byte offsets | Live `FProperty::get_offset()` or parsed from Dumper-7's `GObjects-Dump-WithProperties.txt` on the fallback path |
| `= 0.25f` / `= FName(TEXT("None"))` default values | Phase 4C: `UClass::ClassDefaultObject` walked after field enumeration — typed literal rendered via `RenderCppLiteral()` |
| Forward declarations | Tag graph traversal — every `propertyClass` / `structName` / `enumName` tag walks back to its declared type and emits one forward decl |
| `// @kismet N bytes: OP1, ...` | `UFunction::Script` probe + opcode walk, 32-opcode cap, emitted only for non-native BP-hosted functions |
| `class SDK_API FWeaponData` | `<moduleName>_API` inferred from the target module name |
| Inherits from `public AProjectile` | USMAP / live-reflection super field with A-prefix if the super-chain reaches Actor |
| Implements interfaces via `UINTERFACE` / `public IFoo` | `UClass::Interfaces` probe (TArray<FImplementedInterface>) |

Every field in the table is a separately-probed data source — any one of them can be missing without breaking the others. If a field's offset probe fails, the offset renders as `+0x0` but the type, name, and flags still emit correctly.

## How it works

The plugin is a C++ DLL loaded by UEVR into the game process. It uses the UEVR C++ API to access Unreal Engine's reflection system — every UObject, UClass, UStruct, UFunction, and FProperty is reachable. The plugin exposes this as a REST API:

1. **Type queries** go straight to UE's reflection system — no live instance needed
2. **Object inspection** reads field values from live objects via FProperty offsets
3. **Method invocation** calls UFunctions through `process_event` with full parameter marshaling
4. **Chain queries** walk the object graph server-side, expanding fields, calling methods, filtering, and collecting results in a single request
5. **Screenshot capture** prefers UEVR's D3D11 post-render target, falls back to present when needed, and encodes via WIC
6. **Diagnostics snapshot** bundles structured plugin logs, callback health, breadcrumbs, render metadata, loaded plugins, crash-dump info, runtime world state, and the UEVR log tail
7. **Lua execution** runs code in an embedded Sol2/Lua 5.4 state with UEVR API bindings

The MCP server is a thin C# translation layer. Each MCP tool maps to one HTTP endpoint. A named pipe provides a secondary channel for status and log operations.

## Architecture

```
  AI Agent / MCP Client
        |
        | MCP protocol (stdio)
        v
  +-----------------+
  |   MCP Server    |  mcp-server/     .NET console app
  |  (stdio bridge) |  Translates MCP tool calls -> HTTP
  +-----------------+
        |
        | HTTP (localhost:8899)
        v
  +-----------------+
  |  Game Plugin    |  plugin/          C++ DLL (UEVR plugin)
  |  (HTTP server)  |  Runs inside the game process
  +-----------------+
        |
        | UEVR C++ API (UObject reflection, D3D11, XInput)
        v
  +-----------------+
  |  Unreal Engine  |  The actual game
  |  Game Process   |
  +-----------------+
```

**`plugin/`** — A C++ DLL loaded by UEVR inside the game. Embeds Lua 5.4 + Sol2, cpp-httplib, nlohmann/json. Starts an HTTP server on `localhost:8899` exposing the gameplay API plus a diagnostics surface for structured logs, breadcrumbs, callback counters, plugin inventory, render metadata, and crash snapshots. Hooks into the engine tick, D3D present, D3D11 post-render, and XInput callbacks. HTTP handler threads submit work to the game thread via a `GameThreadQueue` (std::promise/future, up to 16 items per tick, 5s timeout) to safely access UE internals. A named pipe (`\\.\pipe\UEVR_MCP`) provides a secondary channel for status and log operations that work even before the HTTP server is ready.

**`mcp-server/`** — A standalone .NET console app that speaks MCP over stdio. Translates tool calls into HTTP requests, falling back to the named pipe for status/log/game-info when HTTP is unavailable. Diagnostics tools map directly to the HTTP snapshot and per-surface diagnostics routes. Host-side tools can also launch games through UEVRJ's RenderDoc launcher, write the RenderDoc capture sentinel, and validate `.rdc` files with `renderdoccmd.exe`.

## 178 MCP Tools

### Setup & Lifecycle (8 tools)

| Tool | Description |
|------|-------------|
| `uevr_setup_paths` | Report the plugin DLL / backend DLL paths the setup flow will use (overridable via `$UEVR_MCP_PLUGIN_DLL` / `$UEVR_BACKEND_DLL`) |
| `uevr_find_steam_game` | Scan every Steam library's `steamapps/common/*/` for UE `*-Shipping.exe` — returns `{gameName, exe, libraryPath}` tuples |
| `uevr_install_plugin` | Copy `uevr_mcp.dll` to `%APPDATA%\UnrealVRMod\<GameName>\plugins\` (idempotent) |
| `uevr_setup_game` | One-call install + launch + inject backend + verify. Returns per-step results; reuses an already-running process if present |
| `uevr_wait_for_plugin` | Block until the plugin's HTTP endpoint responds; polls every ~300ms, bails if the game process dies |
| `uevr_is_ready` | Single-call health check: process alive, `UEVRBackend.dll` + `uevr_mcp.dll` loaded, HTTP responding |
| `uevr_stop_game` | Graceful `WM_CLOSE` then `Kill` fallback after `gracefulMs`; `force=true` skips the graceful step |
| `uevr_uevr_log` | Tail `%APPDATA%\UnrealVRMod\<GameName>\log.txt` (UEVR host log — distinct from `uevr_get_log` which is our plugin's own ring buffer) |

### Reflection Dumping (11 tools)

| Tool | Description |
|------|-------------|
| `uevr_dump_usmap` | Full `.usmap` binary (v4 / ExplicitEnumValues) with jmap-matching tag recursion; optional Brotli compression |
| `uevr_dump_usmap_selftest` | Synthesize a USMAP from a built-in fixture covering every tag variant — no game required |
| `uevr_dump_sdk_cpp` | Cast-style C++ headers with byte offsets in comments (for trainers / VR mods / raw-cast workflows) |
| `uevr_dump_uht_sdk` | UHT-style single-dir headers with `UCLASS / USTRUCT / UPROPERTY` macros, forward decls, decoded CPF/CLASS flags, CDO default values, and Kismet bytecode previews |
| `uevr_dump_ue_project` | Buildable UE project — `.uproject` + per-module `Source/<Module>/{Public,Private}/*.h` tree |
| `uevr_dump_uht_from_usmap` | Convert any USMAP (ours, Dumper-7's, jmap's) into the same UE project; pass `gObjectsPath` to backfill real offsets from Dumper-7's `GObjects-Dump-WithProperties.txt` |
| `uevr_dump_reflection_json` | Raw JSON of every class/struct/enum + fields + offsets + flags + `scriptBytes`/`scriptOps` for BP-hosted functions |
| `uevr_dump_cache_status` / `uevr_dump_cache_clear` | Inspect / clear the per-session reflection-walk cache |
| `uevr_probe_status` / `uevr_probe_reset` | Show which FProperty / UClass / UFunction::Script raw-offset probes have resolved, at which offsets, plus `engineVersionHint` ("UE4" / "UE5"); clear for re-discovery |

### Emitter Validation (1 tool)

| Tool | Description |
|------|-------------|
| `uevr_compile_check` | Copy emitted UHT headers into a host `.uproject`, run UnrealBuildTool, and report per-file pass/fail. Auto-excludes headers colliding with the installed engine's own types via a two-pass macro-scan of `Engine/Source` + `Engine/Plugins` (handles `UE_DEPRECATED`, nested-paren `meta=(...)`, `UENUM() + enum X : int`, `DEPRECATED_` prefixes, case-insensitive). `maxHeaders` / `headerFilter` for smoke tests. |

### Plugin Dev (3 tools)

| Tool | Description |
|------|-------------|
| `uevr_plugin_rebuild` | Run `cmake --build plugin/build --config Release --target uevr_mcp` from the MCP server. Configures first if `plugin/build/CMakeCache.txt` is missing. Returns new DLL path, size, mtime, and tail of build output |
| `uevr_plugin_stage` | Copy a freshly built `uevr_mcp.dll` as `uevr_mcp.dll.pending` next to the installed one — escape hatch when the active DLL is file-locked by a running game |
| `uevr_plugin_info` | Report built-vs-installed DLL mtimes, pending-DLL presence, live plugin HTTP reachability, and `engineVersionHint` from the running game's probe diagnostics. Tells you if a rebuild + install is needed |

### USMAP Validation & Conversion (1 tool)

| Tool | Description |
|------|-------------|
| `uevr_validate_usmap` | Local header parse + optional jmap `usmap to-json` round-trip with struct/enum counts |

### Dumper-7 Integration (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_dumper7_inject` | Inject our vendored `dumper7.dll` into a running UE process (no D3D hooking — survives games UEVR crashes on) |
| `uevr_dumper7_run` | One-shot inject + wait for SDK output to finalize + return the SDK folder layout |
| `uevr_dumper7_status` | Report DLL path resolution, output roots, latest SDK folder, whether Dumper-7 is loaded in a target |
| `uevr_dumper7_list` | List files in a produced SDK folder (fuzzy pattern match, paginated) |
| `uevr_dumper7_read` | Read a single generated header by path or fuzzy package-name query with byte offset / length for paging |

### Memory Write (2 tools)

| Tool | Description |
|------|-------------|
| `uevr_write_memory` | Write raw bytes (hex string or int array) to any game-process address; SEH-guarded with VirtualProtect toggling for code patches |
| `uevr_patch_bytes` | AoB-scan + write in one call; aborts on multi-match unless `force=true` |

### External Tool Wrappers (3 tools)

| Tool | Description |
|------|-------------|
| `uevr_uesave` | Shell to [uesave](https://github.com/trumank/uesave-rs) for GVAS save-file `to-json` / `from-json` roundtrips |
| `uevr_patternsleuth` | Shell to [patternsleuth](https://github.com/trumank/patternsleuth) for UE symbol / AoB signature resolution |
| `uevr_validate_usmap` | (listed above — shells to jmap's `usmap` CLI) |

### Stability Mode for Fragile Games (2 tools)

| Tool | Description |
|------|-------------|
| `uevr_write_stability_config` | Write a conservative UEVR `config.txt` (ExtremeCompatibilityMode, 2DScreenMode, SkipPostInitProperties, etc.) to `%APPDATA%\UnrealVRMod\<GameName>\` before launch |
| `uevr_suppress_d3d_monitor` | Enumerate threads in the target, suspend the UEVR worker threads that drive the 10-second rehook retry cycle (the classic "Last chance encountered for hooking" death loop) |

### Host RenderDoc Capture (6 tools)

See [`docs/renderdoc-capture.md`](docs/renderdoc-capture.md) for the full
setup and one-call capture workflow.

| Tool | Description |
|------|-------------|
| `uevr_renderdoc_paths` | Resolve UEVRJ/RenderDoc paths: launcher, backend, `renderdoc.dll`, `renderdoccmd.exe`, smoke app, and sentinel file |
| `uevr_renderdoc_launch_game` | Launch a game through `UEVRRenderDocLauncher.exe` so RenderDoc is resident before first D3D12/DXGI object creation |
| `uevr_renderdoc_request_capture` | Write `%TEMP%\uevr_renderdoc_capture.req`, wait for the `.rdc`, then optionally index and thumbnail it |
| `uevr_renderdoc_capture_game` | One-call launch + startup wait + capture + validation flow |
| `uevr_renderdoc_validate_capture` | Run `renderdoccmd index-capture` and `renderdoccmd thumb` on an existing `.rdc` |
| `uevr_renderdoc_list_captures` | List recent `.rdc` captures from the common UEVR temp capture directories |

### Object Exploration (13 tools)

| Tool | Description |
|------|-------------|
| `uevr_is_valid` | Check if a UObject pointer is still alive — returns class/name if valid |
| `uevr_search_objects` | Search GUObjectArray by name substring |
| `uevr_search_classes` | Search UClass objects by name |
| `uevr_get_type` | Full type schema — all fields with types/offsets, all methods with signatures |
| `uevr_inspect_object` | Full inspection of a live object — all field values and methods |
| `uevr_summary` | Lightweight one-line-per-field overview (start here) |
| `uevr_read_field` | Read a single field, follows object references automatically |
| `uevr_call_method` | Call a 0-parameter getter method |
| `uevr_objects_by_class` | Find all live instances of a class |
| `uevr_get_singletons` | List common singletons (GameEngine, World, GameMode, etc.) |
| `uevr_get_singleton` | Find first live instance of a type |
| `uevr_get_array` | Paginated array property reading |
| `uevr_chain` | Multi-step object graph traversal (field → method → array → filter → collect) |

### Mutation (4 tools)

| Tool | Description |
|------|-------------|
| `uevr_write_field` | Write any field (bool, int, float, string, struct, enum, object ref) |
| `uevr_invoke_method` | Call any UFunction with arguments |
| `uevr_batch` | Multiple operations in one game-thread tick |
| `uevr_exec_command` | Execute console commands (stat fps, show collision, etc.) |

### Memory (2 tools)

| Tool | Description |
|------|-------------|
| `uevr_read_memory` | Raw hex dump with ASCII sidebar (max 8192 bytes) |
| `uevr_read_typed` | Read typed values sequentially (u8–u64, i8–i64, f32, f64, ptr) |

### Player & Camera (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_get_player` | Player controller + pawn addresses and classes |
| `uevr_set_position` | Teleport player (partial updates — omit axes to keep current) |
| `uevr_set_health` | Set health (searches common field names on pawn + components) |
| `uevr_get_camera` | Camera position, rotation, FOV |
| `uevr_get_game_info` | Game exe path, VR runtime, uptime |

### Console Variables (3 tools)

| Tool | Description |
|------|-------------|
| `uevr_list_cvars` | List console variables with optional filter |
| `uevr_get_cvar` | Read a CVar's value |
| `uevr_set_cvar` | Set a CVar's value |

### VR Controls (11 tools)

| Tool | Description |
|------|-------------|
| `uevr_vr_status` | VR runtime (OpenVR/OpenXR), HMD active, resolution, controllers |
| `uevr_vr_poses` | Live HMD + controller grip/aim poses and standing origin |
| `uevr_vr_settings` | Current snap turn, aim method, decoupled pitch settings |
| `uevr_set_vr_setting` | Change any VR setting or arbitrary mod value |
| `uevr_vr_input` | Controller input state: joystick axes, movement orientation, OpenXR action queries |
| `uevr_get_world_scale` | Get WorldToMetersScale from UWorld (default 100) |
| `uevr_set_world_scale` | Set WorldToMetersScale — lower = player feels larger, higher = smaller. Some games reset each tick; use a looping timer to persist. |
| `uevr_recenter` | Recenter the VR view |
| `uevr_haptics` | Trigger controller vibration |
| `uevr_save_config` | Save UEVR config to disk |
| `uevr_reload_config` | Reload UEVR config from disk |

### Motion Controller Attachment (4 tools)

| Tool | Description |
|------|-------------|
| `uevr_attach_to_controller` | Attach a USceneComponent to a VR controller (left/right/HMD) with position/rotation offsets. Must be a component address, not an actor — use `uevr_world_components` to find them. |
| `uevr_detach_from_controller` | Detach a component from its motion controller |
| `uevr_list_motion_controllers` | List all current motion controller attachments |
| `uevr_clear_motion_controllers` | Remove all motion controller attachments |

### Timer / Scheduler (4 tools)

| Tool | Description |
|------|-------------|
| `uevr_timer_create` | Create a one-shot or looping timer that executes Lua code after a delay |
| `uevr_timer_list` | List active timers with IDs, delays, remaining time, looping status |
| `uevr_timer_cancel` | Cancel a timer by ID |
| `uevr_timer_clear` | Cancel all active timers |

### Lua Scripting (9 tools)

| Tool | Description |
|------|-------------|
| `uevr_lua_exec` | Execute Lua with full UEVR API access. Persistent state, frame callbacks, timers, async coroutines, module system. |
| `uevr_lua_reset` | Destroy and recreate the Lua state |
| `uevr_lua_state` | Diagnostics: exec count, callback count, timer count, coroutine count, memory |
| `uevr_lua_reload` | Hot-reload a script file without losing state (preserves globals, callbacks, timers) |
| `uevr_lua_globals` | Inspect top-level Lua global variables — names, types, values |
| `uevr_lua_write_script` | Write a .lua file to UEVR scripts dir (with autorun option) |
| `uevr_lua_list_scripts` | List script files |
| `uevr_lua_read_script` | Read script content |
| `uevr_lua_delete_script` | Delete a script file |

### Blueprint / Object Lifecycle (7 tools)

| Tool | Description |
|------|-------------|
| `uevr_spawn_object` | Spawn a new UObject or Actor by class name |
| `uevr_add_component` | Add a component to an actor |
| `uevr_get_cdo` | Get Class Default Object — default field values for a class |
| `uevr_write_cdo` | Write a CDO field — affects all future spawns |
| `uevr_destroy_object` | Destroy an actor |
| `uevr_set_transform` | Set actor location, rotation, scale (partial updates OK) |
| `uevr_list_spawned` | List all MCP-spawned objects with alive/dead status |

### Screenshot (2 tools)

| Tool | Description |
|------|-------------|
| `uevr_screenshot` | Capture from D3D11/D3D12 backbuffer as JPEG. Works when game isn't in front. Handles R10G10B10A2, FP16, BGRA8, RGBA8. Configurable resolution and quality. |
| `uevr_screenshot_info` | Check if D3D capture is initialized and which renderer is in use |

### Diagnostics (4 tools)

| Tool | Description |
|------|-------------|
| `uevr_get_diagnostics` | One-shot crash/debug snapshot: callback health, breadcrumb, plugin log, render metadata, loaded plugins, latest crash dump, runtime map/controller/pawn, UEVR log tail |
| `uevr_get_callback_health` | Per-callback invocation/success/failure counters and last error info |
| `uevr_get_breadcrumb` | Read the persisted breadcrumb file/state written around risky callback boundaries |
| `uevr_get_loaded_plugins` | Inventory UEVR plugin DLLs in global and game-specific plugin folders and show which are currently loaded |

### Property Watch & Snapshot/Diff (9 tools)

| Tool | Description |
|------|-------------|
| `uevr_watch_add` | Watch a field for changes — checks every N ticks, records deltas |
| `uevr_watch_remove` | Remove a watch |
| `uevr_watch_list` | List all watches with current/previous values and change counts |
| `uevr_watch_changes` | Get recent change events across all watches |
| `uevr_watch_clear` | Clear all watches |
| `uevr_snapshot` | Snapshot all field values on an object |
| `uevr_snapshot_list` | List saved snapshots |
| `uevr_diff` | Diff a snapshot against current state — see exactly what changed |
| `uevr_snapshot_delete` | Delete a snapshot |

### World & Spatial Queries (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_world_actors` | List actors in the world with optional class filter |
| `uevr_world_components` | Get all components attached to an actor |
| `uevr_line_trace` | Raycast — returns hit location, normal, distance, actor, component |
| `uevr_sphere_overlap` | Find all actors within a sphere radius |
| `uevr_hierarchy` | Get outer chain, owner, attachment parent/children, class super chain |

### Input Injection (4 tools)

| Tool | Description |
|------|-------------|
| `uevr_input_key` | Simulate keyboard key press/release/tap |
| `uevr_input_mouse` | Simulate mouse button clicks or movement |
| `uevr_input_gamepad` | Override XInput gamepad state (buttons, sticks, triggers) |
| `uevr_input_text` | Type a string of text character by character |

### Material Control (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_material_create_dynamic` | Create a dynamic material instance for parameter modification |
| `uevr_material_set_scalar` | Set scalar parameter (EmissiveIntensity, Roughness, Opacity, etc.) |
| `uevr_material_set_vector` | Set vector/color parameter (BaseColor, EmissiveColor, etc.) |
| `uevr_material_params` | List scalar, vector, and texture parameters on a material |
| `uevr_material_set_on_actor` | Apply a material to an actor's mesh at a given slot |

### Animation Control (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_animation_play_montage` | Play an animation montage on a skeletal mesh |
| `uevr_animation_stop_montage` | Stop a playing montage with blend-out |
| `uevr_animation_state` | Get current animation state, playing montage, anim variables |
| `uevr_animation_set_variable` | Set an animation variable (float, bool, int) on the AnimInstance |
| `uevr_animation_montages` | List available animation montages loaded in memory |

### Physics (6 tools)

| Tool | Description |
|------|-------------|
| `uevr_physics_add_impulse` | Add instant impulse to a component (knockback, launching) |
| `uevr_physics_add_force` | Add continuous force for one frame |
| `uevr_physics_set_simulate` | Enable/disable physics simulation |
| `uevr_physics_set_gravity` | Enable/disable gravity |
| `uevr_physics_set_collision` | Enable/disable collision |
| `uevr_physics_set_mass` | Set mass override |

### Asset Discovery (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_asset_find` | Find a loaded asset by path |
| `uevr_asset_search` | Search loaded assets by name, optionally filtered by type |
| `uevr_asset_load` | Attempt to load or find an asset |
| `uevr_asset_classes` | List loaded asset types/classes |
| `uevr_asset_load_class` | Find or load a UClass by name |

### Deep Discovery (6 tools)

| Tool | Description |
|------|-------------|
| `uevr_subclasses` | Find all subclasses of a base class — discover game-specific types (enemies, items, etc.) |
| `uevr_search_names` | Search all reflected names (classes, properties, functions) — finds types without live instances |
| `uevr_delegates` | Inspect delegate properties and event functions on an object (OnTakeDamage, OnDeath, etc.) |
| `uevr_vtable` | Compare virtual function table against parent class — find overridden C++ functions |
| `uevr_pattern_scan` | Signature scan executable memory for byte patterns with wildcards |
| `uevr_all_children` | Brute-force enumerate all properties and functions across full inheritance chain |

### Function Hooks (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_hook_add` | Hook any UFunction — log, block, or run Lua callbacks. Lua hooks receive context (object, function, args) and can conditionally block. |
| `uevr_hook_remove` | Remove a hook |
| `uevr_hook_list` | List all hooks with call counts |
| `uevr_hook_log` | Get the call log for a hook — who called what and when |
| `uevr_hook_clear` | Clear all hooks |

### ProcessEvent Listener (9 tools)

| Tool | Description |
|------|-------------|
| `uevr_process_event_start` | Start the global ProcessEvent listener — hooks UObject::ProcessEvent to capture all function calls in real time |
| `uevr_process_event_stop` | Stop the listener (hook stays installed for cheap restart) |
| `uevr_process_event_status` | Listener state: whether active, hook installed, unique functions seen, ignore list size |
| `uevr_process_event_functions` | All tracked functions sorted by call count, with search/filter/limit/sort options |
| `uevr_process_event_recent` | Most recent function calls (newest first) — the live stream |
| `uevr_process_event_ignore` | Ignore functions matching a name pattern (substring) |
| `uevr_process_event_ignore_all` | Ignore all currently seen functions — establish a clean baseline |
| `uevr_process_event_clear` | Clear all tracked data (does not affect ignore list) |
| `uevr_process_event_clear_ignored` | Clear the ignore list so all functions are tracked again |

### Macro System (5 tools)

| Tool | Description |
|------|-------------|
| `uevr_macro_save` | Save a named operation sequence with $param placeholders. Persists to disk. |
| `uevr_macro_play` | Execute a macro with parameter substitution and state propagation ($result[N].field references) |
| `uevr_macro_list` | List saved macros |
| `uevr_macro_delete` | Delete a macro |
| `uevr_macro_get` | Get a macro's full definition |

### Event Streaming (1 tool)

| Tool | Description |
|------|-------------|
| `uevr_events_poll` | Long-poll for real-time events (hook fires, watch changes, Lua output) |

### System (4 tools)

| Tool | Description |
|------|-------------|
| `uevr_get_status` | Plugin health via named pipe (works even if HTTP is down) |
| `uevr_get_log` | Recent log entries from ring buffer |
| `uevr_clear_log` | Clear the log |
| `uevr_help` | Agent navigation guide |

## Usage examples

Once connected, you can ask your agent things like:

- *"What's my current health and position?"*
- *"Find the type that manages enemies and show me its fields."*
- *"Set my health to max."*
- *"Take a screenshot and tell me what you see."*

But the real power is in open-ended requests. You don't need to know the game's internals — the agent will figure it out:

- *"Reverse-engineer the damage formula. Snapshot my health, take damage, diff to see what changed, then hook the damage function to log every call."*
- *"Make the player glow red. Find the mesh, get its material, create a dynamic instance, crank up the emissive color."*
- *"Figure out which animation plays when I dodge, then write a Lua script that plays it on a timer for testing."*
- *"Find every enemy within 10 meters of me, read their health, and set them all to 1 HP."*
- *"Attach the sword to my right VR hand with a comfortable grip angle."*
- *"Use the ProcessEvent listener to figure out what happens when I open a chest, then hook those functions."*
- *"Make the world feel twice as big — adjust the VR scale."*
- *"Dump this game as a buildable UE4 project — I want to read the source in my editor."*
- *"Launch this DX12 game through UEVRJ's RenderDoc path, capture a frame, validate the `.rdc`, and show me the thumbnail."*

### End-to-end: dump a game you've never touched before

The shortest path from "I installed the game" to "I have a UE4 editor project on disk":

```
# 1. Locate it
uevr_find_steam_game { query: "Robo" }
→ [{gameName: "RoboQuest", exe: "E:\\SteamLibrary\\steamapps\\common\\RoboQuest\\RoboQuest\\Binaries\\Win64\\RoboQuest-Win64-Shipping.exe", libraryPath: "E:\\SteamLibrary"}]

# 2. Launch + inject (or attach to a running instance)
uevr_setup_game       { gameExe: "<exe from step 1>" }
uevr_wait_for_plugin  { timeoutMs: 30000 }
uevr_is_ready
→ { ready: true, backendLoaded: true, pluginLoaded: true, httpAlive: true }

# 3. Dump
uevr_dump_usmap       { outPath: "E:\\out\\roboquest.usmap" }
uevr_dump_ue_project  { outDir:  "E:\\out\\RoboQuestMirror",
                        projectName: "RoboQuestMirror",
                        engineAssociation: "4.26" }

# 4. Verify
uevr_validate_usmap   { usmapPath: "E:\\out\\roboquest.usmap" }
→ { headerOk: true, fullParse: "ok", parsedStructCount: 5620, parsedEnumCount: 1314 }

uevr_probe_status
→ 9/9 probes resolved

# 5. Cleanup
uevr_stop_game
```

The output at `E:\out\RoboQuestMirror\` is a ready-to-open UE4 project. Open it in the UE editor, build, and you have IntelliSense / go-to-definition / decompile-Blueprints against the game's real types.

### End-to-end with compile validation

For "I want the project AND proof it builds":

```
# 1-3 as before, then validate:
uevr_compile_check {
    emittedDir:   "E:\\out\\RoboQuestMirror",
    hostUproject: "C:\\Users\\me\\Documents\\Unreal Projects\\Scratch\\Scratch.uproject"
}
→ { ok: true, totalErrors: 0, totalWarnings: 12, srcHeaders: 4805,
    skippedEngineConflicts: 1988, elapsedSec: 11.5,
    engineRoot: "E:\\UnrealEngine\\UE_5.5" }
```

The host `.uproject` is any blank UE5 C++ scratch project — create one in 60 seconds via File → New → Games → Blank → C++. The compile-check copies the emitted headers in, runs UnrealBuildTool, and reports what fails. Rerun after edits; subsequent runs reuse the engine-type scan cache.

### When UEVR crashes on a specific game (Dumper-7 fallback)

```
# Launch game, wait for main menu (memory stabilizes above a threshold)
# Don't call uevr_setup_game — it would crash the game.

# Inject Dumper-7 instead
uevr_dumper7_run { pid: <running-game-pid>, gameName: "MyGame-Win64-Shipping", timeoutMs: 180000 }
→ { sdk: { Dir: "C:\\Dumper-7\\MyGame-...", UsmapCount: 1, HppCount: 910 } }

# Convert its USMAP into our UE project format
uevr_dump_uht_from_usmap {
    usmapPath: "C:\\Dumper-7\\MyGame-...\\Mappings\\<version>-MyGame.usmap",
    outDir:    "E:\\out\\MyGameMirror",
    projectName: "MyGameMirror",
    moduleName:  "MyGame"
}
```

### Reverse engineering workflow: snapshot → action → diff

The snapshot/diff system makes reverse engineering fast. Instead of manually comparing field values:

```
1. uevr_snapshot("0x...")          → capture all 200+ fields
2. (do something in-game)
3. uevr_diff(snapshotId)           → see exactly which fields changed

Result: "MaxWalkSpeed: 600.0 → 300.0, bIsCrouched: false → true"
```

Combine with function hooks for complete visibility:

```
1. uevr_hook_add("Actor", "ReceiveDamage", "log")   → start logging
2. (take damage in-game)
3. uevr_hook_log(hookId)                              → see every call with caller info
```

### ProcessEvent listener: discover what the game calls

The ProcessEvent listener captures every Blueprint/native function call in real time — equivalent to UEVR's Developer tab:

```
1. uevr_process_event_start                         → start listening
2. uevr_process_event_functions                      → see all functions sorted by call count
3. uevr_process_event_ignore("Tick")                 → filter out noisy per-frame functions
4. uevr_process_event_ignore_all                     → ignore everything seen so far
5. uevr_process_event_clear                          → reset, then do an action in-game
6. uevr_process_event_functions(search: "Damage")    → see only new functions that fired
7. uevr_process_event_recent                         → live stream of the most recent calls
```

This is the fastest way to find game-specific functions. Ignore the noise, perform an action, and see exactly which functions fire.

### Live Lua scripting

The Lua engine persists between calls and supports frame callbacks and timers:

```lua
-- Attach sword to VR right hand
local pawn = uevr.api:get_local_pawn(0)
local mesh = pawn:get_property("Mesh")
local state = uevr.uobject_hook:get_or_add_motion_controller_state(mesh)
state:set_hand(1)  -- right hand
state:set_permanent(true)

-- Run code every frame
mcp.on_frame(function(dt)
    local pos = uevr.api:get_local_pawn(0):get_property("Location")
    if pos.z < -1000 then
        mcp.log("Player fell below kill plane!")
    end
end)

-- Delayed execution
mcp.set_timer(5.0, function()
    mcp.log("5 seconds elapsed!")
end, false)  -- false = one-shot

-- Repeating timer
mcp.set_timer(1.0, function()
    mcp.log("Tick!")
end, true)  -- true = repeating

-- Async coroutines with mcp.wait()
mcp.async(function()
    mcp.log("Starting patrol...")
    mcp.wait(2.0)  -- resume after 2 seconds
    mcp.log("Moving to waypoint...")
    mcp.wait_until(function() return get_health() < 50 end)
    mcp.log("Health dropped below 50!")
end)

-- Module system (require)
local utils = require("my_utils")  -- loads scripts/my_utils.lua
utils.do_something()
```

### Spatial reasoning

Line traces and overlap tests let the agent understand 3D space:

```
uevr_line_trace(
  start={x:0, y:0, z:100},
  end={x:1000, y:0, z:100}
)
→ {hit: true, location: {x:342, y:0, z:100}, actor: "0x...", distance: 342.0}

uevr_sphere_overlap(
  center={x:0, y:0, z:0},
  radius=500
)
→ {actors: [{address: "0x...", class: "Enemy", name: "Goblin_3"}, ...], count: 4}
```

### Macros for reusable operations

Save common operation sequences as parameterized macros:

```
uevr_macro_save("kill_actor", [
  {"type": "write_field", "address": "$target", "fieldName": "Health", "value": 0}
])

uevr_macro_play("kill_actor", {target: "0x12345678"})
```

## HTTP API

All endpoints are accessible directly via HTTP at `http://localhost:8899/api`. Call `GET /api` for the full endpoint index. The MCP server is a thin wrapper — you can also use curl, a browser, or any HTTP client.

The web dashboard (if present in a `web/` folder next to the DLL) is served at `http://localhost:8899/`.

## Testing

### Unit tests (no game required)

The C# unit tests verify tool registration, parameter signatures, and HTTP contract correctness via reflection — no running game needed.

```bash
cd tests/UevrMcpTests
dotnet test
```

### Integration tests (live game required)

The Python integration tests hit the plugin's HTTP API against a running game. Install dependencies and run with pytest:

```bash
pip install pytest requests
pytest tests/integration/ -v
```

The tests use `http://localhost:8899` by default. Set `UEVR_MCP_API_URL` to override. Tests that require a game connection are marked with the `require_game` fixture and will skip if the plugin isn't reachable.

## Plugin development workflow

Iterating on the plugin itself (not scripts — actual C++ changes) goes through `uevr_plugin_rebuild` + `uevr_plugin_stage` + a game restart. UEVR loads plugin DLLs once per process and holds the file handle for the life of the session, so true in-process hot-reload needs a proxy-shim architecture that's not yet shipped (see [Roadmap](#roadmap) #4).

```
# 1. Build — from MCP, no shell required
uevr_plugin_rebuild
→ { ok: true, dll: "E:\\Github\\uevr-mcp\\plugin\\build\\Release\\uevr_mcp.dll",
    size: 8425472, mtime: "2026-04-22T18:14:02Z", logTail: "..." }

# 2a. If the game is NOT running — install directly
uevr_install_plugin    { gameName: "MyGame-Win64-Shipping" }

# 2b. If the game IS running and the DLL is file-locked — stage a pending copy
uevr_plugin_stage      { gameExe: "...\\MyGame-Win64-Shipping.exe" }
→ { staged: "%APPDATA%\\UnrealVRMod\\MyGame-...\\plugins\\uevr_mcp.dll.pending",
    note: "Rename .pending to .dll after game exits to activate." }

# 3. Check the state — shows build vs installed vs pending mtimes and
#    whether the running plugin is still responding on :8899
uevr_plugin_info       { gameExe: "...\\MyGame-Win64-Shipping.exe" }
→ { built:     { path: ..., mtime: "2026-04-22T18:14:02Z" },
    installed: { path: ..., mtime: "2026-04-22T18:14:05Z" },
    pending:   null,
    pluginReachable: true,
    engineVersionHint: "UE4",
    needsInstall: false }

# 4. Restart the game — the new DLL loads on next inject
uevr_stop_game         { gameExe: "..." }
uevr_setup_game        { gameExe: "..." }
```

**`uevr_plugin_rebuild` under the hood:** shells to `cmake`, first resolving it from `where cmake` then falling back to the Visual Studio 2022 CMake install path. Configures the build dir with `cmake -S plugin -B plugin/build` if `CMakeCache.txt` is missing. Then runs `cmake --build plugin/build --config Release --target uevr_mcp`. 10-minute default timeout (generous — full rebuild from clean is ~2 min on a modern machine, incremental is ~10 s).

**`uevr_plugin_stage` rationale:** when UEVR is loaded into a game, `uevr_mcp.dll` is held open by the process and Windows refuses to overwrite it. Copying the fresh build as `uevr_mcp.dll.pending` sidesteps this — on next game launch, either rename the file manually or let `uevr_install_plugin` do it. The staged file is preserved across builds.

**`uevr_plugin_info` telemetry fields:**

| Field | Meaning |
|---|---|
| `built.mtime` | When `plugin/build/Release/uevr_mcp.dll` was last written |
| `installed.mtime` | When the DLL under `%APPDATA%\UnrealVRMod\<Game>\plugins\` was last written |
| `pending.path` | Presence indicates a staged build waiting to activate on next launch |
| `pluginReachable` | Whether the live plugin is responding on `http://127.0.0.1:8899` (2s timeout) |
| `engineVersionHint` | "UE4" / "UE5" from running game's probe diagnostics, or `null` if plugin unreachable |
| `needsInstall` | `true` when `built.mtime > installed.mtime` — the shortcut for "should I deploy?" |

## Supported games

Any Unreal Engine game that UEVR supports. The core reflection tools work universally — they inspect whatever the game has loaded. Per-game differences are just different class names and field layouts, which the agent discovers through exploration.

Engine versions tested with:
- Unreal Engine 4.22–4.27 titles (float vectors, single-precision) — live UEVR pipeline
- Unreal Engine 5.x titles via emit-side — USMAP → UHT project → UE 5.5 compile-check passes through UHT
- `engineVersionHint` probe auto-reports "UE4" vs "UE5" based on presence of `DoubleProperty` in GUObjectArray

### Game-by-game dump pipeline

| Game | UE ver | Protection | Renderer | Pipeline | Dump result |
|---|---|---|---|---|---|
| RoboQuest | 4.26 | — | DX11 | live `uevr_dump_*` | 5620 structs + 1314 enums, 15 modules, 1927 headers |
| Severed Steel | 4.26 | — | DX11 | live + stability mode | 6194 structs + 1261 enums, 30 modules, 1649 headers |
| Hogwarts Legacy | 4.27 | Denuvo | DX12 | live `uevr_dump_*` | **16407 structs + 2626 enums, 141 modules, 12460 headers** |
| Stellar Blade | 4.26.2 | Denuvo | DX12 | Dumper-7 + `uht_from_usmap` | 6946 structs + 1837 enums, 8783 headers |

Denuvo / DX12 / AAA-size by themselves are **not** blockers for the live pipeline — Hogwarts Legacy validates that combo cleanly. The Stellar Blade fallback is specific to that title's custom UE4.26.2 fork, which crashes UEVR's `FViewport::GetRenderTargetTexture` PointerHook on the first rendered frame (see [Troubleshooting](#troubleshooting--game-specific-notes) for the signature).

### Emit-side UE 5.5 validation

`uevr_compile_check` validates the full emit pipeline against an installed UE 5.5 toolchain. Latest run — RoboQuest USMAP (Dumper-7 source) → UHT project with `engineAssociation: "5.5"` → compile inside a blank UE 5.5 C++ project:

| Metric | Value |
|---|---|
| Emitted UHT headers | 6927 |
| Headers skipped as engine-name collisions | 4489 |
| Headers copied to the host project | 2438 |
| UHT parses clean | 2402 |
| Emitter-defect hits | 36 (struct-vs-class super misclassification) |
| UBT wall time | ~12 s end-to-end (UHT parse + module compile + link) |

A single `UnrealEditor-SDK.dll` is produced on a subset emit, confirming the generated headers survive both UHT's reflection pass and MSVC's C++ compile.

## Troubleshooting / game-specific notes

### Game crashes ~7s after `uevr_setup_game`

Check `%APPDATA%\UnrealVRMod\<GameName>\log.txt` for the last line. Two common signatures:

**Signature A: UEVR stereo render hook crash** — last lines are:
```
FFakeStereoRenderingHook.cpp:2248 Hooking FViewport::GetRenderTargetTexture
FFakeStereoRenderingHook.cpp:2260 UGameViewportClient::Draw called for the first time.
FFakeStereoRenderingHook.cpp:2050 FViewport::GetRenderTargetTexture called!
```
…followed by process exit. Something in this specific game's render pipeline is incompatible with UEVR's PointerHook. Known on Stellar Blade. Config-level `uevr_write_stability_config` does not help. Workaround: use the Dumper-7 fallback path above.

**Signature B: "Last chance encountered for hooking" retry-loop crash** — last lines cycle:
```
Last chance encountered for hooking
Sending rehook request for D3D
Hooking D3D12
```
…for a few iterations then die. UEVR's D3D monitor thread is re-hooking every 10s and racing with the game's live render. Call `uevr_suppress_d3d_monitor` immediately after `uevr_setup_game` to freeze the monitor — works on most games that hit this.

### "Process not running" error from `uevr_setup_game`

On Steam games with a launcher-stub pattern (bootstrap `<Game>.exe` at the root spawns the real game at `<Subfolder>/Binaries/Win64/<Game>.exe`), both processes share an image name. The setup tool picks the exact `MainModule.FileName` match first, but if you see this error ensure you're passing the **inner** exe path, not the root launcher.

### "Plugin DLL is locked by another process"

A previous setup injected `uevr_mcp.dll` into a now-crashed process and Windows is holding the file handle. `taskkill /F /PID <pid>` the stuck process and retry.

### `uevr_dump_reflection_json` times out / returns transport error

Two scenarios:
1. **First heavy dump** — initial probe discovery adds per-batch overhead. The reflection walk's first batch can take several seconds. Subsequent batches in the same session hit cached probe offsets. If you see `Game thread request timed out`, bump the plugin's per-batch timeout in `plugin/src/routes/explorer_routes.cpp` (default 20s with methods, 10s without).
2. **`includeMethods=true`** — method enumeration per class is ~10x more work than fields alone. `uevr_dump_sdk_cpp` and `uevr_dump_usmap` both default to `methods=false` (methods emit as comments only in the SDK and USMAP doesn't carry them). Only pass `includeMethods=true` if you explicitly need them.

### Probes stay `undiscovered`

Game's UE build puts the target field outside our candidate offset range. Extend `kUClassRange` / `kShortRange` / `kFieldRange` / `kUClassInterfacesRange` in `plugin/src/reflection/property_probes.cpp` — each is a small `int32_t[]` of candidate offsets to try. Rebuild, re-inject, re-probe.

### USMAP parses with our validator but FModel rejects it

FModel's parser is sometimes stricter about compression. Re-dump with `compression: "none"` (the default). If you passed `compression: "brotli"`, FModel may not support brotli in your build — the jmap-compatible path never compresses.

## Roadmap

Shipped in recent commits:

- `uevr_dump_project` one-call smart dump with live-first / Dumper-7 fallback decision caching
- Phase 4C CDO default values rendered as `= FName(TEXT("..."))` / `= 0.5f` initializers in emitted UHT headers
- Dumper-7 `GObjects-Dump-WithProperties.txt` offset backfill into `uevr_dump_uht_from_usmap` output
- Per-game profile persistence in `~/.uevr-mcp/state.json`
- `uevr_build_info` + `uevr_diff_usmap` for supply-chain + patch-diff workflows
- UE5 probe range widening (UClass to 0x160, FProperty to 0xB0) + `engineVersionHint` in `uevr_probe_status`
- Kismet bytecode preview — `UFunction::Script` probe + EX_* opcode mnemonic list in method JSON and emitter output
- `uevr_compile_check` — runs real UnrealBuildTool + UHT against emitted headers and reports per-file pass/fail, with automatic engine-type collision filtering
- `uevr_plugin_rebuild` / `uevr_plugin_stage` / `uevr_plugin_info` — plugin dev workflow

Still open, ordered by impact:

1. **Struct-vs-class super-kind inference.** The compile-check still surfaces ~36 emitter defects on RoboQuest where game code inherits from engine structs but the emitter emitted it as a class. Fix: during engine-header scan, track kind per type (class / struct / interface), then match the declared super's kind and flip emission accordingly.

2. **Single-header drop-in SDK.** Alongside the multi-file `uevr_dump_uht_sdk` output, emit one `sdk.hpp` that concatenates everything. Matches what [Dumper-7](https://github.com/Encryqed/Dumper-7) ships. Useful for quick drop-in C++ mods that don't want to build a full editor project.

3. **Full Kismet decompiler.** Current bytecode preview is opcode mnemonics only. Integrating [KismetKompiler](https://github.com/TGE-TJ/KismetKompiler) or [Kismet Analyzer](https://github.com/trumank/kismet-analyzer) could produce real method bodies in the UHT output instead of mnemonic comments.

4. **True live plugin hot-reload.** Current `uevr_plugin_rebuild` + `_stage` requires a game restart to pick up new builds. A proxy-shim approach — thin `uevr_mcp.dll` that `LoadLibrary`s the real impl and supports re-loading — would eliminate the restart.

5. **MCP Prompts for common workflows.** Define templates like "Bring up VR for Game X", "Find the player's health field", "Snapshot → do action → diff" that agents can invoke by name.

6. **Runtime-patch UEVR's FFakeStereoRenderingHook** so the live pipeline works on Stellar Blade and similar. Find the hook-installation function via AoB scan of the "is stereo enabled called!" / "UGameViewportClient::Draw called for the first time." string xrefs, NOP the stereo-hook installation. Nontrivial but would eliminate the last known gap vs jmap.

## License

MIT
