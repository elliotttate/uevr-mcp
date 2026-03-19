using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UevrMcp;

static class JsonArgs
{
    public static object? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return text;
        }
    }
}

// ── Pipe tools (always available, even without HTTP) ────────────────

[McpServerToolType]
public static class PipeTools
{
    [McpServerTool(Name = "uevr_get_status")]
    [Description("Get plugin health: uptime, game process, VR runtime, queue depth. Works via named pipe even when HTTP is down.")]
    public static async Task<string> GetStatus()
        => await Pipe.Request("get_status") ?? """{"error":"pipe not connected — is the game running with the UEVR-MCP plugin loaded?"}""";

    [McpServerTool(Name = "uevr_get_log")]
    [Description("Get recent log entries from the UEVR-MCP plugin ring buffer (via named pipe).")]
    public static async Task<string> GetLog(
        [Description("Max entries to return (default 100)")] int maxEntries = 100)
    {
        var pipe = await Pipe.Request("get_log", new() { ["max_entries"] = maxEntries });
        if (pipe is not null) return pipe;

        try {
            return await Http.Get("/api/log", new() { ["maxEntries"] = maxEntries.ToString() });
        } catch {
            return """{"error":"pipe and HTTP log endpoints unavailable"}""";
        }
    }

    [McpServerTool(Name = "uevr_clear_log")]
    [Description("Clear the UEVR-MCP log ring buffer (via named pipe).")]
    public static async Task<string> ClearLog()
    {
        var pipe = await Pipe.Request("clear_log");
        if (pipe is not null) return pipe;

        try {
            return await Http.Delete("/api/log", new { });
        } catch {
            return """{"error":"pipe and HTTP log endpoints unavailable"}""";
        }
    }
}

[McpServerToolType]
public static class DiagnosticsTools
{
    [McpServerTool(Name = "uevr_get_diagnostics")]
    [Description("Get comprehensive plugin diagnostics: callback health, breadcrumb, plugin log, render metadata, loaded plugins, latest crash dump, current world/controller/pawn, and UEVR log tail.")]
    public static async Task<string> GetDiagnostics(
        [Description("Max plugin log entries to include (default 150)")] int maxLogEntries = 150,
        [Description("Max UEVR log lines to include (default 120)")] int maxUevrLogLines = 120)
        => await Http.Get("/api/diagnostics/snapshot", new() {
            ["maxLogEntries"] = maxLogEntries.ToString(),
            ["maxUevrLogLines"] = maxUevrLogLines.ToString()
        });

    [McpServerTool(Name = "uevr_get_callback_health")]
    [Description("Get callback invocation/success/failure counters and last error info for key plugin callbacks like Lua frame/timer dispatch, property watch tick, and render hooks.")]
    public static async Task<string> GetCallbackHealth()
        => await Http.Get("/api/diagnostics/callbacks");

    [McpServerTool(Name = "uevr_get_breadcrumb")]
    [Description("Get the current crash breadcrumb state and breadcrumb file path written by the plugin around risky callback boundaries.")]
    public static async Task<string> GetBreadcrumb()
        => await Http.Get("/api/diagnostics/breadcrumb");

    [McpServerTool(Name = "uevr_get_loaded_plugins")]
    [Description("List UEVR plugin DLLs discovered in the global and game-specific plugin folders, including which ones are currently loaded in the game process.")]
    public static async Task<string> GetLoadedPlugins()
        => await Http.Get("/api/diagnostics/plugins");
}

// ── Agent guide ─────────────────────────────────────────────────────

[McpServerToolType]
public static class GuideTools
{
    static readonly string? AgentMdPath = ResolveAgentMd();

    static string? ResolveAgentMd()
    {
        var dir = Path.GetDirectoryName(typeof(GuideTools).Assembly.Location);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "AGENT.md");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    [McpServerTool(Name = "uevr_help")]
    [Description("Get the UEVR-MCP agent navigation guide. Call this FIRST in a new session — it explains how to explore UE game objects, read properties, call functions, and use VR controls.")]
    public static async Task<string> Help()
    {
        try { return await Http.Get("/api"); }
        catch { /* game not running — try local file */ }

        if (AgentMdPath is not null && File.Exists(AgentMdPath))
            return await File.ReadAllTextAsync(AgentMdPath);

        return """{"error": "AGENT.md not found. Ensure the MCP server is run from the uevr-mcp directory."}""";
    }
}

// ── Explorer read tools ─────────────────────────────────────────────

[McpServerToolType]
public static class ExplorerReadTools
{
    [McpServerTool(Name = "uevr_is_valid")]
    [Description("Check if a UObject pointer is still alive in the GUObjectArray. Returns true/false with optional class/name info if valid. Use this before operating on saved addresses — objects can be garbage collected at any time.")]
    public static async Task<string> IsValid(
        [Description("Object address (0xHEX)")] string address)
        => await Http.Get("/api/explorer/is_valid", new() { ["address"] = address });

    [McpServerTool(Name = "uevr_search_objects")]
    [Description("Search GUObjectArray by full name substring. Returns matching UObject addresses, full names, and class names. Use to find specific objects in the game.")]
    public static async Task<string> SearchObjects(
        [Description("Substring to search (case-insensitive)")] string query,
        [Description("Max results (default 50)")] int? limit = null)
        => await Http.Get("/api/explorer/search", new() { ["query"] = query, ["limit"] = limit?.ToString() });

    [McpServerTool(Name = "uevr_search_classes")]
    [Description("Search for UClass objects by name. Returns matching class addresses and full names.")]
    public static async Task<string> SearchClasses(
        [Description("Substring to search in class names")] string query,
        [Description("Max results (default 50)")] int? limit = null)
        => await Http.Get("/api/explorer/classes", new() { ["query"] = query, ["limit"] = limit?.ToString() });

    [McpServerTool(Name = "uevr_get_type")]
    [Description("Get full type schema: all fields (with types/offsets) and methods (with parameter signatures and return types). No live instance needed — works on UClass/UStruct by name.")]
    public static async Task<string> GetType(
        [Description("Full type name, e.g. 'Class /Script/Engine.PlayerController' or '/Script/Engine.PlayerController'")] string typeName)
        => await Http.Get("/api/explorer/type", new() { ["typeName"] = typeName });

    [McpServerTool(Name = "uevr_inspect_object")]
    [Description("Full inspection of a live UObject: all field values and method list. Returns detailed data — prefer uevr_summary first for a lightweight overview, then inspect specific objects.")]
    public static async Task<string> InspectObject(
        [Description("Object address (0xHEX)")] string address,
        [Description("Recursion depth for nested objects/structs (default 2)")] int? depth = null)
        => await Http.Get("/api/explorer/object", new() { ["address"] = address, ["depth"] = depth?.ToString() });

    [McpServerTool(Name = "uevr_summary")]
    [Description("Quick lightweight scan of an object's fields as compact one-line-per-field strings. Use this FIRST to get an overview before using inspect_object for full details.")]
    public static async Task<string> Summary(
        [Description("Object address (0xHEX)")] string address)
        => await Http.Get("/api/explorer/summary", new() { ["address"] = address });

    [McpServerTool(Name = "uevr_read_field")]
    [Description("Read a single named field from an object. For reference-type fields (ObjectProperty), follows the pointer and returns the child object's address and class.")]
    public static async Task<string> ReadField(
        [Description("Object address (0xHEX)")] string address,
        [Description("Field name to read")] string fieldName)
        => await Http.Get("/api/explorer/field", new() { ["address"] = address, ["fieldName"] = fieldName });

    [McpServerTool(Name = "uevr_call_method")]
    [Description("Call a 0-parameter getter method on an object and return its result. For methods that take no arguments (getters, Is* checks, etc.).")]
    public static async Task<string> CallMethod(
        [Description("Object address (0xHEX)")] string address,
        [Description("Method name (e.g. 'GetActorLocation', 'IsAlive')")] string methodName)
        => await Http.Get("/api/explorer/method", new() { ["address"] = address, ["methodName"] = methodName });

