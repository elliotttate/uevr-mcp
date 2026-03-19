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
- **Works across games.** Same 137 tools work on any Unreal Engine game that UEVR supports.

## Setup

### Prerequisites

- [UEVR](https://github.com/praydog/UEVR) installed and configured for your game
- [.NET 9+ SDK](https://dotnet.microsoft.com/download/dotnet/9.0) for the MCP server
- [CMake 3.21+](https://cmake.org) and Visual Studio 2022 Build Tools to compile the plugin (or use a pre-built release)

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

**`mcp-server/`** — A standalone .NET console app that speaks MCP over stdio. Translates tool calls into HTTP requests, falling back to the named pipe for status/log/game-info when HTTP is unavailable. Diagnostics tools map directly to the HTTP snapshot and per-surface diagnostics routes.

## 137 MCP Tools

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

## Supported games

Any Unreal Engine game that UEVR supports. The core reflection tools work universally — they inspect whatever the game has loaded. Per-game differences are just different class names and field layouts, which the agent discovers through exploration.

Games tested with:
- Unreal Engine 4.x titles (float vectors, single-precision)
- Unreal Engine 5.x titles (double vectors, Nanite, Lumen)

## License

MIT
