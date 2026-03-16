# UEVR-MCP Agent Navigation Guide

> For the full comprehensive reference (all mod values, Lua API, rendering pipeline, etc.), see **UEVR_GUIDE.md** in the project root.

## Overview

UEVR-MCP lets you inspect and manipulate Unreal Engine game state at runtime via the UEVR plugin API. The game must be running with UEVR injected and the uevr_mcp plugin loaded.

## Getting Started

1. **Check status**: `uevr_get_status` — confirms plugin is loaded, shows game process and VR runtime
2. **Get player**: `uevr_get_player` — returns player controller and pawn addresses
3. **Explore from there**: Use `uevr_summary` on an address for a quick field overview, then drill into specific fields

## Exploration Flow

```
uevr_search_objects("PlayerController")     → find objects by name
uevr_search_classes("Character")            → find UClass types
uevr_get_type("/Script/Engine.Character")   → see all fields and methods (no instance needed)
uevr_summary("0x...")                       → quick one-line-per-field scan of a live object
uevr_inspect_object("0x...")                → full field values + method list
uevr_read_field("0x...", "Health")          → read a single field, follows object refs
uevr_call_method("0x...", "GetActorLocation") → call a 0-param getter
```

## Key Patterns

### Finding Objects
- `uevr_search_objects(query)` — searches GUObjectArray full names (slow on large games, use specific queries)
- `uevr_search_classes(query)` — searches only UClass objects
- `uevr_objects_by_class(className)` — find all instances of a specific class (fast)
- `uevr_get_player` — shortcut for player controller + pawn
- `uevr_get_singletons` — list common singleton objects (GameEngine, GameInstance, World, GameMode, etc.)
- `uevr_get_singleton(typeName)` — find first live instance of a class

### Reading State
- `uevr_summary(address)` — compact overview, always start here
- `uevr_read_field(address, fieldName)` — single field with full detail
- `uevr_inspect_object(address)` — everything at once (large output)
- `uevr_call_method(address, methodName)` — call any 0-param method
- `uevr_get_array(address, fieldName, offset, limit)` — paginated array reading
- `uevr_get_camera` — camera position, rotation, FOV

### Chain Queries (Most Powerful Tool)
Navigate entire object graphs in a single request:
```
uevr_chain("0x...", [
  {"type": "field", "name": "CharacterMovement"},
  {"type": "method", "name": "GetOwner"},
  {"type": "collect", "fields": ["Health"], "methods": ["GetActorLocation"]}
])
```
Step types:
- `field` — follow an ObjectProperty to the referenced object
- `method` — call a 0-arg getter, follow the returned object
- `array` — expand an array field, subsequent steps apply to ALL elements
- `filter` — keep only objects matching a condition (`method`/`field` + `value`)
- `collect` — terminal step: read multiple fields/methods from every object in the set

### Raw Memory Access
- `uevr_read_memory(address, size)` — hex dump with ASCII sidebar (max 8192 bytes)
- `uevr_read_typed(address, type, count, stride)` — read typed values: u8, i8, u16, i16, u32, i32, u64, i64, f32, f64, ptr

### Modifying State
- `uevr_write_field(address, fieldName, value)` — set a field value
- `uevr_invoke_method(address, methodName, args)` — call a method with arguments
- `uevr_set_cvar(name, value)` — change a console variable
- `uevr_exec_command(command)` — run a console command
- `uevr_set_position(x, y, z)` — teleport the player (partial update OK)
- `uevr_set_health(value)` — set player health (searches common field names)

### Batch Operations
- `uevr_batch([{type:"read_field", address, fieldName}, ...])` — multiple ops in one game-thread tick

### Game & System Info
- `uevr_get_game_info` — game exe path, directory, VR runtime, uptime (works via pipe fallback)

### Lua Execution (Live Coding)
Execute Lua code directly in the game process with full UEVR API access:
```
uevr_lua_exec("return 1 + 1")                             → {success:true, result:2}
uevr_lua_exec("return uevr.api.get_local_pawn():get_full_name()")  → pawn name
uevr_lua_exec("print('hello'); print('world')")           → output: ["hello", "world"]
```

The Lua state persists between calls — set globals, define functions, register frame callbacks:
```
uevr_lua_exec("counter = (counter or 0) + 1; return counter")   → increments each call
uevr_lua_exec("mcp.on_frame(function(dt) ... end)")              → runs every game tick
uevr_lua_exec("mcp.clear_callbacks()")                           → remove all frame callbacks
```