    [McpServerTool(Name = "uevr_objects_by_class")]
    [Description("Find all live instances of a UClass. Returns addresses and full names.")]
    public static async Task<string> ObjectsByClass(
        [Description("Class name, e.g. '/Script/Engine.PlayerController'")] string className,
        [Description("Max results (default 50)")] int? limit = null)
        => await Http.Get("/api/explorer/objects_by_class", new() { ["className"] = className, ["limit"] = limit?.ToString() });
}

// ── Explorer write tools ────────────────────────────────────────────

[McpServerToolType]
public static class ExplorerWriteTools
{
    [McpServerTool(Name = "uevr_write_field")]
    [Description("Write a value to a field on a live UObject. Supports bool, int, float, double, string (FName), enum, struct (as JSON object), and object references (as address string).")]
    public static async Task<string> WriteField(
        [Description("Object address (0xHEX)")] string address,
        [Description("Field name to write")] string fieldName,
        [Description("New value (JSON: number, bool, string, or object)")] string value)
        => await Http.Post("/api/explorer/field", new { address, fieldName, value = JsonArgs.Parse(value) });

    [McpServerTool(Name = "uevr_invoke_method")]
    [Description("Call a UFunction with arguments on an object. Pass args as a JSON object with parameter names as keys, or as a JSON array for positional args.")]
    public static async Task<string> InvokeMethod(
        [Description("Object address (0xHEX)")] string address,
        [Description("Method name")] string methodName,
        [Description("Arguments as JSON object {paramName: value} or array [value1, value2]")] string? args = null)
        => await Http.Post("/api/explorer/method", new { address, methodName, args = JsonArgs.Parse(args) });

    [McpServerTool(Name = "uevr_batch")]
    [Description("Execute multiple operations in one game-thread request. Each operation has a 'type' (read_field, write_field, inspect, call_method, search) and type-specific params. Errors in one operation don't abort others.")]
    public static async Task<string> Batch(
        [Description("Array of operations: [{type, address?, fieldName?, value?, methodName?, args?, query?, limit?, depth?}]")] string operations)
        => await Http.Post("/api/explorer/batch", new { operations = JsonArgs.Parse(operations) });
}

// ── Console tools ───────────────────────────────────────────────────

[McpServerToolType]
public static class ConsoleTools
{
    [McpServerTool(Name = "uevr_list_cvars")]
    [Description("List console variables/commands. Optionally filter by name substring.")]
    public static async Task<string> ListCvars(
        [Description("Optional substring filter")] string? query = null,
        [Description("Max results (default 100)")] int? limit = null)
        => await Http.Get("/api/console/cvars", new() { ["query"] = query, ["limit"] = limit?.ToString() });

    [McpServerTool(Name = "uevr_get_cvar")]
    [Description("Read a console variable's current value (int and float representations).")]
    public static async Task<string> GetCvar(
        [Description("CVar name, e.g. 'r.ScreenPercentage'")] string name)
        => await Http.Get("/api/console/cvar", new() { ["name"] = name });

    [McpServerTool(Name = "uevr_set_cvar")]
    [Description("Set a console variable's value.")]
    public static async Task<string> SetCvar(
        [Description("CVar name")] string name,
        [Description("New value (number or string)")] string value)
        => await Http.Post("/api/console/cvar", new { name, value = JsonArgs.Parse(value) });

    [McpServerTool(Name = "uevr_exec_command")]
    [Description("Execute an Unreal Engine console command (e.g. 'stat fps', 'show collision').")]
    public static async Task<string> ExecCommand(
        [Description("Console command string")] string command)
        => await Http.Post("/api/console/command", new { command });
}

// ── VR tools ────────────────────────────────────────────────────────

[McpServerToolType]
public static class VrTools
{
    [McpServerTool(Name = "uevr_vr_status")]
    [Description("Get VR runtime state: OpenVR/OpenXR, HMD active, resolution, controller status.")]
    public static async Task<string> VrStatus()
        => await Http.Get("/api/vr/status");

    [McpServerTool(Name = "uevr_vr_poses")]
    [Description("Get live VR device poses: HMD position/rotation, left/right controller grip and aim poses, standing origin.")]
    public static async Task<string> VrPoses()
        => await Http.Get("/api/vr/poses");

    [McpServerTool(Name = "uevr_vr_settings")]
    [Description("Get current UEVR VR settings: snap turn, aim method, decoupled pitch, etc.")]
    public static async Task<string> VrSettings()
        => await Http.Get("/api/vr/settings");

    [McpServerTool(Name = "uevr_set_vr_setting")]
    [Description("Change a VR setting. Supports named settings (snapTurnEnabled, decoupledPitchEnabled, aimMethod, aimAllowed) or arbitrary mod values via key/value.")]
    public static async Task<string> SetVrSetting(
        [Description("Setting to change: snapTurnEnabled (bool), decoupledPitchEnabled (bool), aimMethod (int: 0=Game, 1=Head, 2=Right, 3=Left), aimAllowed (bool), or key+value for arbitrary mod values")] string settings)
        => await Http.Post("/api/vr/settings", JsonArgs.Parse(settings) ?? new { });

    [McpServerTool(Name = "uevr_recenter")]
    [Description("Recenter the VR view to the current head position.")]
    public static async Task<string> Recenter()
        => await Http.Post("/api/vr/recenter", new { });

    [McpServerTool(Name = "uevr_haptics")]
    [Description("Trigger controller vibration/haptic feedback.")]
    public static async Task<string> Haptics(
        [Description("Which hand: 'left' or 'right' (default 'right')")] string hand = "right",
        [Description("Duration in seconds (default 0.1)")] float duration = 0.1f,
        [Description("Amplitude 0.0-1.0 (default 0.5)")] float amplitude = 0.5f,
        [Description("Frequency (default 1.0)")] float frequency = 1.0f)
        => await Http.Post("/api/vr/haptics", new { hand, duration, amplitude, frequency });

    [McpServerTool(Name = "uevr_save_config")]
    [Description("Save the current UEVR configuration to disk.")]
    public static async Task<string> SaveConfig()
        => await Http.Post("/api/vr/config/save", new { });

    [McpServerTool(Name = "uevr_reload_config")]
    [Description("Reload UEVR configuration from disk.")]
    public static async Task<string> ReloadConfig()
        => await Http.Post("/api/vr/config/reload", new { });

    [McpServerTool(Name = "uevr_vr_input")]
    [Description("Get VR controller input state: left/right joystick axes, movement orientation, and optionally query specific OpenXR action states. Joystick values range from -1.0 to 1.0 on each axis.")]
    public static async Task<string> VrInput(
        [Description("Comma-separated OpenXR action paths to query (e.g. '/actions/default/in/Trigger,/actions/default/in/Grip'). Optional — omit to just get joystick axes.")] string? actions = null)
        => await Http.Get("/api/vr/input", new() { ["actions"] = actions });

    [McpServerTool(Name = "uevr_get_world_scale")]
    [Description("Get the current world-to-meters scale from UWorld. Default is 100 (1 UE unit = 1 cm). Changing this affects how large the VR player feels relative to the game world.")]
    public static async Task<string> GetWorldScale()
        => await Http.Get("/api/vr/world_scale");

    [McpServerTool(Name = "uevr_set_world_scale")]
    [Description("Set the world-to-meters scale on WorldSettings. Default is 100. Lower values make the player feel larger (like a giant), higher values make them feel smaller (like a mouse). NOTE: many games reset this value each tick — if it doesn't persist, use uevr_timer_create with looping=true to reapply it every frame via Lua.")]
    public static async Task<string> SetWorldScale(
        [Description("World-to-meters scale value (must be > 0, default is 100)")] float scale)
        => await Http.Post("/api/vr/world_scale", new { scale });
}

// ── Motion controller tools ─────────────────────────────────────────

