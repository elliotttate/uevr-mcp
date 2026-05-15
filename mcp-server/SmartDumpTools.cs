using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// High-level "just dump this game" tools that compose the live UEVR pipeline,
/// the Dumper-7 fallback, the USMAP→UHT converter, per-game profile caching,
/// and cross-dump USMAP diffing into a single entry point.
///
/// The flagship is <c>uevr_dump_project</c>: give it a game exe, it picks
/// the right pipeline (live first, Dumper-7 fallback on crash), produces a
/// buildable UE project, and caches the decision so next time is instant.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class SmartDumpTools
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── Profile cache (~/.uevr-mcp/state.json) ─────────────────────────
    //
    // Remember per-game: last-working pipeline (live vs dumper7), preferred
    // stability mode flags, last successful project path, UE version hint.
    // Second dump of the same game hits this cache and skips the pipeline
    // decision dance.

    static string StatePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".uevr-mcp", "state.json");

    class GameProfileData
    {
        public string? exe { get; set; }
        public string? lastPipeline { get; set; }         // "live" | "dumper7"
        public string? ueVersion { get; set; }            // e.g. "4.26.2"
        public bool?   stabilityMode { get; set; }
        public string? lastProject { get; set; }
        public string? lastUsmap { get; set; }
        public DateTime lastSuccess { get; set; }
        public int?    pidHint { get; set; }
    }

    class ProfileStore
    {
        public Dictionary<string, GameProfileData> games { get; set; } = new();
    }

    static ProfileStore LoadStore()
    {
        try
        {
            var p = StatePath();
            if (!File.Exists(p)) return new ProfileStore();
            return JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(p)) ?? new ProfileStore();
        }
        catch { return new ProfileStore(); }
    }

    static void SaveStore(ProfileStore store)
    {
        var p = StatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(store, Json));
    }

    [McpServerTool(Name = "uevr_game_profile")]
    [Description("Read, write, or list per-game decision cache entries at ~/.uevr-mcp/state.json. Each entry records which pipeline last worked (live vs Dumper-7), UE version, whether stability mode was needed, and paths to the last successful dump outputs. `uevr_dump_project` consults this cache automatically; this tool lets you inspect or edit it directly.")]
    public static string GameProfileCrud(
        [Description("'get' / 'set' / 'list' / 'clear'. Default 'get'.")] string op = "list",
        [Description("Game name (exe basename without .exe). Required for get/set/clear.")] string? gameName = null,
        [Description("When op='set': JSON patch of fields to update. Keys: exe, lastPipeline, ueVersion, stabilityMode, lastProject, lastUsmap, pidHint.")] string? patch = null)
    {
        var store = LoadStore();
        switch (op)
        {
            case "list":
                return JsonSerializer.Serialize(new {
                    ok = true,
                    path = StatePath(),
                    games = store.games.Select(kv => new {
                        name = kv.Key,
                        kv.Value.exe,
                        kv.Value.lastPipeline,
                        kv.Value.ueVersion,
                        kv.Value.stabilityMode,
                        kv.Value.lastSuccess,
                    })
                }, Json);
            case "get":
                if (string.IsNullOrEmpty(gameName)) return Err("gameName required");
                return store.games.TryGetValue(gameName, out var prof)
                    ? JsonSerializer.Serialize(new { ok = true, profile = prof }, Json)
                    : JsonSerializer.Serialize(new { ok = true, profile = (object?)null }, Json);
            case "set":
                if (string.IsNullOrEmpty(gameName)) return Err("gameName required");
                if (string.IsNullOrEmpty(patch))   return Err("patch required");
                {
                    var cur = store.games.TryGetValue(gameName, out var existing) ? existing : new GameProfileData();
                    try
                    {
                        var incoming = JsonSerializer.Deserialize<GameProfileData>(patch);
                        if (incoming is not null)
                        {
                            if (incoming.exe is not null)            cur.exe = incoming.exe;
                            if (incoming.lastPipeline is not null)   cur.lastPipeline = incoming.lastPipeline;
                            if (incoming.ueVersion is not null)      cur.ueVersion = incoming.ueVersion;
                            if (incoming.stabilityMode is not null)  cur.stabilityMode = incoming.stabilityMode;
                            if (incoming.lastProject is not null)    cur.lastProject = incoming.lastProject;
                            if (incoming.lastUsmap is not null)      cur.lastUsmap = incoming.lastUsmap;
                            if (incoming.pidHint is not null)        cur.pidHint = incoming.pidHint;
                        }
                    }
                    catch (Exception ex) { return Err("invalid patch JSON: " + ex.Message); }
                    store.games[gameName] = cur;
                    SaveStore(store);
                    return JsonSerializer.Serialize(new { ok = true, profile = cur }, Json);
                }
            case "clear":
                if (string.IsNullOrEmpty(gameName))
                {
                    store.games.Clear();
                    SaveStore(store);
                    return JsonSerializer.Serialize(new { ok = true, cleared = "all" }, Json);
                }
                store.games.Remove(gameName);
                SaveStore(store);
                return JsonSerializer.Serialize(new { ok = true, cleared = gameName }, Json);
            default:
                return Err($"unknown op '{op}' — expected get/set/list/clear");
        }
    }

    // ─── uevr_dump_project — one-call smart dump ────────────────────────

    [McpServerTool(Name = "uevr_dump_project")]
    [Description("One-call smart dump. Tries the live UEVR pipeline first (richer output: real offsets, UCLASS flags, interfaces, UFUNCTION specifiers). If UEVR crashes or times out on this game, falls back to Dumper-7 + uevr_dump_uht_from_usmap automatically. Caches the decision in ~/.uevr-mcp/state.json so subsequent runs pick the winning pipeline immediately. Returns the project path either way.")]
    public static async Task<string> DumpProject(
        [Description("Absolute path to the game's shipping exe.")] string gameExe,
        [Description("Where to write the UE project. Default: E:\\tmp\\uevr-<gameName>\\proj.")] string? outDir = null,
        [Description("Project name for the .uproject. Default: <GameName>Mirror.")] string? projectName = null,
        [Description("Engine association in the .uproject (e.g. '4.26', '4.27', '5.3'). Default '4.26'.")] string engineAssociation = "4.26",
        [Description("Force the Dumper-7 fallback path (skip live pipeline). Useful when you know live will crash.")] bool forceDumper7 = false,
        [Description("Apply UEVR stability mode (write_stability_config + suppress_d3d_monitor). Default: auto from cached decision.")] bool? stabilityMode = null)
    {
        if (!File.Exists(gameExe))
            return Err($"Game exe not found: {gameExe}");
        gameExe = Path.GetFullPath(gameExe);
        var gameName = Path.GetFileNameWithoutExtension(gameExe);
        outDir ??= $@"E:\tmp\uevr-{gameName}\proj";
        projectName ??= SanitizeProjectName(gameName) + "Mirror";

        var store = LoadStore();
        store.games.TryGetValue(gameName, out var profile);
        profile ??= new GameProfileData { exe = gameExe };

        // Decide pipeline. Explicit forceDumper7 > cached decision > live-first.
        bool tryLive = !forceDumper7 && profile.lastPipeline != "dumper7";
        bool useStability = stabilityMode ?? profile.stabilityMode ?? false;

        var attempts = new List<object>();

        // ─── Attempt 1: live pipeline ─────────────────────────────────
        if (tryLive)
        {
            var liveResult = await TryLivePipeline(gameExe, gameName, outDir, projectName,
                engineAssociation, useStability);
            attempts.Add(new { pipeline = "live", outcome = liveResult.outcome, detail = liveResult.detail });
            if (liveResult.success)
            {
                profile.exe = gameExe;
                profile.lastPipeline = "live";
                profile.lastProject = outDir;
                profile.lastUsmap = liveResult.usmapPath;
                profile.stabilityMode = useStability;
                profile.lastSuccess = DateTime.UtcNow;
                store.games[gameName] = profile;
                SaveStore(store);
                return JsonSerializer.Serialize(new {
                    ok = true,
                    data = new {
                        pipeline = "live",
                        projectPath = outDir,
                        usmapPath = liveResult.usmapPath,
                        stats = liveResult.stats,
                        profileCachedAt = StatePath(),
                        attempts,
                    }
                }, Json);
            }
        }

        // ─── Attempt 2: Dumper-7 fallback ─────────────────────────────
        var dumper7Result = await TryDumper7Pipeline(gameExe, gameName, outDir, projectName, engineAssociation);
        attempts.Add(new { pipeline = "dumper7", outcome = dumper7Result.outcome, detail = dumper7Result.detail });
        if (dumper7Result.success)
        {
            profile.exe = gameExe;
            profile.lastPipeline = "dumper7";
            profile.lastProject = outDir;
            profile.lastUsmap = dumper7Result.usmapPath;
            profile.ueVersion = dumper7Result.ueVersion;
            profile.lastSuccess = DateTime.UtcNow;
            store.games[gameName] = profile;
            SaveStore(store);
            return JsonSerializer.Serialize(new {
                ok = true,
                data = new {
                    pipeline = "dumper7",
                    projectPath = outDir,
                    usmapPath = dumper7Result.usmapPath,
                    stats = dumper7Result.stats,
                    ueVersion = dumper7Result.ueVersion,
                    profileCachedAt = StatePath(),
                    attempts,
                }
            }, Json);
        }

        return JsonSerializer.Serialize(new {
            ok = false,
            error = "Both pipelines failed. See 'attempts' for per-pipeline detail.",
            attempts,
        }, Json);
    }

    record PipelineResult(bool success, string outcome, string? detail, string? usmapPath, object? stats, string? ueVersion);

    static async Task<PipelineResult> TryLivePipeline(string gameExe, string gameName, string outDir,
        string projectName, string engineAssociation, bool useStability)
    {
        try
        {
            // Optional stability config before launch.
            if (useStability) StabilityTools.WriteStabilityConfig(gameName);

            // Setup: install plugin + launch (if needed) + inject.
            var setupRaw = await InvokeSetupGame(gameExe);
            using var setupDoc = JsonDocument.Parse(setupRaw);
            if (setupDoc.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                return new(false, "setup_failed", setupRaw, null, null, null);
            var pid = setupDoc.RootElement.GetProperty("data").GetProperty("pid").GetInt32();

            // Optional suppression of D3D monitor threads.
            if (useStability)
            {
                try { StabilityTools.SuppressD3DMonitor(pid, includeCommandThread: true); }
                catch { /* best effort */ }
            }

            // Wait for the plugin HTTP endpoint.
            var waitRaw = await ReadinessTools.WaitForPlugin(timeoutMs: 30000);
            using var waitDoc = JsonDocument.Parse(waitRaw);
            if (!waitDoc.RootElement.TryGetProperty("ok", out var waitOk) || !waitOk.GetBoolean())
                return new(false, "plugin_http_never_came_up", waitRaw, null, null, null);

            // The live dump flow. Use the dump_ue_project tool for the full project.
            var usmapPath = Path.Combine(Path.GetDirectoryName(outDir) ?? outDir, gameName + ".usmap");
            var usmapRaw = await DumpTools.DumpUsmap(usmapPath, filter: null, compression: "none");
            using var usmapDoc = JsonDocument.Parse(usmapRaw);
            if (!usmapDoc.RootElement.TryGetProperty("ok", out var usOk) || !usOk.GetBoolean())
                return new(false, "dump_usmap_failed", usmapRaw, null, null, null);

            var projRaw = await UhtSdkTools.DumpUeProject(outDir, projectName,
                modules: null, engineAssociation: engineAssociation, skipEngineModules: true);
            using var projDoc = JsonDocument.Parse(projRaw);
            if (!projDoc.RootElement.TryGetProperty("ok", out var pOk) || !pOk.GetBoolean())
                return new(false, "dump_ue_project_failed", projRaw, null, null, null);

            object? stats = null;
            if (projDoc.RootElement.TryGetProperty("data", out var projData))
                stats = JsonArgs.Parse(projData.GetRawText());

            return new(true, "ok", null, usmapPath, stats, null);
        }
        catch (Exception ex)
        {
            return new(false, "exception", ex.ToString(), null, null, null);
        }
    }

    static async Task<PipelineResult> TryDumper7Pipeline(string gameExe, string gameName, string outDir,
        string projectName, string engineAssociation)
    {
        try
        {
            // The Dumper-7 tool needs the game already running. If it isn't, launch
            // via setup_game with skipInject=true so UEVR doesn't get involved.
            int pid;
            var running = System.Diagnostics.Process.GetProcessesByName(gameName);
            if (running.Length == 0)
            {
                var launchRaw = await InvokeSetupGame(gameExe, skipInject: true, skipPluginInstall: true);
                using var doc = JsonDocument.Parse(launchRaw);
                if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                    return new(false, "launch_failed", launchRaw, null, null, null);
                pid = doc.RootElement.GetProperty("data").GetProperty("pid").GetInt32();
                // Wait a beat for UE reflection to populate before Dumper-7 reads.
                await Task.Delay(30_000);
            }
            else
            {
                pid = running[0].Id;
            }

            var dumperRaw = await Dumper7Tools.Dumper7Run(pid: pid, gameName: gameName, timeoutMs: 180_000);
            using var dumpDoc = JsonDocument.Parse(dumperRaw);
            if (!dumpDoc.RootElement.TryGetProperty("ok", out var dok) || !dok.GetBoolean())
                return new(false, "dumper7_failed", dumperRaw, null, null, null);
            var sdk = dumpDoc.RootElement.GetProperty("data").GetProperty("sdk");
            var sdkDir = sdk.GetProperty("Dir").GetString()!;
            var ueVersion = InferUeVersion(sdkDir);

            // Find the USMAP file inside the SDK dir.
            var usmapPath = Directory.EnumerateFiles(sdkDir, "*.usmap", SearchOption.AllDirectories).FirstOrDefault();
            if (usmapPath is null)
                return new(false, "no_usmap_in_dumper7_output", sdkDir, null, null, null);

            // Pass the Dumper-7 root so the converter can backfill offsets from
            // GObjects-Dump-WithProperties.txt.
            var projRaw = UsmapSdkTools.DumpUhtFromUsmap(
                usmapPath: usmapPath,
                outDir: outDir,
                projectName: projectName,
                moduleName: SanitizeProjectName(gameName),
                engineAssociation: engineAssociation,
                gObjectsPath: Path.Combine(sdkDir, "GObjects-Dump-WithProperties.txt"));
            using var projDoc = JsonDocument.Parse(projRaw);
            if (!projDoc.RootElement.TryGetProperty("ok", out var pOk) || !pOk.GetBoolean())
                return new(false, "uht_from_usmap_failed", projRaw, null, null, null);

            object? stats = null;
            if (projDoc.RootElement.TryGetProperty("data", out var d))
                stats = JsonArgs.Parse(d.GetRawText());
            return new(true, "ok", null, usmapPath, stats, ueVersion);
        }
        catch (Exception ex)
        {
            return new(false, "exception", ex.ToString(), null, null, null);
        }
    }

    static async Task<string> InvokeSetupGame(string gameExe, bool skipInject = false, bool skipPluginInstall = false)
        => await SetupTools.SetupGame(
            gameExe: gameExe,
            launchIfMissing: true,
            launchArgs: "-windowed",
            backendDll: null,
            pluginDll: null,
            windowWaitMs: 25_000,
            skipPluginInstall: skipPluginInstall,
            skipInject: skipInject);

    static string InferUeVersion(string sdkDir)
    {
        // Dumper-7 names folders like "SB-4.26.2-0+++UE4+Release-4.26"
        var m = Regex.Match(Path.GetFileName(sdkDir) ?? "", @"(\d+\.\d+(?:\.\d+)?)");
        return m.Success ? m.Groups[1].Value : "";
    }

    static string SanitizeProjectName(string name)
    {
        // Strip common UE exe suffixes so the derived project name is clean.
        var cleaned = name.Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("-WinGDK-Shipping", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("-Shipping", "", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var c in cleaned)
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        if (sb.Length == 0) sb.Append("Game");
        return sb.ToString();
    }

    // ─── uevr_build_info — for reproducibility / bug reports ───────────

    [McpServerTool(Name = "uevr_build_info")]
    [Description("Report plugin + server + backend build info: assembly version, built-DLL timestamps, UEVRBackend.dll file version, Dumper-7 DLL presence. Useful for reproducibility and bug reports.")]
    public static string BuildInfo()
    {
        var asm = typeof(SmartDumpTools).Assembly;
        var asmVer = asm.GetName().Version?.ToString() ?? "?";
        var asmPath = asm.Location;

        string FileInfoStr(string p) {
            try {
                var fi = new FileInfo(p);
                if (!fi.Exists) return "missing";
                return $"{fi.Length} bytes, {fi.LastWriteTimeUtc:s}Z";
            } catch { return "err"; }
        }

        string FileVerStr(string p) {
            try {
                if (!File.Exists(p)) return "missing";
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(p);
                return $"{v.FileVersion} ({v.ProductVersion})";
            } catch { return "err"; }
        }

        var pluginDll  = GuessPluginDll();
        var backendDll = GuessBackendDll();
        var dumper7Dll = GuessDumper7Dll();

        return JsonSerializer.Serialize(new {
            ok = true,
            data = new {
                server = new {
                    version = asmVer,
                    path = asmPath,
                    file = FileInfoStr(asmPath),
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    dotnet = Environment.Version.ToString(),
                },
                plugin = new { path = pluginDll, file = FileInfoStr(pluginDll ?? "") },
                backend = new { path = backendDll, file = FileInfoStr(backendDll ?? ""), version = FileVerStr(backendDll ?? "") },
                dumper7 = new { path = dumper7Dll, file = FileInfoStr(dumper7Dll ?? "") },
            }
        }, Json);
    }

    // Single-file-safe starting directory for path probes.
    static string GuessExeDir()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) return Path.GetDirectoryName(exe)!;
        }
        catch { }
        var asm = typeof(SmartDumpTools).Assembly.Location;
        if (!string.IsNullOrEmpty(asm))
            return Path.GetDirectoryName(asm) ?? AppContext.BaseDirectory;
        return AppContext.BaseDirectory;
    }

    static string? GuessPluginDll()
    {
        var env = Environment.GetEnvironmentVariable("UEVR_MCP_PLUGIN_DLL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var exeDir = GuessExeDir();
        // Release-zip layout: uevr_mcp.dll sits next to UevrMcpServer.exe.
        var nextToExe = Path.Combine(exeDir, "uevr_mcp.dll");
        if (File.Exists(nextToExe)) return nextToExe;
        // Repo dev layout: walk up looking for plugin/build/Release.
        var dir = exeDir;
        while (dir is not null)
        {
            var c = Path.Combine(dir, "plugin", "build", "Release", "uevr_mcp.dll");
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
    static string? GuessBackendDll()
    {
        var env = Environment.GetEnvironmentVariable("UEVR_BACKEND_DLL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        // Release-zip layout: UEVRBackend.dll sits next to UevrMcpServer.exe.
        var nextToExe = Path.Combine(GuessExeDir(), "UEVRBackend.dll");
        if (File.Exists(nextToExe)) return nextToExe;
        const string fallback = @"E:\Github\UEVR\build\bin\uevr\UEVRBackend.dll";
        return File.Exists(fallback) ? fallback : null;
    }
    static string? GuessDumper7Dll()
    {
        var exeDir = GuessExeDir();
        var nextToExe = Path.Combine(exeDir, "dumper7.dll");
        if (File.Exists(nextToExe)) return nextToExe;
        var dir = exeDir;
        while (dir is not null)
        {
            var c = Path.Combine(dir, "plugin", "build", "Release", "dumper7.dll");
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ─── uevr_diff_usmap — cross-build comparison ──────────────────────

    [McpServerTool(Name = "uevr_diff_usmap")]
    [Description("Diff two USMAP files and report added/removed/changed structs and enums. Uses jmap's usmap CLI for parsing (so bring your own usmap.exe on PATH or at $USMAP_CLI). Useful for tracking type-layout changes across game patches.")]
    public static async Task<string> DiffUsmap(
        [Description("Left-hand USMAP (the 'before' version).")] string leftPath,
        [Description("Right-hand USMAP (the 'after' version).")] string rightPath)
    {
        if (!File.Exists(leftPath))  return Err($"left usmap not found: {leftPath}");
        if (!File.Exists(rightPath)) return Err($"right usmap not found: {rightPath}");

        try
        {
            var left = await ParseUsmap(leftPath);
            var right = await ParseUsmap(rightPath);

            var diff = new Diff();
            DiffMap(left.structs, right.structs, diff.structs);
            DiffMap(left.enums,   right.enums,   diff.enums);

            return JsonSerializer.Serialize(new {
                ok = true,
                data = new {
                    left = new { path = leftPath, structs = left.structs.Count, enums = left.enums.Count },
                    right = new { path = rightPath, structs = right.structs.Count, enums = right.enums.Count },
                    diff,
                }
            }, Json);
        }
        catch (Exception ex) { return Err("diff failed: " + ex.Message); }
    }

    class UsmapContents
    {
        public Dictionary<string, string> structs = new(); // name → super (for fingerprinting)
        public Dictionary<string, string> enums = new();   // name → entries-count as string
    }

    class DiffBucket
    {
        public List<string> added = new();
        public List<string> removed = new();
        public List<string> changed = new();
    }

    class Diff
    {
        public DiffBucket structs { get; set; } = new();
        public DiffBucket enums { get; set; } = new();
    }

    static void DiffMap(Dictionary<string, string> l, Dictionary<string, string> r, DiffBucket b)
    {
        foreach (var k in r.Keys.Except(l.Keys, StringComparer.Ordinal)) b.added.Add(k);
        foreach (var k in l.Keys.Except(r.Keys, StringComparer.Ordinal)) b.removed.Add(k);
        foreach (var k in l.Keys.Intersect(r.Keys, StringComparer.Ordinal))
            if (!string.Equals(l[k], r[k], StringComparison.Ordinal)) b.changed.Add(k);
        b.added.Sort(StringComparer.Ordinal);
        b.removed.Sort(StringComparer.Ordinal);
        b.changed.Sort(StringComparer.Ordinal);
    }

    static async Task<UsmapContents> ParseUsmap(string usmapPath)
    {
        // Shell to jmap's usmap CLI — same resolution as uevr_validate_usmap.
        var cli = Environment.GetEnvironmentVariable("USMAP_CLI");
        if (string.IsNullOrEmpty(cli) || !File.Exists(cli))
            cli = @"E:\Github\jmap\target\release\usmap.exe";
        if (!File.Exists(cli))
            throw new FileNotFoundException("usmap CLI not found; set $USMAP_CLI");

        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("to-json");
        psi.ArgumentList.Add(usmapPath);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new Exception($"usmap CLI exit {p.ExitCode}: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var c = new UsmapContents();
        if (doc.RootElement.TryGetProperty("structs", out var structs))
        {
            foreach (var pair in structs.EnumerateObject())
            {
                string fingerprint = pair.Value.TryGetProperty("super_struct", out var ss) && ss.ValueKind != JsonValueKind.Null
                    ? "super:" + ss.GetString() : "super:null";
                if (pair.Value.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
                    fingerprint += "|props:" + props.GetArrayLength();
                c.structs[pair.Name] = fingerprint;
            }
        }
        if (doc.RootElement.TryGetProperty("enums", out var enums))
        {
            foreach (var pair in enums.EnumerateObject())
            {
                int entryCount = 0;
                if (pair.Value.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
                    entryCount = entries.EnumerateObject().Count();
                c.enums[pair.Name] = "entries:" + entryCount;
            }
        }
        return c;
    }

    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, Json);
}
