using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xunit;

namespace UevrMcpTests;

/// <summary>
/// Tests that MCP tools have the correct parameter signatures and descriptions
/// for constructing proper HTTP requests. These don't make actual HTTP calls.
/// </summary>
public class HttpContractTests
{
    static MethodInfo GetTool(string name)
    {
        var asm = typeof(UevrMcp.PipeTools).Assembly;
        foreach (var type in asm.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr?.Name == name) return method;
            }
        }
        throw new Exception($"Tool '{name}' not found");
    }

    // ── Chain ──

    [Fact]
    public void Chain_HasRequiredParams()
    {
        var method = GetTool("uevr_chain");
        var ps = method.GetParameters();
        Assert.Equal(2, ps.Length);
        Assert.Equal("address", ps[0].Name);
        Assert.Equal(typeof(string), ps[0].ParameterType);
        Assert.Equal("steps", ps[1].Name);
        Assert.Equal(typeof(JsonElement), ps[1].ParameterType);
    }

    [Fact]
    public void Chain_DescriptionMentionsStepTypes()
    {
        var method = GetTool("uevr_chain");
        var desc = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("field", desc);
        Assert.Contains("method", desc);
        Assert.Contains("array", desc);
        Assert.Contains("filter", desc);
        Assert.Contains("collect", desc);
    }

    // ── Singletons ──

    [Fact]
    public void GetSingletons_NoParams()
    {
        var method = GetTool("uevr_get_singletons");
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void GetSingleton_RequiresTypeName()
    {
        var method = GetTool("uevr_get_singleton");
        var ps = method.GetParameters();
        Assert.Single(ps);
        Assert.Equal("typeName", ps[0].Name);
        Assert.Equal(typeof(string), ps[0].ParameterType);
    }

    // ── Array ──

    [Fact]
    public void GetArray_HasRequiredAndOptionalParams()
    {
        var method = GetTool("uevr_get_array");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 2);
        Assert.Equal("address", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue); // required
        Assert.Equal("fieldName", ps[1].Name);
        Assert.False(ps[1].HasDefaultValue); // required
        // offset and limit are optional
        if (ps.Length >= 3) Assert.True(ps[2].HasDefaultValue);
        if (ps.Length >= 4) Assert.True(ps[3].HasDefaultValue);
    }

    [Fact]
    public void GetArray_OptionalParamsDefaultToNull()
    {
        var method = GetTool("uevr_get_array");
        var ps = method.GetParameters();
        var offset = ps.FirstOrDefault(p => p.Name == "offset");
        var limit = ps.FirstOrDefault(p => p.Name == "limit");
        Assert.NotNull(offset);
        Assert.NotNull(limit);
        Assert.Null(offset.DefaultValue);
        Assert.Null(limit.DefaultValue);
    }

    // ── Memory ──

    [Fact]
    public void ReadMemory_HasAddressRequired_SizeOptional()
    {
        var method = GetTool("uevr_read_memory");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 1);
        Assert.Equal("address", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
        if (ps.Length >= 2)
        {
            Assert.Equal("size", ps[1].Name);
            Assert.True(ps[1].HasDefaultValue);
        }
    }

    [Fact]
    public void ReadTyped_HasRequiredAddressAndType()
    {
        var method = GetTool("uevr_read_typed");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 2);
        Assert.Equal("address", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
        Assert.Equal("type", ps[1].Name);
        Assert.False(ps[1].HasDefaultValue);
    }

    [Fact]
    public void ReadTyped_DescriptionListsAllTypes()
    {
        var method = GetTool("uevr_read_typed");
        var typeParam = method.GetParameters().First(p => p.Name == "type");
        var desc = typeParam.GetCustomAttribute<DescriptionAttribute>()!.Description;
        foreach (var t in new[] { "u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64", "f32", "f64", "ptr" })
            Assert.Contains(t, desc);
    }

    // ── Camera ──

    [Fact]
    public void GetCamera_NoParams()
    {
        var method = GetTool("uevr_get_camera");
        Assert.Empty(method.GetParameters());
    }

    // ── Game Info ──

    [Fact]
    public void GetGameInfo_NoParams()
    {
        var method = GetTool("uevr_get_game_info");
        Assert.Empty(method.GetParameters());
    }

    // ── Player Write ──

    [Fact]
    public void SetPosition_AllParamsOptional()
    {
        var method = GetTool("uevr_set_position");
        var ps = method.GetParameters();
        Assert.Equal(3, ps.Length);
        foreach (var p in ps)
        {
            Assert.True(p.HasDefaultValue, $"Parameter '{p.Name}' should be optional");
            Assert.Null(p.DefaultValue); // nullable, defaults to null
        }
    }

    [Fact]
    public void SetPosition_ParamsAreNullableDouble()
    {
        var method = GetTool("uevr_set_position");
        var ps = method.GetParameters();
        foreach (var p in ps)
            Assert.Equal(typeof(double?), p.ParameterType);
    }

    [Fact]
    public void SetHealth_RequiresValue()
    {
        var method = GetTool("uevr_set_health");
        var ps = method.GetParameters();
        Assert.Single(ps);
        Assert.Equal("value", ps[0].Name);
        Assert.Equal(typeof(JsonElement), ps[0].ParameterType);
    }

    // ── Lua Exec ──

    [Fact]
    public void LuaExec_RequiresCode()
    {
        var method = GetTool("uevr_lua_exec");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 1);
        Assert.Equal("code", ps[0].Name);
        Assert.Equal(typeof(string), ps[0].ParameterType);
        Assert.False(ps[0].HasDefaultValue); // required
    }

    [Fact]
    public void LuaExec_TimeoutIsOptional()
    {
        var method = GetTool("uevr_lua_exec");
        var ps = method.GetParameters();
        var timeout = ps.FirstOrDefault(p => p.Name == "timeout");
        Assert.NotNull(timeout);
        Assert.True(timeout.HasDefaultValue);
    }

    [Fact]
    public void LuaExec_DescriptionMentionsUevrApi()
    {
        var method = GetTool("uevr_lua_exec");
        var desc = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("UEVR", desc);
        Assert.Contains("Lua", desc);
    }

    // ── Lua Reset ──

    [Fact]
    public void LuaReset_NoRequiredParams()
    {
        var method = GetTool("uevr_lua_reset");
        Assert.Empty(method.GetParameters());
    }

    // ── Lua State ──

    [Fact]
    public void LuaState_NoParams()
    {
        var method = GetTool("uevr_lua_state");
        Assert.Empty(method.GetParameters());
    }

    // ── Lua Script Management ──

    [Fact]
    public void LuaWriteScript_HasRequiredFilenameAndContent()
    {
        var method = GetTool("uevr_lua_write_script");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 2);
        Assert.Equal("filename", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
        Assert.Equal("content", ps[1].Name);
        Assert.False(ps[1].HasDefaultValue);
    }

    [Fact]
    public void LuaWriteScript_AutorunIsOptional()
    {
        var method = GetTool("uevr_lua_write_script");
        var ps = method.GetParameters();
        var autorun = ps.FirstOrDefault(p => p.Name == "autorun");
        Assert.NotNull(autorun);
        Assert.True(autorun.HasDefaultValue);
        Assert.Equal(false, autorun.DefaultValue);
    }

    [Fact]
    public void LuaListScripts_NoParams()
    {
        var method = GetTool("uevr_lua_list_scripts");
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void LuaReadScript_RequiresFilename()
    {
        var method = GetTool("uevr_lua_read_script");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 1);
        Assert.Equal("filename", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
    }

    [Fact]
    public void LuaDeleteScript_RequiresFilename()
    {
        var method = GetTool("uevr_lua_delete_script");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 1);
        Assert.Equal("filename", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
    }

    // ── Blueprint Spawn ──

    [Fact]
    public void SpawnObject_RequiresClassName()
    {
        var method = GetTool("uevr_spawn_object");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 1);
        Assert.Equal("className", ps[0].Name);
        Assert.Equal(typeof(string), ps[0].ParameterType);
        Assert.False(ps[0].HasDefaultValue);
    }

    [Fact]
    public void SpawnObject_OuterIsOptional()
    {
        var method = GetTool("uevr_spawn_object");
        var ps = method.GetParameters();
        var outer = ps.FirstOrDefault(p => p.Name == "outerAddress");
        Assert.NotNull(outer);
        Assert.True(outer.HasDefaultValue);
    }

    // ── Blueprint Add Component ──

    [Fact]
    public void AddComponent_HasRequiredParams()
    {
        var method = GetTool("uevr_add_component");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 2);
        Assert.Equal("actorAddress", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
        Assert.Equal("componentClass", ps[1].Name);
        Assert.False(ps[1].HasDefaultValue);
    }

    [Fact]
    public void AddComponent_DeferredIsOptional()
    {
        var method = GetTool("uevr_add_component");
        var ps = method.GetParameters();
        var deferred = ps.FirstOrDefault(p => p.Name == "deferred");
        Assert.NotNull(deferred);
        Assert.True(deferred.HasDefaultValue);
        Assert.Equal(false, deferred.DefaultValue);
    }

    // ── Blueprint CDO ──

    [Fact]
    public void GetCdo_RequiresClassName()
    {
        var method = GetTool("uevr_get_cdo");
        var ps = method.GetParameters();
        Assert.Single(ps);
        Assert.Equal("className", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
    }

    [Fact]
    public void WriteCdo_HasRequiredParams()
    {
        var method = GetTool("uevr_write_cdo");
        var ps = method.GetParameters();
        Assert.Equal(3, ps.Length);
        Assert.Equal("className", ps[0].Name);
        Assert.Equal("fieldName", ps[1].Name);
        Assert.Equal("value", ps[2].Name);
        Assert.Equal(typeof(JsonElement), ps[2].ParameterType);
    }

    // ── Blueprint Destroy ──

    [Fact]
    public void DestroyObject_RequiresAddress()
    {
        var method = GetTool("uevr_destroy_object");
        var ps = method.GetParameters();
        Assert.Single(ps);
        Assert.Equal("address", ps[0].Name);
        Assert.Equal(typeof(string), ps[0].ParameterType);
    }

    // ── Blueprint Set Transform ──

    [Fact]
    public void SetTransform_RequiresAddress()
    {
        var method = GetTool("uevr_set_transform");
        var ps = method.GetParameters();
        Assert.True(ps.Length >= 1);
        Assert.Equal("address", ps[0].Name);
        Assert.False(ps[0].HasDefaultValue);
    }

    [Fact]
    public void SetTransform_TransformComponentsAreOptional()
    {
        var method = GetTool("uevr_set_transform");
        var ps = method.GetParameters();
        var location = ps.FirstOrDefault(p => p.Name == "location");
        var rotation = ps.FirstOrDefault(p => p.Name == "rotation");
        var scale = ps.FirstOrDefault(p => p.Name == "scale");
        Assert.NotNull(location);
        Assert.NotNull(rotation);
        Assert.NotNull(scale);
        Assert.True(location.HasDefaultValue);
        Assert.True(rotation.HasDefaultValue);
        Assert.True(scale.HasDefaultValue);
    }

    // ── Blueprint List Spawned ──

    [Fact]
    public void ListSpawned_NoParams()
    {
        var method = GetTool("uevr_list_spawned");
        Assert.Empty(method.GetParameters());
    }
}