[McpServerToolType]
public static class MotionControllerTools
{
    [McpServerTool(Name = "uevr_attach_to_controller")]
    [Description("Attach a USceneComponent to a VR motion controller. IMPORTANT: the address must be a component (SkeletalMeshComponent, StaticMeshComponent, etc.), NOT an Actor — use uevr_world_components to find component addresses. The component will track the controller's position and rotation in real-time.")]
    public static async Task<string> Attach(
        [Description("Component address (0xHEX) — must be a USceneComponent, not an Actor")] string address,
        [Description("Which hand: 'left', 'right', or 'hmd' (default 'right')")] string hand = "right",
        [Description("Keep attachment across level transitions (default true)")] bool permanent = true,
        [Description("Position offset from controller {x, y, z} (default {0,0,0})")] string? locationOffset = null,
        [Description("Rotation offset as quaternion {x, y, z, w} (default {0,0,0,1} = no rotation)")] string? rotationOffset = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["address"] = address,
            ["hand"] = hand,
            ["permanent"] = permanent
        };
        if (locationOffset != null)
            body["locationOffset"] = JsonArgs.Parse(locationOffset);
        if (rotationOffset != null)
            body["rotationOffset"] = JsonArgs.Parse(rotationOffset);
        return await Http.Post("/api/vr/attach", body);
    }

    [McpServerTool(Name = "uevr_detach_from_controller")]
    [Description("Detach a component from its VR motion controller. The component stops tracking the controller.")]
    public static async Task<string> Detach(
        [Description("Component address (0xHEX) to detach")] string address)
        => await Http.Post("/api/vr/detach", new { address });

    [McpServerTool(Name = "uevr_list_motion_controllers")]
    [Description("List all objects currently attached to VR motion controllers. Shows which hand each object is attached to, position/rotation offsets, and whether the attachment is permanent.")]
    public static async Task<string> ListAttachments()
        => await Http.Get("/api/vr/attachments");

    [McpServerTool(Name = "uevr_clear_motion_controllers")]
    [Description("Remove ALL motion controller attachments. All objects stop tracking controllers.")]
    public static async Task<string> ClearAttachments()
        => await Http.Post("/api/vr/clear_attachments", new { });
}

// ── Timer tools ─────────────────────────────────────────────────────

[McpServerToolType]
public static class TimerTools
{
    [McpServerTool(Name = "uevr_timer_create")]
    [Description("Create a timer that executes Lua code after a delay. Can be one-shot or looping. Returns a timer ID for later cancellation. The Lua code runs on the game thread with full UEVR API access.")]
    public static async Task<string> TimerCreate(
        [Description("Delay in seconds before the code runs (min 0.001)")] float delay,
        [Description("Lua code to execute when the timer fires")] string code,
        [Description("If true, repeats every 'delay' seconds until cancelled (default false)")] bool looping = false)
        => await Http.Post("/api/timer/create", new { delay, code, looping });

    [McpServerTool(Name = "uevr_timer_list")]
    [Description("List all active timers with their IDs, delays, remaining time, and looping status.")]
    public static async Task<string> TimerList()
        => await Http.Get("/api/timer/list");

    [McpServerTool(Name = "uevr_timer_cancel")]
    [Description("Cancel a timer by ID. The timer's Lua code will no longer execute.")]
    public static async Task<string> TimerCancel(
        [Description("Timer ID to cancel")] int timerId)
        => await Http.Delete("/api/timer/cancel", new { timerId });

    [McpServerTool(Name = "uevr_timer_clear")]
    [Description("Cancel ALL active timers.")]
    public static async Task<string> TimerClear()
        => await Http.Post("/api/timer/clear", new { });
}

// ── Player tools ────────────────────────────────────────────────────

[McpServerToolType]
public static class PlayerTools
{
    [McpServerTool(Name = "uevr_get_player")]
    [Description("Get player controller and pawn info: addresses, class names. Use the returned addresses with inspect_object or read_field for detailed state.")]
    public static async Task<string> GetPlayer()
        => await Http.Get("/api/player");

    [McpServerTool(Name = "uevr_set_position")]
    [Description("Set the player pawn's world position. Supports partial updates — omit x/y/z to keep the current value for that axis.")]
    public static async Task<string> SetPosition(
        [Description("X coordinate (omit to keep current)")] double? x = null,
        [Description("Y coordinate (omit to keep current)")] double? y = null,
        [Description("Z coordinate (omit to keep current)")] double? z = null)
    {
        var body = new Dictionary<string, object>();
        if (x.HasValue) body["x"] = x.Value;
        if (y.HasValue) body["y"] = y.Value;
        if (z.HasValue) body["z"] = z.Value;
        return await Http.Post("/api/player/position", body);
    }

    [McpServerTool(Name = "uevr_set_health")]
    [Description("Set the player's health. Searches common field names (Health, CurrentHealth, HP, etc.) on the pawn and its components.")]
    public static async Task<string> SetHealth(
        [Description("New health value")] string value)
        => await Http.Post("/api/player/health", new { value = JsonArgs.Parse(value) });
}

// ── Explorer advanced tools ─────────────────────────────────────────

[McpServerToolType]
public static class ExplorerAdvancedTools
{
    [McpServerTool(Name = "uevr_chain")]
    [Description("Multi-step object graph traversal. Navigate from a starting object through fields, methods, arrays, filters, and collect results — all in one request. Replaces 5-10 sequential calls. Steps: 'field' (follow ObjectProperty), 'method' (call getter, follow result), 'array' (expand array field elements), 'filter' (keep objects matching condition), 'collect' (terminal: read fields/methods from all objects).")]
    public static async Task<string> Chain(
        [Description("Starting object address (0xHEX)")] string address,
        [Description("Array of steps: [{type:'field',name:'X'}, {type:'method',name:'GetY'}, {type:'array',name:'Items'}, {type:'filter',method:'IsAlive'}, {type:'collect',fields:['Name'],methods:['GetHP']}]")] string steps)
        => await Http.Post("/api/explorer/chain", new { address, steps = JsonArgs.Parse(steps) });

    [McpServerTool(Name = "uevr_get_singletons")]
    [Description("List commonly-used singleton objects: GameEngine, GameInstance, PlayerController, Pawn, World, GameMode, GameState, etc. Great starting point for exploring game state.")]
    public static async Task<string> GetSingletons()
        => await Http.Get("/api/explorer/singletons");

    [McpServerTool(Name = "uevr_get_singleton")]
    [Description("Find a singleton by type name — returns the first live (non-default) instance of that class.")]
    public static async Task<string> GetSingleton(
        [Description("Type name, e.g. '/Script/Engine.GameInstance' or 'MyGameInstance'")] string typeName)
        => await Http.Get("/api/explorer/singleton", new() { ["typeName"] = typeName });

    [McpServerTool(Name = "uevr_get_array")]
    [Description("Read an array property with pagination. Returns elements from offset to offset+limit with total count.")]
    public static async Task<string> GetArray(
        [Description("Object address (0xHEX)")] string address,
        [Description("Array field name")] string fieldName,
        [Description("Starting index (default 0)")] int? offset = null,
        [Description("Max elements to return (default 50)")] int? limit = null)
        => await Http.Get("/api/explorer/array", new() {
            ["address"] = address, ["fieldName"] = fieldName,
            ["offset"] = offset?.ToString(), ["limit"] = limit?.ToString()
        });
}

// ── Memory tools ────────────────────────────────────────────────────

[McpServerToolType]
public static class MemoryTools
{
    [McpServerTool(Name = "uevr_read_memory")]
    [Description("Read raw memory as a hex dump with ASCII sidebar. Useful for probing unknown struct layouts, verifying field offsets, and inspecting raw data.")]
    public static async Task<string> ReadMemory(
        [Description("Memory address (0xHEX)")] string address,
        [Description("Number of bytes to read (default 256, max 8192)")] int? size = null)
        => await Http.Get("/api/explorer/memory", new() {
            ["address"] = address, ["size"] = size?.ToString()
        });

