using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// MCP surface for the StereoForensics layer (UEVRJ src/render/StereoForensics.cpp).
///
/// Three transports, picked per data type (see docs/STEREO_FORENSICS_MCP_PLAN.md):
///   • Live control/state  -> plugin HTTP (/api/render/stereo-forensics[/arm]).
///       Those live in RenderDiagnosticsTools (uevr_render_stereo_forensics[_arm]).
///   • Bundle file contents -> read directly from the on-disk session_dir here.
///   • Offline analysis     -> shell out to the UEVRJ Python tools (no game needed),
///       reusing ExternalTools.Run.
///
/// Path config (env, with sensible defaults):
///   UEVR_FORENSICS_TOOLS_DIR   tools dir (default E:\Github\UEVRJ\tools)
///   UEVR_FORENSICS_PYTHON      python exe (default "python")
///   UEVR_STEREO_FORENSICS_DIR  session root (default C:\tmp\uevr_forensics)
///   UEVR_FORENSICS_DB          sqlite path (default {forensics_dir}\stereo_forensics.sqlite)
///   UEVR_STEREO_EXPERIMENTS_FILE  rules file the game hot-reloads
///                                 (default {forensics_dir}\experiments_active.json)
/// </summary>
[McpServerToolType]
public static class StereoForensicsTools
{
    // ── path / config resolution ───────────────────────────────────────

    static string ToolsDir() =>
        Environment.GetEnvironmentVariable("UEVR_FORENSICS_TOOLS_DIR") is { Length: > 0 } d
            ? d : @"E:\Github\UEVRJ\tools";

    static string PythonExe() =>
        Environment.GetEnvironmentVariable("UEVR_FORENSICS_PYTHON") is { Length: > 0 } p
            ? p : "python";

    static string ForensicsDir() =>
        Environment.GetEnvironmentVariable("UEVR_STEREO_FORENSICS_DIR") is { Length: > 0 } d
            ? d : @"C:\tmp\uevr_forensics";

    static string DbPath() =>
        Environment.GetEnvironmentVariable("UEVR_FORENSICS_DB") is { Length: > 0 } d
            ? d : Path.Combine(ForensicsDir(), "stereo_forensics.sqlite");

    static string ExperimentsFile() =>
        Environment.GetEnvironmentVariable("UEVR_STEREO_EXPERIMENTS_FILE") is { Length: > 0 } f
            ? f : Path.Combine(ForensicsDir(), "experiments_active.json");

    static string Script(string name) => Path.Combine(ToolsDir(), name);

