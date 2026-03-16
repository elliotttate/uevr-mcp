using System.Reflection;
using ModelContextProtocol.Server;
using UevrMcp;
using Xunit;

namespace UevrMcpTests;

/// <summary>
/// Verify all expected MCP tools are properly registered via reflection attributes.
/// These tests don't require a running game — they validate the tool metadata.
/// </summary>
public class ToolRegistrationTests
{
    static readonly Assembly ToolAssembly = typeof(PipeTools).Assembly;

    static readonly HashSet<string> ExpectedNewTools = new()
    {
        // The 10 new tools added in this feature set
        "uevr_chain", "uevr_get_singletons", "uevr_get_singleton", "uevr_get_array",
        "uevr_read_memory", "uevr_read_typed",
        "uevr_get_camera",
        "uevr_get_game_info",
        "uevr_set_position", "uevr_set_health"
    };

    static readonly HashSet<string> ExpectedTools = new()
    {
        // Core tools
        "uevr_get_status", "uevr_get_log", "uevr_clear_log",
        "uevr_help",
        "uevr_search_objects", "uevr_search_classes", "uevr_get_type",
        "uevr_inspect_object", "uevr_summary", "uevr_read_field",
        "uevr_call_method", "uevr_objects_by_class",
        "uevr_write_field", "uevr_invoke_method", "uevr_batch",
        "uevr_list_cvars", "uevr_get_cvar", "uevr_set_cvar", "uevr_exec_command",
        "uevr_vr_status", "uevr_vr_poses", "uevr_vr_settings",
        "uevr_set_vr_setting", "uevr_recenter", "uevr_haptics",
        "uevr_save_config", "uevr_reload_config",
        "uevr_get_player",
        // New exploration + memory + camera + game info + player write (10)
        "uevr_chain", "uevr_get_singletons", "uevr_get_singleton", "uevr_get_array",
        "uevr_read_memory", "uevr_read_typed",
        "uevr_get_camera",
        "uevr_get_game_info",
        "uevr_set_position", "uevr_set_health",
        // Lua tools (9)
        "uevr_lua_exec", "uevr_lua_reset", "uevr_lua_state",
        "uevr_lua_reload", "uevr_lua_globals",
        "uevr_lua_write_script", "uevr_lua_list_scripts", "uevr_lua_read_script", "uevr_lua_delete_script",
        // Blueprint tools (7)
        "uevr_spawn_object", "uevr_add_component", "uevr_get_cdo", "uevr_write_cdo",
        "uevr_destroy_object", "uevr_set_transform", "uevr_list_spawned",
        // Screenshot tools (2)
        "uevr_screenshot", "uevr_screenshot_info",
        // Watch/Snapshot tools (9)
        "uevr_watch_add", "uevr_watch_remove", "uevr_watch_list",
        "uevr_watch_changes", "uevr_watch_clear",
        "uevr_snapshot", "uevr_snapshot_list", "uevr_diff", "uevr_snapshot_delete",
        // World tools (5)
        "uevr_world_actors", "uevr_world_components",
        "uevr_line_trace", "uevr_sphere_overlap", "uevr_hierarchy",
        // Input tools (4)
        "uevr_input_key", "uevr_input_mouse", "uevr_input_gamepad", "uevr_input_text",
        // Material tools (5)
        "uevr_material_create_dynamic", "uevr_material_set_scalar",
        "uevr_material_set_vector", "uevr_material_params", "uevr_material_set_on_actor",
        // Animation tools (5)
        "uevr_animation_play_montage", "uevr_animation_stop_montage",
        "uevr_animation_state", "uevr_animation_set_variable", "uevr_animation_montages",
        // Physics tools (6)
        "uevr_physics_add_impulse", "uevr_physics_add_force",
        "uevr_physics_set_simulate", "uevr_physics_set_gravity",
        "uevr_physics_set_collision", "uevr_physics_set_mass",
        // Asset tools (5)
        "uevr_asset_find", "uevr_asset_search", "uevr_asset_load",
        "uevr_asset_classes", "uevr_asset_load_class",
        // Hook tools (5)
        "uevr_hook_add", "uevr_hook_remove", "uevr_hook_list",
        "uevr_hook_log", "uevr_hook_clear",
        // Macro tools (5)
        "uevr_macro_save", "uevr_macro_play", "uevr_macro_list",
        "uevr_macro_delete", "uevr_macro_get",
        // Event streaming (1)
        "uevr_events_poll",
        // Discovery tools (6)
        "uevr_subclasses", "uevr_search_names", "uevr_delegates",
        "uevr_vtable", "uevr_pattern_scan", "uevr_all_children"
    };