    [McpServerTool(Name = "uevr_read_typed")]
    [Description("Read typed values from memory sequentially. Types: u8, i8, u16, i16, u32, i32, u64, i64, f32, f64, ptr. Useful for scanning struct fields, reading arrays of primitives, following raw pointers.")]
    public static async Task<string> ReadTyped(
        [Description("Memory address (0xHEX)")] string address,
        [Description("Value type: u8, i8, u16, i16, u32, i32, u64, i64, f32, f64, ptr")] string type,
        [Description("Number of sequential values to read (default 1, max 50)")] int? count = null,
        [Description("Stride in bytes between values (default: auto from type size)")] int? stride = null)
        => await Http.Get("/api/explorer/typed", new() {
            ["address"] = address, ["type"] = type,
            ["count"] = count?.ToString(), ["stride"] = stride?.ToString()
        });
}

// ── Camera tools ────────────────────────────────────────────────────

[McpServerToolType]
public static class CameraTools
{
    [McpServerTool(Name = "uevr_get_camera")]
    [Description("Get the game camera state: position (x,y,z), rotation, FOV, and camera manager info. Reads from the PlayerCameraManager.")]
    public static async Task<string> GetCamera()
        => await Http.Get("/api/camera");
}

// ── Game info tools ─────────────────────────────────────────────────

[McpServerToolType]
public static class GameInfoTools
{
    [McpServerTool(Name = "uevr_get_game_info")]
    [Description("Get game executable path, directory, VR runtime info, and uptime. Works via named pipe fallback if HTTP is down.")]
    public static async Task<string> GetGameInfo()
    {
        // Try HTTP first, fall back to pipe
        try { return await Http.Get("/api/game_info"); }
        catch { /* HTTP not available, try pipe */ }

        return await Pipe.Request("game_info")
            ?? """{"error":"pipe not connected — is the game running with the UEVR-MCP plugin loaded?"}""";
    }
}

// ── Lua tools ──────────────────────────────────────────────────────

[McpServerToolType]
public static class LuaTools
{
    [McpServerTool(Name = "uevr_lua_exec")]
    [Description("Execute Lua code in the game process with full UEVR API access. The Lua state persists between calls — globals, functions, and frame callbacks survive. Access UEVR API via 'uevr.api', VR via 'uevr.vr', UObjectHook via 'uevr.uobject_hook'. Register frame callbacks with 'mcp.on_frame(function(dt) ... end)'. Use print() to capture output.")]
    public static async Task<string> LuaExec(
        [Description("Lua code to execute")] string code,
        [Description("Timeout in ms (default 10000)")] int? timeout = null)
        => await Http.Post("/api/lua/exec", new { code, timeout });

    [McpServerTool(Name = "uevr_lua_reset")]
    [Description("Reset the Lua state — clears all globals, frame callbacks, and cached data. Use when starting fresh or after errors corrupt state.")]
    public static async Task<string> LuaReset()
        => await Http.Post("/api/lua/reset", new { });

    [McpServerTool(Name = "uevr_lua_state")]
    [Description("Get Lua engine diagnostics: initialized status, execution count, frame callback count, memory usage.")]
    public static async Task<string> LuaState()
        => await Http.Get("/api/lua/state");

    [McpServerTool(Name = "uevr_lua_write_script")]
    [Description("Write a Lua script file to the UEVR scripts directory. Set autorun=true to place in the autorun folder (runs on game start).")]
    public static async Task<string> LuaWriteScript(
        [Description("Filename (e.g. 'my_script.lua')")] string filename,
        [Description("Lua script content")] string content,
        [Description("Place in autorun folder (default false)")] bool autorun = false)
        => await Http.Post("/api/lua/scripts/write", new { filename, content, autorun });

    [McpServerTool(Name = "uevr_lua_list_scripts")]
    [Description("List Lua script files in the UEVR scripts directory (both regular and autorun).")]
    public static async Task<string> LuaListScripts()
        => await Http.Get("/api/lua/scripts/list");

    [McpServerTool(Name = "uevr_lua_read_script")]
    [Description("Read the content of a Lua script file.")]
    public static async Task<string> LuaReadScript(
        [Description("Filename to read")] string filename,
        [Description("Read from autorun folder (default false)")] bool autorun = false)
        => await Http.Get("/api/lua/scripts/read", new() { ["filename"] = filename, ["autorun"] = autorun.ToString().ToLower() });

    [McpServerTool(Name = "uevr_lua_delete_script")]
    [Description("Delete a Lua script file from the UEVR scripts directory.")]
    public static async Task<string> LuaDeleteScript(
        [Description("Filename to delete")] string filename,
        [Description("Delete from autorun folder (default false)")] bool autorun = false)
        => await Http.Delete("/api/lua/scripts/delete", new { filename, autorun });

    [McpServerTool(Name = "uevr_lua_reload")]
    [Description("Hot-reload a Lua script file — executes it in the existing Lua state without resetting. Preserves globals, frame callbacks, and timers while re-defining functions from the file. Use after editing a script to apply changes without losing state.")]
    public static async Task<string> LuaReload(
        [Description("Filename to reload (e.g. 'my_script.lua')")] string filename,
        [Description("Reload from autorun folder (default false)")] bool autorun = false)
        => await Http.Post("/api/lua/reload", new { filename, autorun });

    [McpServerTool(Name = "uevr_lua_globals")]
    [Description("Inspect top-level Lua global variables — names, types, and values. Use to understand current Lua state without executing diagnostic code. Skips built-in tables and internal variables.")]
    public static async Task<string> LuaGlobals()
        => await Http.Get("/api/lua/globals");
}

// ── Event streaming ───────────────────────────────────────────────

[McpServerToolType]
public static class EventTools
{
    [McpServerTool(Name = "uevr_events_poll")]
    [Description("Poll for real-time events (hook fires, watch changes, Lua output). Uses long-polling: waits up to timeout for new events, returns immediately if events are already available. Pass the returned 'seq' as 'since' on the next call to get only new events. Event types: 'hook_fire', 'watch_change', 'lua_output'.")]
    public static async Task<string> EventsPoll(
        [Description("Sequence number — only return events after this seq (default 0 = all)")] long? since = null,
        [Description("Max wait time in ms if no events yet (default 5000, max 60000)")] int? timeout = null)
        => await Http.Get("/api/events", new() { ["since"] = since?.ToString(), ["timeout"] = timeout?.ToString() });
}

// ── Blueprint tools ────────────────────────────────────────────────

[McpServerToolType]
public static class BlueprintTools
{
    [McpServerTool(Name = "uevr_spawn_object")]
    [Description("Spawn a new UObject or Actor by class name. Uses StaticConstructObject_Internal. The object is tracked — use uevr_list_spawned to see all MCP-spawned objects.")]
    public static async Task<string> SpawnObject(
        [Description("Full class path, e.g. '/Script/Engine.PointLight' or 'Class /Script/Engine.StaticMeshActor'")] string className,
        [Description("Optional outer object address (default: /Engine/Transient)")] string? outerAddress = null)
        => await Http.Post("/api/blueprint/spawn", new { className, outerAddress });

    [McpServerTool(Name = "uevr_add_component")]
    [Description("Add a component to an actor by class name. Returns the new component's address.")]
    public static async Task<string> AddComponent(
        [Description("Actor address (0xHEX)")] string actorAddress,
        [Description("Component class path, e.g. '/Script/Engine.StaticMeshComponent'")] string componentClass,
        [Description("Use deferred spawning (default false)")] bool deferred = false)
        => await Http.Post("/api/blueprint/add_component", new { actorAddress, componentClass, deferred });

    [McpServerTool(Name = "uevr_get_cdo")]
    [Description("Get the Class Default Object (CDO) for a class — shows default field values that all new instances inherit.")]
    public static async Task<string> GetCdo(
        [Description("Class path, e.g. '/Script/Engine.Character'")] string className)
        => await Http.Get("/api/blueprint/cdo", new() { ["className"] = className });