    /// <summary>Most-recently-modified session_* dir, or null.</summary>
    static string? LatestSession()
    {
        var root = ForensicsDir();
        if (!Directory.Exists(root)) return null;
        return new DirectoryInfo(root)
            .EnumerateDirectories("session_*")
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>Explicit session path if given+exists, else the latest session.</summary>
    static string? ResolveSession(string? session)
    {
        if (!string.IsNullOrWhiteSpace(session) && Directory.Exists(session))
            return Path.GetFullPath(session);
        return LatestSession();
    }

    static async Task<string> RunPy(string script, IEnumerable<string> args, int timeoutMs = 120000)
    {
        var scriptPath = Script(script);
        if (!File.Exists(scriptPath))
            return ExternalTools.Err($"forensics tool not found: {scriptPath} (set $UEVR_FORENSICS_TOOLS_DIR)");

        var argv = new List<string> { scriptPath };
        argv.AddRange(args);
        var r = await ExternalTools.Run(PythonExe(), argv, timeoutMs: timeoutMs, cwd: ToolsDir());
        return JsonSerializer.Serialize(new
        {
            ok = r.ExitCode == 0,
            command = r.Command,
            exitCode = r.ExitCode,
            stdout = r.Stdout,
            stderr = r.Stderr
        }, ExternalTools.Json);
    }

    static string ReadBundleFile(string? session, string fileName, int maxChars)
    {
        var s = ResolveSession(session);
        if (s is null)
            return ExternalTools.Err($"no session found under {ForensicsDir()} — run a capture (uevr_render_stereo_forensics_arm) first, or pass an explicit session path.");
        var path = Path.Combine(s, fileName);
        if (!File.Exists(path))
            return ExternalTools.Err($"{fileName} not present in {s} (capture may not have written it yet).");
        var text = File.ReadAllText(path);
        var truncated = text.Length > maxChars;
        if (truncated) text = text[..maxChars];
        return JsonSerializer.Serialize(new
        {
            ok = true,
            session = s,
            file = path,
            truncated,
            length = new FileInfo(path).Length,
            content = TryParse(text)
        }, ExternalTools.Json);
    }

    // Return parsed JSON when possible (so the model sees structure), else raw text.
    static object TryParse(string text)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(text); }
        catch { return text; }
    }

    // ── session / bundle readout (local files, no game required) ────────

    [McpServerTool(Name = "uevr_forensics_sessions")]
    [Description("List StereoForensics capture sessions on disk under $UEVR_STEREO_FORENSICS_DIR (default C:\\tmp\\uevr_forensics), newest first, with their bundle files and sizes. Use this to find a session path to pass to the other forensics tools (most tools default to the latest session when omitted).")]
    public static Task<string> Sessions()
    {
        var root = ForensicsDir();
        if (!Directory.Exists(root))
            return Task.FromResult(ExternalTools.Err($"forensics dir not found: {root}"));
        var sessions = new DirectoryInfo(root).EnumerateDirectories("session_*")
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Take(25)
            .Select(d => new {
                session = d.FullName,
                modified = d.LastWriteTimeUtc,
                files = d.EnumerateFiles().Select(f => new { f.Name, f.Length }).ToArray()
            }).ToArray();
        return Task.FromResult(JsonSerializer.Serialize(new { ok = true, root, count = sessions.Length, sessions }, ExternalTools.Json));
    }

    [McpServerTool(Name = "uevr_forensics_eye_diff")]
    [Description("Read eye_diff.json from a StereoForensics session (the per-eye draw/descriptor diff: count mismatches, PSO/shader differences, CBV-hash differences, same-resource-different-slice, descriptor_resource_differs, producer_lineage_differs, with pair scores). Defaults to the latest session. This is the primary 'what differs between the eyes this frame' readout.")]
    public static Task<string> EyeDiff(
        [Description("Explicit session dir path; omit for the latest session.")] string? session = null,
        [Description("Max characters of JSON to return (default 60000).")] int maxChars = 60000)
        => Task.FromResult(ReadBundleFile(session, "eye_diff.json", maxChars));

    [McpServerTool(Name = "uevr_forensics_lineage")]
    [Description("Read lineage.json from a StereoForensics session: consumer->resource/view read edges, latest resource/view producers, and writer history (resource lifetime DAG with alias awareness). Defaults to the latest session. Use to answer 'who produced the view this draw sampled?'.")]
    public static Task<string> Lineage(
        [Description("Explicit session dir path; omit for the latest session.")] string? session = null,
        [Description("Max characters of JSON to return (default 60000).")] int maxChars = 60000)
        => Task.FromResult(ReadBundleFile(session, "lineage.json", maxChars));

    [McpServerTool(Name = "uevr_forensics_manifest")]
    [Description("Read manifest.json from a StereoForensics session, including limiter_status (captured/skipped frames, per-kind counts, dropped events, total bytes, capture_stopped) and paths to the other bundle files. Use to confirm capture is healthy and bounded. Defaults to the latest session.")]
    public static Task<string> Manifest(
        [Description("Explicit session dir path; omit for the latest session.")] string? session = null)
        => Task.FromResult(ReadBundleFile(session, "manifest.json", 40000));

    // ── offline analysis (shell out to UEVRJ Python tools) ──────────────

    [McpServerTool(Name = "uevr_forensics_ingest")]
    [Description("Ingest a session bundle into the SQLite forensics DB and materialize event pairs, lineage paths, resource lifetimes, and ranked suspects (runs stereo_forensics_query.py summary --ingest-db --analyze-db --auto-findings). Defaults to the latest session and $UEVR_FORENSICS_DB. Do this before uevr_forensics_suspects / uevr_forensics_generate_rules.")]
    public static async Task<string> Ingest(
        [Description("Explicit session dir path; omit for the latest session.")] string? session = null,
        [Description("Game label stored with the capture (default 'sn2').")] string game = "sn2")
    {
        var s = ResolveSession(session);
        if (s is null) return ExternalTools.Err($"no session under {ForensicsDir()} to ingest.");
        return await RunPy("stereo_forensics_query.py", new[] {
            "summary", s, "--ingest-db", DbPath(), "--analyze-db", "--auto-findings", "--game", game
        });
    }

    [McpServerTool(Name = "uevr_forensics_suspects")]
    [Description("Rank the materialized suspects in the forensics DB (stereo_forensics_db.py rank-suspects) — the prioritized list of per-eye issues most likely to be the bug, with score/kind/root/slot/resource. Run uevr_forensics_ingest first. Optionally filter by game.")]
    public static async Task<string> Suspects(
        [Description("Game label filter (default 'sn2').")] string game = "sn2",
        [Description("Max suspects to return (default 20).")] int limit = 20)
        => await RunPy("stereo_forensics_db.py", new[] {
            "--db", DbPath(), "rank-suspects", "--game", game, "--limit", limit.ToString()
        });

    [McpServerTool(Name = "uevr_forensics_query")]
    [Description("Query a session bundle: summary | issues | event | shader | lineage | alias. Pass the subcommand and any extra args verbatim (e.g. subcommand='issues' extraArgs='--severity high --verbose', or subcommand='event' extraArgs='--id 7245', subcommand='shader' extraArgs='--ps 0xDE7C3822'). Defaults to the latest session.")]
    public static async Task<string> Query(
        [Description("query subcommand: summary | issues | event | shader | lineage | alias")] string subcommand,
        [Description("Extra args forwarded verbatim after '<subcommand> <session>'.")] string? extraArgs = null,
        [Description("Explicit session dir path; omit for the latest session.")] string? session = null)
    {
        var s = ResolveSession(session);
        if (s is null) return ExternalTools.Err($"no session under {ForensicsDir()}.");
        var args = new List<string> { subcommand, s };
        if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(ExternalTools.SplitArgs(extraArgs));
        return await RunPy("stereo_forensics_query.py", args);
    }

    [McpServerTool(Name = "uevr_forensics_generate_rules")]
    [Description("Turn ranked DB suspects into v2 experiment rule files (stereo_forensics_run_experiments.py generate). mode='probes' emits color_override probes to prove a suspect touches the ROI; mode='mutations' emits real interventions (swap_cbv/swap_descriptor/force_srv_array_slice) with a color-probe fallback for non-executable kinds. Writes candidate_rules.json + per-rule files to out_dir. Run uevr_forensics_ingest first.")]
    public static async Task<string> GenerateRules(
        [Description("Output dir for the candidate rule files.")] string outDir,
        [Description("'probes' (default) or 'mutations'.")] string mode = "probes",
        [Description("Game label (default 'sn2').")] string game = "sn2",
        [Description("Max candidates (default 12).")] int limit = 12,
        [Description("Also record planned experiments into the DB.")] bool recordDb = false)
    {
        var args = new List<string> {
            "--db", DbPath(), "generate", "--game", game, "--mode", mode,
            "--limit", limit.ToString(), "--out-dir", outDir
        };
        if (recordDb) args.Add("--record-db");
        return await RunPy("stereo_forensics_run_experiments.py", args);
    }

    [McpServerTool(Name = "uevr_forensics_shader_semantics")]
    [Description("Summarize shader semantics for stereo debugging (stereo_forensics_shader_semantics.py): parses DXBC SM4/5 / DXIL containers and reports sampled SRV/UAV slots, sample/branch/discard counts, and CB usage — i.e. whether a shader can actually affect visible pixels. Use 'analyze' for one .dxbc/.dxil file or 'analyze-dir' for a directory of dumps.")]
    public static async Task<string> ShaderSemantics(
        [Description("'analyze' (single file) or 'analyze-dir' (directory).")] string subcommand,
        [Description("Path to the .dxbc/.dxil file or directory of dumps.")] string path,
        [Description("Extra args forwarded verbatim.")] string? extraArgs = null)
    {
        var args = new List<string> { subcommand, path };
        if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(ExternalTools.SplitArgs(extraArgs));
        return await RunPy("stereo_forensics_shader_semantics.py", args, timeoutMs: 180000);
    }

    [McpServerTool(Name = "uevr_forensics_compile_rule")]
    [Description("Compile a captured event into a durable v2 rule skeleton (stereo_forensics_compile_rule.py compile). Specify the session, the event index, and the action (e.g. swap_cbv_left_to_right, swap_descriptor_from_left, force_srv_array_slice, skip, color_override). Forward action params (--root/--slot/--slice/--rgb/...) and optional --db/--out via extraArgs. Defaults to the latest session.")]
    public static async Task<string> CompileRule(
        [Description("Event index to compile a rule for.")] int eventIndex,
        [Description("Action type, e.g. swap_cbv_left_to_right | swap_descriptor_from_left | force_srv_array_slice | skip | color_override.")] string action,
        [Description("Extra args forwarded verbatim (e.g. '--root 5 --slot 8 --out rule.json --db <db> --game sn2').")] string? extraArgs = null,
        [Description("Explicit session dir path; omit for the latest session.")] string? session = null)
    {
        var s = ResolveSession(session);
        if (s is null) return ExternalTools.Err($"no session under {ForensicsDir()}.");
        var args = new List<string> { "compile", "--session", s, "--event", eventIndex.ToString(), "--action", action };
        if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(ExternalTools.SplitArgs(extraArgs));
        return await RunPy("stereo_forensics_compile_rule.py", args);
    }

    [McpServerTool(Name = "uevr_forensics_score")]
    [Description("Score an intervention by comparing baseline-vs-trial eye captures per eye (stereo_forensics_experiment.py score). Causal control: rewards a change to the right eye while the left stays put (score = right_delta - left_delta*penalty); likely_causal requires score>=threshold AND a confirmed apply. Pass the four PPM paths. Optional --db/--session/--promote-fix-rule via extraArgs.")]
    public static async Task<string> Score(
        [Description("Experiment name (free-form label).")] string experiment,
        [Description("Baseline left-eye PPM path.")] string baselineLeft,
        [Description("Baseline right-eye PPM path.")] string baselineRight,
        [Description("Trial left-eye PPM path.")] string trialLeft,
        [Description("Trial right-eye PPM path.")] string trialRight,
        [Description("ROI as 'x,y,w,h' or 'all' (default 'all').")] string roi = "all",
        [Description("Extra args forwarded verbatim (e.g. '--db <db> --session <dir> --promote-fix-rule').")] string? extraArgs = null)
    {
        var args = new List<string> {
            "score", "--experiment", experiment,
            "--baseline-left", baselineLeft, "--baseline-right", baselineRight,
            "--trial-left", trialLeft, "--trial-right", trialRight, "--roi", roi
        };
        if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(ExternalTools.SplitArgs(extraArgs));
        return await RunPy("stereo_forensics_experiment.py", args);
    }

    [McpServerTool(Name = "uevr_forensics_ab_loop")]
    [Description("Run the closed-loop A/B experiment runner (stereo_forensics_ab_loop.py run): for each candidate rule, capture a baseline, write the rule (game hot-reloads), capture a trial, score the ROI per eye, record to the DB, and optionally promote likely-causal winners to durable fix rules. REQUIRES the game running with the eye-screenshot trigger env (UEVR_SN2_EYE_SCREENSHOT_*) and the experiments file watched. Long-running (minutes). Point --candidate-rules at uevr_forensics_generate_rules output; --rules-file must equal the game's UEVR_STEREO_EXPERIMENTS_FILE.")]
    public static async Task<string> AbLoop(
        [Description("Candidate rules bundle (candidate_rules.json from uevr_forensics_generate_rules).")] string candidateRules,
        [Description("Output dir for per-rule captures + results.json.")] string outDir,
        [Description("Rules file the game watches; defaults to $UEVR_STEREO_EXPERIMENTS_FILE.")] string? rulesFile = null,
        [Description("Extra args forwarded verbatim (e.g. '--db <db> --game sn2 --promote-fix-rule --baseline-mode once --limit 4').")] string? extraArgs = null,
        [Description("Timeout in ms (default 900000 = 15 min).")] int timeoutMs = 900000)
    {
        var args = new List<string> {
            "run", "--candidate-rules", candidateRules,
            "--rules-file", rulesFile ?? ExperimentsFile(),
            "--out-dir", outDir
        };
        if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(ExternalTools.SplitArgs(extraArgs));
        return await RunPy("stereo_forensics_ab_loop.py", args, timeoutMs: timeoutMs);
    }

    // ── experiment rule control (write the file the game hot-reloads) ───

    [McpServerTool(Name = "uevr_forensics_set_experiments")]
    [Description("Write the experiment rules JSON the running game hot-reloads (UEVR_STEREO_EXPERIMENTS_FILE, polled ~every 30 frames). Pass a full rules document (a JSON string with a 'rules':[...] array, or a bare array). Supported actions are skip/color_override/swap_cbv_left_to_right/swap_descriptor_from_left/force_srv_array_slice. Use uevr_forensics_clear_experiments to disable.")]
    public static Task<string> SetExperiments(
        [Description("Rules JSON: either {\"rules\":[...]} or a bare [...] array.")] string rulesJson)
    {
        JsonElement parsed;
        try { parsed = JsonSerializer.Deserialize<JsonElement>(rulesJson); }
        catch (Exception ex) { return Task.FromResult(ExternalTools.Err($"invalid rules JSON: {ex.Message}")); }

        // Normalize to {"rules":[...]} which the loader accepts.
        object doc = parsed.ValueKind == JsonValueKind.Array
            ? new { schema = "uevr.stereo_forensics.rule_bundle.v2", rules = parsed }
            : (object)parsed;

        var path = ExperimentsFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(doc, ExternalTools.Json));
        }
        catch (Exception ex) { return Task.FromResult(ExternalTools.Err($"failed to write {path}: {ex.Message}")); }

        return Task.FromResult(JsonSerializer.Serialize(new {
            ok = true, file = path,
            note = "Game hot-reloads within ~30 frames if launched with UEVR_STEREO_EXPERIMENTS=1 and UEVR_STEREO_EXPERIMENTS_FILE pointing here."
        }, ExternalTools.Json));
    }

    [McpServerTool(Name = "uevr_forensics_clear_experiments")]
    [Description("Clear all active experiment rules by writing an empty rules document to UEVR_STEREO_EXPERIMENTS_FILE (the game hot-reloads to no interventions). Always do this after an A/B run so leftover rules don't keep mutating the game.")]
    public static Task<string> ClearExperiments()
    {
        var path = ExperimentsFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                schema = "uevr.stereo_forensics.rule_bundle.v2", rules = Array.Empty<object>()
            }, ExternalTools.Json));
        }
        catch (Exception ex) { return Task.FromResult(ExternalTools.Err($"failed to write {path}: {ex.Message}")); }
        return Task.FromResult(JsonSerializer.Serialize(new { ok = true, file = path, cleared = true }, ExternalTools.Json));
    }

    // ── symptom -> cause bridge (decompile / engine-source correlation) ─

    [McpServerTool(Name = "uevr_forensics_symbolize")]
    [Description("Resolve runtime addresses (e.g. a forensics issuer_stack, captured with UEVR_STEREO_FORENSICS_STACKS=1) to engine functions via the binfold symbol dump (220k UE syms) + the curated SN2 RVA dictionary. Returns demangled names + offsets, and source/role/confidence where curated. This is the keystone that turns a D3D12 symptom into the engine code that issued it. Pass a comma-separated list of VAs (and the module base if the addresses are ASLR'd absolute addresses).")]
    public static async Task<string> Symbolize(
        [Description("Comma-separated addresses (VAs by default, or RVAs).")] string addresses,
        [Description("Loaded module base (the event's module_base) for ASLR'd absolute addresses; omit if addresses already match the dictionary image base.")] string? moduleBase = null)
    {
        var args = new List<string> { "stack", addresses };
        if (!string.IsNullOrWhiteSpace(moduleBase)) { args.Add("--base"); args.Add(moduleBase); }
        return await RunPy("sn2_symbolizer.py", args);
    }

    [McpServerTool(Name = "uevr_forensics_shader_code")]
    [Description("Map a shader CRC or compute-shader friendly name to its semantic role and (with source=true) the backing UE 5.6.1 source files (.cpp/.usf). Unifies the render_names dictionaries. Use to turn a captured ps_crc/cs_crc into 'this is the SingleLayerWater base pass / Nanite RasterBinBuild' plus the engine source to inspect. Provide either crc OR name.")]
    public static async Task<string> ShaderCode(
        [Description("Pixel/compute shader CRC, e.g. 0x166dba88 (omit if using name).")] string? crc = null,
        [Description("Compute-shader friendly name, e.g. Nanite.RasterBinBuild (omit if using crc).")] string? name = null,
        [Description("Also locate UE engine source files (slower grep).")] bool source = false)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(crc)) { args.Add("crc"); args.Add(crc); }
        else if (!string.IsNullOrWhiteSpace(name)) { args.Add("name"); args.Add(name); }
        else return ExternalTools.Err("provide crc or name");
        if (source) args.Add("--source");
        return await RunPy("sn2_shader_code_map.py", args, timeoutMs: 60000);
    }

    [McpServerTool(Name = "uevr_forensics_ida_ue_diff")]
    [Description("Side-by-side a shipping-binary function with its UE 5.6.1 source: locates the UE source function body (Class::Method) and loads the IDA decompile from cache (or prints the ida-pro-mcp/idat64 invocation to fetch it). Answers 'is this stock UE behavior under -emulatestereo or a UWE/game modification?' for a code site sn2_trace pointed at. Pass a symbol (Class::Method or mangled) or a VA.")]
    public static async Task<string> IdaUeDiff(
        [Description("Symbol (e.g. FDeferredShadingSceneRenderer::RenderSingleLayerWater) or a VA (0x142EC70C0).")] string target)
        => await RunPy("sn2_ida_ue_diff.py", new[] { target }, timeoutMs: 60000);

    [McpServerTool(Name = "uevr_forensics_trace")]
    [Description("CAPSTONE: trace StereoForensics eye_diff issues back to a code-side root-cause hypothesis. Joins each issue -> involved D3D12 events -> symbolized issuer callstack (engine fn) -> shader/code map (UE source) -> producer lineage, and emits a ranked hypothesis with the engine function/source file. Defaults to the latest session. Best results when the capture ran with UEVR_STEREO_FORENSICS_STACKS=1 (otherwise it still maps shaders + symptoms and tells you to enable stacks for the exact code site).")]
    public static async Task<string> Trace(
        [Description("Explicit session dir; omit for latest.")] string? session = null,
        [Description("Trace only this issue index (omit to trace the top issues).")] int? issue = null,
        [Description("Filter issues by kind, e.g. eye_event_count_mismatch.")] string? kind = null,
        [Description("Max issues to trace (default 8).")] int limit = 8)
    {
        var args = new List<string>();
        var s = ResolveSession(session);
        if (s != null) { args.Add("--session"); args.Add(s); }
        if (issue.HasValue) { args.Add("--issue"); args.Add(issue.Value.ToString()); }
        if (!string.IsNullOrWhiteSpace(kind)) { args.Add("--kind"); args.Add(kind); }
        args.Add("--limit"); args.Add(limit.ToString());
        return await RunPy("sn2_trace.py", args, timeoutMs: 120000);
    }
}