Available Lua APIs:
- `uevr.api` — `find_uobject(name)`, `get_engine()`, `get_player_controller(idx)`, `get_local_pawn(idx)`, `spawn_object(cls, outer)`, `add_component_by_class(actor, cls)`, `execute_command(cmd)`
- `UObject` — `:get_address()`, `:get_fname()`, `:get_full_name()`, `:get_class()`, `:get_outer()`, `:is_a(cls)`, `:get_bool_property(name)`, `:set_bool_property(name, val)`
- `UClass` — `:get_class_default_object()`, `:get_objects_matching()`, `:get_first_object_matching()`
- `UStruct` — `:get_super()`, `:find_function(name)`, `:find_property(name)`, `:get_child_properties()`
- `uevr.uobject_hook` — `.exists(obj)`, `.get_or_add_motion_controller_state(obj)`, `.get_objects_by_class(cls)`
- `uevr.vr` — `.is_runtime_ready()`, `.get_pose(idx)`, `.recenter_view()`, `.set_aim_method(n)`, `.trigger_haptic_vibration(...)`
- `uevr.console` — `.find_variable(name)`, `.find_command(name)`
- `mcp` — `.on_frame(fn)`, `.remove_callback(id)`, `.clear_callbacks()`, `.log(msg)`

Script management:
- `uevr_lua_write_script(filename, content, autorun?)` — write to UEVR scripts dir
- `uevr_lua_list_scripts()` — list .lua files
- `uevr_lua_read_script(filename)` — read content
- `uevr_lua_delete_script(filename)` — delete

Other Lua tools:
- `uevr_lua_reset()` — destroy and recreate Lua state
- `uevr_lua_state()` — diagnostics (exec count, callback count, memory)

### Blueprint Editing (Object Lifecycle)
Spawn, modify, and destroy objects:
```
uevr_spawn_object("/Script/Engine.PointLight")             → spawn a new light actor
uevr_add_component("0x...", "/Script/Engine.StaticMeshComponent") → add component
uevr_set_transform("0x...", location={x:0,y:0,z:500})     → move actor
uevr_destroy_object("0x...")                                → destroy actor
```

Class Default Objects (affect all future spawns):
```
uevr_get_cdo("/Script/MyGame.Enemy")                       → see default field values
uevr_write_cdo("/Script/MyGame.Enemy", "MaxHealth", 999)   → all new enemies get 999 HP
```

Tracking:
- `uevr_list_spawned()` — list all MCP-spawned objects with alive/dead status

## VR Controls

- `uevr_vr_status` — check VR runtime, HMD status
- `uevr_vr_poses` — live tracking data (HMD + controllers)
- `uevr_vr_settings` / `uevr_set_vr_setting` — read/write VR mod settings
- `uevr_recenter` — recenter the VR view
- `uevr_haptics(hand, duration, amplitude)` — trigger controller vibration
- `uevr_save_config` / `uevr_reload_config` — persist VR settings

## Common UE Types

| Type | Description |
|------|-------------|
| `PlayerController` | Input handling, camera, HUD |
| `Pawn` / `Character` | The player's physical representation |
| `GameMode` | Game rules and state |
| `GameState` | Replicated game state |
| `PlayerState` | Per-player replicated state |
| `GameInstance` | Persistent across level loads |

## Property Types

| FProperty Type | JSON Representation |
|---------------|-------------------|
| BoolProperty | `true` / `false` |
| FloatProperty, DoubleProperty | number |
| IntProperty, ByteProperty, Int64Property | integer |
| NameProperty, StrProperty | string |
| ObjectProperty | `{"address":"0x...", "className":"..."}` or null |
| StructProperty | nested JSON object (e.g. `{"x":1, "y":2, "z":3}`) |
| ArrayProperty | JSON array |
| EnumProperty | `{"enumType":"...", "value": N}` |

## Tips

- **Start with summary**, not inspect — it's much lighter
- **Object addresses are volatile** — they can change between levels or when objects are garbage collected
- **BoolProperty** uses bitmask packing — always use the API, never read raw memory
- **StrProperty (FString)** is `TArray<wchar_t>` internally — the API handles conversion to UTF-8
- **UE5 uses doubles** for Vector/Rotator — the plugin auto-detects this
- **Console commands** execute on the game thread — they're safe but some may cause hitches
- **Batch operations** run in a single game-thread tick — use for atomic multi-field reads/writes