    [McpServerTool(Name = "uevr_write_cdo")]
    [Description("Write a field on a Class Default Object — affects all future spawns of that class.")]
    public static async Task<string> WriteCdo(
        [Description("Class path")] string className,
        [Description("Field name to write")] string fieldName,
        [Description("New value (JSON)")] string value)
        => await Http.Post("/api/blueprint/cdo", new { className, fieldName, value = JsonArgs.Parse(value) });

    [McpServerTool(Name = "uevr_destroy_object")]
    [Description("Destroy an actor by calling K2_DestroyActor. Removes it from the world and the MCP spawn tracker.")]
    public static async Task<string> DestroyObject(
        [Description("Object address (0xHEX)")] string address)
        => await Http.Post("/api/blueprint/destroy", new { address });

    [McpServerTool(Name = "uevr_set_transform")]
    [Description("Set an actor's world transform: location, rotation, and/or scale. All components are optional — omit to keep current value.")]
    public static async Task<string> SetTransform(
        [Description("Actor address (0xHEX)")] string address,
        [Description("Location as {x, y, z}")] string? location = null,
        [Description("Rotation as {pitch, yaw, roll}")] string? rotation = null,
        [Description("Scale as {x, y, z}")] string? scale = null)
        => await Http.Post("/api/blueprint/set_transform", new {
            address,
            location = JsonArgs.Parse(location),
            rotation = JsonArgs.Parse(rotation),
            scale = JsonArgs.Parse(scale)
        });

    [McpServerTool(Name = "uevr_list_spawned")]
    [Description("List all objects spawned through MCP with alive/dead status.")]
    public static async Task<string> ListSpawned()
        => await Http.Get("/api/blueprint/spawned");
}

// ── Screenshot tools ───────────────────────────────────────────────

[McpServerToolType]
public static class ScreenshotTools
{
    [McpServerTool(Name = "uevr_screenshot")]
    [Description("Capture a screenshot from the game's D3D backbuffer. Works even when the game window is not in front. Returns base64-encoded JPEG image data.")]
    public static async Task<string> Screenshot(
        [Description("Max width in pixels (default 640, image scales proportionally)")] int? maxWidth = null,
        [Description("Max height in pixels (default auto from aspect ratio)")] int? maxHeight = null,
        [Description("JPEG quality 1-100 (default 75)")] int? quality = null,
        [Description("Timeout in ms (default 5000)")] int? timeout = null)
        => await Http.Get("/api/screenshot", new() {
            ["maxWidth"] = maxWidth?.ToString(), ["maxHeight"] = maxHeight?.ToString(),
            ["quality"] = quality?.ToString(), ["timeout"] = timeout?.ToString()
        });

    [McpServerTool(Name = "uevr_screenshot_info")]
    [Description("Get screenshot capability info: whether D3D capture is initialized and which renderer is in use.")]
    public static async Task<string> ScreenshotInfo()
        => await Http.Get("/api/screenshot/info");
}

// ── Watch/Snapshot tools ───────────────────────────────────────────

[McpServerToolType]
public static class WatchTools
{
    [McpServerTool(Name = "uevr_watch_add")]
    [Description("Watch a property for changes. The system checks the field every N ticks and records when it changes. Use uevr_watch_changes to see what changed. Optionally provide a Lua script that executes on each change — it receives ctx.watch_id, ctx.address, ctx.field_name, ctx.old_value, ctx.new_value, ctx.change_count.")]
    public static async Task<string> WatchAdd(
        [Description("Object address (0xHEX)")] string address,
        [Description("Field name to watch")] string fieldName,
        [Description("Check interval in ticks (default 1 = every frame)")] int? interval = null,
        [Description("Lua script to execute on change (receives ctx table)")] string? script = null)
        => await Http.Post("/api/watch/add", new { address, fieldName, interval, script });

    [McpServerTool(Name = "uevr_watch_remove")]
    [Description("Remove a property watch by ID.")]
    public static async Task<string> WatchRemove(
        [Description("Watch ID to remove")] int watchId)
        => await Http.Delete("/api/watch/remove", new { watchId });

    [McpServerTool(Name = "uevr_watch_list")]
    [Description("List all active property watches with current/previous values and change counts.")]
    public static async Task<string> WatchList()
        => await Http.Get("/api/watch/list");

    [McpServerTool(Name = "uevr_watch_changes")]
    [Description("Get recent property change events across all watches. Shows what changed, when, and the old/new values.")]
    public static async Task<string> WatchChanges(
        [Description("Max events to return (default 100)")] int? max = null)
        => await Http.Get("/api/watch/changes", new() { ["max"] = max?.ToString() });

    [McpServerTool(Name = "uevr_watch_clear")]
    [Description("Clear all property watches.")]
    public static async Task<string> WatchClear()
        => await Http.Post("/api/watch/clear", new { });

    [McpServerTool(Name = "uevr_snapshot")]
    [Description("Take a snapshot of all field values on an object. Use uevr_diff to compare against current state later — essential for reverse engineering (snapshot → do action → diff → see what changed).")]
    public static async Task<string> Snapshot(
        [Description("Object address (0xHEX)")] string address)
        => await Http.Post("/api/watch/snapshot", new { address });

    [McpServerTool(Name = "uevr_snapshot_list")]
    [Description("List all saved snapshots with metadata.")]
    public static async Task<string> SnapshotList()
        => await Http.Get("/api/watch/snapshots");

    [McpServerTool(Name = "uevr_diff")]
    [Description("Compare a snapshot against the object's current state. Returns fields that changed, were added, or were removed since the snapshot.")]
    public static async Task<string> Diff(
        [Description("Snapshot ID to compare against")] int snapshotId,
        [Description("Object address to diff (default: same address as snapshot)")] string? address = null)
        => await Http.Post("/api/watch/diff", new { snapshotId, address });

    [McpServerTool(Name = "uevr_snapshot_delete")]
    [Description("Delete a saved snapshot.")]
    public static async Task<string> SnapshotDelete(
        [Description("Snapshot ID to delete")] int snapshotId)
        => await Http.Delete("/api/watch/snapshot", new { snapshotId });
}

// ── World tools ────────────────────────────────────────────────────

[McpServerToolType]
public static class WorldTools
{
    [McpServerTool(Name = "uevr_world_actors")]
    [Description("List actors in the current world. Optionally filter by class name.")]
    public static async Task<string> WorldActors(
        [Description("Max actors to return (default 50)")] int? limit = null,
        [Description("Filter by class name substring")] string? filter = null)
        => await Http.Get("/api/world/actors", new() { ["limit"] = limit?.ToString(), ["filter"] = filter });

    [McpServerTool(Name = "uevr_world_components")]
    [Description("Get all components attached to an actor.")]
    public static async Task<string> WorldComponents(
        [Description("Actor address (0xHEX)")] string address)
        => await Http.Get("/api/world/components", new() { ["address"] = address });

    [McpServerTool(Name = "uevr_line_trace")]
    [Description("Perform a line trace (raycast) in the world. Returns hit location, normal, distance, and the actor/component that was hit.")]
    public static async Task<string> LineTrace(
        [Description("Start point {x, y, z}")] string start,
        [Description("End point {x, y, z}")] string end,
        [Description("Trace channel (default 0 = Visibility)")] int? channel = null,
        [Description("Use complex collision (default false)")] bool? complex = null)
        => await Http.Post("/api/world/line_trace", new {
            start = JsonArgs.Parse(start),
            end = JsonArgs.Parse(end),
            channel,
            complex
        });

    [McpServerTool(Name = "uevr_sphere_overlap")]
    [Description("Find all actors within a sphere. Returns overlapping actors with addresses and classes.")]
    public static async Task<string> SphereOverlap(
        [Description("Center point {x, y, z}")] string center,
        [Description("Sphere radius")] float radius)
        => await Http.Post("/api/world/sphere_overlap", new { center = JsonArgs.Parse(center), radius });

