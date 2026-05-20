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
                ["hunter_override_stub_capture"] = Has("hunter_capture_active_override_stub"),
                ["manifest_hlsl_override_authoring"] = true,
                ["manifest_bind_override_authoring"] = true,
                ["manifest_dxil_text_patch_authoring"] = true,
                ["manifest_container_patch_authoring"] = true,
                ["manifest_dxil_transform_authoring"] = true,
                ["bind_time_cbv_root_constant_overrides"] = Has("shaders"),
                ["per_eye_pso_variant_manifests"] = Has("shaders"),
                ["runtime_override_ab_toggle"] = Has("set_runtime_overrides_enabled"),
                ["frame_pair_diff_export"] = Has("export_frame_pair_diff"),
                ["renderdoc_bridge"] = Has("renderdoc_status") && Has("renderdoc_trigger"),
                ["stereo_trace"] = Has("set_stereo_trace_enabled") && Has("stereo_trace_json"),
                ["eye_texture_readback"] = Has("eye_sample") && Has("eye_dump")
            },
            ["missing_high_value_symbols"] = new JsonArray()
        };

        var missing = caps["missing_high_value_symbols"]!.AsArray();
        foreach (var name in new[]
        {
            "shader_bytecode", "hunter_capture_active_override_stub",
            "set_runtime_overrides_enabled", "export_frame_pair_diff",
            "set_stereo_trace_enabled", "stereo_trace_json"
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
    [Description("Write the current D3D12 draw/bind snapshot to <persistent>/render_inspector/frame_diffs. The file includes recent draw events, root binds, decoded root signatures, descriptor read producer lineage, CBV/root-constant hashes, and the symmetry oracle. Use this when attaching a compact left/right render-diff artifact to a bug report.")]
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
    [Description("Return the D3D12 auto-symmetry oracle plus sample left/right draw events for asymmetric PSOs. Flags PSOs where left/right draw counts differ or where per-eye resource/root-bind fingerprints diverge. This is the first tool to call after sampling a broken stereo frame.")]
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
