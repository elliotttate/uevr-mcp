using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Render diagnostics — bridges UEVRJ's Render Inspector (D3D12Diagnostics,
/// FrameResourceInspector, ShaderOverrideRegistry, RenderAnalysisExport) into
/// MCP. The plugin resolves these against UEVRBackend.dll at runtime via
/// GetProcAddress, so a "503 / UEVRJ render diagnostics FFI not available"
/// response means the running UEVR build predates the render-diagnostics CAPI.
/// </summary>
[McpServerToolType]
public static class RenderDiagnosticsTools
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static string JsonText(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    static JsonNode? ParseNode(string raw)
    {
        try { return JsonNode.Parse(raw); }
        catch { return null; }
    }

    static string? StringProp(JsonNode? node, string name)
        => node is JsonObject obj && obj[name] is JsonValue v ? v.ToString() : null;

    static long LongProp(JsonNode? node, string name, long fallback = 0)
    {
        try
        {
            if (node is JsonObject obj && obj[name] is JsonValue v)
                return v.GetValue<long>();
        }
        catch { /* fall through */ }
        return fallback;
    }

    static bool BoolProp(JsonNode? node, string name, bool fallback = false)
    {
        try
        {
            if (node is JsonObject obj && obj[name] is JsonValue v)
                return v.GetValue<bool>();
        }
        catch { /* fall through */ }
        return fallback;
    }

    static JsonArray ArrayProp(JsonNode? node, string name)
        => node is JsonObject obj && obj[name] is JsonArray arr ? arr : new JsonArray();

    static JsonNode? CloneNode(JsonNode? node) => node?.DeepClone();

    static bool JsonContainsText(JsonNode? node, string? needle)
    {
        if (node is null || string.IsNullOrWhiteSpace(needle))
            return false;
        return node.ToJsonString().Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    static double[] NumberArrayProp(JsonNode? node, string name, int count = 4)
    {
        var values = new double[count];
        var arr = ArrayProp(node, name);
        for (var i = 0; i < count && i < arr.Count; ++i)
        {
            try
            {
                if (arr[i] is JsonValue v)
                    values[i] = v.GetValue<double>();
            }
            catch { /* leave zero */ }
        }
        return values;
    }

    static JsonObject ParseError(string surface, string raw)
        => new()
        {
            ["error"] = $"Failed to parse {surface} JSON",
            ["raw"] = raw
        };

    static string NormalizeHashArg(string targetHash)
    {
        var h = targetHash.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            h = h[2..];
        return h.ToLowerInvariant();
    }

    static string NormalizePointerArg(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var v = value.Trim();
        if (!v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = "0x" + v;
        return v.ToLowerInvariant();
    }

    static HashSet<int>? ParseSlotSet(string? slotsCsv)
    {
        if (string.IsNullOrWhiteSpace(slotsCsv))
            return null;

        var set = new HashSet<int>();
        foreach (var raw in slotsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(raw, out var value) && value >= 0)
                set.Add(value);
        return set.Count == 0 ? null : set;
    }

    static HashSet<string> MatchingPsoPointers(JsonNode? shaders, string? targetHash)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(targetHash))
            return set;

        var hash = NormalizeHashArg(targetHash);
        foreach (var item in ArrayProp(shaders, "d3d12_pso_aggregates"))
        {
            if (!JsonContainsText(item, hash))
                continue;
            foreach (var key in new[] { "original_pso", "last_bound_pso", "pipeline_state", "original_pipeline_state", "bound_pipeline_state" })
            {
                var ptr = StringProp(item, key);
                if (!string.IsNullOrWhiteSpace(ptr) && !ptr.Equals("0x0", StringComparison.OrdinalIgnoreCase))
                    set.Add(ptr!);
            }
        }
        foreach (var item in ArrayProp(shaders, "distinct_d3d12_pairs"))
        {
            if (!JsonContainsText(item, hash))
                continue;
            foreach (var key in new[] { "original_pipeline_state", "bound_pipeline_state" })
            {
                var ptr = StringProp(item, key);
                if (!string.IsNullOrWhiteSpace(ptr) && !ptr.Equals("0x0", StringComparison.OrdinalIgnoreCase))
                    set.Add(ptr!);
            }
        }
        return set;
    }

    static JsonArray FilterDescriptorReads(JsonNode? draw, int? rootParameter, HashSet<int>? slots)
    {
        var result = new JsonArray();
        foreach (var read in ArrayProp(draw, "descriptor_reads"))
        {
            if (rootParameter is int rp && LongProp(read, "root_parameter", -1) != rp)
                continue;
            if (slots is not null && !slots.Contains((int)LongProp(read, "descriptor_index", -1)))
                continue;
            result.Add(CloneNode(read));
        }
        return result;
    }

    static JsonObject CompactDrawInput(JsonNode? draw, int? rootParameter = null, HashSet<int>? slots = null)
        => new()
        {
            ["frame"] = CloneNode(draw?["frame"]),
            ["draw_index"] = CloneNode(draw?["draw_index"]),
            ["kind"] = StringProp(draw, "kind"),
            ["eye_bucket"] = CloneNode(draw?["eye_bucket"]),
            ["pipeline_state"] = StringProp(draw, "pipeline_state"),
            ["root_signature"] = StringProp(draw, "root_signature"),
            ["viewport0"] = CloneNode(draw?["viewport0"]),
            ["scissor0"] = CloneNode(draw?["scissor0"]),
            ["rtv0"] = StringProp(draw, "rtv0"),
            ["rtv0_resource"] = StringProp(draw, "rtv0_resource"),
            ["graphics_root_descriptor_tables"] = CloneNode(draw?["graphics_root_descriptor_tables"]),
            ["graphics_root_cbvs"] = CloneNode(draw?["graphics_root_cbvs"]),
            ["graphics_root_cbv_hash"] = CloneNode(draw?["graphics_root_cbv_hash"]),
            ["graphics_root_descriptor_table_resource_hash"] = CloneNode(draw?["graphics_root_descriptor_table_resource_hash"]),
            ["descriptor_reads"] = FilterDescriptorReads(draw, rootParameter, slots),
            ["render_target_writes"] = CloneNode(draw?["render_target_writes"]),
            ["uav_writes"] = CloneNode(draw?["uav_writes"])
        };

    static string NormalizeStageArg(string stage)
    {
        var s = stage.Trim().ToLowerInvariant();
        return s switch
        {
            "vs" or "vertex" or "vertex_shader" => "vs",
            "ps" or "pixel" or "pixel_shader" => "ps",
            "cs" or "compute" or "compute_shader" => "cs",
            "as" or "amplification" or "amplification_shader" => "as",
            "ms" or "mesh" or "mesh_shader" => "ms",
            _ => throw new ArgumentException("stage must be one of vs, ps, cs, as, or ms")
        };
    }

    static string DefaultProfileForStage(string stage)
        => NormalizeStageArg(stage) switch
        {
            "vs" => "vs_6_0",
            "ps" => "ps_6_0",
            "cs" => "cs_6_0",
            "as" => "as_6_5",
            "ms" => "ms_6_5",
            _ => "ps_6_0"
        };

    static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is ':' or '/' or '\\')
                sb.Append('_');
        }
        var slug = sb.ToString().Trim('_', '.');
        return string.IsNullOrWhiteSpace(slug) ? "override" : slug;
    }

    static JsonNode ParseJsonArgument(string raw, string name)
    {
        try
        {
            return JsonNode.Parse(raw) ?? throw new ArgumentException($"{name} parsed as null");
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"{name} must be valid JSON: {ex.Message}");
        }
    }

    static JsonArray ParseJsonArrayArgument(string raw, string name)
    {
        var text = raw.Trim();
        if (!text.StartsWith("[", StringComparison.Ordinal))
            text = "[" + text + "]";

        return ParseJsonArgument(text, name) as JsonArray
            ?? throw new ArgumentException($"{name} must be a JSON array");
    }

    static string UniqueChildDirectory(string parent, string preferredName)
    {
        var baseName = Slug(preferredName);
        var path = Path.Combine(parent, baseName);
        if (!Directory.Exists(path) && !File.Exists(path))
            return path;

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmssfff");
        for (var i = 0; i < 1000; ++i)
        {
            var candidate = Path.Combine(parent, $"{baseName}_{stamp}_{i}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Could not allocate a unique override directory under {parent}");
    }

    static async Task<string> ResolveProfileOverrideDir(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideDir));

        var shadersRaw = await Http.Get("/api/render/shaders", new() {
            ["maxDistinctPairs"] = "1",
            ["maxPsoAggregates"] = "1"
        });
        var shaders = ParseNode(shadersRaw);
        var dir = StringProp(shaders, "profile_override_dir");
        if (!string.IsNullOrWhiteSpace(dir))
            return dir!;

        var contextRaw = await Http.Get("/api/render/context");
        var context = ParseNode(contextRaw);
        var persistent = StringProp(context, "persistent_dir");
        if (!string.IsNullOrWhiteSpace(persistent))
            return Path.Combine(persistent!, "shader_overrides");

        throw new InvalidOperationException("Could not resolve UEVR profile shader_overrides directory. Start the game/plugin or pass overrideDir explicitly.");
    }

    static async Task AttachReloadResult(JsonObject result, bool reload)
    {
        if (!reload)
            return;

        var reloadRaw = await Http.Post("/api/render/request-shader-reload", new { });
        result["reload_result"] = ParseNode(reloadRaw) ?? JsonValue.Create(reloadRaw);
    }

    static JsonObject FileResult(string kind, string dir, string manifestPath)
        => new()
        {
            ["ok"] = true,
            ["kind"] = kind,
            ["override_dir"] = dir,
            ["manifest_path"] = manifestPath
        };

    [McpServerTool(Name = "uevr_render_status")]
    [Description("Probe whether the UEVRJ render-diagnostics FFI is available. Returns which symbols resolved against UEVRBackend.dll, plus the current framework/renderer context and force-sampling state. Call this first to verify the FFI surface is wired up.")]
    public static async Task<string> Status()
        => await Http.Get("/api/render/status");

    [McpServerTool(Name = "uevr_render_dxil_capabilities")]
    [Description("Summarize the DXIL/render-diagnostics capabilities exposed by the running UEVR build: container bytecode inspection/disassembly, active Hunter override-stub capture, runtime override A/B toggling, frame-pair diff export, RenderDoc bridge, stereo trace, and baseline render snapshots. This is the quickest compatibility check before using DXIL tools.")]
    public static async Task<string> DxilCapabilities()
    {
        var statusRaw = await Http.Get("/api/render/status");
        var status = ParseNode(statusRaw);
        if (status is null) return JsonText(ParseError("render status", statusRaw));

        var symbols = status["resolved_symbols"] as JsonObject ?? new JsonObject();
        bool Has(string name) => BoolProp(symbols, name);

        var caps = new JsonObject
        {
            ["available"] = BoolProp(status, "available"),
            ["backend_module"] = StringProp(status, "backend_module"),
            ["renderer"] = status["context"]?["renderer"]?.ToString(),
            ["profile_name"] = status["context"]?["profile_name"]?.ToString(),
            ["capabilities"] = new JsonObject
            {
                ["snapshots"] = Has("snapshot") && Has("d3d12") && Has("shaders"),
                ["shader_bytecode_inspection"] = Has("shader_bytecode"),
                ["dxil_disassembly"] = Has("shader_bytecode"),
                ["pdb_source_recovery"] = Has("shader_bytecode"),
                ["gpu_timestamp_timings"] = Has("d3d12"),
                ["pipeline_cache_event_tracking"] = Has("d3d12"),
                ["automated_ab_pixel_diff"] = Has("set_runtime_overrides_enabled") && Has("eye_sample"),
                ["hunter_override_stub_capture"] = Has("hunter_capture_active_override_stub"),
                ["manifest_hlsl_override_authoring"] = true,
                ["manifest_bind_override_authoring"] = true,
                ["manifest_dxil_text_patch_authoring"] = true,
                ["manifest_container_patch_authoring"] = true,
                ["manifest_dxil_transform_authoring"] = true,
                ["manifest_dxil_semantic_transform_authoring"] = true,
                ["bind_time_cbv_root_constant_overrides"] = Has("shaders"),
                ["per_eye_pso_variant_manifests"] = Has("shaders"),
                ["runtime_override_ab_toggle"] = Has("set_runtime_overrides_enabled"),
                ["frame_pair_diff_export"] = Has("export_frame_pair_diff"),
                ["renderdoc_bridge"] = Has("renderdoc_status") && Has("renderdoc_trigger"),
                ["stereo_trace"] = Has("set_stereo_trace_enabled") && Has("stereo_trace_json"),
                ["stereo_forensics_rearm"] = Has("stereo_forensics_arm"),
                ["eye_texture_readback"] = Has("eye_sample") && Has("eye_dump")
            },
            ["missing_high_value_symbols"] = new JsonArray()
        };

        var missing = caps["missing_high_value_symbols"]!.AsArray();
        foreach (var name in new[]
        {
            "shader_bytecode", "hunter_capture_active_override_stub",
            "set_runtime_overrides_enabled", "export_frame_pair_diff",
            "set_stereo_trace_enabled", "stereo_trace_json",
            "stereo_forensics_arm"
        })
        {
            if (!Has(name)) missing.Add(name);
        }

        caps["raw_status"] = CloneNode(status);
        return caps.ToJsonString(JsonOptions);
    }

    // ── Snapshots ─────────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_render_snapshot")]
    [Description("Full render-pipeline snapshot: tracked resources (D3D11/D3D12 textures with depth/RT/UI/swapchain/eye tags), D3D12Diagnostics (heaps, barriers, bindings, warnings, currently-bound render targets), and ShaderOverrideRegistry (bound VS/PS, distinct D3D12 PSO pairs, PSO aggregates with likely render-target associations, override entries). Use this for a one-shot view of the entire rendering state.")]
    public static async Task<string> Snapshot(
        [Description("Cap on resources returned (default 512)")] int? maxResources = null,
        [Description("Cap on D3D12 recent events per category — bindings/barriers/warnings (default 64)")] int? maxD3d12Events = null,
        [Description("Cap on distinct D3D12 VS/PS pipeline pairs (default 64)")] int? maxDistinctPairs = null,
        [Description("Cap on PSO aggregates with target associations (default 64)")] int? maxPsoAggregates = null)
        => await Http.Get("/api/render/snapshot", new() {
            ["maxResources"] = maxResources?.ToString(),
            ["maxD3d12Events"] = maxD3d12Events?.ToString(),
            ["maxDistinctPairs"] = maxDistinctPairs?.ToString(),
            ["maxPsoAggregates"] = maxPsoAggregates?.ToString(),
        });

    [McpServerTool(Name = "uevr_render_resources")]
    [Description("FrameResourceInspector resource list: per-resource keys (use uevr_render_select_resource), pointers, names, sources, formats, resolutions, tags, seen counts, and flags (is_depth, is_render_target, is_ui, is_swapchain, is_eye, is_velocity_candidate, is_rt_pool, is_transient, is_recent). Sampling only runs when the Resources sidebar is open OR force_resources_sampling is on.")]
    public static async Task<string> Resources(
        [Description("Cap on resources returned (default 1024)")] int? maxResources = null)
        => await Http.Get("/api/render/resources", new() {
            ["maxResources"] = maxResources?.ToString(),
        });

    [McpServerTool(Name = "uevr_render_d3d12")]
    [Description("D3D12Diagnostics snapshot: device/swapchain/queue pointers, render+display dimensions, descriptor heaps (active CBV/SRV/UAV + sampler, plus all tracked), per-frame counters (heap sets/switches, barriers, RTV binds, transient allocations + bytes), currently-bound RTV/DSV context, and tails of recent bindings/barriers/warnings.")]
    public static async Task<string> D3D12(
        [Description("Cap on heaps returned (default 64)")] int? maxHeaps = null,
        [Description("Cap on recent events per category (default 64)")] int? maxEvents = null)
        => await Http.Get("/api/render/d3d12", new() {
            ["maxHeaps"] = maxHeaps?.ToString(),
            ["maxEvents"] = maxEvents?.ToString(),
        });

    [McpServerTool(Name = "uevr_render_shaders")]
    [Description("ShaderOverrideRegistry snapshot: bound VS/PS (backend, hash, override status), current+captured D3D12 pipeline pair, distinct VS/PS pairs with hit counts, D3D12 PSO aggregates with sample_share + likely_targets (RT/DSV names + share), discovered override entries (manifest path, source, profile, compile status, last_error), and recent events.")]
    public static async Task<string> Shaders(
        [Description("Cap on distinct D3D12 pipeline pairs (default 64)")] int? maxDistinctPairs = null,
        [Description("Cap on PSO aggregates (default 64)")] int? maxPsoAggregates = null)
        => await Http.Get("/api/render/shaders", new() {
            ["maxDistinctPairs"] = maxDistinctPairs?.ToString(),
            ["maxPsoAggregates"] = maxPsoAggregates?.ToString(),
        });

    [McpServerTool(Name = "uevr_render_shader_bytecode")]
    [Description("Inspect a recorded D3D12 shader bytecode container by stage+hash. Returns DXBC/DXIL container kind, 16-byte container hash, declared size, chunk list (DXIL/PSV0/RDAT/STAT/ISG1/OSG1/etc.), compiler tag, and optional DXIL disassembly text. Stage accepts 'ps', 'vs', 'cs', 'as', 'ms', or 'any'. Hash comes from uevr_render_shaders bound/current/captured pairs or override entries.")]
    public static async Task<string> ShaderBytecode(
        [Description("Shader stage: 'ps', 'vs', 'cs', 'as', 'ms', or 'any'")] string stage,
        [Description("Shader hash string from uevr_render_shaders")] string hash,
        [Description("true to include DXIL disassembly text; can be large")] bool disassemble = false,
        [Description("Maximum disassembly characters returned when disassemble=true (default 131072)")] int? maxDisassemblyChars = null)
        => await Http.Get("/api/render/shader-bytecode", new() {
            ["stage"] = stage,
            ["hash"] = hash,
            ["disassemble"] = disassemble ? "1" : "0",
            ["maxDisassemblyChars"] = maxDisassemblyChars?.ToString()
        });

    [McpServerTool(Name = "uevr_render_shader_recovered_sources")]
    [Description("Return PDB/source-recovery output for a recorded D3D12 shader bytecode container, when the shipped DXIL/PDB contains source records. This is a smaller source-focused wrapper around uevr_render_shader_bytecode. UE builds often strip PDBs, but non-UE engines sometimes preserve them.")]
    public static async Task<string> ShaderRecoveredSources(
        [Description("Shader stage: 'ps', 'vs', 'cs', 'as', 'ms', or 'any'")] string stage,
        [Description("Shader hash string from uevr_render_shaders")] string hash,
        [Description("Maximum characters to include per recovered source file (default 65536)")] int? maxSourceChars = null)
    {
        var raw = await ShaderBytecode(stage, hash, disassemble: false);
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("shader bytecode", raw));

        var bytecode = root["bytecode"];
        var sources = ArrayProp(bytecode, "recovered_sources");
        var takeChars = Math.Max(1024, maxSourceChars ?? 65536);
        var outSources = new JsonArray();
        foreach (var src in sources)
        {
            var text = StringProp(src, "text") ?? "";
            outSources.Add(new JsonObject
            {
                ["name"] = StringProp(src, "name"),
                ["original_chars"] = text.Length,
                ["truncated"] = text.Length > takeChars,
                ["text"] = text.Length > takeChars ? text[..takeChars] : text
            });
        }

        return JsonText(new
        {
            found = BoolProp(root, "found"),
            stage,
            hash,
            source_count = sources.Count,
            recovered_sources = outSources,
            error = StringProp(bytecode, "error")
        });
    }

    [McpServerTool(Name = "uevr_render_runtime_overrides")]
    [Description("Runtime A/B switch for shader overrides. Keeps manifests loaded but makes the resolver return original shaders/PSOs while disabled. Use this to compare frames with and without overrides without editing manifests or restarting.")]
    public static async Task<string> RuntimeOverrides(
        [Description("true to enable active overrides, false to bypass all active shader overrides")] bool enabled)
        => await Http.Post("/api/render/runtime-overrides", new { enabled });

    [McpServerTool(Name = "uevr_render_preview")]
    [Description("PreviewInfo for the currently-selected FrameResourceInspector resource: backend, format, width/height, texture_id (for in-engine display), status, and backend_note. Returns has_selection=false if nothing is selected. Pair with uevr_render_select_resource.")]
    public static async Task<string> Preview()
        => await Http.Get("/api/render/preview");

    [McpServerTool(Name = "uevr_render_context")]
    [Description("Lightweight context: whether the framework is ready, current renderer (D3D11/D3D12), profile name, persistent dir, and which force-sampling flags are currently set. Use this to verify state before/after toggling force flags.")]
    public static async Task<string> Context()
        => await Http.Get("/api/render/context");

    // ── Mutators ──────────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_render_select_resource")]
    [Description("Set the FrameResourceInspector's currently-selected resource key (from uevr_render_resources). Pass 0 to clear. Triggers preview-SRV creation on the next present, after which uevr_render_preview returns metadata for the texture.")]
    public static async Task<string> SelectResource(
        [Description("Resource key from uevr_render_resources (0 to clear)")] ulong key)
        => await Http.Post("/api/render/selected-resource", new { key });

    [McpServerTool(Name = "uevr_render_force_resources_sampling")]
    [Description("Force the FrameResourceInspector to keep sampling D3D11/D3D12 resources even when the Resources sidebar tab is closed. Required for headless/scripted resource enumeration. Default off; UEVR's normal behavior resets the inspector when the sidebar is hidden.")]
    public static async Task<string> ForceResourcesSampling(
        [Description("true to keep sampling resources, false to honor the sidebar state")] bool enabled)
        => await Http.Post("/api/render/force-resources-sampling", new { enabled });

    [McpServerTool(Name = "uevr_render_force_shader_tracking")]
    [Description("Force ShaderOverrideRegistry tracking on (records distinct VS/PS pairs and PSO aggregates) even when neither the Shaders nor PSO Profiler sidebar is open. Use before kicking off automated PSO/shader analysis.")]
    public static async Task<string> ForceShaderTracking(
        [Description("true to keep shader tracking on, false to honor the sidebar state")] bool enabled)
        => await Http.Post("/api/render/force-shader-tracking", new { enabled });

    [McpServerTool(Name = "uevr_render_force_d3d12_diagnostics")]
    [Description("Force D3D12Diagnostics enabled (heap tracking, barrier recording, RTV/DSV binding capture) even when the DX12 Diagnostics sidebar is closed. Requires a D3D12 game; no-op on D3D11.")]
    public static async Task<string> ForceD3d12Diagnostics(
        [Description("true to keep DX12 diagnostics on, false to honor the sidebar state")] bool enabled)
        => await Http.Post("/api/render/force-d3d12-diagnostics", new { enabled });

    [McpServerTool(Name = "uevr_render_request_shader_reload")]
    [Description("Trigger ShaderOverrideRegistry to re-scan the global and profile override directories on the next present. Use after editing or adding shader override manifests on disk so changes pick up without restarting the game.")]
    public static async Task<string> RequestShaderReload()
        => await Http.Post("/api/render/request-shader-reload", new { });

    [McpServerTool(Name = "uevr_render_write_hlsl_override")]
    [Description("Write a profile-local HLSL shader override manifest plus main.hlsl for a target DX12 shader hash, optionally marking it for per-eye PSO variants. Defaults enabled=false so it is a safe scaffold until you explicitly enable it. Calls shader reload unless reload=false.")]
    public static async Task<string> WriteHlslOverride(
        [Description("Target shader hash from uevr_render_shaders or uevr_render_shader_bytecode")] string targetHash,
        [Description("Shader stage: vs, ps, cs, as, or ms")] string stage,
        [Description("Full HLSL source text to write to main.hlsl")] string hlslSource,
        [Description("Override display/name slug. Default: hlsl_<stage>_<hash>")] string? name = null,
        [Description("HLSL entry point. Default: main")] string entryPoint = "main",
        [Description("Shader model profile. Default follows stage: ps_6_0/vs_6_0/cs_6_0/as_6_5/ms_6_5")] string? profile = null,
        [Description("Compiler backend: dxc, fxc, or auto. Default: dxc")] string compiler = "dxc",
        [Description("Set true to let UEVR create distinct left/right PSO variants for this override")] bool perEyeVariants = false,
        [Description("Whether the manifest starts enabled. Default false for safety.")] bool enabled = false,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var normalizedStage = NormalizeStageArg(stage);
            var hash = NormalizeHashArg(targetHash);
            var root = await ResolveProfileOverrideDir(overrideDir);
            Directory.CreateDirectory(root);

            var overrideName = string.IsNullOrWhiteSpace(name) ? $"hlsl_{normalizedStage}_{hash}" : name!;
            var dir = UniqueChildDirectory(root, overrideName);
            Directory.CreateDirectory(dir);

            var sourcePath = Path.Combine(dir, "main.hlsl");
            var manifestPath = Path.Combine(dir, "manifest.json");
            await File.WriteAllTextAsync(sourcePath, hlslSource);

            var manifest = new JsonObject
            {
                ["backend"] = "dx12",
                ["stage"] = normalizedStage,
                ["target_hash"] = hash,
                ["name"] = overrideName,
                ["enabled"] = enabled,
                ["entry_point"] = string.IsNullOrWhiteSpace(entryPoint) ? "main" : entryPoint,
                ["profile"] = string.IsNullOrWhiteSpace(profile) ? DefaultProfileForStage(normalizedStage) : profile,
                ["compiler"] = string.IsNullOrWhiteSpace(compiler) ? "dxc" : compiler,
                ["per_eye_variants"] = perEyeVariants,
                ["source"] = "main.hlsl"
            };
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions));

            var result = FileResult("hlsl_override", dir, manifestPath);
            result["source_path"] = sourcePath;
            result["enabled"] = enabled;
            result["per_eye_variants"] = perEyeVariants;
            await AttachReloadResult(result, reload);
            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_write_bind_override")]
    [Description("Write a bind-time CBV or root-constant override manifest. This lets UEVR replace a CBV GPU VA or root constants when a matching PSO/stage/eye/root parameter is bound, often fixing stereo matrix/LUT bugs without shader bytecode edits. Defaults enabled=false for safety.")]
    public static async Task<string> WriteBindOverride(
        [Description("Target shader or PSO hash to match, typically from uevr_render_shaders or draw events")] string targetHash,
        [Description("D3D12 root parameter index to override")] int rootParameter,
        [Description("Override data as JSON array or comma-separated u32 values. For cbv this becomes data_u32; for root_constants this becomes values_u32. Optional if kind=cbv and dataHex is supplied.")] string? valuesU32 = null,
        [Description("Override kind: cbv or root_constants. Default cbv.")] string kind = "cbv",
        [Description("Stage selector: any, vs, ps, cs, as, ms. Default any.")] string stage = "any",
        [Description("Pipeline selector: graphics, compute, or any. Default graphics.")] string pipeline = "graphics",
        [Description("Eye selector: any, left, right, unknown, full, or multi. Default any.")] string eye = "any",
        [Description("Optional CBV data as hex bytes. Used only when valuesU32 is empty.")] string? dataHex = null,
        [Description("Root-constant destination offset in u32s. Default 0.")] int destOffset = 0,
        [Description("Override display/name slug. Default: bind_<root>_<hash>")] string? name = null,
        [Description("Whether the manifest starts enabled. Default false for safety.")] bool enabled = false,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var hash = NormalizeHashArg(targetHash);
            var normalizedKind = kind.Trim().ToLowerInvariant() switch
            {
                "cbv" or "constant_buffer" or "constant_buffer_view" => "cbv",
                "root_constant" or "root_constants" or "constants" => "root_constants",
                _ => throw new ArgumentException("kind must be cbv or root_constants")
            };

            var root = await ResolveProfileOverrideDir(overrideDir);
            Directory.CreateDirectory(root);
            var overrideName = string.IsNullOrWhiteSpace(name) ? $"bind_{rootParameter}_{hash}" : name!;
            var dir = UniqueChildDirectory(root, overrideName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "manifest.json");

            var manifest = new JsonObject
            {
                ["kind"] = "bind_override",
                ["target_hash"] = hash,
                ["name"] = overrideName,
                ["enabled"] = enabled,
                ["stage"] = string.IsNullOrWhiteSpace(stage) ? "any" : stage.Trim().ToLowerInvariant(),
                ["pipeline"] = string.IsNullOrWhiteSpace(pipeline) ? "graphics" : pipeline.Trim().ToLowerInvariant(),
                ["eye"] = string.IsNullOrWhiteSpace(eye) ? "any" : eye.Trim().ToLowerInvariant(),
                ["root_parameter"] = rootParameter,
                ["override"] = normalizedKind,
                ["dest_offset"] = Math.Max(0, destOffset)
            };

            if (!string.IsNullOrWhiteSpace(valuesU32))
            {
                var values = ParseJsonArrayArgument(valuesU32, nameof(valuesU32));
                manifest[normalizedKind == "root_constants" ? "values_u32" : "data_u32"] = values;
            }
            else if (!string.IsNullOrWhiteSpace(dataHex) && normalizedKind == "cbv")
            {
                manifest["data_hex"] = dataHex;
            }
            else
            {
                throw new ArgumentException("valuesU32 is required unless kind=cbv and dataHex is supplied");
            }

            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions));

            var result = FileResult("bind_override", dir, manifestPath);
            result["enabled"] = enabled;
            result["root_parameter"] = rootParameter;
            result["override"] = normalizedKind;
            await AttachReloadResult(result, reload);
            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_write_dxil_text_patch")]
    [Description("Write a DXIL disassembly text-patch manifest. UEVR will disassemble the matching DXIL container, apply find/replace edits, reassemble/sign it with DXC, and substitute the patched bytecode at PSO creation. Use for small targeted DXIL edits before reaching for full bitcode transforms.")]
    public static async Task<string> WriteDxilTextPatch(
        [Description("Target shader hash from uevr_render_shaders or uevr_render_shader_bytecode")] string targetHash,
        [Description("Shader stage: vs, ps, cs, as, or ms")] string stage,
        [Description("JSON array of {find, replace} objects, or a single object.")] string replacementsJson,
        [Description("Override display/name slug. Default: dxil_text_<stage>_<hash>")] string? name = null,
        [Description("Set true to let UEVR create distinct left/right PSO variants for this patched shader")] bool perEyeVariants = false,
        [Description("Whether the manifest starts enabled. Default false for safety.")] bool enabled = false,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var normalizedStage = NormalizeStageArg(stage);
            var hash = NormalizeHashArg(targetHash);
            var root = await ResolveProfileOverrideDir(overrideDir);
            Directory.CreateDirectory(root);

            var overrideName = string.IsNullOrWhiteSpace(name) ? $"dxil_text_{normalizedStage}_{hash}" : name!;
            var dir = UniqueChildDirectory(root, overrideName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "manifest.json");
            var patchPath = Path.Combine(dir, "patch.json");

            var parsed = ParseJsonArgument(replacementsJson, nameof(replacementsJson));
            JsonNode patchPayload = parsed switch
            {
                JsonArray => new JsonObject { ["replacements"] = parsed },
                JsonObject obj when obj["replacements"] is JsonArray => obj,
                JsonObject => new JsonObject { ["replacements"] = new JsonArray(parsed) },
                _ => throw new ArgumentException("replacementsJson must be a JSON object or array")
            };

            var manifest = new JsonObject
            {
                ["backend"] = "dx12",
                ["stage"] = normalizedStage,
                ["target_hash"] = hash,
                ["name"] = overrideName,
                ["enabled"] = enabled,
                ["profile"] = DefaultProfileForStage(normalizedStage),
                ["per_eye_variants"] = perEyeVariants,
                ["dxil_text_patch"] = "patch.json"
            };

            await File.WriteAllTextAsync(patchPath, patchPayload.ToJsonString(JsonOptions));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions));

            var result = FileResult("dxil_text_patch", dir, manifestPath);
            result["patch_path"] = patchPath;
            result["enabled"] = enabled;
            result["per_eye_variants"] = perEyeVariants;
            await AttachReloadResult(result, reload);
            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_write_container_patch")]
    [Description("Write a DXIL/DXBC container chunk-edit manifest. Edits are JSON objects like {fourcc:'RDAT', remove:true} or {fourcc:'DXIL', data_hex:'...'}; UEVR applies them with IDxcContainerBuilder, recomputes/signs the container, and substitutes the patched bytecode at PSO creation.")]
    public static async Task<string> WriteContainerPatch(
        [Description("Target shader hash from uevr_render_shaders or uevr_render_shader_bytecode")] string targetHash,
        [Description("Shader stage: vs, ps, cs, as, or ms")] string stage,
        [Description("JSON array of container edits, a single edit object, or {edits:[...]}.")] string editsJson,
        [Description("Override display/name slug. Default: container_<stage>_<hash>")] string? name = null,
        [Description("Set true to let UEVR create distinct left/right PSO variants for this patched shader")] bool perEyeVariants = false,
        [Description("Whether the manifest starts enabled. Default false for safety.")] bool enabled = false,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var normalizedStage = NormalizeStageArg(stage);
            var hash = NormalizeHashArg(targetHash);
            var root = await ResolveProfileOverrideDir(overrideDir);
            Directory.CreateDirectory(root);

            var overrideName = string.IsNullOrWhiteSpace(name) ? $"container_{normalizedStage}_{hash}" : name!;
            var dir = UniqueChildDirectory(root, overrideName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "manifest.json");
            var patchPath = Path.Combine(dir, "container_patch.json");

            var parsed = ParseJsonArgument(editsJson, nameof(editsJson));
            JsonNode patchPayload = parsed switch
            {
                JsonArray => new JsonObject { ["edits"] = parsed },
                JsonObject obj when obj["edits"] is JsonArray => obj,
                JsonObject => new JsonObject { ["edits"] = new JsonArray(parsed) },
                _ => throw new ArgumentException("editsJson must be a JSON object or array")
            };

            var manifest = new JsonObject
            {
                ["backend"] = "dx12",
                ["stage"] = normalizedStage,
                ["target_hash"] = hash,
                ["name"] = overrideName,
                ["enabled"] = enabled,
                ["profile"] = DefaultProfileForStage(normalizedStage),
                ["per_eye_variants"] = perEyeVariants,
                ["container_patch"] = "container_patch.json"
            };

            await File.WriteAllTextAsync(patchPath, patchPayload.ToJsonString(JsonOptions));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions));

            var result = FileResult("container_patch", dir, manifestPath);
            result["patch_path"] = patchPath;
            result["enabled"] = enabled;
            result["per_eye_variants"] = perEyeVariants;
            await AttachReloadResult(result, reload);
            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_write_dxil_transform")]
    [Description("Write a rule-driven DXIL stereo transform manifest. UEVR will run dxil-patch transform on the matching container, applying typed rules such as redirect_handle, rewrite_cbuffer_load_index, and replace_cbuffer_extract_literal, then reassemble/sign and substitute the transformed bytecode at PSO creation.")]
    public static async Task<string> WriteDxilTransform(
        [Description("Target shader hash from uevr_render_shaders or uevr_render_shader_bytecode")] string targetHash,
        [Description("Shader stage: vs, ps, cs, as, or ms")] string stage,
        [Description("JSON transform rule array, a single rule object, or {transforms:[...]}. Rule kinds include redirect_handle, rewrite_cbuffer_load_index, replace_cbuffer_extract_literal, replace_text, replace_regex, insert_before, insert_after, and require_regex.")] string rulesJson,
        [Description("Override display/name slug. Default: dxil_transform_<stage>_<hash>")] string? name = null,
        [Description("Optional target eye label recorded in transform.json for operator clarity, e.g. left or right. Runtime selection still uses perEyeVariants.")] string? eye = null,
        [Description("Set true to let UEVR create distinct left/right PSO variants for this transformed shader. Default true because stereo transforms usually target one eye.")] bool perEyeVariants = true,
        [Description("Whether the manifest starts enabled. Default false for safety.")] bool enabled = false,
        [Description("Optional dxil-patch.exe path override to write as manifest patch_tool. Default uses UEVR_DXIL_PATCH_TOOL or dxil-patch.exe beside UEVRBackend.dll.")] string? patchTool = null,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var normalizedStage = NormalizeStageArg(stage);
            var hash = NormalizeHashArg(targetHash);
            var root = await ResolveProfileOverrideDir(overrideDir);
            Directory.CreateDirectory(root);

            var overrideName = string.IsNullOrWhiteSpace(name) ? $"dxil_transform_{normalizedStage}_{hash}" : name!;
            var dir = UniqueChildDirectory(root, overrideName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "manifest.json");
            var transformPath = Path.Combine(dir, "transform.json");

            var parsed = ParseJsonArgument(rulesJson, nameof(rulesJson));
            JsonNode transformPayload = parsed switch
            {
                JsonArray => new JsonObject { ["transforms"] = parsed },
                JsonObject obj when obj["transforms"] is JsonArray => obj,
                JsonObject obj when obj["rules"] is JsonArray => obj,
                JsonObject obj when obj["patches"] is JsonArray => obj,
                JsonObject => new JsonObject { ["transforms"] = new JsonArray(parsed) },
                _ => throw new ArgumentException("rulesJson must be a JSON object or array")
            };

            if (!string.IsNullOrWhiteSpace(eye) && transformPayload is JsonObject payloadObj)
                payloadObj["eye"] = eye!.Trim().ToLowerInvariant();

            var manifest = new JsonObject
            {
                ["backend"] = "dx12",
                ["stage"] = normalizedStage,
                ["target_hash"] = hash,
                ["name"] = overrideName,
                ["enabled"] = enabled,
                ["profile"] = DefaultProfileForStage(normalizedStage),
                ["per_eye_variants"] = perEyeVariants,
                ["dxil_transform"] = "transform.json"
            };

            if (!string.IsNullOrWhiteSpace(patchTool))
                manifest["patch_tool"] = patchTool;

            await File.WriteAllTextAsync(transformPath, transformPayload.ToJsonString(JsonOptions));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions));

            var result = FileResult("dxil_transform", dir, manifestPath);
            result["transform_path"] = transformPath;
            result["enabled"] = enabled;
            result["per_eye_variants"] = perEyeVariants;
            if (!string.IsNullOrWhiteSpace(eye)) result["eye"] = eye!.Trim().ToLowerInvariant();
            await AttachReloadResult(result, reload);
            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_write_dxil_semantic_transform")]
    [Description("Write a DXC-internals semantic DXIL transform manifest. UEVR will run uevr-dxil-semantic-pass on the matching container, using LLVM/DxilModule rules such as rewrite_cbuffer_load_index, redirect_handle, and replace_cbuffer_extract_literal, then substitute the signed output at PSO creation.")]
    public static async Task<string> WriteDxilSemanticTransform(
        [Description("Target shader hash from uevr_render_shaders or uevr_render_shader_bytecode")] string targetHash,
        [Description("Shader stage: vs, ps, cs, as, or ms")] string stage,
        [Description("JSON semantic transform rule array, a single rule object, or {transforms:[...]}.")] string rulesJson,
        [Description("Override display/name slug. Default: dxil_semantic_<stage>_<hash>")] string? name = null,
        [Description("Optional target eye label recorded in transform.json for operator clarity, e.g. left or right. Runtime selection still uses perEyeVariants.")] string? eye = null,
        [Description("Set true to let UEVR create distinct left/right PSO variants for this transformed shader. Default true because stereo semantic transforms usually target one eye.")] bool perEyeVariants = true,
        [Description("Whether the manifest starts enabled. Default false for safety.")] bool enabled = false,
        [Description("Optional uevr-dxil-semantic-pass.exe path override to write as manifest semantic_tool. Default uses UEVR_DXIL_SEMANTIC_TOOL or the executable beside UEVRBackend.dll.")] string? semanticTool = null,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var normalizedStage = NormalizeStageArg(stage);
            var hash = NormalizeHashArg(targetHash);
            var root = await ResolveProfileOverrideDir(overrideDir);
            Directory.CreateDirectory(root);

            var overrideName = string.IsNullOrWhiteSpace(name) ? $"dxil_semantic_{normalizedStage}_{hash}" : name!;
            var dir = UniqueChildDirectory(root, overrideName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "manifest.json");
            var transformPath = Path.Combine(dir, "semantic_transform.json");

            var parsed = ParseJsonArgument(rulesJson, nameof(rulesJson));
            JsonNode transformPayload = parsed switch
            {
                JsonArray => new JsonObject { ["transforms"] = parsed },
                JsonObject obj when obj["transforms"] is JsonArray => obj,
                JsonObject obj when obj["rules"] is JsonArray => obj,
                JsonObject obj when obj["patches"] is JsonArray => obj,
                JsonObject => new JsonObject { ["transforms"] = new JsonArray(parsed) },
                _ => throw new ArgumentException("rulesJson must be a JSON object or array")
            };

            if (!string.IsNullOrWhiteSpace(eye) && transformPayload is JsonObject payloadObj)
                payloadObj["eye"] = eye!.Trim().ToLowerInvariant();

            var manifest = new JsonObject
            {
                ["backend"] = "dx12",
                ["stage"] = normalizedStage,
                ["target_hash"] = hash,
                ["name"] = overrideName,
                ["enabled"] = enabled,
                ["profile"] = DefaultProfileForStage(normalizedStage),
                ["per_eye_variants"] = perEyeVariants,
                ["dxil_semantic_transform"] = "semantic_transform.json"
            };

            if (!string.IsNullOrWhiteSpace(semanticTool))
                manifest["semantic_tool"] = semanticTool;

            await File.WriteAllTextAsync(transformPath, transformPayload.ToJsonString(JsonOptions));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions));

            var result = FileResult("dxil_semantic_transform", dir, manifestPath);
            result["transform_path"] = transformPath;
            result["enabled"] = enabled;
            result["per_eye_variants"] = perEyeVariants;
            if (!string.IsNullOrWhiteSpace(eye)) result["eye"] = eye!.Trim().ToLowerInvariant();
            await AttachReloadResult(result, reload);
            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_write_per_eye_shader_payloads")]
    [Description("Write a profile-local per-eye shader payload manifest. Supports left/right bytecode files, DXIL transform JSON, DXIL semantic transform JSON, DXIL text patch JSON, or container patch JSON via manifest keys such as left_dxil_transform, left_dxil_semantic_transform, right_bytecode, and right_container_patch. Use this when a shader fix must affect only one eye while sharing the same original shader hash.")]
    public static async Task<string> WritePerEyeShaderPayloads(
        [Description("Target shader hash from uevr_render_shaders or uevr_render_shader_bytecode")] string targetHash,
        [Description("Shader stage: vs, ps, cs, as, or ms")] string stage,
        [Description("Optional left-eye replacement bytecode path. Mutually exclusive with left*Json payloads.")] string? leftBytecodePath = null,
        [Description("Optional right-eye replacement bytecode path. Mutually exclusive with right*Json payloads.")] string? rightBytecodePath = null,
        [Description("Optional left-eye DXIL transform JSON: array, single rule, or {transforms:[...]}.")] string? leftTransformJson = null,
        [Description("Optional right-eye DXIL transform JSON: array, single rule, or {transforms:[...]}.")] string? rightTransformJson = null,
        [Description("Optional left-eye DXC-internals semantic transform JSON: array, single rule, or {transforms:[...]}.")] string? leftSemanticTransformJson = null,
        [Description("Optional right-eye DXC-internals semantic transform JSON: array, single rule, or {transforms:[...]}.")] string? rightSemanticTransformJson = null,
        [Description("Optional left-eye DXIL text patch JSON: array, single replacement, or {replacements:[...]}.")] string? leftTextPatchJson = null,
        [Description("Optional right-eye DXIL text patch JSON: array, single replacement, or {replacements:[...]}.")] string? rightTextPatchJson = null,
        [Description("Optional left-eye container patch JSON: array, single edit, or {edits:[...]}.")] string? leftContainerPatchJson = null,
        [Description("Optional right-eye container patch JSON: array, single edit, or {edits:[...]}.")] string? rightContainerPatchJson = null,
        [Description("Override display/name slug. Default: per_eye_<stage>_<hash>")] string? name = null,
        [Description("Whether the manifest starts enabled. Default false for safety.")] bool enabled = false,
        [Description("Optional dxil-patch.exe path override to write as manifest patch_tool.")] string? patchTool = null,
        [Description("Optional uevr-dxil-semantic-pass.exe path override to write as manifest semantic_tool.")] string? semanticTool = null,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var normalizedStage = NormalizeStageArg(stage);
            var hash = NormalizeHashArg(targetHash);
            var root = await ResolveProfileOverrideDir(overrideDir);
            Directory.CreateDirectory(root);

            var overrideName = string.IsNullOrWhiteSpace(name) ? $"per_eye_{normalizedStage}_{hash}" : name!;
            var dir = UniqueChildDirectory(root, overrideName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "manifest.json");

            static int PayloadCount(params string?[] values)
                => values.Count(v => !string.IsNullOrWhiteSpace(v));

            if (PayloadCount(leftBytecodePath, leftTransformJson, leftSemanticTransformJson, leftTextPatchJson, leftContainerPatchJson) > 1)
                throw new ArgumentException("left eye may specify only one payload kind");
            if (PayloadCount(rightBytecodePath, rightTransformJson, rightSemanticTransformJson, rightTextPatchJson, rightContainerPatchJson) > 1)
                throw new ArgumentException("right eye may specify only one payload kind");
            if (PayloadCount(leftBytecodePath, leftTransformJson, leftSemanticTransformJson, leftTextPatchJson, leftContainerPatchJson,
                             rightBytecodePath, rightTransformJson, rightSemanticTransformJson, rightTextPatchJson, rightContainerPatchJson) == 0)
                throw new ArgumentException("at least one left/right payload is required");

            static JsonNode WrapRules(string jsonText)
            {
                var parsed = ParseJsonArgument(jsonText, nameof(jsonText));
                return parsed switch
                {
                    JsonArray => new JsonObject { ["transforms"] = parsed },
                    JsonObject obj when obj["transforms"] is JsonArray => obj,
                    JsonObject obj when obj["rules"] is JsonArray => obj,
                    JsonObject obj when obj["patches"] is JsonArray => obj,
                    JsonObject => new JsonObject { ["transforms"] = new JsonArray(parsed) },
                    _ => throw new ArgumentException("transform JSON must be an object or array")
                };
            }

            static JsonNode WrapTextPatch(string jsonText)
            {
                var parsed = ParseJsonArgument(jsonText, nameof(jsonText));
                return parsed switch
                {
                    JsonArray => new JsonObject { ["replacements"] = parsed },
                    JsonObject obj when obj["replacements"] is JsonArray => obj,
                    JsonObject => new JsonObject { ["replacements"] = new JsonArray(parsed) },
                    _ => throw new ArgumentException("text patch JSON must be an object or array")
                };
            }

            static JsonNode WrapContainerPatch(string jsonText)
            {
                var parsed = ParseJsonArgument(jsonText, nameof(jsonText));
                return parsed switch
                {
                    JsonArray => new JsonObject { ["edits"] = parsed },
                    JsonObject obj when obj["edits"] is JsonArray => obj,
                    JsonObject => new JsonObject { ["edits"] = new JsonArray(parsed) },
                    _ => throw new ArgumentException("container patch JSON must be an object or array")
                };
            }

            var manifest = new JsonObject
            {
                ["backend"] = "dx12",
                ["stage"] = normalizedStage,
                ["target_hash"] = hash,
                ["name"] = overrideName,
                ["enabled"] = enabled,
                ["profile"] = DefaultProfileForStage(normalizedStage),
                ["per_eye_variants"] = true
            };

            var writtenPayloads = new JsonObject();
            async Task AddPayload(string side, string? bytecodePath, string? transformJson, string? semanticTransformJson, string? textPatchJson, string? containerPatchJson)
            {
                if (!string.IsNullOrWhiteSpace(bytecodePath))
                {
                    manifest[$"{side}_bytecode"] = bytecodePath;
                    writtenPayloads[$"{side}_bytecode"] = bytecodePath;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(transformJson))
                {
                    var file = $"{side}_transform.json";
                    await File.WriteAllTextAsync(Path.Combine(dir, file), WrapRules(transformJson!).ToJsonString(JsonOptions));
                    manifest[$"{side}_dxil_transform"] = file;
                    writtenPayloads[$"{side}_dxil_transform"] = Path.Combine(dir, file);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(semanticTransformJson))
                {
                    var file = $"{side}_semantic_transform.json";
                    await File.WriteAllTextAsync(Path.Combine(dir, file), WrapRules(semanticTransformJson!).ToJsonString(JsonOptions));
                    manifest[$"{side}_dxil_semantic_transform"] = file;
                    writtenPayloads[$"{side}_dxil_semantic_transform"] = Path.Combine(dir, file);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(textPatchJson))
                {
                    var file = $"{side}_text_patch.json";
                    await File.WriteAllTextAsync(Path.Combine(dir, file), WrapTextPatch(textPatchJson!).ToJsonString(JsonOptions));
                    manifest[$"{side}_dxil_text_patch"] = file;
                    writtenPayloads[$"{side}_dxil_text_patch"] = Path.Combine(dir, file);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(containerPatchJson))
                {
                    var file = $"{side}_container_patch.json";
                    await File.WriteAllTextAsync(Path.Combine(dir, file), WrapContainerPatch(containerPatchJson!).ToJsonString(JsonOptions));
                    manifest[$"{side}_container_patch"] = file;
                    writtenPayloads[$"{side}_container_patch"] = Path.Combine(dir, file);
                }
            }

            await AddPayload("left", leftBytecodePath, leftTransformJson, leftSemanticTransformJson, leftTextPatchJson, leftContainerPatchJson);
            await AddPayload("right", rightBytecodePath, rightTransformJson, rightSemanticTransformJson, rightTextPatchJson, rightContainerPatchJson);

            if (!string.IsNullOrWhiteSpace(patchTool))
                manifest["patch_tool"] = patchTool;
            if (!string.IsNullOrWhiteSpace(semanticTool))
                manifest["semantic_tool"] = semanticTool;

            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions));

            var result = FileResult("per_eye_shader_payloads", dir, manifestPath);
            result["enabled"] = enabled;
            result["per_eye_variants"] = true;
            result["payloads"] = writtenPayloads;
            await AttachReloadResult(result, reload);
            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_capture_next_d3d12_change")]
    [Description("Arm the 'capture next D3D12 pipeline change' trigger. The next time the bound VS/PS pair differs from the previous frame, the registry stores it as the 'captured pair'. Useful for isolating a specific draw call after triggering a UI/menu transition. Read back via uevr_render_shaders → captured_d3d12_pair.")]
    public static async Task<string> CaptureNextD3D12Change()
        => await Http.Post("/api/render/capture-next-d3d12-change", new { });

    [McpServerTool(Name = "uevr_render_clear_captured_d3d12_change")]
    [Description("Clear the captured D3D12 pipeline pair (the one stored by capture-next-d3d12-change). Disarms any pending capture too.")]
    public static async Task<string> ClearCapturedD3D12Change()
        => await Http.Post("/api/render/clear-captured-d3d12-change", new { });

    [McpServerTool(Name = "uevr_render_reset_d3d12")]
    [Description("Reset all accumulated D3D12Diagnostics state: tracked heaps, resources, RTV/DSV descriptors, recent bindings/barriers/warnings, and per-frame counters. Use after a level transition or to clear stale entries before sampling a specific scenario.")]
    public static async Task<string> ResetD3D12()
        => await Http.Post("/api/render/reset-d3d12", new { });

    [McpServerTool(Name = "uevr_render_hunter_capture_active_override_stub")]
    [Description("Capture the currently-selected Shader Hunter PS/VS/CS hash as a disabled override scaffold in the active profile's shader_overrides directory. Writes manifest.json and main.hlsl with the correct target hash/profile/compiler fields, then returns paths. Stage accepts 'ps', 'vs', or 'cs'.")]
    public static async Task<string> HunterCaptureActiveOverrideStub(
        [Description("'ps', 'vs', or 'cs'")] string stage = "ps")
        => await Http.Post("/api/render/hunter/capture-active-override-stub", new { stage });

    // ── Disk exports ─────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_render_export_d3d12_pairs")]
    [Description("Export the distinct D3D12 VS/PS pipeline pairs to disk in the UEVR persistent dir. Returns the written path. Format is 'json' (default) or 'csv'. CSV is friendlier for spreadsheet inspection; JSON preserves full structure.")]
    public static async Task<string> ExportD3D12Pairs(
        [Description("'json' (default) or 'csv'")] string? format = null)
        => await Http.Post("/api/render/export-d3d12-pairs", new { format });

    [McpServerTool(Name = "uevr_render_export_bundle")]
    [Description("Export a full RenderAnalysisExport bundle to <persistent_dir>/render_inspector/bundles/<timestamp>/: resources.json+csv, dx12_diagnostics.json, shader_pairs.json+csv, pso_profiler.json+csv, overrides.json, plus bundle_manifest.json. The same set the Render Inspector's 'Export Render Analysis Bundle' button writes. Returns bundle_dir and the file list.")]
    public static async Task<string> ExportBundle(
        [Description("Profile name to embed in manifest (default: UEVR persistent dir leaf)")] string? profileName = null,
        [Description("Backend label to embed in manifest, e.g. 'D3D12' (default: detected from framework)")] string? backend = null)
        => await Http.Post("/api/render/export-bundle", new { profileName, backend });

    [McpServerTool(Name = "uevr_render_export_frame_pair_diff")]
    [Description("Write the current D3D12 draw/bind snapshot to <persistent>/render_inspector/frame_diffs. The file includes recent draw events, root binds, decoded root signatures, descriptor read producer lineage, CBV/root-constant hashes, and the symmetry oracle. Use this when attaching a compact left/right render-diff artifact to a bug report. NOTE: this is the live (no capture session) snapshot; for the richer capture-session bundle (events.jsonl + eye_diff.json + lineage.json with the limiter), use uevr_render_stereo_forensics / uevr_forensics_eye_diff / uevr_forensics_lineage.")]
    public static async Task<string> ExportFramePairDiff(
        [Description("Maximum recent draw/root events included in the file (default 512)")] int? maxEvents = null)
        => await Http.Post("/api/render/export-frame-pair-diff", new { maxEvents });

    // ── DXIL/root-bind analysis helpers ──────────────────────────────

    [McpServerTool(Name = "uevr_render_root_signatures")]
    [Description("Curated view of decoded D3D12 root signatures from D3D12CreateVersionedRootSignatureDeserializer. Shows each root signature pointer, version, flags, parameter count, static samplers, decode errors, and optionally every root parameter/range (CBV/SRV/UAV/descriptor table/register space). Use this to map root param indices from draw events to shader binding slots.")]
    public static async Task<string> RootSignatures(
        [Description("Maximum root signatures returned (default 32)")] int? maxSignatures = null,
        [Description("Include full parameter/range arrays (default true)")] bool includeParameters = true)
    {
        var raw = await Http.Get("/api/render/d3d12", new() { ["maxEvents"] = "16", ["maxHeaps"] = "16" });
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("d3d12", raw));

        var rootSigs = ArrayProp(root, "root_signatures");
        var take = Math.Max(1, maxSignatures ?? 32);
        var result = new JsonObject
        {
            ["available"] = BoolProp(root, "available"),
            ["frame"] = CloneNode(root["frame"]),
            ["root_signature_count"] = rootSigs.Count,
            ["returned_count"] = Math.Min(rootSigs.Count, take),
            ["decoded_count"] = 0,
            ["decode_error_count"] = 0,
            ["root_signatures"] = new JsonArray()
        };

        var outArr = result["root_signatures"]!.AsArray();
        foreach (var rs in rootSigs.Take(take))
        {
            var decodeError = StringProp(rs, "decode_error") ?? "";
            if (string.IsNullOrWhiteSpace(decodeError)) result["decoded_count"] = LongProp(result, "decoded_count") + 1;
            else result["decode_error_count"] = LongProp(result, "decode_error_count") + 1;

            var item = new JsonObject
            {
                ["pointer"] = StringProp(rs, "pointer"),
                ["first_seen_frame"] = CloneNode(rs?["first_seen_frame"]),
                ["last_seen_frame"] = CloneNode(rs?["last_seen_frame"]),
                ["blob_size"] = CloneNode(rs?["blob_size"]),
                ["version"] = StringProp(rs, "version"),
                ["flags"] = StringProp(rs, "flags"),
                ["static_sampler_count"] = CloneNode(rs?["static_sampler_count"]),
                ["parameter_count"] = (rs?["parameters"] as JsonArray)?.Count ?? 0,
                ["decode_error"] = decodeError
            };
            if (includeParameters)
                item["parameters"] = CloneNode(rs?["parameters"]);
            outArr.Add(item);
        }

        return result.ToJsonString(JsonOptions);
    }

    [McpServerTool(Name = "uevr_render_draw_events")]
    [Description("Filtered recent D3D12 draw/dispatch event log. Each event includes draw_index, PSO, root signature, eye bucket, bound root CBV/SRV/UAV/descriptor-table values, CBV/root-constant hashes, descriptor table resource hashes, RTV0 producer info, and descriptor_reads. Use pipelineState/rootSignature/eyeBucket filters to isolate a bad draw.")]
    public static async Task<string> DrawEvents(
        [Description("Maximum recent draw events to request from UEVR (default 256)")] int? maxEvents = null,
        [Description("Optional pipeline_state pointer filter, e.g. 0x1234")] string? pipelineState = null,
        [Description("Optional root_signature pointer filter, e.g. 0x1234")] string? rootSignature = null,
        [Description("Optional eye bucket filter: 1=left, 2=right, 0=unknown")] int? eyeBucket = null,
        [Description("If true, return only events with descriptor_reads entries")] bool onlyDescriptorReads = false)
    {
        var raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = (maxEvents ?? 256).ToString(),
            ["maxHeaps"] = "16"
        });
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("d3d12", raw));

        var events = ArrayProp(root, "recent_draw_events");
        var outEvents = new JsonArray();
        var byEye = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in events)
        {
            if (!string.IsNullOrWhiteSpace(pipelineState) &&
                !string.Equals(StringProp(ev, "pipeline_state"), pipelineState, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(rootSignature) &&
                !string.Equals(StringProp(ev, "root_signature"), rootSignature, StringComparison.OrdinalIgnoreCase))
                continue;
            if (eyeBucket is int wantedEye && LongProp(ev, "eye_bucket", -999) != wantedEye)
                continue;
            if (onlyDescriptorReads && ArrayProp(ev, "descriptor_reads").Count == 0)
                continue;

            var eye = LongProp(ev, "eye_bucket").ToString();
            byEye[eye] = byEye.GetValueOrDefault(eye) + 1;
            var kind = StringProp(ev, "kind") ?? "unknown";
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            outEvents.Add(CloneNode(ev));
        }

        return JsonText(new
        {
            available = BoolProp(root, "available"),
            frame = root["frame"],
            requested_events = maxEvents ?? 256,
            matched_events = outEvents.Count,
            by_eye_bucket = byEye,
            by_kind = byKind,
            events = outEvents
        });
    }

    [McpServerTool(Name = "uevr_render_symmetry_oracle")]
    [Description("Live (no capture session needed) D3D12 auto-symmetry oracle plus sample left/right draw events for asymmetric PSOs. Flags PSOs where left/right draw counts differ or where per-eye resource/root-bind fingerprints diverge. Good first quick-triage call after sampling a broken stereo frame. For a deeper, capture-session-based per-eye diff (descriptor slices, CBV-content hashes, producer lineage, pair scores) use the StereoForensics path instead: arm with uevr_render_stereo_forensics_arm, then read uevr_forensics_eye_diff.")]
    public static async Task<string> SymmetryOracle(
        [Description("Maximum recent draw events to analyze (default 512)")] int? maxEvents = null,
        [Description("Maximum asymmetric PSOs to include with sample events (default 16)")] int? maxAsymmetricPsos = null)
    {
        var raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = (maxEvents ?? 512).ToString(),
            ["maxHeaps"] = "16"
        });
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("d3d12", raw));

        var oracle = root["symmetry_oracle"];
        var drawEvents = ArrayProp(root, "recent_draw_events");
        var asymmetric = ArrayProp(oracle, "asymmetric_psos");
        var samples = new JsonArray();

        foreach (var pso in asymmetric.Take(Math.Max(1, maxAsymmetricPsos ?? 16)))
        {
            var psoPtr = StringProp(pso, "pipeline_state");
            var psoSamples = new JsonArray();
            foreach (var ev in drawEvents)
            {
                if (!string.Equals(StringProp(ev, "pipeline_state"), psoPtr, StringComparison.OrdinalIgnoreCase))
                    continue;
                psoSamples.Add(CloneNode(ev));
                if (psoSamples.Count >= 6) break;
            }
            samples.Add(new JsonObject
            {
                ["pipeline_state"] = psoPtr,
                ["oracle"] = CloneNode(pso),
                ["sample_draw_events"] = psoSamples
            });
        }

        var hints = new JsonArray();
        if (LongProp(oracle, "asymmetric_pso_count") > 0)
            hints.Add("Inspect sample_draw_events for mismatched root_signature, descriptor_reads, root CBV hashes, or descriptor-table resource hashes.");
        if (LongProp(oracle, "recent_draws_analyzed") == 0)
            hints.Add("No draw events analyzed. Enable uevr_render_force_d3d12_diagnostics(true), wait a few frames, then retry.");

        return JsonText(new
        {
            available = BoolProp(root, "available"),
            frame = root["frame"],
            oracle,
            asymmetric_samples = samples,
            hints
        });
    }

    [McpServerTool(Name = "uevr_render_descriptor_lineage")]
    [Description("Summarize descriptor read lineage from recent draw events. Reports descriptor_reads entries with resource pointer, root parameter, descriptor index, descriptor type, and most-recent producer draw/PSO when known. Filter by resource, pipelineState, or rootParameter to chase bugs like one eye sampling the wrong LUT/RT.")]
    public static async Task<string> DescriptorLineage(
        [Description("Maximum recent draw events to analyze (default 512)")] int? maxEvents = null,
        [Description("Optional resource pointer filter from descriptor_reads[].resource, e.g. 0x1234")] string? resource = null,
        [Description("Optional pipeline_state pointer filter, e.g. 0x1234")] string? pipelineState = null,
        [Description("Optional root parameter index filter")] int? rootParameter = null)
    {
        var raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = (maxEvents ?? 512).ToString(),
            ["maxHeaps"] = "16"
        });
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("d3d12", raw));

        var samples = new JsonArray();
        var producerLinks = 0;
        var byResource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byRootParam = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in ArrayProp(root, "recent_draw_events"))
        {
            if (!string.IsNullOrWhiteSpace(pipelineState) &&
                !string.Equals(StringProp(ev, "pipeline_state"), pipelineState, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var read in ArrayProp(ev, "descriptor_reads"))
            {
                if (!string.IsNullOrWhiteSpace(resource) &&
                    !string.Equals(StringProp(read, "resource"), resource, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (rootParameter is int rp && LongProp(read, "root_parameter", -1) != rp)
                    continue;

                var res = StringProp(read, "resource") ?? "0x0";
                byResource[res] = byResource.GetValueOrDefault(res) + 1;
                var rootKey = LongProp(read, "root_parameter", -1).ToString();
                byRootParam[rootKey] = byRootParam.GetValueOrDefault(rootKey) + 1;
                if (LongProp(read, "producer_draw") != 0 || !string.Equals(StringProp(read, "producer_pso"), "0x0", StringComparison.OrdinalIgnoreCase))
                    producerLinks++;

                samples.Add(new JsonObject
                {
                    ["frame"] = CloneNode(ev?["frame"]),
                    ["draw_index"] = CloneNode(ev?["draw_index"]),
                    ["eye_bucket"] = CloneNode(ev?["eye_bucket"]),
                    ["pipeline_state"] = StringProp(ev, "pipeline_state"),
                    ["root_signature"] = StringProp(ev, "root_signature"),
                    ["rtv0_resource"] = StringProp(ev, "rtv0_resource"),
                    ["read"] = CloneNode(read)
                });
            }
        }

        return JsonText(new
        {
            available = BoolProp(root, "available"),
            frame = root["frame"],
            matched_reads = samples.Count,
            reads_with_known_producer = producerLinks,
            by_resource = byResource.OrderByDescending(kv => kv.Value).Take(32).ToDictionary(kv => kv.Key, kv => kv.Value),
            by_root_parameter = byRootParam.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            samples
        });
    }

    [McpServerTool(Name = "uevr_render_pso_churn")]
    [Description("Summarize D3D12 PSO creation churn from ShaderOverrideRegistry. Reports current-frame creations, recent 120-frame totals, tracked PSO count, and a warning when PSOs are being regenerated fast enough that hash-based overrides may go stale.")]
    public static async Task<string> PsoChurn(
        [Description("Cap on distinct D3D12 pipeline pairs requested from shader snapshot (default 16)")] int? maxDistinctPairs = null,
        [Description("Cap on PSO aggregates requested from shader snapshot (default 16)")] int? maxPsoAggregates = null)
    {
        var raw = await Http.Get("/api/render/shaders", new() {
            ["maxDistinctPairs"] = (maxDistinctPairs ?? 16).ToString(),
            ["maxPsoAggregates"] = (maxPsoAggregates ?? 16).ToString()
        });
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("shaders", raw));

        var churn = root["d3d12_pso_churn"];
        var recentTotal = LongProp(churn, "recent_total_creations");
        var currentTotal = LongProp(churn, "current_frame_total_creations");
        var hints = new JsonArray();
        if (currentTotal > 20)
            hints.Add("High current-frame PSO creation. Wait for load to settle before creating hash-based overrides.");
        if (recentTotal > 240)
            hints.Add("High recent PSO churn. Overrides may miss if the game is regenerating PSOs during level/material streaming.");

        return JsonText(new
        {
            frame = root["frame"],
            runtime_overrides_enabled = root["runtime_overrides_enabled"],
            churn,
            hints
        });
    }

    [McpServerTool(Name = "uevr_render_pipeline_cache_events")]
    [Description("Return recent D3D12 pipeline/library/PSO provenance events captured by UEVR: CreateGraphicsPipelineState, CreateComputePipelineState, CreatePipelineState streams, CreatePipelineLibrary, ID3D12PipelineLibrary Store/Load calls, optional cached-PSO stripping, and SetPipelineState calls for PSOs UEVR did not see being created. This is the first place to check when an override target is visible at draw time but never appears in PSO creation hooks.")]
    public static async Task<string> PipelineCacheEvents(
        [Description("Maximum recent D3D12 events requested from UEVR (default 256)")] int? maxEvents = null,
        [Description("If true, only return events that imply a possible override blocker: cached PSO, stripped cache, untracked PSO, or failed library load/store. Default false.")] bool onlyProblematic = false)
    {
        var raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = (maxEvents ?? 256).ToString(),
            ["maxHeaps"] = "16"
        });
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("d3d12", raw));

        var events = new JsonArray();
        var actions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cached = 0;
        var untracked = 0;
        var stripped = 0;
        var rawEventCount = 0;

        foreach (var ev in ArrayProp(root, "recent_pipeline_cache_events"))
        {
            rawEventCount++;
            var action = StringProp(ev, "action") ?? "";
            actions[action] = actions.GetValueOrDefault(action) + 1;
            var hasCached = BoolProp(ev, "has_cached_pso");
            var wasStripped = BoolProp(ev, "stripped_cached_pso");
            var isUntracked = action.Equals("set_untracked_pso", StringComparison.OrdinalIgnoreCase);
            if (hasCached) cached++;
            if (wasStripped) stripped++;
            if (isUntracked) untracked++;

            var result = StringProp(ev, "result") ?? "0x0";
            var failed = !result.Equals("0x0", StringComparison.OrdinalIgnoreCase);
            if (onlyProblematic && !hasCached && !wasStripped && !isUntracked && !failed)
                continue;
            events.Add(CloneNode(ev));
        }

        var hints = new JsonArray();
        if (rawEventCount == 0)
            hints.Add("No pipeline cache events were captured in the requested window. Force D3D12 diagnostics on before launch or before the PSO load window if possible.");
        else if (events.Count == 0 && onlyProblematic)
            hints.Add("PSO provenance events were captured, but none matched the problematic filter.");
        if (untracked > 0)
            hints.Add("SetPipelineState saw untracked PSOs. Creation happened before UEVR injection or via a path not represented by current creation records; use pipeline-library events, cached-PSO stripping, or pre-injection hooks.");
        if (cached > 0)
            hints.Add("CreatePipelineState streams carried CACHED_PSO data. If shader bytecode is missing or overrides do not apply, relaunch once with UEVR_D3D12_STRIP_CACHED_PSO=1 as a diagnostic to force runtime compile from stream bytecode when present.");
        if (stripped > 0)
            hints.Add("Cached PSO blobs were stripped this run. If PSO creation now exposes shader bytecode, leave stripping as a diagnostic only and move the real fix to a normal manifest/override path.");

        return JsonText(new JsonObject
        {
            ["available"] = BoolProp(root, "available"),
            ["frame"] = CloneNode(root["frame"]),
            ["raw_event_count"] = rawEventCount,
            ["event_count"] = events.Count,
            ["action_counts"] = JsonSerializer.SerializeToNode(actions, JsonOptions),
            ["cached_pso_events"] = cached,
            ["stripped_cached_pso_events"] = stripped,
            ["untracked_pso_events"] = untracked,
            ["events"] = events,
            ["hints"] = hints
        });
    }

    [McpServerTool(Name = "uevr_render_override_status")]
    [Description("Explain whether a shader override target hash is currently reachable: matching manifest entries, tracked PSO aggregates/pairs, replacement PSO evidence, and pipeline-cache blockers such as untracked PSO binds. Use this before assuming a DXIL/HLSL override failed semantically; it may simply never have reached the PSO.")]
    public static async Task<string> OverrideStatus(
        [Description("Target shader hash or CRC, with or without 0x.")] string targetHash,
        [Description("Optional shader stage label for the report: any, ps, vs, cs, as, ms. Default any.")] string stage = "any")
    {
        var hash = NormalizeHashArg(targetHash);
        var shadersRaw = await Http.Get("/api/render/shaders", new() {
            ["maxDistinctPairs"] = "1024",
            ["maxPsoAggregates"] = "4096"
        });
        var d3d12Raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = "256",
            ["maxHeaps"] = "16"
        });

        var shaders = ParseNode(shadersRaw);
        if (shaders is null) return JsonText(ParseError("shaders", shadersRaw));
        var d3d12 = ParseNode(d3d12Raw);

        var matchingOverrides = new JsonArray();
        foreach (var ov in ArrayProp(shaders, "overrides"))
            if (JsonContainsText(ov, hash))
                matchingOverrides.Add(CloneNode(ov));

        var matchingBindOverrides = new JsonArray();
        foreach (var ov in ArrayProp(shaders, "bind_overrides"))
            if (JsonContainsText(ov, hash))
                matchingBindOverrides.Add(CloneNode(ov));

        var matchingAggregates = new JsonArray();
        foreach (var pso in ArrayProp(shaders, "d3d12_pso_aggregates"))
            if (JsonContainsText(pso, hash))
                matchingAggregates.Add(CloneNode(pso));

        var matchingPairs = new JsonArray();
        foreach (var pair in ArrayProp(shaders, "distinct_d3d12_pairs"))
            if (JsonContainsText(pair, hash))
                matchingPairs.Add(CloneNode(pair));

        var cacheBlockers = new JsonArray();
        foreach (var ev in ArrayProp(d3d12, "recent_pipeline_cache_events"))
        {
            var action = StringProp(ev, "action") ?? "";
            if (action.Equals("set_untracked_pso", StringComparison.OrdinalIgnoreCase) ||
                BoolProp(ev, "has_cached_pso") ||
                BoolProp(ev, "stripped_cached_pso"))
                cacheBlockers.Add(CloneNode(ev));
        }

        var hints = new JsonArray();
        if (matchingOverrides.Count == 0 && matchingBindOverrides.Count == 0)
            hints.Add("No loaded manifest currently targets this hash. Write a disabled manifest first, then request shader reload.");
        if (matchingAggregates.Count == 0 && matchingPairs.Count == 0)
            hints.Add("No tracked PSO/pair currently references this hash or CRC in the enlarged sample. Either the shader has not rendered recently, tracking is off, or the PSO was created before injection.");
        if (cacheBlockers.Count > 0)
            hints.Add("Recent cache/untracked-PSO events exist. If this target is visible in Shader Hunter but not replaceable, investigate cached PSO/library paths before editing the shader again.");
        if (matchingOverrides.Count > 0 && matchingAggregates.Count > 0)
            hints.Add("A manifest and tracked PSO both exist. If pixels do not change, use uevr_render_ab_pixel_diff and inspect override compile/apply status.");

        return JsonText(new JsonObject
        {
            ["target_hash"] = hash,
            ["stage"] = stage,
            ["runtime_overrides_enabled"] = CloneNode(shaders["runtime_overrides_enabled"]),
            ["matching_override_count"] = matchingOverrides.Count,
            ["matching_bind_override_count"] = matchingBindOverrides.Count,
            ["matching_pso_aggregate_count"] = matchingAggregates.Count,
            ["matching_pair_count"] = matchingPairs.Count,
            ["cache_blocker_count"] = cacheBlockers.Count,
            ["matching_overrides"] = matchingOverrides,
            ["matching_bind_overrides"] = matchingBindOverrides,
            ["matching_pso_aggregates"] = matchingAggregates,
            ["matching_pairs"] = matchingPairs,
            ["cache_blockers"] = cacheBlockers,
            ["hints"] = hints
        });
    }

    [McpServerTool(Name = "uevr_render_sn2_state")]
    [Description("Subnautica 2 active-test-state check. Returns UEVR/SN2 env gates, fog descriptor map counters, loaded/enabled shader manifests, runtime override state, and pso3069 reachability. Use this before every SN2 visual test to catch stale WIP mutations.")]
    public static async Task<string> Sn2State(
        [Description("Optional target hash for override proof; defaults to pso3069 166dba88.")] string targetHash = "166dba88")
    {
        var sn2Raw = await Http.Get("/api/render/sn2-state");
        var shadersRaw = await Http.Get("/api/render/shaders", new() {
            ["maxDistinctPairs"] = "256",
            ["maxPsoAggregates"] = "512"
        });
        var d3d12Raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = "128",
            ["maxHeaps"] = "16"
        });

        var sn2 = ParseNode(sn2Raw);
        var shaders = ParseNode(shadersRaw);
        var d3d12 = ParseNode(d3d12Raw);
        var hash = NormalizeHashArg(targetHash);

        var enabledOverrides = new JsonArray();
        var matchingOverrides = new JsonArray();
        foreach (var ov in ArrayProp(shaders, "overrides"))
        {
            if (BoolProp(ov, "enabled"))
                enabledOverrides.Add(CloneNode(ov));
            if (JsonContainsText(ov, hash))
                matchingOverrides.Add(CloneNode(ov));
        }

        var matchingPsos = new JsonArray();
        foreach (var pso in ArrayProp(shaders, "d3d12_pso_aggregates"))
            if (JsonContainsText(pso, hash))
                matchingPsos.Add(CloneNode(pso));

        var activeEnv = sn2?["environment"]?["active_truthy_names"]?.DeepClone() ?? new JsonArray();
        var hints = new JsonArray();
        if (sn2 is null)
            hints.Add("Running UEVR build does not expose /api/render/sn2-state yet; rebuild/stage the updated plugin and UEVRBackend.");
        if (activeEnv is JsonArray activeArray && activeArray.Count > 0)
            hints.Add("SN2 environment gates are active. Treat this as a mutation run unless each active flag is intentional.");
        if (matchingOverrides.Count == 0)
            hints.Add($"No loaded shader override manifest matches {hash}.");
        if (matchingPsos.Count == 0)
            hints.Add($"No tracked PSO aggregate currently references {hash}; wait for the relevant draw or force shader tracking before testing the transform.");
        if (LongProp(d3d12, "draw_events_this_frame") == 0)
            hints.Add("No D3D12 draw events in the current snapshot. Force D3D12 diagnostics and wait a few frames before relying on input reports.");

        return JsonText(new JsonObject
        {
            ["ok"] = sn2 is not null || shaders is not null || d3d12 is not null,
            ["target_hash"] = hash,
            ["sn2_state"] = sn2 ?? JsonValue.Create(sn2Raw),
            ["runtime_overrides_enabled"] = CloneNode(shaders?["runtime_overrides_enabled"]),
            ["enabled_override_count"] = enabledOverrides.Count,
            ["matching_override_count"] = matchingOverrides.Count,
            ["matching_pso_count"] = matchingPsos.Count,
            ["enabled_overrides"] = enabledOverrides,
            ["matching_overrides"] = matchingOverrides,
            ["matching_psos"] = matchingPsos,
            ["d3d12_frame"] = CloneNode(d3d12?["frame"]),
            ["d3d12_available"] = BoolProp(d3d12, "available"),
            ["hints"] = hints
        });
    }

    [McpServerTool(Name = "uevr_render_pso_input_report")]
    [Description("Explain the recent draw inputs for a target shader hash or PSO pointer: viewport/eye, root signature, CBVs/hashes, descriptor-table reads, producers, RTV/UAV writes, and matching shader/override metadata. For SN2 use targetHash=166dba88 and rootParameter=0, slotsCsv='5,8,9,10'.")]
    public static async Task<string> PsoInputReport(
        [Description("Optional target shader hash/CRC, e.g. pso3069 166dba88.")] string? targetHash = null,
        [Description("Optional exact pipeline_state pointer, e.g. 0x1234. Overrides targetHash matching when supplied.")] string? pipelineState = null,
        [Description("Optional descriptor root parameter filter, e.g. 0 for pso3069's SRV table.")] int? rootParameter = null,
        [Description("Optional comma-separated descriptor indices to keep, e.g. '5,8,9,10'.")] string? slotsCsv = null,
        [Description("Maximum recent D3D12 events to inspect. Default 1024.")] int maxEvents = 1024,
        [Description("Maximum matching draw rows returned. Default 16.")] int limit = 16)
    {
        maxEvents = Math.Clamp(maxEvents, 64, 4096);
        limit = Math.Clamp(limit, 1, 128);
        var slots = ParseSlotSet(slotsCsv);
        var d3d12Raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = maxEvents.ToString(),
            ["maxHeaps"] = "64"
        });
        var shadersRaw = await Http.Get("/api/render/shaders", new() {
            ["maxDistinctPairs"] = "1024",
            ["maxPsoAggregates"] = "4096"
        });
        var d3d12 = ParseNode(d3d12Raw);
        var shaders = ParseNode(shadersRaw);
        if (d3d12 is null) return JsonText(ParseError("d3d12", d3d12Raw));

        var psoFilter = NormalizePointerArg(pipelineState);
        var matchingPsos = string.IsNullOrWhiteSpace(psoFilter)
            ? MatchingPsoPointers(shaders, targetHash)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { psoFilter };

        var rows = new JsonArray();
        var byEye = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in ArrayProp(d3d12, "recent_draw_events"))
        {
            var evPso = NormalizePointerArg(StringProp(ev, "pipeline_state"));
            if (matchingPsos.Count > 0 && !matchingPsos.Contains(evPso))
                continue;
            if (matchingPsos.Count == 0 && (!string.IsNullOrWhiteSpace(targetHash) || !string.IsNullOrWhiteSpace(pipelineState)))
                continue;

            var compact = CompactDrawInput(ev, rootParameter, slots);
            var eye = LongProp(ev, "eye_bucket", -1).ToString();
            byEye[eye] = byEye.GetValueOrDefault(eye) + 1;
            rows.Add(compact);
            if (rows.Count >= limit)
                break;
        }

        var rootSignatures = new JsonArray();
        var seenRootSigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var rootSig = StringProp(row, "root_signature");
            if (string.IsNullOrWhiteSpace(rootSig) || !seenRootSigs.Add(rootSig!))
                continue;
            foreach (var rs in ArrayProp(d3d12, "root_signatures"))
            {
                if (string.Equals(StringProp(rs, "pointer"), rootSig, StringComparison.OrdinalIgnoreCase))
                    rootSignatures.Add(CloneNode(rs));
            }
        }

        var hints = new JsonArray();
        if (rows.Count == 0)
            hints.Add("No matching recent draws found. Enable shader tracking and D3D12 diagnostics, wait until the target renders, then retry with a larger maxEvents.");
        if (rootParameter is null)
            hints.Add("No rootParameter filter was supplied; descriptor_reads may include many slots. For pso3069, use rootParameter=0.");
        if (slots is null)
            hints.Add("No slot filter was supplied; for SN2 pso3069 start with slotsCsv='5,8,9,10'.");

        return JsonText(new JsonObject
        {
            ["available"] = BoolProp(d3d12, "available"),
            ["frame"] = CloneNode(d3d12["frame"]),
            ["target_hash"] = targetHash,
            ["pipeline_state_filter"] = pipelineState,
            ["matched_pso_pointers"] = JsonSerializer.SerializeToNode(matchingPsos.OrderBy(x => x), JsonOptions),
            ["matched_draw_count_returned"] = rows.Count,
            ["by_eye_bucket"] = JsonSerializer.SerializeToNode(byEye, JsonOptions),
            ["root_signatures"] = rootSignatures,
            ["draws"] = rows,
            ["matching_shader_metadata"] = CloneNode(shaders),
            ["hints"] = hints
        });
    }

    [McpServerTool(Name = "uevr_render_descriptor_slot_report")]
    [Description("Compare descriptor resources for selected slots across recent matching left/right draws. This is a compact slot-focused view over uevr_render_pso_input_report, useful for pso3069 t5/t8/t9 checks.")]
    public static async Task<string> DescriptorSlotReport(
        [Description("Target shader hash/CRC, e.g. 166dba88.")] string targetHash = "166dba88",
        [Description("Root parameter containing the descriptor table. Default 0.")] int rootParameter = 0,
        [Description("Comma-separated descriptor slots. Default '5,8,9,10'.")] string slotsCsv = "5,8,9,10",
        [Description("Maximum recent D3D12 events to inspect. Default 1024.")] int maxEvents = 1024)
    {
        var reportRaw = await PsoInputReport(targetHash, null, rootParameter, slotsCsv, maxEvents, 64);
        var report = ParseNode(reportRaw);
        if (report is null) return JsonText(ParseError("pso input report", reportRaw));

        var slots = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        foreach (var draw in ArrayProp(report, "draws"))
        {
            var eye = LongProp(draw, "eye_bucket", -1);
            foreach (var read in ArrayProp(draw, "descriptor_reads"))
            {
                var slot = LongProp(read, "descriptor_index", -1).ToString();
                if (!slots.TryGetValue(slot, out var arr))
                    slots[slot] = arr = new JsonArray();
                arr.Add(new JsonObject
                {
                    ["eye_bucket"] = eye,
                    ["draw_index"] = CloneNode(draw?["draw_index"]),
                    ["pipeline_state"] = StringProp(draw, "pipeline_state"),
                    ["resource"] = StringProp(read, "resource"),
                    ["descriptor_type"] = StringProp(read, "descriptor_type"),
                    ["descriptor_cpu"] = StringProp(read, "descriptor_cpu"),
                    ["descriptor_source_cpu"] = StringProp(read, "descriptor_source_cpu"),
                    ["producer_draw"] = CloneNode(read?["producer_draw"]),
                    ["producer_pso"] = StringProp(read, "producer_pso"),
                    ["producer_kind"] = StringProp(read, "producer_kind"),
                    ["producer_eye_bucket"] = CloneNode(read?["producer_eye_bucket"])
                });
            }
        }

        var slotSummary = new JsonObject();
        foreach (var (slot, arr) in slots.OrderBy(kv => int.TryParse(kv.Key, out var i) ? i : int.MaxValue))
            slotSummary[slot] = arr;

        return JsonText(new JsonObject
        {
            ["target_hash"] = targetHash,
            ["root_parameter"] = rootParameter,
            ["slots_csv"] = slotsCsv,
            ["draw_count"] = LongProp(report, "matched_draw_count_returned"),
            ["slot_summary"] = slotSummary,
            ["raw_input_report"] = CloneNode(report)
        });
    }

    [McpServerTool(Name = "uevr_render_lineage_diff_for_pso")]
    [Description("Find first-level left/right descriptor-read divergence for a target PSO/hash. Groups recent matching draws by descriptor root+slot and reports slots where resources, producers, or descriptor sources differ between eyes.")]
    public static async Task<string> LineageDiffForPso(
        [Description("Target shader hash/CRC. Default pso3069 166dba88.")] string targetHash = "166dba88",
        [Description("Descriptor root parameter. Default 0.")] int rootParameter = 0,
        [Description("Optional comma-separated descriptor slots to inspect. Empty means all.")] string? slotsCsv = null,
        [Description("Maximum recent D3D12 events to inspect. Default 2048.")] int maxEvents = 2048)
    {
        var reportRaw = await PsoInputReport(targetHash, null, rootParameter, slotsCsv, maxEvents, 128);
        var report = ParseNode(reportRaw);
        if (report is null) return JsonText(ParseError("pso input report", reportRaw));

        var latestByEyeSlot = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var draw in ArrayProp(report, "draws"))
        {
            var eye = LongProp(draw, "eye_bucket", -1);
            if (eye != 1 && eye != 2)
                continue;
            foreach (var read in ArrayProp(draw, "descriptor_reads"))
            {
                var slot = LongProp(read, "descriptor_index", -1);
                latestByEyeSlot[$"{eye}:{slot}"] = new JsonObject
                {
                    ["draw_index"] = CloneNode(draw?["draw_index"]),
                    ["resource"] = StringProp(read, "resource"),
                    ["descriptor_type"] = StringProp(read, "descriptor_type"),
                    ["descriptor_cpu"] = StringProp(read, "descriptor_cpu"),
                    ["descriptor_source_cpu"] = StringProp(read, "descriptor_source_cpu"),
                    ["producer_draw"] = CloneNode(read?["producer_draw"]),
                    ["producer_pso"] = StringProp(read, "producer_pso"),
                    ["producer_kind"] = StringProp(read, "producer_kind"),
                    ["producer_eye_bucket"] = CloneNode(read?["producer_eye_bucket"])
                };
            }
        }

        var slotIds = latestByEyeSlot.Keys
            .Select(k => k.Split(':')[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => int.TryParse(s, out var i) ? i : int.MaxValue)
            .ToArray();

        var diffs = new JsonArray();
        foreach (var slot in slotIds)
        {
            latestByEyeSlot.TryGetValue($"1:{slot}", out var left);
            latestByEyeSlot.TryGetValue($"2:{slot}", out var right);
            var differs = left is null || right is null ||
                          !string.Equals(StringProp(left, "resource"), StringProp(right, "resource"), StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(StringProp(left, "producer_pso"), StringProp(right, "producer_pso"), StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(StringProp(left, "descriptor_source_cpu"), StringProp(right, "descriptor_source_cpu"), StringComparison.OrdinalIgnoreCase);
            if (!differs)
                continue;

            diffs.Add(new JsonObject
            {
                ["slot"] = slot,
                ["left"] = CloneNode(left),
                ["right"] = CloneNode(right),
                ["resource_mismatch"] = !string.Equals(StringProp(left, "resource"), StringProp(right, "resource"), StringComparison.OrdinalIgnoreCase),
                ["producer_mismatch"] = !string.Equals(StringProp(left, "producer_pso"), StringProp(right, "producer_pso"), StringComparison.OrdinalIgnoreCase),
                ["descriptor_source_mismatch"] = !string.Equals(StringProp(left, "descriptor_source_cpu"), StringProp(right, "descriptor_source_cpu"), StringComparison.OrdinalIgnoreCase)
            });
        }

        return JsonText(new JsonObject
        {
            ["target_hash"] = targetHash,
            ["root_parameter"] = rootParameter,
            ["slots_csv"] = slotsCsv,
            ["diff_count"] = diffs.Count,
            ["diffs"] = diffs,
            ["input_report_summary"] = new JsonObject
            {
                ["draw_count"] = CloneNode(report["matched_draw_count_returned"]),
                ["by_eye_bucket"] = CloneNode(report["by_eye_bucket"])
            }
        });
    }

    [McpServerTool(Name = "uevr_render_write_dxil_probe_transform")]
    [Description("Generate a disabled or enabled per-eye DXIL probe manifest from known templates. Current templates include pso3069_t9_zero_only, pso3069_t5_bypass, pso3069_t5_require, and a custom_regex probe. Use this to make repeatable shader probes without hand-writing manifest JSON.")]
    public static async Task<string> WriteDxilProbeTransform(
        [Description("Target shader hash/CRC. Default pso3069 166dba88.")] string targetHash = "166dba88",
        [Description("Shader stage. Default ps.")] string stage = "ps",
        [Description("Probe template: pso3069_t9_zero_only, pso3069_t5_bypass, pso3069_t5_require, or custom_regex.")] string probe = "pso3069_t9_zero_only",
        [Description("Eye to patch: left or right. Default right.")] string eye = "right",
        [Description("Override display/name slug. Default generated from probe.")] string? name = null,
        [Description("Whether the manifest starts enabled. Default false.")] bool enabled = false,
        [Description("For custom_regex: regex find pattern.")] string? findRegex = null,
        [Description("For custom_regex: replacement text.")] string? replaceText = null,
        [Description("Override directory. Default resolves from running UEVR profile_shader_overrides.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        var normalizedProbe = probe.Trim().ToLowerInvariant();
        JsonObject payload;
        switch (normalizedProbe)
        {
            case "pso3069_t9_zero_only":
                payload = new JsonObject
                {
                    ["name"] = "sn2_pso3069_t9_zero_only",
                    ["description"] = "Right-eye pso3069 probe: zero only the t9 VolumeLightingB sample RGB extracts; leaves the shared t5 branch intact.",
                    ["transforms"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "require_regex",
                            ["find"] = "%580 = call %dx\\.types\\.ResRet\\.f32 @dx\\.op\\.sampleLevel\\.f32\\(i32 62, %dx\\.types\\.Handle %579",
                            ["required"] = true,
                            ["note"] = "pso3069 t9 volume-lighting sample is present"
                        },
                        new JsonObject
                        {
                            ["kind"] = "replace_regex",
                            ["find"] = "  %581 = extractvalue %dx\\.types\\.ResRet\\.f32 %580, 0",
                            ["replace"] = "  %581 = fadd fast float 0.000000e+00, 0.000000e+00 ; uevr SN2 pso3069 t9 zero probe r",
                            ["count"] = 1,
                            ["required"] = true
                        },
                        new JsonObject
                        {
                            ["kind"] = "replace_regex",
                            ["find"] = "  %582 = extractvalue %dx\\.types\\.ResRet\\.f32 %580, 1",
                            ["replace"] = "  %582 = fadd fast float 0.000000e+00, 0.000000e+00 ; uevr SN2 pso3069 t9 zero probe g",
                            ["count"] = 1,
                            ["required"] = true
                        },
                        new JsonObject
                        {
                            ["kind"] = "replace_regex",
                            ["find"] = "  %583 = extractvalue %dx\\.types\\.ResRet\\.f32 %580, 2",
                            ["replace"] = "  %583 = fadd fast float 0.000000e+00, 0.000000e+00 ; uevr SN2 pso3069 t9 zero probe b",
                            ["count"] = 1,
                            ["required"] = true
                        }
                    }
                };
                break;
            case "pso3069_t5_bypass":
                payload = new JsonObject
                {
                    ["name"] = "sn2_pso3069_t5_bypass_only",
                    ["description"] = "Diagnostic only: bypass the final pso3069 t5 volume-tint branch. Latest SN2 evidence says t5 is shared/valid, so this can darken the right eye and should not be treated as a fix.",
                    ["transforms"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "require_regex",
                            ["find"] = "%2793 = call %dx\\.types\\.ResRet\\.f32 @dx\\.op\\.sampleLevel\\.f32\\(i32 62, %dx\\.types\\.Handle %2792",
                            ["required"] = true,
                            ["note"] = "pso3069 t5 final volume-tint sample is present"
                        },
                        new JsonObject
                        {
                            ["kind"] = "replace_regex",
                            ["find"] = "  %2772 = extractvalue %dx\\.types\\.CBufRet\\.f32 %2771, 0",
                            ["replace"] = "  %2772 = fadd fast float 0.000000e+00, 0.000000e+00 ; uevr SN2 pso3069 t5 bypass probe",
                            ["count"] = 1,
                            ["required"] = true
                        }
                    }
                };
                break;
            case "pso3069_t5_require":
                payload = new JsonObject
                {
                    ["name"] = "sn2_pso3069_t5_require_only",
                    ["description"] = "Compile/apply proof that only requires the final pso3069 t5 sample to exist.",
                    ["transforms"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "require_regex",
                            ["find"] = "%2793 = call %dx\\.types\\.ResRet\\.f32 @dx\\.op\\.sampleLevel\\.f32\\(i32 62, %dx\\.types\\.Handle %2792",
                            ["required"] = true,
                            ["note"] = "pso3069 t5 final volume-tint sample is present"
                        }
                    }
                };
                break;
            case "custom_regex":
                if (string.IsNullOrWhiteSpace(findRegex) || replaceText is null)
                    return JsonText(new { ok = false, error = "custom_regex requires findRegex and replaceText" });
                payload = new JsonObject
                {
                    ["name"] = "custom_regex_probe",
                    ["transforms"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "replace_regex",
                            ["find"] = findRegex,
                            ["replace"] = replaceText,
                            ["count"] = 1,
                            ["required"] = true
                        }
                    }
                };
                break;
            default:
                return JsonText(new { ok = false, error = "Unknown probe template", supported = new[] { "pso3069_t9_zero_only", "pso3069_t5_bypass", "pso3069_t5_require", "custom_regex" } });
        }

        var left = eye.Equals("left", StringComparison.OrdinalIgnoreCase) ? payload.ToJsonString(JsonOptions) : null;
        var right = eye.Equals("left", StringComparison.OrdinalIgnoreCase) ? null : payload.ToJsonString(JsonOptions);
        return await WritePerEyeShaderPayloads(
            targetHash,
            stage,
            leftTransformJson: left,
            rightTransformJson: right,
            name: name ?? $"probe_{normalizedProbe}_{NormalizeStageArg(stage)}_{NormalizeHashArg(targetHash)}_{eye}",
            enabled: enabled,
            overrideDir: overrideDir,
            reload: reload);
    }

    [McpServerTool(Name = "uevr_render_override_proof")]
    [Description("One-call override proof panel: reports active SN2 state, target override reachability, current pso input rows, then optionally runs verified A/B pixel diff. Use after enabling a manifest to prove it actually bound and moved pixels.")]
    public static async Task<string> OverrideProof(
        [Description("Target shader hash/CRC. Default pso3069 166dba88.")] string targetHash = "166dba88",
        [Description("Shader stage. Default ps.")] string stage = "ps",
        [Description("Run A/B pixel diff after status checks. Default true.")] bool runPixelDiff = true,
        [Description("A/B cycles. Default 2.")] int cycles = 2,
        [Description("Descriptor root parameter for input report. Default 0.")] int rootParameter = 0,
        [Description("Descriptor slots for input report. Default '5,8,9,10'.")] string slotsCsv = "5,8,9,10")
    {
        var sn2 = ParseNode(await Sn2State(targetHash));
        var status = ParseNode(await OverrideStatus(targetHash, stage));
        var inputs = ParseNode(await PsoInputReport(targetHash, null, rootParameter, slotsCsv, 1024, 8));
        JsonNode? pixelDiff = null;
        if (runPixelDiff)
            pixelDiff = ParseNode(await AbPixelDiff(cycles: cycles, framesPerState: 4, sampleW: 96, sampleH: 96, restoreInitialState: true, includeRawSamples: false));

        var hints = new JsonArray();
        if (LongProp(status, "matching_override_count") == 0)
            hints.Add("Target has no matching loaded manifest; pixels cannot change through shader override yet.");
        if (LongProp(inputs, "matched_draw_count_returned") == 0)
            hints.Add("Target has no matching draw rows in the sampled D3D12 window.");
        if (runPixelDiff && !BoolProp(pixelDiff, "changed"))
            hints.Add("A/B pixel diff did not move the sampled eye region. Check whether the transform bound, whether the sample window covers the changed pixels, or whether the probe is semantically ineffective.");

        return JsonText(new JsonObject
        {
            ["target_hash"] = NormalizeHashArg(targetHash),
            ["stage"] = stage,
            ["sn2_state"] = sn2,
            ["override_status"] = status,
            ["pso_input_report"] = inputs,
            ["pixel_diff"] = pixelDiff,
            ["hints"] = hints
        });
    }

    [McpServerTool(Name = "uevr_render_write_sn2_clean_launch_script")]
    [Description("Write a PowerShell launch helper for controlled SN2 tests. The script disables known SN2 WIP mutations, hides the UEVR overlay on startup, optionally enables one experiment, launches the existing launch_uevr_clean_observe.ps1, and writes a run directory marker.")]
    public static async Task<string> WriteSn2CleanLaunchScript(
        [Description("Output .ps1 path. Default under E:/Github/Subnautica 2/moddingkit/runs.")] string? outPath = null,
        [Description("Optional experiment environment JSON object, e.g. {\"UEVR_SN2_FOG_SRV_REDIRECT\":\"1\"}.")] string? experimentEnvJson = null,
        [Description("Path to launch_uevr_clean_observe.ps1. Default uses the current SN2 repo location.")] string launchScript = @"E:\Github\Subnautica 2\moddingkit\runs\launch_uevr_clean_observe.ps1")
    {
        var baseDir = @"E:\Github\Subnautica 2\moddingkit\runs";
        Directory.CreateDirectory(baseDir);
        var path = string.IsNullOrWhiteSpace(outPath)
            ? Path.Combine(baseDir, $"launch_sn2_clean_generated_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.ps1")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(outPath!));

        JsonObject experiment = new();
        if (!string.IsNullOrWhiteSpace(experimentEnvJson))
        {
            var parsed = ParseJsonArgument(experimentEnvJson!, nameof(experimentEnvJson));
            experiment = parsed as JsonObject ?? throw new ArgumentException("experimentEnvJson must be a JSON object");
        }

        var disabled = new SortedDictionary<string, string>
        {
            ["UEVR_HIDE_MENU_ON_STARTUP"] = "1",
            ["UEVR_FORCE_MENU_CLOSED"] = "1",
            ["UEVR_SN2_FOG_SRV_REDIRECT"] = "0",
            ["UEVR_SN2_COPYRECT_RIGHT_TABLE0_FROM_LEFT"] = "0",
            ["UEVR_SN2_COPYRECT_CB2_MODE"] = "0",
            ["UEVR_SN2_COPYRECT_RIGHT_CBV_FROM_LEFT"] = "0",
            ["UEVR_SN2_UPSTREAM_SKIP_CRCS"] = "",
            ["UEVR_SN2_SKYATMOS_SKIP_RIGHT"] = "0",
            ["UEVR_SN2_PSO3069_SUBST"] = "0",
            ["UEVR_SN2_TAIL_SRV_REPAIR"] = "0",
            ["UEVR_SUBNAUTICA2_FOG_ALIAS_THUNK_MODE"] = "0",
            ["UEVR_SUBNAUTICA2_LIGHT_AFFECTS_VIEW_MODE"] = "0",
            ["UEVR_SUBNAUTICA2_VISIBLE_LIGHT_INFOS_COPY_MODE"] = "0",
            ["UEVR_SUBNAUTICA2_DISABLE_COMPOSE_VOLUMETRIC_VIEW_RECT_FIX"] = "1",
            ["UEVR_SUBNAUTICA2_DISABLE_RENDER_FOG_VIEW_RECT_FIX"] = "1",
            ["UEVR_SUBNAUTICA2_DISABLE_UNDERWATER_FOG_VIEW_DATA_FIX"] = "1",
            ["UEVR_SUBNAUTICA2_DISABLE_SINGLE_LAYER_WATER_VIEW_RECT_FIX"] = "1"
        };

        foreach (var kv in experiment)
            disabled[kv.Key] = kv.Value?.ToString() ?? "";

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$runRoot = 'E:\\Github\\Subnautica 2\\moddingkit\\runs'");
        sb.AppendLine("$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'");
        sb.AppendLine("$runDir = Join-Path $runRoot \"sn2_clean_generated_$stamp\"");
        sb.AppendLine("New-Item -ItemType Directory -Force -Path $runDir | Out-Null");
        foreach (var (key, value) in disabled)
            sb.AppendLine($"$env:{key} = {JsonSerializer.Serialize(value)}");
        sb.AppendLine($"pwsh -NoProfile -ExecutionPolicy Bypass -File {JsonSerializer.Serialize(launchScript)} -TailRepairMode 0 -FogSrvRedirect 0 -FogAliasThunkMode 0 -LightAffectsViewMode 0 -VisibleLightInfosCopyMode 0");
        sb.AppendLine("$runDir | Set-Content -Encoding UTF8 (Join-Path $runRoot 'last_generated_clean_run.txt')");

        await File.WriteAllTextAsync(path, sb.ToString());
        return JsonText(new JsonObject
        {
            ["ok"] = true,
            ["path"] = path,
            ["launch_script"] = launchScript,
            ["env"] = JsonSerializer.SerializeToNode(disabled, JsonOptions),
            ["note"] = "Run this script from PowerShell to start a clean SN2 test with the generated env block."
        });
    }

    // ── Stereo / one-eye-bug diagnostics ──────────────────────────────

    [McpServerTool(Name = "uevr_render_stereo_summary")]
    [Description("Designed for diagnosing one-eye-only or black-eye VR bugs. Groups tracked resources and PSO aggregates per eye (Left/Right) and surfaces asymmetries: missing eye textures, change_count=0 on one side, asymmetric seen/bind/change drift, no PSOs targeting a side, and the currently-bound RT classification. Output includes a 'hint' field with the most common UEVR root causes. REQUIRES force_resources_sampling+force_shader_tracking on (or the relevant sidebars open) so the inspector has data to classify. Returns warnings under 'asymmetries[]'.")]
    public static async Task<string> StereoSummary()
        => await Http.Get("/api/render/stereo-summary");

    [McpServerTool(Name = "uevr_render_select_eye")]
    [Description("Auto-select the most-recent Left- or Right-eye texture for preview (so uevr_render_preview returns its metadata). Use after uevr_render_stereo_summary identifies an asymmetric eye to visualize its current contents. Side is 'left'/'l'/'0' or 'right'/'r'/'1'.")]
    public static async Task<string> SelectEye(
        [Description("'left' or 'right' (also accepts 'l'/'r'/'0'/'1')")] string side)
        => await Http.Post("/api/render/select-eye", new { side });

    [McpServerTool(Name = "uevr_render_stereo_forensics")]
    [Description("Return Stereo Forensics status and latest capture file paths: session_dir, manifest, events.jsonl, eye_diff.json, lineage.json, and experiments.json. Requires a UEVRJ build exporting uevr_render_diag_stereo_forensics_json.")]
    public static async Task<string> StereoForensics()
        => await Http.Get("/api/render/stereo-forensics");

    [McpServerTool(Name = "uevr_render_stereo_forensics_arm")]
    [Description("Re-arm Stereo Forensics to capture the next eligible burst from the live game now. Use this when the player is in the problem scene, instead of relying on the sentinel file. Returns the capture session paths.")]
    public static async Task<string> StereoForensicsArm()
        => await Http.Post("/api/render/stereo-forensics/arm", new { });

    // ── RenderDoc integration ─────────────────────────────────────────

    [McpServerTool(Name = "uevr_render_renderdoc_status")]
    [Description("Probe whether RenderDoc's in-app API is loaded into the game process. Returns loaded=false if renderdoc.dll isn't injected (game must be launched via RenderDoc or attached via 'Inject into Process'). When loaded: API version, number of captures so far, paths to each capture file, current capture file template, target-control state, and whether a capture is in progress. Use this before trying to trigger a capture.")]
    public static async Task<string> RenderDocStatus()
        => await Http.Get("/api/render/renderdoc/status");

    [McpServerTool(Name = "uevr_render_renderdoc_trigger_capture")]
    [Description("Queue a RenderDoc capture of the next N frames (default 1). Returns {ok, queued_frames} on success. The capture .rdc lands in RenderDoc's configured directory; read it back with uevr_render_renderdoc_status → captures[]. For one-eye bugs: pair with uevr_render_capture_next_d3d12_change to also snag the responsible PSO pair in the same window.")]
    public static async Task<string> RenderDocTriggerCapture(
        [Description("Number of frames to capture (default 1, max practical ~30)")] int frames = 1)
        => await Http.Post("/api/render/renderdoc/trigger", new { frames });

    [McpServerTool(Name = "uevr_render_renderdoc_launch_ui")]
    [Description("Launch the RenderDoc replay UI as a child process and have it connect back to the running game. Lets you immediately inspect captured frames after triggering. Returns the pid on success.")]
    public static async Task<string> RenderDocLaunchUI()
        => await Http.Post("/api/render/renderdoc/launch-ui", new { });

    [McpServerTool(Name = "uevr_render_renderdoc_set_capture_template")]
    [Description("Override the path template RenderDoc uses when writing .rdc files (e.g. 'D:/captures/uevr_oneeye'). RenderDoc auto-appends timestamps/extensions. Pass empty/null just to read back the current template via the returned value.")]
    public static async Task<string> RenderDocSetCaptureTemplate(
        [Description("Path prefix without extension (RenderDoc appends timestamp + .rdc). Empty/null to leave unchanged.")] string? template = null)
        => await Http.Post("/api/render/renderdoc/set-template", new { template });

    // ── VR mod / D3D12Component state probes ──────────────────────────

    [McpServerTool(Name = "uevr_render_vr_state")]
    [Description("Compact dump of the UEVR VR mod's render-relevant state: renderer (D3D11/D3D12), framework render/display sizes, HMD active, using_afr/synchronized_afr, depth_enabled, decoupled_pitch, native_stereo_fix flags, world_to_meters, runtime (OpenXR/OpenVR/none, loaded, ready), and crucially for stereo bugs: D3D12Component.shf_scene_mode (Stereo3D/Mono2D/Unknown), backbuffer_size, has_game_and_ui_textures, and per-eye resource pointers. Includes a 'hints' array with high-signal diagnostic flags (e.g. 'Mono2D — stereo fix may be missing').")]
    public static async Task<string> VrState()
        => await Http.Get("/api/render/vr-state");

    [McpServerTool(Name = "uevr_render_cvars")]
    [Description("Dump every cvar UEVR's CVarManager is tracking (those the user can override in cvars.txt). Returns each cvar's module, name, key, frozen state, and if frozen its int/float value. Filter is a case-insensitive substring matched against 'module/name'. Useful for catching mis-set rendering cvars (e.g. r.SeparateTranslucency=0) that break stereo.")]
    public static async Task<string> Cvars(
        [Description("Case-insensitive substring filter on 'module/name' (e.g. 'translucency', 'r.', 'mblur')")] string? filter = null)
        => await Http.Get("/api/render/cvars", new() { ["filter"] = filter });

    [McpServerTool(Name = "uevr_render_frame_timing")]
    [Description("Per-render-path FrameTimingStats from UEVR's D3D12Component: on_frame, ui_copy, swapchain_copy, openxr_submit, spectator_mirror, post_present. Each returns {count, avg_ms, max_ms}. D3D12 only. Spikes in a specific path point to the culprit when one eye lags or hitches.")]
    public static async Task<string> FrameTiming()
        => await Http.Get("/api/render/frame-timing");

    [McpServerTool(Name = "uevr_render_gpu_timings")]
    [Description("Return opt-in GPU timestamp query aggregates recorded by UEVR's D3D12 command-list hooks. Requires the game process to run with UEVR_ENABLE_D3D12_GPU_TIMESTAMPS=1 before injection. Groups samples by draw/dispatch kind, PSO, root signature, and eye bucket.")]
    public static async Task<string> GpuTimings(
        [Description("Maximum recent D3D12 events requested from UEVR while fetching timing aggregates (default 64)")] int? maxEvents = null,
        [Description("Maximum timing aggregate rows returned (default 128)")] int? maxTimings = null)
    {
        var raw = await Http.Get("/api/render/d3d12", new() {
            ["maxEvents"] = (maxEvents ?? 64).ToString(),
            ["maxHeaps"] = "16"
        });
        var root = ParseNode(raw);
        if (root is null) return JsonText(ParseError("d3d12", raw));

        var timings = ArrayProp(root, "gpu_timings");
        var resultTimings = new JsonArray();
        foreach (var item in timings.Take(Math.Max(1, maxTimings ?? 128)))
            resultTimings.Add(CloneNode(item));

        var hints = new JsonArray();
        if (timings.Count == 0)
            hints.Add("No GPU timing aggregates are present. Start/inject with UEVR_ENABLE_D3D12_GPU_TIMESTAMPS=1 and sample a few frames.");

        return JsonText(new
        {
            available = BoolProp(root, "available"),
            frame = root["frame"],
            timing_count = timings.Count,
            returned_count = resultTimings.Count,
            gpu_timings = resultTimings,
            hints
        });
    }

    [McpServerTool(Name = "uevr_render_eye_pixel_sample")]
    [Description("CPU readback of a centered NxN region from the requested eye texture (Left/Right). Returns RGBA min/max/mean across the region, plus is_black (max RGB <= 3) and is_uniform (min==max) classifications. The definitive 'is this eye actually black?' check — doesn't require rendering or screenshots. D3D12 only. Eye textures must be allocated (D3D12Component initialized).")]
    public static async Task<string> EyePixelSample(
        [Description("'left' or 'right'")] string side,
        [Description("Region width (default 64, clamped to texture)")] int? sampleW = null,
        [Description("Region height (default 64, clamped to texture)")] int? sampleH = null)
        => await Http.Get("/api/render/eye-sample", new() {
            ["side"] = side,
            ["sampleW"] = sampleW?.ToString(),
            ["sampleH"] = sampleH?.ToString(),
        });

    [McpServerTool(Name = "uevr_render_ab_pixel_diff")]
    [Description("Automated override A/B verification loop. Alternates runtime shader overrides OFF/ON, waits a small frame-based delay, samples centered regions from both eye textures, and reports RGBA mean deltas plus changed/not-changed hints. Use after adding a shader/CBV/DXIL override to prove the fix actually affects pixels without manually toggling in-game.")]
    public static async Task<string> AbPixelDiff(
        [Description("Number of off/on cycles to sample (default 3, clamped 1..12)")] int cycles = 3,
        [Description("Approximate frames to wait after each toggle before sampling (default 4, converted to ~16ms/frame)")] int framesPerState = 4,
        [Description("Sample region width (default 64)")] int sampleW = 64,
        [Description("Sample region height (default 64)")] int sampleH = 64,
        [Description("If true, restore runtime override enabled state to what it was before the test. Default true.")] bool restoreInitialState = true,
        [Description("If true, include raw eye-sample JSON for every off/on sample. Default false.")] bool includeRawSamples = false)
    {
        cycles = Math.Clamp(cycles, 1, 12);
        framesPerState = Math.Clamp(framesPerState, 1, 120);
        sampleW = Math.Clamp(sampleW, 1, 512);
        sampleH = Math.Clamp(sampleH, 1, 512);
        var delayMs = Math.Clamp(framesPerState * 16, 16, 2000);

        var shadersRaw = await Http.Get("/api/render/shaders", new() {
            ["maxDistinctPairs"] = "0",
            ["maxPsoAggregates"] = "0"
        });
        var shaders = ParseNode(shadersRaw);
        var initialEnabled = BoolProp(shaders, "runtime_overrides_enabled", true);

        static JsonObject SampleError(string side, string raw)
            => new()
            {
                ["side"] = side,
                ["available"] = false,
                ["error"] = "Failed to parse eye sample JSON",
                ["raw"] = raw
            };

        async Task<JsonNode> SampleEye(string side)
        {
            var raw = await Http.Get("/api/render/eye-sample", new() {
                ["side"] = side,
                ["sampleW"] = sampleW.ToString(),
                ["sampleH"] = sampleH.ToString()
            });
            return ParseNode(raw) ?? SampleError(side, raw);
        }

        static JsonObject DiffSample(JsonNode? off, JsonNode? on)
        {
            var offMean = NumberArrayProp(off, "rgba_mean");
            var onMean = NumberArrayProp(on, "rgba_mean");
            var delta = new double[4];
            var absTotal = 0.0;
            var maxAbs = 0.0;
            for (var i = 0; i < 4; ++i)
            {
                delta[i] = onMean[i] - offMean[i];
                var abs = Math.Abs(delta[i]);
                absTotal += abs;
                maxAbs = Math.Max(maxAbs, abs);
            }

            return new JsonObject
            {
                ["off_available"] = BoolProp(off, "available"),
                ["on_available"] = BoolProp(on, "available"),
                ["off_is_black"] = BoolProp(off, "is_black"),
                ["on_is_black"] = BoolProp(on, "is_black"),
                ["off_rgba_mean"] = new JsonArray(offMean[0], offMean[1], offMean[2], offMean[3]),
                ["on_rgba_mean"] = new JsonArray(onMean[0], onMean[1], onMean[2], onMean[3]),
                ["delta_rgba_mean"] = new JsonArray(delta[0], delta[1], delta[2], delta[3]),
                ["mean_abs_delta_rgba"] = absTotal / 4.0,
                ["max_abs_delta_rgba"] = maxAbs,
                ["changed"] = maxAbs >= 1.0
            };
        }

        var cycleResults = new JsonArray();
        try
        {
            for (var i = 0; i < cycles; ++i)
            {
                await Http.Post("/api/render/runtime-overrides", new { enabled = false });
                await Task.Delay(delayMs);
                var offLeft = await SampleEye("left");
                var offRight = await SampleEye("right");

                await Http.Post("/api/render/runtime-overrides", new { enabled = true });
                await Task.Delay(delayMs);
                var onLeft = await SampleEye("left");
                var onRight = await SampleEye("right");

                var leftDiff = DiffSample(offLeft, onLeft);
                var rightDiff = DiffSample(offRight, onRight);

                var cycle = new JsonObject
                {
                    ["cycle"] = i + 1,
                    ["left"] = leftDiff,
                    ["right"] = rightDiff
                };

                if (includeRawSamples)
                {
                    cycle["raw"] = new JsonObject
                    {
                        ["off_left"] = CloneNode(offLeft),
                        ["off_right"] = CloneNode(offRight),
                        ["on_left"] = CloneNode(onLeft),
                        ["on_right"] = CloneNode(onRight)
                    };
                }

                cycleResults.Add(cycle);
            }
        }
        finally
        {
            if (restoreInitialState)
                await Http.Post("/api/render/runtime-overrides", new { enabled = initialEnabled });
        }

        static double MaxCycleDelta(JsonArray cycles, string side)
        {
            var max = 0.0;
            foreach (var cycle in cycles)
            {
                try
                {
                    var value = cycle?[side]?["max_abs_delta_rgba"]?.GetValue<double>() ?? 0.0;
                    max = Math.Max(max, value);
                }
                catch { /* ignore malformed rows */ }
            }
            return max;
        }

        var leftMax = MaxCycleDelta(cycleResults, "left");
        var rightMax = MaxCycleDelta(cycleResults, "right");
        var hints = new JsonArray();
        if (leftMax < 1.0 && rightMax < 1.0)
            hints.Add("No sampled eye-region pixel delta exceeded 1.0. The override may not be bound, may affect a different screen region, or may need a larger sample/window.");
        else if (rightMax >= 1.0 && leftMax < 1.0)
            hints.Add("Only the right eye changed in the sampled region. This is the expected shape for a right-eye-only override.");
        else if (leftMax >= 1.0 && rightMax < 1.0)
            hints.Add("Only the left eye changed in the sampled region. Check whether the override eye targeting is reversed.");
        else
            hints.Add("Both eyes changed in the sampled region. Use per-eye payloads or a narrower transform if the fix was intended to affect one eye only.");

        return JsonText(new JsonObject
        {
            ["ok"] = true,
            ["cycles"] = cycles,
            ["frames_per_state"] = framesPerState,
            ["delay_ms"] = delayMs,
            ["sample_w"] = sampleW,
            ["sample_h"] = sampleH,
            ["initial_runtime_overrides_enabled"] = initialEnabled,
            ["restored_initial_state"] = restoreInitialState,
            ["left_max_abs_delta_rgba"] = leftMax,
            ["right_max_abs_delta_rgba"] = rightMax,
            ["changed"] = leftMax >= 1.0 || rightMax >= 1.0,
            ["cycle_results"] = cycleResults,
            ["hints"] = hints
        });
    }

    [McpServerTool(Name = "uevr_render_eye_dump")]
    [Description("Save the full current-frame eye texture (Left or Right) to disk as JPG (default — small, ideal for LLM context), PNG (lossless), or BMP. Returns the absolute path. Auto-picks <persistent_dir>/render_inspector/eye_dumps/<timestamp>_<side>.<ext> if outPath is empty. D3D12 only. Pair both sides for a side-by-side comparison the LLM can Read with the image-aware tool.")]
    public static async Task<string> EyeDump(
        [Description("'left' or 'right'")] string side,
        [Description("Absolute output path; empty to auto-pick under render_inspector/eye_dumps")] string? outPath = null,
        [Description("'jpg' (default — smallest), 'png', or 'bmp'")] string? format = null)
        => await Http.Post("/api/render/eye-dump", new { side, outPath, format });

    [McpServerTool(Name = "uevr_render_eye_dump_both")]
    [Description("Convenience: dump both eyes in one call. Returns {left, right} with each side's dump result (path, width, height, ok). Same options as uevr_render_eye_dump applied to both sides.")]
    public static async Task<string> EyeDumpBoth(
        [Description("'jpg' (default), 'png', or 'bmp'")] string? format = null)
    {
        var left  = await Http.Post("/api/render/eye-dump", new { side = "left",  format });
        var right = await Http.Post("/api/render/eye-dump", new { side = "right", format });
        return $$"""{ "left": {{left}}, "right": {{right}} }""";
    }

    // ── DXGI / D3D debug-layer message pump ───────────────────────────

    [McpServerTool(Name = "uevr_render_dxgi_messages")]
    [Description("Pump D3D11InfoQueue / D3D12InfoQueue / DXGIInfoQueue for stored warnings and errors. Returns up to N most-recent messages with severity/category/id/description. IMPORTANT: requires the D3D debug layer to be active at device creation — UEVR does not enable it by default. Enable via the DirectX Control Panel ('Force on' for the game) or a launcher that sets D3D11_CREATE_DEVICE_DEBUG. When active, catches 'render target dimension mismatch', 'resource bound to non-existent slot', etc.")]
    public static async Task<string> DxgiMessages(
        [Description("Maximum messages to return (default 64)")] int max = 64)
        => await Http.Get("/api/render/dxgi-messages", new() { ["max"] = max.ToString() });

    // ── renderdoccmd bridge ───────────────────────────────────────────

    [McpServerTool(Name = "uevr_render_renderdoccmd_thumb")]
    [Description("Extract a thumbnail JPG/PNG from an existing RenderDoc .rdc capture file via renderdoccmd.exe (no replay-lib linking). Lets the LLM see what a previously-triggered capture contained. Requires renderdoccmd.exe on PATH or in 'renderdocPath'. Returns the thumbnail path and renderdoccmd's stdout.")]
    public static async Task<string> RenderDocCmdThumb(
        [Description("Absolute path to the .rdc capture file")] string rdcPath,
        [Description("Output thumbnail path (.jpg/.png). Default: rdcPath + '.thumb.jpg'")] string? outPath = null,
        [Description("Path to renderdoccmd.exe; auto-locate if null")] string? renderdocPath = null,
        [Description("Max thumbnail dimension in pixels (default: native)")] int? maxSize = null)
        => RenderDocCmd.RunThumb(rdcPath, outPath, renderdocPath, maxSize);

    [McpServerTool(Name = "uevr_render_renderdoccmd_convert")]
    [Description("Convert a .rdc capture file to a different format (e.g. .xml for inspectable metadata) via renderdoccmd.exe. Useful for letting the LLM read draw-event metadata as text without launching the GUI. Requires renderdoccmd.exe on PATH.")]
    public static async Task<string> RenderDocCmdConvert(
        [Description("Absolute path to the input .rdc capture file")] string inputPath,
        [Description("Absolute path for the output file (e.g. .xml, .zip.xml)")] string outputPath,
        [Description("Conversion type, e.g. 'xml', 'zip.xml'; default 'xml'")] string? type = null,
        [Description("Path to renderdoccmd.exe; auto-locate if null")] string? renderdocPath = null)
        => RenderDocCmd.RunConvert(inputPath, outputPath, type, renderdocPath);

    // ── Per-eye bind-history filter (no UEVRJ change) ─────────────────

    [McpServerTool(Name = "uevr_render_bind_history_per_eye")]
    [Description("Pull D3D12 recent_bindings (OMSetRenderTargets event log) and classify each bind by resolved RT/DSV resource — not by string. Each event now carries structured render_targets[]+depth_target with resource pointers and names (from FrameResourceInspector tagging). Reports counts per eye, top RTs hit, and unmapped descriptor handles (RTV not tracked yet by the inspector).")]
    public static async Task<string> BindHistoryPerEye(
        [Description("Cap on bindings inspected (default 256)")] int? maxEvents = null)
    {
        var raw = await Http.Get("/api/render/d3d12", new() { ["maxEvents"] = maxEvents?.ToString() ?? "256" });
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            int left = 0, right = 0, unknown = 0, no_rt = 0, unresolved_descriptors = 0;
            var rt_hits = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unresolved_sample = new System.Collections.Generic.List<string>();

            static int Classify(string name)
            {
                var n = name.ToLowerInvariant();
                if (n.Contains("right") || n.Contains("[1]") || n.Contains("righteye") || n.EndsWith("_r") || n.Contains("_r_")) return 1;
                if (n.Contains("left")  || n.Contains("[0]") || n.Contains("lefteye")  || n.EndsWith("_l") || n.Contains("_l_")) return 0;
                return -1;
            }

            if (root.TryGetProperty("recent_bindings", out var bindings) && bindings.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var ev in bindings.EnumerateArray())
                {
                    if (!ev.TryGetProperty("render_targets", out var rts) || rts.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        // Older builds without structured RT info OR an event with rtv_count=0.
                        no_rt++;
                        continue;
                    }
                    bool any = false;
                    foreach (var rt in rts.EnumerateArray())
                    {
                        any = true;
                        var name = rt.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
                        var resource = rt.TryGetProperty("resource", out var rv) ? rv.GetString() ?? "" : "";
                        // 'name' is the FrameResourceInspector-tagged name; falls back to formatted handle when unmapped.
                        var key = string.IsNullOrEmpty(name) ? resource : name;
                        rt_hits[key] = rt_hits.GetValueOrDefault(key, 0) + 1;
                        switch (Classify(key))
                        {
                            case 0: left++; break;
                            case 1: right++; break;
                            default:
                                unknown++;
                                if (key.StartsWith("0x") && unresolved_sample.Count < 8) unresolved_sample.Add(key);
                                if (key.StartsWith("0x")) unresolved_descriptors++;
                                break;
                        }
                    }
                    if (!any) no_rt++;
                }
            }
            // Top 8 RT names hit.
            var top = new System.Collections.Generic.List<object>();
            foreach (var kv in rt_hits.OrderByDescending(kv => kv.Value).Take(8))
                top.Add(new { rt = kv.Key, hits = kv.Value });

            return System.Text.Json.JsonSerializer.Serialize(new {
                left_eye_binds = left,
                right_eye_binds = right,
                unknown_binds = unknown,
                events_without_rt = no_rt,
                unresolved_descriptor_binds = unresolved_descriptors,
                unresolved_descriptor_sample = unresolved_sample,
                top_rt_targets = top,
                total = left + right + unknown,
                hint = left > 0 && right == 0
                    ? "All eye-classified binds went to LEFT. Right eye not being bound this window."
                    : right > 0 && left == 0
                        ? "All eye-classified binds went to RIGHT. Left eye not being bound this window."
                        : unresolved_descriptors > 0
                            ? "Many binds reference RTV descriptors not tracked by FrameResourceInspector. Enable force_resources_sampling and let it scan a few frames to populate names."
                            : null,
            });
        }
        catch (System.Exception ex)
        {
            return $$"""{ "error": "Failed to parse d3d12 bindings: {{ex.Message}}", "raw": {{raw}} }""";
        }
    }

    // ── D3D12 stereo trace ────────────────────────────────────────────

    [McpServerTool(Name = "uevr_render_stereo_trace_enable")]
    [Description("Enable or disable the low-level D3D12 stereo trace. The trace hooks RSSetViewports / DrawInstanced / DrawIndexedInstanced / ClearRenderTargetView / OMSetRenderTargets / ResourceBarrier and classifies each call as Left/Right/Full/Multi/Unknown based on viewport bounds. Per-game heuristics (e.g. Subnautica2) keep the trace on automatically; this toggle lets you enable it for any other game. Returns {ok, enabled}.")]
    public static async Task<string> StereoTraceEnable(
        [Description("true to enable, false to disable. Default: true.")] bool enabled = true)
        => await Http.Post("/api/render/stereo-trace/enable", new { enabled });

    [McpServerTool(Name = "uevr_render_stereo_trace")]
    [Description("Snapshot the D3D12 stereo trace counters. Returns Left/Right/Full/Multi/Unknown bucket counts for: viewports (RSSetViewports), draws (DrawInstanced), draws_indexed (DrawIndexedInstanced), clears (ClearRenderTargetView), plus om_set_render_targets and resource_barriers totals. Each bucket includes a left_right_ratio. Includes auto-generated hints flagging extreme asymmetries (e.g. 'right_share<0.1 — most scene work culled before right eye'). This is the lowest-latency stereo-asymmetry signal — counters are fed from the actual command-list hooks. Pass reset=true to zero the counters after reading (useful for taking deltas over a known window).")]
    public static async Task<string> StereoTrace(
        [Description("If true, reset the counters to zero after reading (for windowed delta sampling). Default: false.")] bool reset = false)
        => await Http.Get("/api/render/stereo-trace", new() { ["reset"] = reset ? "1" : "0" });

    // ── Composite diagnose workflow ───────────────────────────────────

    [McpServerTool(Name = "uevr_render_llm_frame_investigator")]
    [Description("One-call LLM investigator for stereo rendering bugs. Force-enables render diagnostics, samples a short window, then returns a ranked evidence bundle: eye samples/dumps, D3D12 draw events, symmetry oracle, descriptor lineage, pipeline-cache blockers, override reachability for an optional target hash, GPU timings, and concrete next actions. This is the preferred starting point for bugs like Subnautica 2's right-eye blown-white sky.")]
    public static async Task<string> LlmFrameInvestigator(
        [Description("Optional target shader hash/CRC to track, e.g. pso3069 PS CRC 166dba88.")] string? targetHash = null,
        [Description("Buggy eye label: left or right. Default right.")] string badEye = "right",
        [Description("Milliseconds to sample after enabling diagnostics (default 1500).")] int sampleDelayMs = 1500,
        [Description("Maximum recent draw/root/cache events requested (default 1024).")] int maxEvents = 1024,
        [Description("If true, dump both eye textures to JPG and include paths.")] bool dumpEyes = true,
        [Description("If true, write a frame-pair diff JSON to disk.")] bool exportFramePairDiff = true,
        [Description("If true, write this investigator result under <persistent>/render_inspector/llm_investigations.")] bool writeReport = true)
    {
        maxEvents = Math.Clamp(maxEvents, 64, 4096);
        sampleDelayMs = Math.Clamp(sampleDelayMs, 0, 30000);
        var normalizedHash = string.IsNullOrWhiteSpace(targetHash) ? null : NormalizeHashArg(targetHash);

        await Http.Post("/api/render/force-resources-sampling", new { enabled = true });
        await Http.Post("/api/render/force-shader-tracking",    new { enabled = true });
        await Http.Post("/api/render/force-d3d12-diagnostics",  new { enabled = true });
        await Http.Post("/api/render/stereo-trace/enable",      new { enabled = true });
        await Http.Get("/api/render/stereo-trace", new() { ["reset"] = "1" });

        if (sampleDelayMs > 0)
            await Task.Delay(sampleDelayMs);

        var contextRaw = await Http.Get("/api/render/context");
        var statusRaw = await Http.Get("/api/render/status");
        var vrRaw = await Http.Get("/api/render/vr-state");
        var shadersRaw = await Http.Get("/api/render/shaders", new() { ["maxDistinctPairs"] = "128", ["maxPsoAggregates"] = "128" });
        var d3d12Raw = await Http.Get("/api/render/d3d12", new() { ["maxEvents"] = maxEvents.ToString(), ["maxHeaps"] = "64" });
        var symmetryRaw = await SymmetryOracle(maxEvents, 24);
        var lineageRaw = await DescriptorLineage(maxEvents);
        var pipelineCacheRaw = await PipelineCacheEvents(maxEvents, onlyProblematic: true);
        var gpuRaw = await GpuTimings(128, 128);
        var stereoTraceRaw = await Http.Get("/api/render/stereo-trace", new() { ["reset"] = "0" });
        var leftPixelsRaw = await Http.Get("/api/render/eye-sample", new() { ["side"] = "left", ["sampleW"] = "128", ["sampleH"] = "128" });
        var rightPixelsRaw = await Http.Get("/api/render/eye-sample", new() { ["side"] = "right", ["sampleW"] = "128", ["sampleH"] = "128" });

        string? overrideStatusRaw = null;
        if (normalizedHash is not null)
            overrideStatusRaw = await OverrideStatus(normalizedHash);

        string? eyeDumpsRaw = null;
        if (dumpEyes)
            eyeDumpsRaw = await EyeDumpBoth("jpg");

        string? frameDiffRaw = null;
        if (exportFramePairDiff)
            frameDiffRaw = await ExportFramePairDiff(maxEvents);

        var context = ParseNode(contextRaw);
        var shaders = ParseNode(shadersRaw);
        var d3d12 = ParseNode(d3d12Raw);
        var symmetry = ParseNode(symmetryRaw);
        var pipelineCache = ParseNode(pipelineCacheRaw);

        var rankedSuspects = new JsonArray();
        foreach (var sample in ArrayProp(symmetry?["oracle"], "asymmetric_psos").Take(24))
        {
            var score = 50;
            if (JsonContainsText(sample, normalizedHash)) score += 100;
            if (JsonContainsText(sample, "binding")) score += 15;
            if (JsonContainsText(sample, "count")) score += 10;
            rankedSuspects.Add(new JsonObject
            {
                ["score"] = score,
                ["reason"] = JsonContainsText(sample, normalizedHash)
                    ? "asymmetric PSO also matches requested target hash"
                    : "symmetry oracle reported a per-eye count or binding mismatch",
                ["evidence"] = CloneNode(sample)
            });
        }

        foreach (var ev in ArrayProp(pipelineCache, "events").Take(16))
        {
            var action = StringProp(ev, "action") ?? "";
            var score = action.Equals("set_untracked_pso", StringComparison.OrdinalIgnoreCase) ? 90 : 60;
            rankedSuspects.Add(new JsonObject
            {
                ["score"] = score,
                ["reason"] = action.Equals("set_untracked_pso", StringComparison.OrdinalIgnoreCase)
                    ? "PSO reached SetPipelineState without a creation record; override substitution may be blocked by cache/pre-injection timing"
                    : "pipeline cache/library activity may explain why bytecode replacement did not bind",
                ["evidence"] = CloneNode(ev)
            });
        }

        if (normalizedHash is not null)
        {
            foreach (var pso in ArrayProp(shaders, "d3d12_pso_aggregates"))
            {
                if (!JsonContainsText(pso, normalizedHash))
                    continue;
                rankedSuspects.Add(new JsonObject
                {
                    ["score"] = 80,
                    ["reason"] = "tracked PSO aggregate references requested target hash",
                    ["evidence"] = CloneNode(pso)
                });
            }
        }

        var nextActions = new JsonArray
        {
            "Use ranked_suspects[0] as the first concrete PSO/draw target. If it is a cache blocker, solve reachability before editing DXIL.",
            "For a PSO suspect, call uevr_render_draw_events filtered by pipelineState, then compare left/right descriptor_reads and root hashes.",
            "For a root-parameter mismatch, call uevr_render_root_signatures to map the root parameter to CBV/SRV/UAV register space.",
            "For an SRV mismatch, use uevr_render_descriptor_lineage filtered by pipelineState/rootParameter to find the producer draw/eye.",
            "Use uevr_render_make_fix_candidate to write a disabled candidate manifest, then uevr_render_ab_pixel_diff to verify pixel movement.",
            "If pso3069 remains untracked, relaunch with UEVR_D3D12_STRIP_CACHED_PSO=1 for one diagnostic run, or use pre-injection pipeline-library hooks."
        };

        var result = new JsonObject
        {
            ["ok"] = true,
            ["bad_eye"] = badEye,
            ["target_hash"] = normalizedHash,
            ["sample_delay_ms"] = sampleDelayMs,
            ["context"] = context ?? JsonValue.Create(contextRaw),
            ["status"] = ParseNode(statusRaw) ?? JsonValue.Create(statusRaw),
            ["vr_state"] = ParseNode(vrRaw) ?? JsonValue.Create(vrRaw),
            ["left_eye_pixels"] = ParseNode(leftPixelsRaw) ?? JsonValue.Create(leftPixelsRaw),
            ["right_eye_pixels"] = ParseNode(rightPixelsRaw) ?? JsonValue.Create(rightPixelsRaw),
            ["stereo_trace"] = ParseNode(stereoTraceRaw) ?? JsonValue.Create(stereoTraceRaw),
            ["symmetry_oracle"] = symmetry ?? JsonValue.Create(symmetryRaw),
            ["descriptor_lineage"] = ParseNode(lineageRaw) ?? JsonValue.Create(lineageRaw),
            ["pipeline_cache_events"] = pipelineCache ?? JsonValue.Create(pipelineCacheRaw),
            ["gpu_timings"] = ParseNode(gpuRaw) ?? JsonValue.Create(gpuRaw),
            ["shader_snapshot"] = shaders ?? JsonValue.Create(shadersRaw),
            ["d3d12_snapshot"] = d3d12 ?? JsonValue.Create(d3d12Raw),
            ["ranked_suspects"] = rankedSuspects,
            ["next_actions"] = nextActions
        };

        if (overrideStatusRaw is not null)
            result["override_status"] = ParseNode(overrideStatusRaw) ?? JsonValue.Create(overrideStatusRaw);
        if (eyeDumpsRaw is not null)
            result["eye_dumps"] = ParseNode(eyeDumpsRaw) ?? JsonValue.Create(eyeDumpsRaw);
        if (frameDiffRaw is not null)
            result["frame_pair_diff_export"] = ParseNode(frameDiffRaw) ?? JsonValue.Create(frameDiffRaw);

        if (writeReport)
        {
            try
            {
                var persistent = StringProp(context, "persistent_dir");
                if (!string.IsNullOrWhiteSpace(persistent))
                {
                    var dir = Path.Combine(persistent!, "render_inspector", "llm_investigations");
                    Directory.CreateDirectory(dir);
                    var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
                    var path = Path.Combine(dir, $"investigation_{stamp}.json");
                    await File.WriteAllTextAsync(path, result.ToJsonString(JsonOptions));
                    result["report_path"] = path;
                }
            }
            catch (Exception ex)
            {
                result["report_error"] = ex.Message;
            }
        }

        return result.ToJsonString(JsonOptions);
    }

    [McpServerTool(Name = "uevr_render_make_fix_candidate")]
    [Description("Create a disabled candidate-fix artifact for the current suspect. For bind_override candidates it writes a real CBV/root-constant override manifest. For srv_redirect/per_eye_transform candidates it writes a safe disabled per-eye manifest plus candidate_notes.json; pass explicit rightTransformJson when the lineage/root-signature evidence identifies the exact handle rewrite. Pair with uevr_render_ab_pixel_diff after enabling.")]
    public static async Task<string> MakeFixCandidate(
        [Description("Target shader hash or CRC.")] string targetHash,
        [Description("Shader stage: ps, vs, cs, as, or ms. Default ps.")] string stage = "ps",
        [Description("Candidate kind: srv_redirect, per_eye_transform, bind_override, or root_constants. Default srv_redirect.")] string candidateKind = "srv_redirect",
        [Description("Optional right-eye DXIL transform JSON. If omitted for srv_redirect/per_eye_transform, a no-op scaffold is written with notes.")] string? rightTransformJson = null,
        [Description("Root parameter for bind_override/root_constants candidates.")] int? rootParameter = null,
        [Description("Values as JSON array/comma-separated u32s for bind_override/root_constants candidates.")] string? valuesU32 = null,
        [Description("Eye selector for bind candidates. Default right.")] string eye = "right",
        [Description("Override display/name slug.")] string? name = null,
        [Description("Whether the candidate manifest starts enabled. Default false.")] bool enabled = false,
        [Description("Override directory. Default resolves from the active UEVR profile.")] string? overrideDir = null,
        [Description("Trigger ShaderOverrideRegistry reload after writing. Default true.")] bool reload = true)
    {
        try
        {
            var hash = NormalizeHashArg(targetHash);
            var kind = candidateKind.Trim().ToLowerInvariant();
            var normalizedStage = NormalizeStageArg(stage);
            var candidateName = string.IsNullOrWhiteSpace(name) ? $"candidate_{kind}_{normalizedStage}_{hash}" : name!;

            if (kind is "bind_override" or "cbv")
            {
                if (rootParameter is null || string.IsNullOrWhiteSpace(valuesU32))
                    throw new ArgumentException("bind_override candidates require rootParameter and valuesU32");
                return await WriteBindOverride(hash, rootParameter.Value, valuesU32, kind: "cbv", stage: normalizedStage, pipeline: "graphics", eye: eye, name: candidateName, enabled: enabled, overrideDir: overrideDir, reload: reload);
            }

            if (kind is "root_constants" or "root_constant")
            {
                if (rootParameter is null || string.IsNullOrWhiteSpace(valuesU32))
                    throw new ArgumentException("root_constants candidates require rootParameter and valuesU32");
                return await WriteBindOverride(hash, rootParameter.Value, valuesU32, kind: "root_constants", stage: normalizedStage, pipeline: "graphics", eye: eye, name: candidateName, enabled: enabled, overrideDir: overrideDir, reload: reload);
            }

            var transformJson = rightTransformJson;
            var scaffolded = false;
            if (string.IsNullOrWhiteSpace(transformJson))
            {
                scaffolded = true;
                transformJson = """
                {
                  "transforms": [
                    {
                      "kind": "require_regex",
                      "pattern": "dx.op.createHandle|dx.op.cbufferLoad",
                      "required": false
                    },
                    {
                      "kind": "replace_regex",
                      "pattern": "__UEVR_PLACEHOLDER_NEVER_MATCHES__",
                      "replace": "__UEVR_PLACEHOLDER_NEVER_MATCHES__"
                    }
                  ]
                }
                """;
            }

            var resultRaw = await WritePerEyeShaderPayloads(
                hash,
                normalizedStage,
                rightTransformJson: transformJson,
                name: candidateName,
                enabled: enabled,
                overrideDir: overrideDir,
                reload: reload);

            var result = ParseNode(resultRaw) as JsonObject ?? new JsonObject { ["raw"] = resultRaw };
            result["candidate_kind"] = kind;
            result["scaffolded_noop_transform"] = scaffolded;
            result["next_steps"] = new JsonArray
            {
                "Keep enabled=false until the evidence points at this exact shader/root binding.",
                "If scaffolded_noop_transform=true, edit right_transform.json with the concrete redirect_handle/cbuffer rule from descriptor lineage/root-signature evidence.",
                "Run uevr_render_request_shader_reload, enable runtime overrides, then run uevr_render_ab_pixel_diff.",
                "If pixels do not change, call uevr_render_override_status for the same hash before changing the transform."
            };

            try
            {
                var dir = StringProp(result, "override_dir");
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    var notesPath = Path.Combine(dir!, "candidate_notes.json");
                    var notes = new JsonObject
                    {
                        ["target_hash"] = hash,
                        ["stage"] = normalizedStage,
                        ["candidate_kind"] = kind,
                        ["scaffolded_noop_transform"] = scaffolded,
                        ["created_utc"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["purpose"] = "LLM-generated disabled candidate. Fill concrete transform/bind values from frame investigator evidence before enabling."
                    };
                    await File.WriteAllTextAsync(notesPath, notes.ToJsonString(JsonOptions));
                    result["candidate_notes_path"] = notesPath;
                }
            }
            catch (Exception ex)
            {
                result["candidate_notes_error"] = ex.Message;
            }

            return result.ToJsonString(JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonText(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "uevr_render_capture_investigation_bundle")]
    [Description("Capture a compact investigation bundle for an LLM: optional RenderDoc trigger, frame-pair diff export, both-eye JPG dumps, pipeline-cache events, and the full LLM frame investigator JSON. Use when handing off a rendering bug or before testing a risky override.")]
    public static async Task<string> CaptureInvestigationBundle(
        [Description("Optional target shader hash/CRC to pass to the investigator.")] string? targetHash = null,
        [Description("If true and RenderDoc is loaded, trigger a 1-frame RenderDoc capture.")] bool triggerRenderDoc = false,
        [Description("Milliseconds to sample after enabling diagnostics. Default 1500.")] int sampleDelayMs = 1500)
    {
        string? renderdocRaw = null;
        if (triggerRenderDoc)
            renderdocRaw = await Http.Post("/api/render/renderdoc/trigger", new { frames = 1 });

        var investigatorRaw = await LlmFrameInvestigator(
            targetHash,
            badEye: "right",
            sampleDelayMs: sampleDelayMs,
            maxEvents: 1024,
            dumpEyes: true,
            exportFramePairDiff: true,
            writeReport: true);

        var renderdocStatusRaw = await Http.Get("/api/render/renderdoc/status");
        return JsonText(new JsonObject
        {
            ["ok"] = true,
            ["renderdoc_trigger"] = renderdocRaw is null ? null : (ParseNode(renderdocRaw) ?? JsonValue.Create(renderdocRaw)),
            ["renderdoc_status"] = ParseNode(renderdocStatusRaw) ?? JsonValue.Create(renderdocStatusRaw),
            ["investigator"] = ParseNode(investigatorRaw) ?? JsonValue.Create(investigatorRaw)
        });
    }

    [McpServerTool(Name = "uevr_render_diagnose_eye_bug")]
    [Description("Top-level workflow for chasing 'one eye renders / other eye black or missing' VR bugs. Runs the full diagnostic suite: status check, force-on resources+shader+d3d12 sampling, sample-delay, then aggregates stereo_summary + vr_state + frame_timing + per-eye pixel samples + RenderDoc status + per-eye bind history. Returns ONE payload with everything so the LLM can reason about asymmetries in a single read. Optionally also dumps both eyes to JPG and triggers a RenderDoc capture during sampling.")]
    public static async Task<string> DiagnoseEyeBug(
        [Description("Milliseconds to let the inspector sample after forcing-on (default 1500)")] int sampleDelayMs = 1500,
        [Description("If true and RenderDoc is loaded, also trigger a 1-frame capture during sampling")] bool captureWithRenderDoc = false,
        [Description("If true, dump both eye textures to JPG and include the paths in the result")] bool dumpEyesToJpg = false)
    {
        var status = await Http.Get("/api/render/status");
        await Http.Post("/api/render/force-resources-sampling", new { enabled = true });
        await Http.Post("/api/render/force-shader-tracking",    new { enabled = true });
        await Http.Post("/api/render/force-d3d12-diagnostics",  new { enabled = true });
        await Http.Post("/api/render/stereo-trace/enable",      new { enabled = true });

        // Zero the stereo trace so the sampling window's counts are clean.
        await Http.Get("/api/render/stereo-trace", new() { ["reset"] = "1" });

        string? renderdocCapture = null;

        if (sampleDelayMs > 0)
            await Task.Delay(Math.Max(100, Math.Min(sampleDelayMs, 30000)));

        if (captureWithRenderDoc)
        {
            renderdocCapture = await Http.Post("/api/render/renderdoc/trigger", new { frames = 1 });
        }

        var summary        = await Http.Get("/api/render/stereo-summary");
        var vrState        = await Http.Get("/api/render/vr-state");
        var stereoTrace    = await Http.Get("/api/render/stereo-trace", new() { ["reset"] = "0" });
        var frameTiming    = await Http.Get("/api/render/frame-timing");
        var leftPixels     = await Http.Get("/api/render/eye-sample", new() { ["side"] = "left",  ["sampleW"] = "64", ["sampleH"] = "64" });
        var rightPixels    = await Http.Get("/api/render/eye-sample", new() { ["side"] = "right", ["sampleW"] = "64", ["sampleH"] = "64" });
        var bindHistory    = await BindHistoryPerEye(256);
        var renderdocAfter = await Http.Get("/api/render/renderdoc/status");
        var context        = await Http.Get("/api/render/context");

        string? leftDump = null, rightDump = null;
        if (dumpEyesToJpg)
        {
            leftDump  = await Http.Post("/api/render/eye-dump", new { side = "left",  format = "jpg" });
            rightDump = await Http.Post("/api/render/eye-dump", new { side = "right", format = "jpg" });
        }

        var dumpsBlock = (leftDump is null || rightDump is null)
            ? ""
            : $", \"eye_dumps\": {{ \"left\": {leftDump}, \"right\": {rightDump} }}";

        return $$"""
{
  "status": {{status}},
  "context": {{context}},
  "vr_state": {{vrState}},
  "stereo_summary": {{summary}},
  "stereo_trace": {{stereoTrace}},
  "left_eye_pixels": {{leftPixels}},
  "right_eye_pixels": {{rightPixels}},
  "frame_timing": {{frameTiming}},
  "bind_history_per_eye": {{bindHistory}},
  "renderdoc_status": {{renderdocAfter}}{{(renderdocCapture is null ? "" : $", \"renderdoc_capture_trigger\": {renderdocCapture}")}}{{dumpsBlock}},
  "next_steps": [
    "STEREO TRACE is the highest-signal source. Look at stereo_trace.draws_indexed.left vs .right — a large asymmetry (e.g. L=27000 vs R=70) means scene work is being culled before/during the right-eye passes, not just at the eye-texture-copy stage.",
    "Compare stereo_trace.viewports vs stereo_trace.clears: if viewports.right > 0 but clears.right == 0, the engine sets up the right eye but never clears it (often appears black on re-used RTs).",
    "Check left/right_eye_pixels.is_black — if true on one side, that eye is definitively black at the sampled point (now reads from OpenXR/native-stereo paths, not just OpenVR mirror).",
    "Check vr_state.d3d12.shf_scene_mode — if 'Mono2D', UEVR sees the game as mono this frame; stereo path likely broken.",
    "If 'stereo_summary.asymmetries' flags a side with change_count=0, call uevr_render_eye_dump(side, format='jpg') and Read the file.",
    "bind_history_per_eye now resolves RT debug names (e.g. 'SceneColorRight', 'GBufferA'); check top_rt_targets for asymmetric naming.",
    "If renderdoc_status.loaded=true, uevr_render_renderdoc_trigger_capture(frames=2) saves a frame, then uevr_render_renderdoccmd_thumb extracts a viewable JPG.",
    "Once done, toggle the force-sampling flags off via uevr_render_force_* with enabled=false (and stereo-trace via uevr_render_stereo_trace_enable(false))."
  ]
}
""";
    }

    [McpServerTool(Name = "uevr_render_diagnose_shader_issue")]
    [Description("Top-level workflow for DXIL/PSO/root-binding render bugs. Force-enables resources, shader tracking, D3D12 diagnostics, and stereo trace; samples for a short window; then returns capabilities, context, shader snapshot, D3D12 snapshot, decoded root signatures, symmetry oracle, descriptor lineage, PSO churn, stereo trace, and optional shader bytecode/disassembly plus frame-pair diff export. Use this before deciding whether a bug needs shader bytecode, CBV/root-constant, descriptor, or PSO override work.")]
    public static async Task<string> DiagnoseShaderIssue(
        [Description("Milliseconds to sample after enabling diagnostics (default 1500)")] int sampleDelayMs = 1500,
        [Description("If true, write a frame-pair diff JSON file to disk and include its path")] bool exportFramePairDiff = false,
        [Description("Optional shader stage to inspect: 'ps', 'vs', 'cs', 'as', 'ms', or 'any'")] string? shaderStage = null,
        [Description("Optional shader hash to inspect via uevr_render_shader_bytecode")] string? shaderHash = null,
        [Description("If shaderHash is supplied, include DXIL disassembly text")] bool disassembleShader = false)
    {
        await Http.Post("/api/render/force-resources-sampling", new { enabled = true });
        await Http.Post("/api/render/force-shader-tracking",    new { enabled = true });
        await Http.Post("/api/render/force-d3d12-diagnostics",  new { enabled = true });
        await Http.Post("/api/render/stereo-trace/enable",      new { enabled = true });
        await Http.Get("/api/render/stereo-trace", new() { ["reset"] = "1" });

        if (sampleDelayMs > 0)
            await Task.Delay(Math.Max(100, Math.Min(sampleDelayMs, 30000)));

        var capabilitiesRaw = await DxilCapabilities();
        var contextRaw = await Http.Get("/api/render/context");
        var shadersRaw = await Http.Get("/api/render/shaders", new() { ["maxDistinctPairs"] = "64", ["maxPsoAggregates"] = "64" });
        var d3d12Raw = await Http.Get("/api/render/d3d12", new() { ["maxEvents"] = "512", ["maxHeaps"] = "64" });
        var rootSigsRaw = await RootSignatures(64, includeParameters: true);
        var symmetryRaw = await SymmetryOracle(512, 16);
        var lineageRaw = await DescriptorLineage(512);
        var churnRaw = await PsoChurn(32, 32);
        var gpuTimingsRaw = await GpuTimings(128, 128);
        var stereoTraceRaw = await Http.Get("/api/render/stereo-trace", new() { ["reset"] = "0" });

        string? bytecodeRaw = null;
        if (!string.IsNullOrWhiteSpace(shaderHash))
        {
            bytecodeRaw = await ShaderBytecode(
                string.IsNullOrWhiteSpace(shaderStage) ? "any" : shaderStage,
                shaderHash,
                disassembleShader,
                disassembleShader ? 256 * 1024 : 0);
        }

        string? frameDiffRaw = null;
        if (exportFramePairDiff)
            frameDiffRaw = await ExportFramePairDiff(512);

        var result = new JsonObject
        {
            ["capabilities"] = ParseNode(capabilitiesRaw) ?? JsonValue.Create(capabilitiesRaw),
            ["context"] = ParseNode(contextRaw) ?? JsonValue.Create(contextRaw),
            ["shaders"] = ParseNode(shadersRaw) ?? JsonValue.Create(shadersRaw),
            ["d3d12"] = ParseNode(d3d12Raw) ?? JsonValue.Create(d3d12Raw),
            ["root_signatures"] = ParseNode(rootSigsRaw) ?? JsonValue.Create(rootSigsRaw),
            ["symmetry_oracle"] = ParseNode(symmetryRaw) ?? JsonValue.Create(symmetryRaw),
            ["descriptor_lineage"] = ParseNode(lineageRaw) ?? JsonValue.Create(lineageRaw),
            ["pso_churn"] = ParseNode(churnRaw) ?? JsonValue.Create(churnRaw),
            ["gpu_timings"] = ParseNode(gpuTimingsRaw) ?? JsonValue.Create(gpuTimingsRaw),
            ["stereo_trace"] = ParseNode(stereoTraceRaw) ?? JsonValue.Create(stereoTraceRaw),
            ["next_steps"] = new JsonArray
            {
                "If symmetry_oracle reports binding_mismatch, compare the sample draw events' root_signature, root CBV hashes, descriptor table hashes, and descriptor_reads.",
                "Use root_signatures to map the mismatching root_parameter to CBV/SRV/UAV/register space.",
                "Use descriptor_lineage on the mismatching resource/root parameter to identify which prior draw produced the sampled texture.",
                "Use uevr_render_write_bind_override for confirmed CBV/root-constant mismatches before changing shader bytecode.",
                "Use shader_bytecode/source recovery only after the bind/resource path points at a shader-level issue; many VR stereo bugs are fixable with root/CBV/descriptor overrides instead."
            }
        };

        if (bytecodeRaw is not null)
            result["shader_bytecode"] = ParseNode(bytecodeRaw) ?? JsonValue.Create(bytecodeRaw);
        if (frameDiffRaw is not null)
            result["frame_pair_diff_export"] = ParseNode(frameDiffRaw) ?? JsonValue.Create(frameDiffRaw);

        return result.ToJsonString(JsonOptions);
    }
}

// ── renderdoccmd.exe bridge ──────────────────────────────────────────

static class RenderDocCmd
{
    static string? LocateRenderdocCmd(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint))
            return hint;

        // PATH lookup
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir, "renderdoccmd.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* ignore malformed PATH entries */ }
        }

        // Common install locations
        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables("%ProgramFiles%\\RenderDoc\\renderdoccmd.exe"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%\\RenderDoc\\renderdoccmd.exe"),
            Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\Programs\\RenderDoc\\renderdoccmd.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return null;
    }

    static string Run(string exe, string args, int timeoutMs = 60000)
    {
        using var p = new System.Diagnostics.Process();
        p.StartInfo.FileName = exe;
        p.StartInfo.Arguments = args;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { /* */ }
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "renderdoccmd timed out", exe, args });
        }
        return System.Text.Json.JsonSerializer.Serialize(new {
            ok = p.ExitCode == 0,
            exit_code = p.ExitCode,
            stdout = stdout.GetAwaiter().GetResult(),
            stderr = stderr.GetAwaiter().GetResult(),
            exe,
            args
        });
    }

    public static string RunThumb(string rdcPath, string? outPath, string? renderdocPath, int? maxSize)
    {
        var exe = LocateRenderdocCmd(renderdocPath);
        if (exe is null)
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "renderdoccmd.exe not found on PATH or in standard install locations. Pass renderdocPath explicitly." });
        if (!File.Exists(rdcPath))
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "Input .rdc file not found", rdcPath });

        var thumb = string.IsNullOrWhiteSpace(outPath) ? rdcPath + ".thumb.jpg" : outPath;
        var args = $"thumb \"{rdcPath}\" --out \"{thumb}\"";
        if (maxSize is int n && n > 0) args += $" --max-size {n}";
        var result = Run(exe, args);
        // Append the resolved thumb path for convenience.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var kv in doc.RootElement.EnumerateObject())
                dict[kv.Name] = kv.Value.ValueKind == System.Text.Json.JsonValueKind.String ? (object?)kv.Value.GetString() : kv.Value.ToString();
            dict["thumbnail_path"] = thumb;
            dict["thumbnail_exists"] = File.Exists(thumb);
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch { return result; }
    }

    public static string RunConvert(string inputPath, string outputPath, string? type, string? renderdocPath)
    {
        var exe = LocateRenderdocCmd(renderdocPath);
        if (exe is null)
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "renderdoccmd.exe not found" });
        if (!File.Exists(inputPath))
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "Input file not found", inputPath });
        var t = string.IsNullOrWhiteSpace(type) ? "xml" : type;
        var args = $"convert -i \"{inputPath}\" -o \"{outputPath}\" -c {t}";
        return Run(exe, args);
    }
}