    [McpServerTool(Name = "uevr_hierarchy")]
    [Description("Get the parent/child hierarchy of an object: outer chain, owner, attachment parent/children, and class super chain.")]
    public static async Task<string> Hierarchy(
        [Description("Object address (0xHEX)")] string address)
        => await Http.Get("/api/world/hierarchy", new() { ["address"] = address });
}

// ── Input tools ────────────────────────────────────────────────────

[McpServerToolType]
public static class InputTools
{
    [McpServerTool(Name = "uevr_input_key")]
    [Description("Simulate a keyboard key press, release, or tap. Sends to the game window via PostMessage.")]
    public static async Task<string> InputKey(
        [Description("Key name ('space', 'w', 'escape', 'f1', etc.) or VK code as integer")] string key,
        [Description("Event type: 'press', 'release', or 'tap' (default 'tap')")] string? eventType = null)
        => await Http.Post("/api/input/key", new { key, @event = eventType ?? "tap" });

    [McpServerTool(Name = "uevr_input_mouse")]
    [Description("Simulate mouse button press/release/click or mouse movement.")]
    public static async Task<string> InputMouse(
        [Description("Button: 'left', 'right', 'middle' (for clicks)")] string? button = null,
        [Description("Event: 'press', 'release', 'click' (default 'click')")] string? eventType = null,
        [Description("Mouse position/movement as {x, y}")] string? move = null)
        => await Http.Post("/api/input/mouse", new { button, @event = eventType, move = JsonArgs.Parse(move) });

    [McpServerTool(Name = "uevr_input_gamepad")]
    [Description("Simulate gamepad input (buttons, sticks, triggers). Overrides XInput state until cleared.")]
    public static async Task<string> InputGamepad(
        [Description("Buttons as {a?, b?, x?, y?, lb?, rb?, start?, back?, ...}")] string? buttons = null,
        [Description("Left stick as {x, y} (-1.0 to 1.0)")] string? leftStick = null,
        [Description("Right stick as {x, y} (-1.0 to 1.0)")] string? rightStick = null,
        [Description("Left trigger (0.0 to 1.0)")] float? leftTrigger = null,
        [Description("Right trigger (0.0 to 1.0)")] float? rightTrigger = null,
        [Description("Duration in seconds before auto-clearing (0 = until manual clear)")] float? duration = null)
        => await Http.Post("/api/input/gamepad", new {
            buttons = JsonArgs.Parse(buttons),
            leftStick = JsonArgs.Parse(leftStick),
            rightStick = JsonArgs.Parse(rightStick),
            leftTrigger,
            rightTrigger,
            duration
        });

    [McpServerTool(Name = "uevr_input_text")]
    [Description("Type a string of text by sending character messages to the game window.")]
    public static async Task<string> InputText(
        [Description("Text to type")] string text)
        => await Http.Post("/api/input/text", new { text });
}

// ── Material tools ─────────────────────────────────────────────────

[McpServerToolType]
public static class MaterialTools
{
    [McpServerTool(Name = "uevr_material_create_dynamic")]
    [Description("Create a dynamic material instance from a source material. Returns the new MID address for parameter modification.")]
    public static async Task<string> CreateDynamic(
        [Description("Source material address (0xHEX)")] string sourceMaterial,
        [Description("Optional outer object address")] string? outer = null)
        => await Http.Post("/api/material/create_dynamic", new { sourceMaterial, outer });

    [McpServerTool(Name = "uevr_material_set_scalar")]
    [Description("Set a scalar parameter on a dynamic material instance (e.g., EmissiveIntensity, Roughness, Opacity).")]
    public static async Task<string> SetScalar(
        [Description("MaterialInstanceDynamic address (0xHEX)")] string address,
        [Description("Parameter name")] string paramName,
        [Description("Scalar value")] float value)
        => await Http.Post("/api/material/set_scalar", new { address, paramName, value });

    [McpServerTool(Name = "uevr_material_set_vector")]
    [Description("Set a vector/color parameter on a dynamic material instance (e.g., BaseColor, EmissiveColor).")]
    public static async Task<string> SetVector(
        [Description("MaterialInstanceDynamic address (0xHEX)")] string address,
        [Description("Parameter name")] string paramName,
        [Description("Color/vector value as {r, g, b, a}")] string value)
        => await Http.Post("/api/material/set_vector", new { address, paramName, value = JsonArgs.Parse(value) });

    [McpServerTool(Name = "uevr_material_params")]
    [Description("Get the parameter list of a material (scalar, vector, and texture parameters).")]
    public static async Task<string> GetParams(
        [Description("Material address (0xHEX)")] string address)
        => await Http.Get("/api/material/params", new() { ["address"] = address });

    [McpServerTool(Name = "uevr_material_set_on_actor")]
    [Description("Set a material on an actor's mesh component at the specified slot index.")]
    public static async Task<string> SetOnActor(
        [Description("Actor address (0xHEX)")] string actorAddress,
        [Description("Material address (0xHEX)")] string materialAddress,
        [Description("Material slot index (default 0)")] int? index = null)
        => await Http.Post("/api/material/set_on_actor", new { actorAddress, materialAddress, index });
}

// ── Animation tools ────────────────────────────────────────────────

[McpServerToolType]
public static class AnimationTools
{
    [McpServerTool(Name = "uevr_animation_play_montage")]
    [Description("Play an animation montage on a skeletal mesh component.")]
    public static async Task<string> PlayMontage(
        [Description("SkeletalMeshComponent address (0xHEX)")] string meshAddress,
        [Description("AnimMontage asset address (0xHEX)")] string montageAddress,
        [Description("Play rate (default 1.0)")] float? rate = null,
        [Description("Start section name")] string? startSection = null)
        => await Http.Post("/api/animation/play_montage", new { meshAddress, montageAddress, rate, startSection });

    [McpServerTool(Name = "uevr_animation_stop_montage")]
    [Description("Stop the currently playing montage on a skeletal mesh.")]
    public static async Task<string> StopMontage(
        [Description("SkeletalMeshComponent address (0xHEX)")] string meshAddress,
        [Description("Blend out time in seconds (default 0.25)")] float? blendOutTime = null)
        => await Http.Post("/api/animation/stop_montage", new { meshAddress, blendOutTime });

    [McpServerTool(Name = "uevr_animation_state")]
    [Description("Get the current animation state: playing montage, animation variables, etc.")]
    public static async Task<string> AnimState(
        [Description("SkeletalMeshComponent address (0xHEX)")] string meshAddress)
        => await Http.Get("/api/animation/state", new() { ["meshAddress"] = meshAddress });

    [McpServerTool(Name = "uevr_animation_set_variable")]
    [Description("Set an animation variable (float, bool, int) on the AnimInstance.")]
    public static async Task<string> SetVariable(
        [Description("SkeletalMeshComponent address (0xHEX)")] string meshAddress,
        [Description("Variable name")] string varName,
        [Description("New value")] string value)
        => await Http.Post("/api/animation/set_variable", new { meshAddress, varName, value = JsonArgs.Parse(value) });

    [McpServerTool(Name = "uevr_animation_montages")]
    [Description("List available animation montages loaded in memory.")]
    public static async Task<string> ListMontages(
        [Description("Optional name filter")] string? filter = null,
        [Description("Max results (default 50)")] int? limit = null)
        => await Http.Get("/api/animation/montages", new() { ["filter"] = filter, ["limit"] = limit?.ToString() });
}

// ── Physics tools ──────────────────────────────────────────────────