    static List<(string Name, MethodInfo Method)> DiscoverTools()
    {
        var tools = new List<(string, MethodInfo)>();
        foreach (var type in ToolAssembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr != null)
                    tools.Add((attr.Name!, method));
            }
        }
        return tools;
    }

    [Fact]
    public void AllExpectedToolsAreRegistered()
    {
        var tools = DiscoverTools();
        var registered = tools.Select(t => t.Name).ToHashSet();

        var missing = ExpectedTools.Except(registered).ToList();
        Assert.True(missing.Count == 0,
            $"Missing tools: {string.Join(", ", missing)}");
    }

    [Fact]
    public void TotalToolCount_Is112()
    {
        var tools = DiscoverTools();
        Assert.Equal(112, tools.Count);
    }

    [Fact]
    public void AllNewToolsAreRegistered()
    {
        var tools = DiscoverTools();
        var registered = tools.Select(t => t.Name).ToHashSet();
        var missing = ExpectedNewTools.Except(registered).ToList();
        Assert.True(missing.Count == 0,
            $"Missing new tools: {string.Join(", ", missing)}");
    }

    [Fact]
    public void NoUnexpectedToolsRegistered()
    {
        var tools = DiscoverTools();
        var registered = tools.Select(t => t.Name).ToHashSet();
        var unexpected = registered.Except(ExpectedTools).ToList();
        Assert.True(unexpected.Count == 0,
            $"Unexpected tools: {string.Join(", ", unexpected)}");
    }

    [Fact]
    public void AllToolNamesStartWithUevr()
    {
        var tools = DiscoverTools();
        foreach (var (name, _) in tools)
            Assert.StartsWith("uevr_", name);
    }

    [Fact]
    public void AllToolsHaveDescriptions()
    {
        var tools = DiscoverTools();
        foreach (var (name, method) in tools)
        {
            var desc = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            Assert.True(desc != null && !string.IsNullOrWhiteSpace(desc.Description),
                $"Tool '{name}' has no description");
        }
    }

    [Fact]
    public void AllToolsReturnTaskOfString()
    {
        var tools = DiscoverTools();
        foreach (var (name, method) in tools)
        {
            Assert.True(method.ReturnType == typeof(Task<string>),
                $"Tool '{name}' returns {method.ReturnType.Name} instead of Task<string>");
        }
    }

    // ── New tool-specific registration checks ──

    [Fact]
    public void ChainToolExists()
    {
        var tools = DiscoverTools();
        var chain = tools.FirstOrDefault(t => t.Name == "uevr_chain");
        Assert.NotNull(chain.Method);
        // Should take address (string) and steps (JsonElement)
        var params_ = chain.Method.GetParameters();
        Assert.Equal(2, params_.Length);
        Assert.Equal("address", params_[0].Name);
        Assert.Equal("steps", params_[1].Name);
    }

    [Fact]
    public void SingletonsToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_get_singletons");
        Assert.NotNull(tool.Method);
        Assert.Empty(tool.Method.GetParameters());
    }

    [Fact]
    public void SingletonToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_get_singleton");
        Assert.NotNull(tool.Method);
        var params_ = tool.Method.GetParameters();
        Assert.Single(params_);
        Assert.Equal("typeName", params_[0].Name);
    }

    [Fact]
    public void GetArrayToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_get_array");
        Assert.NotNull(tool.Method);
        var params_ = tool.Method.GetParameters();
        Assert.True(params_.Length >= 2); // address, fieldName, optional offset, limit
        Assert.Equal("address", params_[0].Name);
        Assert.Equal("fieldName", params_[1].Name);
    }

    [Fact]
    public void ReadMemoryToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_read_memory");
        Assert.NotNull(tool.Method);
        var params_ = tool.Method.GetParameters();
        Assert.True(params_.Length >= 1);
        Assert.Equal("address", params_[0].Name);
    }

    [Fact]
    public void ReadTypedToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_read_typed");
        Assert.NotNull(tool.Method);
        var params_ = tool.Method.GetParameters();
        Assert.True(params_.Length >= 2);
        Assert.Equal("address", params_[0].Name);
        Assert.Equal("type", params_[1].Name);
    }

    [Fact]
    public void GetCameraToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_get_camera");
        Assert.NotNull(tool.Method);
        Assert.Empty(tool.Method.GetParameters());
    }

    [Fact]
    public void GetGameInfoToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_get_game_info");
        Assert.NotNull(tool.Method);
        Assert.Empty(tool.Method.GetParameters());
    }

    [Fact]
    public void SetPositionToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_set_position");
        Assert.NotNull(tool.Method);
        var params_ = tool.Method.GetParameters();
        Assert.Equal(3, params_.Length); // x, y, z (all optional)
        Assert.Equal("x", params_[0].Name);
        Assert.Equal("y", params_[1].Name);
        Assert.Equal("z", params_[2].Name);
    }

    [Fact]
    public void SetHealthToolExists()
    {
        var tools = DiscoverTools();
        var tool = tools.FirstOrDefault(t => t.Name == "uevr_set_health");
        Assert.NotNull(tool.Method);
        var params_ = tool.Method.GetParameters();
        Assert.Single(params_);
        Assert.Equal("value", params_[0].Name);
    }
}
