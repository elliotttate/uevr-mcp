# UEVR-MCP Comprehensive Guide

> Reference guide for AI agents working with UEVR via the MCP plugin. Covers VR injection, object manipulation, configuration, input, and the full plugin/Lua API.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [VR Camera & Stereo Rendering](#2-vr-camera--stereo-rendering)
3. [UObjectHook & Motion Controllers](#3-uobjecthook--motion-controllers)
4. [All Configuration / Mod Values](#4-all-configuration--mod-values)
5. [Console Commands & CVars](#5-console-commands--cvars)
6. [VR Input System](#6-vr-input-system)
7. [Lua API Reference](#7-lua-api-reference)
8. [C++ Plugin API Reference](#8-c-plugin-api-reference)
9. [Game Profiles & Persistence](#9-game-profiles--persistence)
10. [MCP Tool Quick Reference](#10-mcp-tool-quick-reference)
11. [Common Recipes](#11-common-recipes)

---

## 1. Architecture Overview

```
┌─────────────┐     ┌──────────────┐     ┌──────────────────────┐
│  VR Runtime  │◄───►│    UEVR      │◄───►│  Unreal Engine Game  │
│ OpenXR/OpenVR│     │  (injected)  │     │  (running process)   │
└─────────────┘     └──────┬───────┘     └──────────────────────┘
                           │
                    ┌──────┴───────┐
                    │  UEVR-MCP    │
                    │  Plugin DLL  │
                    │  (HTTP+Pipe) │
                    └──────┬───────┘
                           │
                    ┌──────┴───────┐
                    │  .NET MCP    │
                    │  Server      │
                    │  (stdio)     │
                    └──────┬───────┘
                           │
                    ┌──────┴───────┐
                    │  Claude Code │
                    │  (AI Agent)  │
                    └──────────────┘
```

UEVR injects into an Unreal Engine game process and:
- Hooks the stereo rendering pipeline to render in VR
- Hooks UObject creation/destruction for object tracking
- Provides Lua scripting and C++ plugin APIs
- Exposes ~70 configurable mod values

The UEVR-MCP plugin runs inside UEVR, providing HTTP (port 8899) and named pipe interfaces that the .NET MCP server proxies to Claude Code.

---

## 2. VR Camera & Stereo Rendering

### Rendering Methods

| Value | Name | Description |
|-------|------|-------------|
| 0 | **Native Stereo** | Uses UE's built-in stereo rendering. Best quality, but not all games support it. |
| 1 | **Synchronized Sequential** | Renders both eyes sequentially in sync. Good compatibility. |
| 2 | **Alternating/AFR** | Alternates left/right eye each frame. Most compatible, but can ghost. |

Set via: `uevr_set_vr_setting` with key `RenderingMethod`

### Synchronization Stages

| Value | Name | When |
|-------|------|------|
| 0 | Early | Before engine tick |
| 1 | Late | After engine tick |
| 2 | Very Late | Just before rendering |

### Camera Attachment to Pawn

UEVR's VR camera is driven by the `CalculateStereoViewOffset` hook. The camera follows the pawn's view via `PlayerCameraManager::ViewTarget`. For the VR view to track the pawn properly:

1. The `ViewTarget.Target` must point to the pawn
2. The pawn must have a camera component (or UEVR uses the pawn's location + eye height)
3. UEVR's `AimMethod` and `MovementOrientation` control how the view/movement relate to the HMD

**Key mod values for camera:**
- `CameraForwardOffset` / `CameraRightOffset` / `CameraUpOffset` — shift camera position (-4000 to 4000)
- `WorldScale` — scale the entire world (0.01 to 10, default 1.0)
- `DepthScale` — stereo depth scale (0.01 to 1.0)
- `DecoupledPitch` — decouple head pitch from game camera rotation
- `LerpCameraPitch` / `LerpCameraYaw` / `LerpCameraRoll` — smooth camera transitions
- `LerpCameraSpeed` — interpolation speed (0.01 to 10)

### Rendering Pipeline Hook Order

1. `on_pre_engine_tick()` — Update HMD state, process input
2. `on_pre_calculate_stereo_view_offset()` — Modify view location/rotation
3. `CalculateStereoViewOffset()` — Engine calculates stereo eye positions
4. `on_post_calculate_stereo_view_offset()` — Post-process view
5. `on_pre_viewport_client_draw()` — Before viewport rendering
6. Render frame
7. `on_present()` — Submit frame to VR runtime

---

## 3. UObjectHook & Motion Controllers

UObjectHook is UEVR's system for tracking UObject lifetimes and attaching scene components to VR controllers.

### Activation

UObjectHook must be activated before use:
- Via UEVR GUI: Runtime → UObjectHook → Enable
- Via config: `EnabledAtStartup` = true
- Via MCP plugin: Called automatically in `on_initialize()`
- Via Lua: `UEVR_UObjectHook.activate()`

### Motion Controller States

Attach any `USceneComponent` to track a VR controller:

```lua
-- Lua example
local state = UEVR_UObjectHook.get_or_add_motion_controller_state(mesh_component)
state:set_hand(0)           -- 0=LEFT, 1=RIGHT, 2=HMD
state:set_permanent(true)   -- Persist across sessions
state:set_location_offset(Vector3f.new(0, 0, 0))
state:set_rotation_offset(quaternion)
```

### Hand Enum

| Value | Hand |
|-------|------|
| 0 | Left |
| 1 | Right |
| 2 | HMD |

### Allowed Attachment Bases

Components can be attached from these root objects:
- **Acknowledged Pawn** — the player character
- **Player Controller** — input handler
- **Camera Manager** — camera system
- **World** — global world object

### Object Lifetime Tracking

UObjectHook hooks `UObjectBase::AddObject` and the UObject destructor to maintain a live set of all objects. This enables:
- `UObjectHook::exists(obj)` — validate an object is still alive
- `get_objects_by_class(class)` — find all instances of a class
- Automatic cleanup when objects are destroyed

### Persistent States

Motion controller attachments are serialized to JSON and saved per-game:
```
AppData/UnrealVRMod/{game_exe}/uobjecthook/persistent_states/
```

Each state saves: component path, hand, location_offset, rotation_offset, permanent flag.

### Attachment Lerp

Smooth attachment transitions:
- `AttachLerpEnabled` (default: true)
- `AttachLerpSpeed` (0.01 to 30, default: 15)

---

## 4. All Configuration / Mod Values

Set via `uevr_set_vr_setting` with `key` and `value` fields, or use the named settings (snapTurnEnabled, aimMethod, etc.).

### Rendering

| Key | Type | Range | Default | Description |
|-----|------|-------|---------|-------------|
| `RenderingMethod` | int | 0-2 | 0 | 0=Native Stereo, 1=Synced Sequential, 2=Alternating/AFR |
| `SyncedSequentialMethod` | int | 0-1 | 1 | 0=Skip Tick, 1=Skip Draw |
| `SynchronizationMode` | int | 0-2 | 2 | 0=Early, 1=Late, 2=Very Late |
| `ExtremeCompatibilityMode` | bool | | false | Maximum compatibility mode |
| `UncapFramerate` | bool | | true | Remove framerate cap |
| `2DScreenMode` | bool | | false | Disable VR, show 2D screen |

### Visual Fixes

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `DisableBlurWidgets` | bool | true | Remove blur from UMG widgets |
| `DisableHDRCompositing` | bool | true | Disable HDR compositing |
| `DisableHZBOcclusion` | bool | true | Disable hierarchical Z-buffer occlusion |
| `DisableInstanceCulling` | bool | true | Disable instance culling |
| `GhostingFix` | bool | false | Fix ghosting artifacts |
| `NativeStereoFix` | bool | false | Fix native stereo issues |
| `NativeStereoFixSamePass` | bool | true | Same-pass native stereo fix |
| `DesktopRecordingFix_V2` | bool | true | Fix desktop mirror for recording |
| `GrowRectangleForProjectionCropping` | bool | false | Expand render rect for projection |

### Camera

| Key | Type | Range | Default | Description |
|-----|------|-------|---------|-------------|
| `CameraForwardOffset` | float | -4000 to 4000 | 0 | Forward camera offset |
| `CameraRightOffset` | float | -4000 to 4000 | 0 | Right camera offset |
| `CameraUpOffset` | float | -4000 to 4000 | 0 | Up camera offset |
| `CameraFOVDistanceMultiplier` | float | 0 to 1000 | 0 | FOV-based distance scaling |
| `WorldScale` | float | 0.01 to 10 | 1.0 | World scale multiplier |
| `DepthScale` | float | 0.01 to 1.0 | 1.0 | Stereo depth scale |
| `DecoupledPitch` | bool | | false | Decouple head pitch from game |
| `DecoupledPitchUIAdjust` | bool | | true | Adjust UI for decoupled pitch |
| `LerpCameraPitch` | bool | | false | Smooth pitch transitions |
| `LerpCameraYaw` | bool | | false | Smooth yaw transitions |
| `LerpCameraRoll` | bool | | false | Smooth roll transitions |
| `LerpCameraSpeed` | float | 0.01 to 10 | 1.0 | Camera lerp speed |

### Depth

| Key | Type | Range | Default | Description |
|-----|------|-------|---------|-------------|
| `PassDepthToRuntime` | bool | | false | Send depth buffer to VR runtime |
| `EnableCustomZNear` | bool | | false | Enable custom near clip plane |
| `CustomZNear` | float | 0.001 to 100 | 0.01 | Custom near clip distance |

### Aiming & Movement

| Key | Type | Range | Default | Description |
|-----|------|-------|---------|-------------|
| `AimMethod` | int | 0-5 | 0 | See aim method table below |
| `MovementOrientation` | int | 0-5 | 0 | Same enum as AimMethod |
| `AimUsePawnControlRotation` | bool | | false | Use pawn's control rotation |
| `AimModifyPlayerControlRotation` | bool | | false | Modify player control rotation |
| `AimMPSupport` | bool | | false | Multiplayer aim support |
| `AimSpeed` | float | 0.01 to 25 | 15 | Aim interpolation speed |
| `AimInterp` | bool | | true | Enable aim interpolation |
| `ControllerPitchOffset` | float | -90 to 90 | 0 | Pitch offset for controllers |
| `RoomscaleMovement` | bool | | false | Physical roomscale movement |
| `RoomscaleMovementSweep` | bool | | true | Sweep check for roomscale |

**Aim Method Enum:**

| Value | Name | Description |
|-------|------|-------------|
| 0 | Game | Use game's default aim |
| 1 | Head/HMD | Aim follows head direction |
| 2 | Right Controller | Aim follows right controller |
| 3 | Left Controller | Aim follows left controller |
| 4 | Two Handed (Right) | Two-hand aim, right dominant |
| 5 | Two Handed (Left) | Two-hand aim, left dominant |

### Input & Controls

| Key | Type | Range | Default | Description |
|-----|------|-------|---------|-------------|
| `ControllersAllowed` | bool | | true | Enable VR controllers |
| `SnapTurn` | bool | | false | Enable snap turning |
| `SnapturnTurnAngle` | int | 1-359 | 45 | Snap turn angle in degrees |
| `SnapturnJoystickDeadzone` | float | 0.01-0.99 | 0.2 | Deadzone for snap turn |
| `JoystickDeadzone` | float | 0.01-0.9 | 0.2 | General joystick deadzone |
| `MotionControlsInactivityTimer` | float | 30-100 | 30 | Seconds before MC sleep |
| `SwapControllerInputs` | bool | | false | Swap left/right controllers |
| `DPadShifting` | bool | | true | Enable D-pad shifting |
| `DPadShiftingMethod` | int | 0-5 | 0 | D-pad input method |

### Projection

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `HorizontalProjectionOverride` | int | 0 | 0=Raw, 1=Symmetrical, 2=Mirrored |
| `VerticalProjectionOverride` | int | 0 | 0=Raw, 1=Symmetrical, 2=Matched |

### Compatibility

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Compatibility_SplitScreen` | bool | false | Split-screen compatibility |
| `SplitscreenViewIndex` | int | 0 | Which view index to use |
| `Compatibility_SceneView` | bool | false | Scene view compatibility |
| `Compatibility_SkipPostInitProperties` | bool | false | Skip post-init properties |
| `Compatibility_SkipUObjectArrayInit` | bool | false | Skip UObject array init |
| `Compatibility_AHUD` | bool | false | AHUD compatibility |

### UI & Display

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `EnableGUI` | bool | true | Show UEVR overlay |
| `ShowFPSOverlay` | bool | false | FPS counter overlay |
| `ShowStatsOverlay` | bool | false | Stats overlay |
| `LoadBlueprintCode` | bool | false | Load blueprint mods |

### UObjectHook

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `EnabledAtStartup` | bool | false | Auto-enable UObjectHook |
| `AttachLerpEnabled` | bool | true | Smooth attachment lerp |
| `AttachLerpSpeed` | float | 15 | Lerp speed (0.01-30) |

### Overlay / UI Positioning

| Key | Type | Range | Default | Description |
|-----|------|-------|---------|-------------|
| `UI_OverlayType` | int | 0-1 | 0 | 0=Quad, 1=Cylinder |
| `UI_Distance` | float | 0.5 to 10 | 2.0 | UI distance from head |
| `UI_X_Offset` | float | -10 to 10 | 0 | UI horizontal offset |
| `UI_Y_Offset` | float | -10 to 10 | 0 | UI vertical offset |
| `UI_Size` | float | 0.5 to 10 | 2.0 | UI panel size |
| `UI_Cylinder_Angle` | float | 0 to 360 | 90 | Cylinder curve angle |
| `UI_FollowView` | bool | | false | UI follows head movement |
| `UI_InvertAlpha` | bool | | false | Invert UI alpha |
| `UI_Framework_Distance` | float | 0.5 to 10 | 1.75 | UEVR menu distance |
| `UI_Framework_Size` | float | 0.5 to 10 | 2.0 | UEVR menu size |
| `UI_Framework_FollowView` | bool | | false | UEVR menu follows head |
| `UI_Framework_WristUI` | bool | | false | Attach UEVR menu to wrist |
| `UI_Framework_MouseEmulation` | bool | | true | Controller mouse emulation |

### OpenXR

| Key | Type | Range | Default | Description |
|-----|------|-------|---------|-------------|
| `OpenXR_ResolutionScale` | float | 0.1 to 3.0 | 1.0 | OpenXR render scale |
| `OpenXR_IgnoreVirtualDesktopChecks` | bool | | false | Skip Virtual Desktop compat |

### Rendering Backend

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `VR_RecreateTexturesOnReset` | bool | true | Recreate textures on device reset |
| `VR_FrameDelayCompensation` | int | 0 | Frame delay compensation |
| `VR_AsynchronousScan` | bool | true | Async object scanning |

### Lua Loader

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `LuaLoader_LogToDisk` | bool | false | Write Lua logs to file |
| `LuaLoader_GarbageCollectionHandler` | int | 0 | 0=UEVR managed, 1=Lua managed |
| `LuaLoader_GarbageCollectionBudget` | int | 1000 | GC budget in microseconds |

---

## 5. Console Commands & CVars

Execute via `uevr_exec_command` or `api:execute_command()` in Lua.

### Useful Engine Commands

| Command | Effect |
|---------|--------|
| `stat fps` | Toggle FPS counter |
| `stat unit` | Toggle detailed frame timing |
| `stat game` | Toggle game thread stats |
| `show collision` | Toggle collision visualization |
| `show bounds` | Toggle bounding box display |
| `slomo X` | Set time dilation (1.0 = normal, 0.5 = half speed) |
| `fov X` | Set field of view |
| `r.ScreenPercentage X` | Set render resolution percentage |
| `r.SetRes WxH` | Set resolution |
| `pause` | Toggle pause |
| `shot` | Take screenshot |
| `viewmode X` | Change render mode (lit, unlit, wireframe, etc.) |

### CVars Managed by UEVR's CVar Manager

UEVR freezes/controls these CVars for VR compatibility. They can be adjusted via the CVar Manager UI or `uevr_set_cvar`:

| CVar | Type | Range | Description |
|------|------|-------|-------------|
| `r.HZBOcclusion` | bool | | Hierarchical Z-buffer occlusion |
| `r.ScreenPercentage` | float | 10-150 | Render resolution % |
| `r.TemporalAA.Algorithm` | int | 0-1 | TAA algorithm |
| `r.TemporalAA.Upsampling` | int | 0-1 | TAA upsampling |
| `r.Upscale.Quality` | int | 0-5 | Upscale quality level |
| `r.Upscale.Softness` | float | 0-1 | Upscale softness |
| `r.MotionBlurQuality` | int | 0-4 | Motion blur (0 = off) |
| `r.DepthOfFieldQuality` | int | 0-2 | DOF quality (0 = off) |
| `r.SceneColorFringeQuality` | int | 0-1 | Chromatic aberration |
| `r.MotionBlur.Max` | float | -1 to 100 | Max motion blur |
| `r.VolumetricCloud` | bool | | Volumetric clouds |
| `r.OneFrameThreadLag` | bool | | Frame thread lag |
| `r.AllowOcclusionQueries` | bool | | Occlusion queries |
| `r.Tonemapper.Sharpen` | float | 0-10 | Sharpening |
| `r.TonemapperGamma` | float | -5 to 5 | Gamma correction |
| `r.Color.Min` | float | -1 to 1 | Color minimum |
| `r.Color.Mid` | float | 0 to 1 | Color midpoint |
| `r.Color.Max` | float | -1 to 2 | Color maximum |

### CVar Read/Write

```
uevr_get_cvar("r.ScreenPercentage")    → {"name":"r.ScreenPercentage","int":100,"float":100.0}
uevr_set_cvar("r.ScreenPercentage", 120)
```

---

## 6. VR Input System

### Action Paths (OpenXR/OpenVR)

| Action | Path |
|--------|------|
| Trigger | `/actions/default/in/Trigger` |
| Grip | `/actions/default/in/Grip` |
| Joystick | `/actions/default/in/Joystick` |
| Joystick Click | `/actions/default/in/JoystickClick` |
| A Button (Left/Right) | `/actions/default/in/AButtonLeft`, `AButtonRight` |
| B Button (Left/Right) | `/actions/default/in/BButtonLeft`, `BButtonRight` |
| A Touch (Left/Right) | `/actions/default/in/AButtonTouchLeft`, `AButtonTouchRight` |
| B Touch (Left/Right) | `/actions/default/in/BButtonTouchLeft`, `BButtonTouchRight` |
| Thumbrest (Left/Right) | `/actions/default/in/ThumbrestTouchLeft`, `ThumbrestTouchRight` |
| D-Pad Up/Right/Down/Left | `/actions/default/in/DPad_Up`, etc. |
| System Button | `/actions/default/in/SystemButton` |

### XInput Mapping

VR controller inputs are translated to XInput for game compatibility. The mapping flows:
1. VR runtime provides pose + button/axis state
2. UEVR maps to XINPUT_STATE (Gamepad struct)
3. Game reads via hooked `XInputGetState()`

### Haptics

```
uevr_haptics(hand="right", duration=0.1, amplitude=0.5, frequency=1.0)
```

Parameters:
- `hand`: "left" or "right"
- `duration`: seconds
- `amplitude`: 0.0 to 1.0
- `frequency`: Hz

---

## 7. Lua API Reference

Scripts are placed in: `AppData/UnrealVRMod/{game_exe}/scripts/`

### Callbacks

```lua
uevr.sdk.callbacks.on_pre_engine_tick(function(engine, delta) end)
uevr.sdk.callbacks.on_post_engine_tick(function(engine, delta) end)
uevr.sdk.callbacks.on_frame(function() end)
uevr.sdk.callbacks.on_draw_ui(function() end)
uevr.sdk.callbacks.on_script_reset(function() end)
uevr.sdk.callbacks.on_pre_calculate_stereo_view_offset(function(device, view_index, world_to_meters, position, rotation, is_double) end)
uevr.sdk.callbacks.on_post_calculate_stereo_view_offset(function(device, view_index, world_to_meters, position, rotation, is_double) end)
uevr.sdk.callbacks.on_pre_viewport_client_draw(function(viewport_client, viewport, canvas) end)
uevr.sdk.callbacks.on_xinput_get_state(function(retval, user_index, state) end)
uevr.sdk.callbacks.on_lua_event(function(event_name, event_data) end)
```

### Object Discovery

```lua
local api = uevr.api
local engine = api:get_engine()
local controller = api:get_player_controller(0)
local pawn = api:get_local_pawn(0)
local obj = api:find_uobject("Class /Script/Engine.PlayerController")
local spawned = api:spawn_object(class, outer)
local component = api:add_component_by_class(actor, comp_class, deferred)
```

### UObject Methods

```lua
obj:get_class()                    -- UClass
obj:get_outer()                    -- UObject
obj:get_fname()                    -- FName
obj:get_full_name()                -- string
obj:get_short_name()               -- string (just the leaf name)
obj:is_a(uclass)                   -- bool
obj:get_address()                  -- integer

-- Properties
obj:get_property("FieldName")      -- auto-typed value
obj:set_property("FieldName", val) -- set value
obj:get_bool_property("bFlag")     -- bool
obj:get_float_property("Speed")    -- float
obj:get_int_property("Count")      -- int
obj:get_uobject_property("Ref")   -- UObject

-- Functions
obj:call("FunctionName", arg1, arg2, ...)  -- call UFunction
obj:process_event(ufunction, param_buffer)

-- Memory (raw, use with caution)
obj:read_float(offset)
obj:write_float(offset, value)
obj:read_dword(offset) / write_dword(offset, value)
obj:read_qword(offset) / write_qword(offset, value)
```

### UStruct / UClass Methods

```lua
class:get_super_struct()           -- parent UStruct
class:find_function("Name")       -- UFunction
class:find_property("Name")       -- FProperty
class:get_child_properties()       -- first FField (linked list via :get_next())
class:get_properties_size()        -- int32
class:get_class_default_object()   -- CDO
class:get_objects_matching(false)  -- all live instances
class:get_first_object_matching(false) -- first live instance
```

### VR Functions (via `uevr.params.vr`)

```lua
local vr = uevr.params.vr
vr:is_hmd_active()
vr:get_hmd_index() / get_left_controller_index() / get_right_controller_index()
vr:get_pose(index)           -- returns position, rotation
vr:get_grip_pose(index)      -- grip pose
vr:get_aim_pose(index)       -- aim pose
vr:get_standing_origin()     -- Vector3f
vr:set_standing_origin(vec)
vr:get_rotation_offset()     -- Quaternionf
vr:set_rotation_offset(quat)
vr:recenter_view()
vr:recenter_horizon()
vr:trigger_haptic_vibration(seconds_from_now, amplitude, frequency, duration, source)
vr:get_joystick_axis(source) -- Vector2f
vr:get_aim_method()          -- int
vr:set_aim_method(method)
vr:set_mod_value(key, value)
vr:get_mod_value(key)
vr:save_config()
vr:reload_config()
```

### UObjectHook (Lua)

```lua
UEVR_UObjectHook.activate()
UEVR_UObjectHook.get_objects_by_class(uclass, allow_default)
UEVR_UObjectHook.get_first_object_by_class(uclass, allow_default)

local state = UEVR_UObjectHook.get_or_add_motion_controller_state(component)
state:set_hand(0)             -- 0=LEFT, 1=RIGHT, 2=HMD
state:set_permanent(true)
state:set_location_offset(Vector3f.new(x, y, z))
state:set_rotation_offset(quat)
```

### Data Types

```lua
Vector3f.new(x, y, z)     -- 3D vector (float)
Vector3d.new(x, y, z)     -- 3D vector (double, UE5)
-- Methods: dot, cross, length, normalize, normalized, lerp, clone
-- Operators: +, -, *

-- FName construction
UEVR_FName.new("name_string")
```

---

## 8. C++ Plugin API Reference

Plugins are DLLs loaded by UEVR. Inherit from `uevr::Plugin`:

```cpp
class MyPlugin : public uevr::Plugin {
    void on_initialize() override;
    void on_pre_engine_tick(API::UGameEngine* engine, float delta) override;
    void on_post_engine_tick(API::UGameEngine* engine, float delta) override;
    void on_present() override;
    void on_device_reset() override;
    bool on_message(HWND, UINT, WPARAM, LPARAM) override;
    void on_xinput_get_state(uint32_t* retval, uint32_t user_index, XINPUT_STATE*) override;
    void on_pre_calculate_stereo_view_offset(...) override;
    void on_post_calculate_stereo_view_offset(...) override;
    void on_pre_viewport_client_draw(...) override;
    void on_custom_event(const char* event_name, const char* event_data) override;
};
```

### Key API Methods

```cpp
auto& api = API::get();

// Objects
api->find_uobject<API::UClass>(L"Class /Script/Engine.Actor");
api->get_engine();
api->get_player_controller(0);
api->get_local_pawn(0);
api->spawn_object(class, outer);
api->execute_command(L"stat fps");

// VR (static methods on API::VR)
API::VR::is_hmd_active();
API::VR::get_pose(index);        // returns VR::Pose{position, rotation}
API::VR::get_grip_pose(index);
API::VR::get_aim_pose(index);
API::VR::recenter_view();
API::VR::set_mod_value(key, value);
API::VR::save_config();

// UObjectHook
API::UObjectHook::activate();
API::UObjectHook::exists(obj);   // validate object lifetime
```

---

## 9. Game Profiles & Persistence

### File Structure

```
%APPDATA%/UnrealVRMod/{GameExeName}/
├── config.txt                        # All mod values
├── cameras.txt                       # Camera presets
├── imgui.ini                         # ImGui layout
├── log.txt                           # UEVR log
├── plugins/                          # Plugin DLLs (e.g. uevr_mcp.dll)
├── scripts/                          # Lua scripts
└── uobjecthook/
    ├── persistent_states/            # Motion controller attachments
    └── persistent_properties/        # Saved property overrides
```

### Config Save/Reload

```
uevr_save_config     # Save current settings to disk
uevr_reload_config   # Reload settings from disk
```

---

## 10. MCP Tool Quick Reference

### Always Available (Pipe)
| Tool | Description |
|------|-------------|
| `uevr_get_status` | Plugin health, game process, VR runtime |
| `uevr_get_log` | Recent log entries |
| `uevr_clear_log` | Clear log buffer |

### Explorer (Read)
| Tool | Description |
|------|-------------|
| `uevr_search_objects(query)` | Search GUObjectArray by name |
| `uevr_search_classes(query)` | Search UClass objects |
| `uevr_get_type(typeName)` | Type schema (fields + methods) |
| `uevr_inspect_object(address)` | Full live object inspection |
| `uevr_summary(address)` | Compact field overview |
| `uevr_read_field(address, fieldName)` | Read single field |
| `uevr_call_method(address, methodName)` | Call 0-param getter |
| `uevr_objects_by_class(className)` | Find all instances |

### Explorer (Write)
| Tool | Description |
|------|-------------|
| `uevr_write_field(address, fieldName, value)` | Write field value |
| `uevr_invoke_method(address, methodName, args)` | Call method with args |
| `uevr_batch(operations)` | Multiple ops in one tick |

### Console
| Tool | Description |
|------|-------------|
| `uevr_list_cvars(query?)` | List console variables |
| `uevr_get_cvar(name)` | Read CVar value |
| `uevr_set_cvar(name, value)` | Set CVar value |
| `uevr_exec_command(command)` | Execute console command |

### VR
| Tool | Description |
|------|-------------|
| `uevr_vr_status` | Runtime state, HMD, resolution |
| `uevr_vr_poses` | HMD + controller poses |
| `uevr_vr_settings` | Current VR settings |
| `uevr_set_vr_setting(settings)` | Change VR settings |
| `uevr_recenter` | Recenter VR view |
| `uevr_haptics(hand, duration, amplitude)` | Controller vibration |
| `uevr_save_config` / `uevr_reload_config` | Persist settings |

### Player
| Tool | Description |
|------|-------------|
| `uevr_get_player` | Controller + pawn addresses |

---

## 11. Common Recipes

### Explore a Game from Scratch

```
1. uevr_get_player                              → get pawn address
2. uevr_summary(pawn_address)                    → see all fields at a glance
3. uevr_read_field(pawn_address, "CharacterMovement") → get movement component
4. uevr_inspect_object(movement_address)         → see speed, gravity, jump settings
5. uevr_get_type("Class /Script/Engine.Character") → see full type schema
```

### Modify Movement

```
uevr_write_field(movement_addr, "MaxWalkSpeed", 2000)
uevr_write_field(movement_addr, "JumpZVelocity", 1200)
uevr_write_field(movement_addr, "GravityScale", 0.3)
uevr_write_field(movement_addr, "AirControl", 0.8)
uevr_write_field(pawn_addr, "JumpMaxCount", 3)
```

### Call UFunction with Arguments

```
uevr_invoke_method(movement_addr, "SetMovementMode", {"NewMovementMode": 5, "NewCustomMode": 0})
# MovementMode: 0=None, 1=Walking, 2=NavWalking, 3=Falling, 4=Swimming, 5=Flying, 6=Custom
```

### Batch Read Multiple Fields

```
uevr_batch([
  {"type": "read_field", "address": "0x...", "fieldName": "Health"},
  {"type": "read_field", "address": "0x...", "fieldName": "MaxHealth"},
  {"type": "read_field", "address": "0x...", "fieldName": "bIsDead"}
])
```

### Configure VR Settings

```
uevr_set_vr_setting({"key": "WorldScale", "value": "2.0"})
uevr_set_vr_setting({"key": "AimMethod", "value": "2"})        # Right controller aim
uevr_set_vr_setting({"key": "RoomscaleMovement", "value": "true"})
uevr_set_vr_setting({"snapTurnEnabled": true})
uevr_save_config
```

### Visual Debug Commands

```
uevr_exec_command("stat fps")
uevr_exec_command("show collision")
uevr_exec_command("show bounds")
uevr_exec_command("slomo 0.5")        # Half speed
uevr_exec_command("viewmode wireframe")
```

### Find All Actors in Level

```
uevr_search_objects("PersistentLevel")
uevr_objects_by_class("/Script/Engine.StaticMeshActor")
uevr_objects_by_class("/Script/Engine.PointLight")
```

---

## Appendix: Property Type Mapping

| UE FProperty Type | JSON Representation | Write Format |
|-------------------|-------------------|--------------|
| BoolProperty | `true` / `false` | `true` / `false` |
| FloatProperty | `1.5` | number |
| DoubleProperty | `1.5` | number |
| IntProperty | `42` | integer |
| ByteProperty | `3` | integer |
| Int64Property | `999999` | integer |
| NameProperty | `"SomeName"` | string |
| StrProperty | `"Hello"` | string |
| ObjectProperty | `{"address":"0x...", "className":"..."}` | address string or null |
| StructProperty | `{"x":1, "y":2, "z":3}` | JSON object with field names |
| ArrayProperty | `[...]` | JSON array |
| EnumProperty | `{"enumType":"...", "value": N}` | integer or `{"value": N}` |