[McpServerToolType]
public static class PhysicsTools
{
    [McpServerTool(Name = "uevr_physics_add_impulse")]
    [Description("Add an impulse to a primitive component (instant force). Great for launching objects, knockback effects.")]
    public static async Task<string> AddImpulse(
        [Description("PrimitiveComponent address (0xHEX)")] string address,
        [Description("Impulse vector {x, y, z}")] string impulse,
        [Description("Treat as velocity change instead of force (default false)")] bool? velocityChange = null)
        => await Http.Post("/api/physics/add_impulse", new { address, impulse = JsonArgs.Parse(impulse), velocityChange });

    [McpServerTool(Name = "uevr_physics_add_force")]
    [Description("Add a continuous force to a primitive component. Force persists for one frame.")]
    public static async Task<string> AddForce(
        [Description("PrimitiveComponent address (0xHEX)")] string address,
        [Description("Force vector {x, y, z}")] string force)
        => await Http.Post("/api/physics/add_force", new { address, force = JsonArgs.Parse(force) });

    [McpServerTool(Name = "uevr_physics_set_simulate")]
    [Description("Enable or disable physics simulation on a component.")]
    public static async Task<string> SetSimulate(
        [Description("PrimitiveComponent address (0xHEX)")] string address,
        [Description("Enable physics simulation")] bool simulate)
        => await Http.Post("/api/physics/set_simulate", new { address, simulate });

    [McpServerTool(Name = "uevr_physics_set_gravity")]
    [Description("Enable or disable gravity on a physics component.")]
    public static async Task<string> SetGravity(
        [Description("PrimitiveComponent address (0xHEX)")] string address,
        [Description("Enable gravity")] bool enabled)
        => await Http.Post("/api/physics/set_gravity", new { address, enabled });

    [McpServerTool(Name = "uevr_physics_set_collision")]
    [Description("Enable or disable collision on a component.")]
    public static async Task<string> SetCollision(
        [Description("PrimitiveComponent address (0xHEX)")] string address,
        [Description("Enable collision")] bool enabled)
        => await Http.Post("/api/physics/set_collision", new { address, enabled });

    [McpServerTool(Name = "uevr_physics_set_mass")]
    [Description("Set mass override on a physics component.")]
    public static async Task<string> SetMass(
        [Description("PrimitiveComponent address (0xHEX)")] string address,
        [Description("Mass in kg")] float mass)
        => await Http.Post("/api/physics/set_mass", new { address, mass });
}

// ── Asset tools ────────────────────────────────────────────────────

[McpServerToolType]
public static class AssetTools
{
    [McpServerTool(Name = "uevr_asset_find")]
    [Description("Find a loaded asset by path. Searches objects currently in memory.")]
    public static async Task<string> AssetFind(
        [Description("Asset path (e.g. '/Game/Materials/MyMaterial')")] string path)
        => await Http.Get("/api/asset/find", new() { ["path"] = path });

    [McpServerTool(Name = "uevr_asset_search")]
    [Description("Search loaded assets by name, optionally filtered by type.")]
    public static async Task<string> AssetSearch(
        [Description("Search query (name substring)")] string query,
        [Description("Asset type filter (e.g. 'StaticMesh', 'Material', 'Texture2D')")] string? type = null,
        [Description("Max results (default 50)")] int? limit = null)
        => await Http.Get("/api/asset/search", new() { ["query"] = query, ["type"] = type, ["limit"] = limit?.ToString() });

    [McpServerTool(Name = "uevr_asset_load")]
    [Description("Attempt to load an asset by path. Returns the object if it's already in memory, otherwise tries to load it.")]
    public static async Task<string> AssetLoad(
        [Description("Asset path")] string path,
        [Description("Expected class (e.g. '/Script/Engine.StaticMesh')")] string? className = null)
        => await Http.Post("/api/asset/load", new { path, className });

    [McpServerTool(Name = "uevr_asset_classes")]
    [Description("List loaded asset types/classes with optional filter.")]
    public static async Task<string> AssetClasses(
        [Description("Filter by class name substring")] string? filter = null,
        [Description("Max results (default 50)")] int? limit = null)
        => await Http.Get("/api/asset/classes", new() { ["filter"] = filter, ["limit"] = limit?.ToString() });

    [McpServerTool(Name = "uevr_asset_load_class")]
    [Description("Find or load a UClass by name.")]
    public static async Task<string> AssetLoadClass(
        [Description("Class name (e.g. '/Script/Engine.StaticMesh')")] string className)
        => await Http.Post("/api/asset/load_class", new { className });
}

// ── Hook tools ─────────────────────────────────────────────────────

[McpServerToolType]
public static class HookTools
{
    [McpServerTool(Name = "uevr_hook_add")]
    [Description("Hook a UFunction to log calls, block execution, run Lua callbacks, or combinations. Actions: 'log' (record calls), 'block' (skip execution), 'log_and_block' (both), 'lua' (run Lua script — return false to block), 'lua_block' (run Lua script, always block). For lua/lua_block actions, provide a script parameter with Lua code. The script receives ctx.object_address, ctx.object_name, ctx.function_name, ctx.class_name, ctx.hook_id, ctx.call_count.")]
    public static async Task<string> HookAdd(
        [Description("Class name (e.g. 'Class /Script/Engine.Actor')")] string className,
        [Description("Function name to hook")] string functionName,
        [Description("Action: 'log', 'block', 'log_and_block', 'lua', 'lua_block'")] string action = "log",
        [Description("Lua script for lua/lua_block actions (receives ctx table with hook context)")] string? script = null)
        => await Http.Post("/api/hook/add", new { className, functionName, action, script });

    [McpServerTool(Name = "uevr_hook_remove")]
    [Description("Remove a function hook by ID.")]
    public static async Task<string> HookRemove(
        [Description("Hook ID to remove")] int hookId)
        => await Http.Delete("/api/hook/remove", new { hookId });

    [McpServerTool(Name = "uevr_hook_list")]
    [Description("List all active function hooks with call counts.")]
    public static async Task<string> HookList()
        => await Http.Get("/api/hook/list");

    [McpServerTool(Name = "uevr_hook_log")]
    [Description("Get the call log for a specific hook — shows which objects called the function and when.")]
    public static async Task<string> HookLog(
        [Description("Hook ID")] int hookId,
        [Description("Max log entries (default 50)")] int? max = null)
        => await Http.Get("/api/hook/log", new() { ["hookId"] = hookId.ToString(), ["max"] = max?.ToString() });

    [McpServerTool(Name = "uevr_hook_clear")]
    [Description("Clear all function hooks.")]
    public static async Task<string> HookClear()
        => await Http.Post("/api/hook/clear", new { });
}

// ── ProcessEvent listener tools ───────────────────────────────────

[McpServerToolType]
public static class ProcessEventTools
{
    [McpServerTool(Name = "uevr_process_event_start")]
    [Description("Start the global ProcessEvent listener — hooks UObject::ProcessEvent to capture ALL Blueprint/native function calls in real time. Use this to discover what functions the game is calling, then filter by name or call count. Equivalent to the 'ProcessEvent Listener' toggle under Developer in UEVR.")]
    public static async Task<string> Start()
        => await Http.Post("/api/process_event/start", new { });

    [McpServerTool(Name = "uevr_process_event_stop")]
    [Description("Stop the ProcessEvent listener. The hook stays installed (cheap to restart) but no data is collected.")]
    public static async Task<string> Stop()
        => await Http.Post("/api/process_event/stop", new { });

    [McpServerTool(Name = "uevr_process_event_status")]
    [Description("Get the ProcessEvent listener status — whether it's listening, hook state, number of unique functions seen, and ignore list size.")]
    public static async Task<string> Status()
        => await Http.Get("/api/process_event/status");

    [McpServerTool(Name = "uevr_process_event_functions")]
    [Description("Get all functions seen by the ProcessEvent listener, sorted by call count (descending). Filter with maxCalls (exclude functions called more than N times — useful to hide noisy tick functions) and search (substring match on function name). This is equivalent to 'All Called Functions' in UEVR's Developer tab.")]
    public static async Task<string> GetFunctions(
        [Description("Exclude functions with more than this many calls (0 = no limit)")] int? maxCalls = null,
        [Description("Filter by function name substring (case-insensitive)")] string? search = null,
        [Description("Max results to return (default 200)")] int? limit = null,
        [Description("Sort order: 'count' (default, descending) or 'name' (alphabetical)")] string? sort = null)
        => await Http.Get("/api/process_event/functions", new() {
            ["maxCalls"] = maxCalls?.ToString(), ["search"] = search,
            ["limit"] = limit?.ToString(), ["sort"] = sort
        });

    [McpServerTool(Name = "uevr_process_event_recent")]
    [Description("Get the most recent ProcessEvent calls (newest first). Shows the live stream of function calls happening in the game right now. Equivalent to 'Recent Functions' in UEVR's Developer tab.")]
    public static async Task<string> GetRecent(
        [Description("Number of recent calls to return (default 50)")] int? count = null)
        => await Http.Get("/api/process_event/recent", new() { ["count"] = count?.ToString() });

    [McpServerTool(Name = "uevr_process_event_ignore")]
    [Description("Ignore functions matching a name pattern — they won't appear in results or recent list. Use this to filter out noisy functions (e.g. 'Tick', 'ReceiveBeginPlay'). Pattern is a substring match.")]
    public static async Task<string> Ignore(
        [Description("Substring pattern to match function names to ignore")] string pattern)
        => await Http.Post("/api/process_event/ignore", new { pattern });

    [McpServerTool(Name = "uevr_process_event_ignore_all")]
    [Description("Ignore ALL currently seen functions — useful to establish a baseline, then watch for new functions that appear when you perform an action in the game.")]
    public static async Task<string> IgnoreAll()
        => await Http.Post("/api/process_event/ignore_all", new { });

    [McpServerTool(Name = "uevr_process_event_clear")]
    [Description("Clear all tracked ProcessEvent data (function list and recent calls). Does not affect the ignore list.")]
    public static async Task<string> Clear()
        => await Http.Post("/api/process_event/clear", new { });

    [McpServerTool(Name = "uevr_process_event_clear_ignored")]
    [Description("Clear the ignore list so all functions are tracked again.")]
    public static async Task<string> ClearIgnored()
        => await Http.Post("/api/process_event/clear_ignored", new { });
}

// ── Macro tools ────────────────────────────────────────────────────

[McpServerToolType]
public static class MacroTools
{
    [McpServerTool(Name = "uevr_macro_save")]
    [Description("Save a reusable macro — a named sequence of operations (same format as batch). Use $paramName placeholders for parameterized values. Macros persist to disk across game restarts.")]
    public static async Task<string> MacroSave(
        [Description("Macro name")] string name,
        [Description("Array of operations (batch format)")] string operations,
        [Description("Optional description")] string? description = null)
        => await Http.Post("/api/macro/save", new { name, operations = JsonArgs.Parse(operations), description });

    [McpServerTool(Name = "uevr_macro_play")]
    [Description("Execute a saved macro with state propagation. Pass params to substitute $placeholders. Use $result[N].field to reference previous operation results (e.g. $result[0].address uses the address from the first operation's result).")]
    public static async Task<string> MacroPlay(
        [Description("Macro name")] string name,
        [Description("Parameters to substitute (e.g. {address: '0x...'})")] string? @params = null)
        => await Http.Post("/api/macro/play", new { name, @params = JsonArgs.Parse(@params) });

    [McpServerTool(Name = "uevr_macro_list")]
    [Description("List all saved macros with names and operation counts.")]
    public static async Task<string> MacroList()
        => await Http.Get("/api/macro/list");

    [McpServerTool(Name = "uevr_macro_delete")]
    [Description("Delete a saved macro.")]
    public static async Task<string> MacroDelete(
        [Description("Macro name")] string name)
        => await Http.Delete("/api/macro/delete", new { name });

    [McpServerTool(Name = "uevr_macro_get")]
    [Description("Get a macro's full definition including all operations.")]
    public static async Task<string> MacroGet(
        [Description("Macro name")] string name)
        => await Http.Get("/api/macro/get", new() { ["name"] = name });
}

// ── Discovery tools ────────────────────────────────────────────────

[McpServerToolType]
public static class DiscoveryTools
{
    [McpServerTool(Name = "uevr_subclasses")]
    [Description("Find ALL subclasses of a given base class. Essential for discovering game-specific types — e.g., find all subclasses of Character to discover enemy/NPC classes. Optionally shows live instance counts.")]
    public static async Task<string> Subclasses(
        [Description("Base class name (e.g. 'Class /Script/Engine.Character' or '/Script/Engine.Pawn')")] string className,
        [Description("Max results (default 100)")] int? limit = null,
        [Description("Include live instance count per class (default false, slower)")] bool? includeInstances = null)
        => await Http.Get("/api/discovery/subclasses", new() {
            ["className"] = className, ["limit"] = limit?.ToString(),
            ["includeInstances"] = includeInstances?.ToString()?.ToLower()
        });

    [McpServerTool(Name = "uevr_search_names")]
    [Description("Search ALL reflected names in the engine — class names, property names, function names. Catches names that uevr_search_objects misses (types without live instances, field names on uninstantiated classes). Use scope to narrow: 'all', 'classes', 'properties', 'functions'.")]
    public static async Task<string> SearchNames(
        [Description("Name substring to search (case-insensitive)")] string query,
        [Description("Max results (default 100)")] int? limit = null,
        [Description("Scope: 'all' (default), 'classes', 'properties', 'functions'")] string? scope = null)
        => await Http.Get("/api/discovery/names", new() {
            ["query"] = query, ["limit"] = limit?.ToString(), ["scope"] = scope
        });

    [McpServerTool(Name = "uevr_delegates")]
    [Description("Inspect delegates and event functions on an object. Finds all delegate properties (OnTakeDamage, OnDeath, etc.) and event-pattern functions (On*, Receive*, Handle*, Notify*, BP_*, K2_*). Essential for understanding what gameplay events an object responds to.")]
    public static async Task<string> Delegates(
        [Description("Object address (0xHEX)")] string address)
        => await Http.Get("/api/discovery/delegates", new() { ["address"] = address });

    [McpServerTool(Name = "uevr_vtable")]
    [Description("Compare an object's virtual function table against its parent class. Shows which C++ virtual functions are overridden — these are the game-specific implementations. Each entry shows the function address, module, and RVA for use with disassemblers.")]
    public static async Task<string> Vtable(
        [Description("Object address (0xHEX)")] string address,
        [Description("Number of vtable entries to read (default 64, max 256)")] int? entries = null)
        => await Http.Get("/api/discovery/vtable", new() {
            ["address"] = address, ["entries"] = entries?.ToString()
        });

    [McpServerTool(Name = "uevr_pattern_scan")]
    [Description("Search executable memory for byte patterns (signature scanning). Use ? for wildcard bytes. Returns matching addresses with module and RVA info. Useful for finding specific native functions.")]
    public static async Task<string> PatternScan(
        [Description("Hex byte pattern with ? wildcards (e.g. '48 89 5C 24 ? 57 48 83 EC 20')")] string pattern,
        [Description("Module name to scan (default: main exe)")] string? module = null,
        [Description("Max matches (default 10)")] int? limit = null)
        => await Http.Post("/api/discovery/pattern_scan", new { pattern, module, limit });

    [McpServerTool(Name = "uevr_all_children")]
    [Description("Brute-force enumerate ALL properties and functions on a type, walking the full inheritance chain. More thorough than uevr_get_type — includes delegate functions, sparse delegates, and non-standard entries. Shows which class each member is declared in.")]
    public static async Task<string> AllChildren(
        [Description("Type name (e.g. 'Class /Script/Engine.Character')")] string typeName,
        [Description("How many super-class levels to walk (default: all)")] int? depth = null)
        => await Http.Get("/api/discovery/all_children", new() {
            ["typeName"] = typeName, ["depth"] = depth?.ToString()
        });
}
