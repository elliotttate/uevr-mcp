using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Dumper-7 integration. Dumper-7 (https://github.com/Encryqed/Dumper-7, MIT) is
/// a universal UE4/UE5 SDK generator: injected into a running game, it walks
/// GUObjectArray and writes a full C++ SDK (plus .usmap and IDA mappings) to
/// Settings::Generator::SDKGenerationPath.
///
/// We *vendor* Dumper-7's source tree under plugin/external/Dumper-7/ and build
/// it as its own DLL (plugin/build/Release/dumper7.dll) alongside uevr_mcp.dll.
/// No external fork or separate build step is required — `cmake --build` from
/// the repo produces both DLLs. Our DllMain shim (external/Dumper-7/main.cpp)
/// reads the DUMPER7_OUTPUT_ROOT env var before starting and writes a
/// `dumper7_done.txt` marker at the end, which these tools use for precise
/// completion detection.
///
/// Process:
///   1. Game must be running (Dumper-7 needs the UE runtime fully initialized —
///      waiting for the main menu is safest)
///   2. uevr_dumper7_run → CreateRemoteThread+LoadLibraryA → DllMain auto-dumps
///   3. Tool polls the output root for `dumper7_done.txt`, returns structure
///
/// Configuration (all optional — defaults work with the vendored DLL):
///   $DUMPER7_DLL         — override DLL path (default: our vendored build)
///   $DUMPER7_OUTPUT_ROOT — where Dumper-7 writes SDKs (default C:\Dumper-7)
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class Dumper7Tools
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    const string DefaultSdkRoot = @"C:\Dumper-7";
    const string DoneMarker = "dumper7_done.txt";

    // ─── Paths ──────────────────────────────────────────────────────────

    static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(Dumper7Tools).Assembly.Location);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "plugin")) &&
                Directory.Exists(Path.Combine(dir, "mcp-server")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        var asm = Path.GetDirectoryName(typeof(Dumper7Tools).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(asm, "..", "..", "..", ".."));
    }

    static string? ResolveDumperDll(string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var env = Environment.GetEnvironmentVariable("DUMPER7_DLL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return Path.GetFullPath(env);

        var root = RepoRoot();
        string[] candidates =
        {
            Path.Combine(root, "plugin", "build", "Release", "dumper7.dll"),
            Path.Combine(root, "plugin", "build", "Debug",   "dumper7.dll"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);

        return null;
    }

    static string DefaultOutputRoot()
    {
        var env = Environment.GetEnvironmentVariable("DUMPER7_OUTPUT_ROOT");
        return string.IsNullOrEmpty(env) ? DefaultSdkRoot : env;
    }

    static List<string> PotentialOutputRoots(string? explicitRoot, string? gameName)
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (!roots.Any(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase)))
                roots.Add(p);
        }
        Add(explicitRoot);
        Add(Environment.GetEnvironmentVariable("DUMPER7_OUTPUT_ROOT"));
        if (!string.IsNullOrEmpty(gameName))
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Add(Path.Combine(appdata, "UnrealVRMod", gameName, "C++HeaderDump"));
        }
        Add(DefaultSdkRoot);
        return roots;
    }

    // ─── Process helpers ────────────────────────────────────────────────

    static Process? ResolveProcess(int? pid, string? gameExe, string? gameName)
    {
        if (pid is int p && p > 0)
        {
            try { return Process.GetProcessById(p); }
            catch { return null; }
        }
        if (!string.IsNullOrEmpty(gameExe) && File.Exists(gameExe))
        {
            var name = Path.GetFileNameWithoutExtension(gameExe);
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    if (string.Equals(proc.MainModule?.FileName, gameExe, StringComparison.OrdinalIgnoreCase))
                        return proc;
                }
                catch { }
                return proc;
            }
        }
        if (!string.IsNullOrEmpty(gameName))
        {
            var procs = Process.GetProcessesByName(gameName);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    static string? GameNameFromProc(Process p)
    {
        try { return Path.GetFileNameWithoutExtension(p.MainModule?.FileName); }
        catch { return p.ProcessName; }
    }

    // ─── Output discovery ───────────────────────────────────────────────

    // Snapshot: recursive file set per root. After injection we diff this to
    // locate new files and infer the SDK folder. Stored as a hashset for O(1)
    // membership checks — Dumper-7 can write thousands of files.
    static Dictionary<string, HashSet<string>> SnapshotRoots(IEnumerable<string> roots)
    {
        var snap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(root))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        set.Add(f);
                }
                catch (UnauthorizedAccessException) { /* skip */ }
                catch (IOException) { /* skip */ }
            }
            snap[root] = set;
        }
        return snap;
    }

    // Given new files (relative to a snapshot), decide which folder is "the SDK":
    //   - if all new files share a single direct-child folder of `root`, return that child
    //   - otherwise (files live directly in root), return `root`
    static string InferSdkDir(string root, IEnumerable<string> newFiles)
    {
        var topLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in newFiles)
        {
            var rel = Path.GetRelativePath(root, f);
            var first = rel.Split(new[] { '/', '\\' }, 2)[0];
            topLevels.Add(Path.Combine(root, first));
        }
        if (topLevels.Count == 1)
        {
            var only = topLevels.First();
            if (Directory.Exists(only)) return only;
        }
        return root;
    }

    // Wait for either (a) the done-marker file our shim writes, or
    // (b) a period of file-write stability. Returns the SDK folder, or null on timeout.
    static async Task<string?> WaitForCompletion(
        Dictionary<string, HashSet<string>> snapshot,
        int timeoutMs,
        int stableSeconds,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int stableTicks = 0;
        (int count, long bytes) lastStat = (0, 0);
        string? lastSdkDir = null;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var kvp in snapshot)
            {
                var root = kvp.Key;
                var baseline = kvp.Value;
                if (!Directory.Exists(root)) continue;

                List<string> current;
                try
                {
                    current = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var newFiles = current.Where(f => !baseline.Contains(f)).ToList();
                if (newFiles.Count == 0) continue;

                var sdkDir = InferSdkDir(root, newFiles);
                lastSdkDir = sdkDir;

                // (a) Marker detection — Dumper-7 shim writes this at the end.
                var marker = Path.Combine(sdkDir, DoneMarker);
                if (File.Exists(marker)) return sdkDir;
                var rootMarker = Path.Combine(root, DoneMarker);
                if (File.Exists(rootMarker)) return InferSdkDir(root, newFiles);

                // (b) Stability fallback — count/bytes unchanged for N ticks.
                long bytes = 0;
                foreach (var f in newFiles)
                {
                    try { bytes += new FileInfo(f).Length; } catch { }
                }
                var cur = (newFiles.Count, bytes);
                if (cur == lastStat && cur.Item1 > 0) stableTicks++;
                else stableTicks = 0;
                lastStat = cur;
                if (stableTicks >= stableSeconds) return sdkDir;
            }

            await Task.Delay(1000, ct);
        }
        return lastSdkDir; // Return best guess even if we never hit stability.
    }

    // ─── SDK layout analysis ───────────────────────────────────────────

    record SdkLayout(
        string Dir,
        string? MasterHeader,
        string? HeaderFolder,
        int HppCount,
        int HCount,
        int UsmapCount,
        long TotalBytes,
        bool HasDoneMarker,
        List<string> SamplePackages);

    static SdkLayout AnalyzeSdk(string dir)
    {
        string? master = null;
        string? headerRoot = null;

        // Stock Dumper-7: <dir>/CppSDK/SDK/*.hpp with <dir>/CppSDK/SDK.hpp
        var cppSdk = Path.Combine(dir, "CppSDK");
        if (Directory.Exists(cppSdk))
        {
            var m = Path.Combine(cppSdk, "SDK.hpp");
            if (File.Exists(m)) master = m;
            var sdk = Path.Combine(cppSdk, "SDK");
            if (Directory.Exists(sdk)) headerRoot = sdk;
        }
        // Flat layout fallback (some forks + CleanUp'd builds): <dir>/*.hpp
        if (master is null)
        {
            var m = Path.Combine(dir, "SDK.hpp");
            if (File.Exists(m)) master = m;
        }
        headerRoot ??= dir;

        int hpp = 0, h = 0, usmap = 0;
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f);
            if (string.Equals(ext, ".hpp", StringComparison.OrdinalIgnoreCase)) hpp++;
            else if (string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase)) h++;
            else if (string.Equals(ext, ".usmap", StringComparison.OrdinalIgnoreCase)) usmap++;
            try { bytes += new FileInfo(f).Length; } catch { }
        }

        var samples = Directory.Exists(headerRoot)
            ? Directory.EnumerateFiles(headerRoot, "*.hpp", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .Take(25)
                .ToList()!
            : new List<string>();

        return new SdkLayout(
            Dir: Path.GetFullPath(dir),
            MasterHeader: master,
            HeaderFolder: Path.GetFullPath(headerRoot),
            HppCount: hpp,
            HCount: h,
            UsmapCount: usmap,
            TotalBytes: bytes,
            HasDoneMarker: File.Exists(Path.Combine(dir, DoneMarker)),
            SamplePackages: samples);
    }

    // ─── Tool: inject ───────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_dumper7_inject")]
    [Description("Inject our vendored dumper7.dll into a running UE process. DllMain spawns a worker thread that walks GUObjectArray and writes a full C++ SDK to Generator::SDKFolder (default C:\\Dumper-7, overridable via DUMPER7_OUTPUT_ROOT). Returns once LoadLibraryA returns — the dump runs on the target's worker thread (seconds to a minute). Use uevr_dumper7_run for a one-shot inject+wait that returns the SDK folder.")]
    public static string Dumper7Inject(
        [Description("Target process id. Either pid or gameExe/gameName is required.")] int? pid = null,
        [Description("Absolute path to the game's exe (used to find the running process).")] string? gameExe = null,
        [Description("Process name (exe without .exe) to match.")] string? gameName = null,
        [Description("Absolute path to dumper7.dll. Defaults to our vendored build at plugin/build/Release/dumper7.dll, then $DUMPER7_DLL.")] string? dumperDll = null,
        [Description("Remote-thread wait timeout in ms (default 30000).")] int waitMs = 30000)
    {
        if (!OperatingSystem.IsWindows())
            return Err("Windows-only.");

        var proc = ResolveProcess(pid, gameExe, gameName);
        if (proc is null)
            return Err("Target process not found. Provide pid, gameExe, or gameName (and ensure the game is running).");

        var dll = ResolveDumperDll(dumperDll);
        if (dll is null)
            return Err("dumper7.dll not found. Build with `cmake --build plugin/build --config Release --target dumper7` or set $DUMPER7_DLL.");

        var dllBasename = Path.GetFileNameWithoutExtension(dll);
        if (Injector.HasModule(proc.Id, dllBasename))
        {
            return Ok(new {
                pid = proc.Id,
                dll,
                skipped = true,
                reason = $"'{dllBasename}' already loaded in target. Dumper-7 is one-shot per load — restart the game to re-dump.",
            });
        }

        var r = Injector.Inject(proc.Id, dll, waitMs);
        return Ok(new {
            pid = proc.Id,
            processName = proc.ProcessName,
            dll,
            ok = r.Ok,
            message = r.Message,
            loadLibraryReturn = r.LoadLibraryReturn,
            note = "Dumper-7 is now running on a worker thread. Poll SDK output with uevr_dumper7_status or uevr_dumper7_list.",
        });
    }

    // ─── Tool: run (inject + wait) ─────────────────────────────────────

    [McpServerTool(Name = "uevr_dumper7_run")]
    [Description("One-shot: inject dumper7.dll, wait for the SDK to finish being written, and return its structure. Detects completion via either (a) our `dumper7_done.txt` marker file or (b) N seconds of file-write stability. Searches candidate output roots in order: explicit outputRoot, $DUMPER7_OUTPUT_ROOT, %APPDATA%\\UnrealVRMod\\<gameName>\\C++HeaderDump (fork-style), and C:\\Dumper-7 (stock default).")]
    public static async Task<string> Dumper7Run(
        [Description("Target process id. Either pid or gameExe/gameName is required.")] int? pid = null,
        [Description("Absolute path to the game's exe.")] string? gameExe = null,
        [Description("Process name (exe without .exe). Used for fork-style output path resolution.")] string? gameName = null,
        [Description("Override dumper7.dll path.")] string? dumperDll = null,
        [Description("Override expected output root. If set, only this root is watched; otherwise multiple candidates are polled.")] string? outputRoot = null,
        [Description("How long to wait for completion (ms). Default 180000.")] int timeoutMs = 180000,
        [Description("Seconds of no file writes to consider stable (default 4). Only used when no done-marker appears.")] int stableSeconds = 4)
    {
        if (!OperatingSystem.IsWindows())
            return Err("Windows-only.");

        var proc = ResolveProcess(pid, gameExe, gameName);
        if (proc is null)
            return Err("Target process not found.");

        var resolvedGameName = gameName ?? GameNameFromProc(proc);
        var dll = ResolveDumperDll(dumperDll);
        if (dll is null)
            return Err("dumper7.dll not found. Build with `cmake --build plugin/build --config Release --target dumper7`.");

        var dllBasename = Path.GetFileNameWithoutExtension(dll);
        if (Injector.HasModule(proc.Id, dllBasename))
            return Err($"{dllBasename} is already loaded in the target. Dumper-7 is one-shot per load — restart the game to re-dump.");

        var roots = PotentialOutputRoots(outputRoot, resolvedGameName);
        var snap = SnapshotRoots(roots);

        var r = Injector.Inject(proc.Id, dll);
        if (!r.Ok)
            return Err($"Injection failed: {r.Message}");

        // Detect if the target crashed mid-dump.
        var watchTask = WaitForCompletion(snap, timeoutMs, stableSeconds);
        while (!watchTask.IsCompleted)
        {
            try
            {
                proc.Refresh();
                if (proc.HasExited)
                    return Err($"Target process {proc.Id} exited (code={proc.ExitCode}) before Dumper-7 finished. Check the game's console output.");
            }
            catch { /* process handle may go bad on exit */ }
            await Task.WhenAny(watchTask, Task.Delay(1000));
        }

        var sdkDir = await watchTask;
        if (sdkDir is null)
        {
            return Ok(new {
                pid = proc.Id,
                dll,
                candidateRoots = roots,
                injected = true,
                sdkDir = (string?)null,
                note = $"Injection succeeded but no output detected in any of the candidate roots within {timeoutMs}ms. " +
                       "Check that Generator::SDKFolder inside dumper7.dll points somewhere you're watching " +
                       "(we read $DUMPER7_OUTPUT_ROOT at DllMain time), and that the target console didn't report an error.",
            });
        }

        var layout = AnalyzeSdk(sdkDir);
        return Ok(new {
            pid = proc.Id,
            processName = proc.ProcessName,
            gameName = resolvedGameName,
            dll,
            candidateRoots = roots,
            sdk = layout,
            hint = layout.HasDoneMarker
                ? "Dumper-7 reported completion. SDK headers are ready."
                : "Detected via stability (no done-marker). Dumper-7 may not have finished cleanly — inspect the target console output if fields seem missing.",
        });
    }

    // ─── Tool: list SDK contents ───────────────────────────────────────

    [McpServerTool(Name = "uevr_dumper7_list")]
    [Description("List files in a Dumper-7 SDK folder (or the most recent one under the output root). Returns paths + sizes, sorted by relative path. Useful for discovering the SDK layout before reading specific headers.")]
    public static string Dumper7List(
        [Description("Specific SDK folder. If omitted, uses the most-recently-modified subfolder of outputRoot.")] string? sdkDir = null,
        [Description("Output root to scan when sdkDir is omitted. Default from $DUMPER7_OUTPUT_ROOT or C:\\Dumper-7.")] string? outputRoot = null,
        [Description("Glob to filter by (e.g. '*.hpp'). Default '*'.")] string pattern = "*",
        [Description("Max entries to return (default 500).")] int maxEntries = 500)
    {
        var dir = sdkDir;
        if (string.IsNullOrEmpty(dir))
        {
            var root = outputRoot ?? DefaultOutputRoot();
            if (!Directory.Exists(root))
                return Err($"Output root does not exist: {root}. Run uevr_dumper7_run first.");

            dir = Directory.EnumerateDirectories(root)
                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                .FirstOrDefault();

            // If root itself contains files (fork's flat layout), use it directly.
            if (dir is null && Directory.EnumerateFiles(root).Any())
                dir = root;

            if (dir is null)
                return Err($"No SDK folders found under {root}. Run uevr_dumper7_run first.");
        }
        if (!Directory.Exists(dir))
            return Err($"SDK folder does not exist: {dir}");

        var entries = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(maxEntries)
            .Select(f => new {
                path = f,
                relative = Path.GetRelativePath(dir, f),
                bytes = new FileInfo(f).Length
            })
            .ToList();

        return Ok(new {
            sdkDir = Path.GetFullPath(dir),
            pattern,
            count = entries.Count,
            totalBytes = entries.Sum(e => e.bytes),
            truncated = entries.Count == maxEntries,
            entries,
        });
    }

    // ─── Tool: read a generated header ─────────────────────────────────

    [McpServerTool(Name = "uevr_dumper7_read")]
    [Description("Read a generated header file from an SDK folder. Accepts either a relative path ('CppSDK/SDK/Engine_classes.hpp') or a package name ('Engine' → matches Engine.hpp, Engine_classes.hpp, etc.). Truncates to maxBytes. Use uevr_dumper7_list to discover available files.")]
    public static string Dumper7Read(
        [Description("Path or package-name query (e.g. 'Engine', 'Engine_classes.hpp', 'CppSDK/SDK/Engine.hpp').")] string query,
        [Description("SDK folder. If omitted, uses the most recent under outputRoot.")] string? sdkDir = null,
        [Description("Output root when sdkDir is omitted. Default C:\\Dumper-7.")] string? outputRoot = null,
        [Description("Max bytes to return (default 200000).")] int maxBytes = 200_000,
        [Description("Byte offset to start reading from (for paging). Default 0.")] long offset = 0)
    {
        var dir = sdkDir;
        if (string.IsNullOrEmpty(dir))
        {
            var root = outputRoot ?? DefaultOutputRoot();
            if (!Directory.Exists(root))
                return Err($"Output root does not exist: {root}");
            dir = Directory.EnumerateDirectories(root)
                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                .FirstOrDefault() ?? root;
        }
        if (!Directory.Exists(dir))
            return Err($"SDK folder does not exist: {dir}");

        string? match = null;
        if (query.Contains('/') || query.Contains('\\'))
        {
            var p = Path.Combine(dir, query);
            if (File.Exists(p)) match = p;
        }
        if (match is null)
        {
            var candidates = new[]
            {
                Path.Combine(dir, query),
                Path.Combine(dir, query + ".hpp"),
                Path.Combine(dir, "CppSDK", "SDK", query),
                Path.Combine(dir, "CppSDK", "SDK", query + ".hpp"),
                Path.Combine(dir, "CppSDK", query + ".hpp"),
            };
            match = candidates.FirstOrDefault(File.Exists);
        }
        if (match is null)
        {
            // Fuzzy: any .hpp whose basename contains the query (case-insensitive).
            match = Directory.EnumerateFiles(dir, "*.hpp", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                    Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        if (match is null)
            return Err($"No file matched '{query}' under {dir}. Use uevr_dumper7_list to see available files.");

        var fi = new FileInfo(match);
        if (offset < 0) offset = 0;
        if (offset >= fi.Length)
        {
            return Ok(new {
                path = match,
                relative = Path.GetRelativePath(dir, match),
                totalBytes = fi.Length,
                offset,
                returnedBytes = 0,
                truncated = false,
                content = "",
                note = "offset is at or beyond end of file",
            });
        }

        using var fs = File.OpenRead(match);
        fs.Seek(offset, SeekOrigin.Begin);
        var toRead = (int)Math.Min(maxBytes, fi.Length - offset);
        var buf = new byte[toRead];
        int read = fs.Read(buf, 0, toRead);
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);

        return Ok(new {
            path = match,
            relative = Path.GetRelativePath(dir, match),
            totalBytes = fi.Length,
            offset,
            returnedBytes = read,
            truncated = offset + read < fi.Length,
            nextOffset = offset + read < fi.Length ? offset + read : (long?)null,
            content = text,
        });
    }

    // ─── Tool: status ───────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_dumper7_status")]
    [Description("Report Dumper-7 state: resolved DLL path, configured output roots, latest SDK folder structure (if any), and whether Dumper-7 is currently loaded in a given process. Call before uevr_dumper7_run to verify setup, or after to confirm the dump completed.")]
    public static string Dumper7Status(
        [Description("Optional pid to check for a loaded dumper7 module.")] int? pid = null,
        [Description("Optional game name for per-game fork-style path resolution.")] string? gameName = null,
        [Description("Override DLL path for the resolution check.")] string? dumperDll = null,
        [Description("Override output root for the most-recent-SDK lookup.")] string? outputRoot = null)
    {
        var dll = ResolveDumperDll(dumperDll);
        var roots = PotentialOutputRoots(outputRoot, gameName);

        var rootInfo = roots.Select(r => new {
            path = r,
            exists = Directory.Exists(r),
            recent = Directory.Exists(r)
                ? Directory.EnumerateDirectories(r)
                    .Select(d => new { path = d, modified = Directory.GetLastWriteTime(d) })
                    .OrderByDescending(x => x.modified)
                    .Take(5)
                    .ToList()
                : new()
        }).ToList();

        // Find the newest SDK across all roots — most useful for follow-up reads.
        var latest = roots
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateDirectories(r).Select(d => new { root = r, dir = d, mt = Directory.GetLastWriteTime(d) }))
            .OrderByDescending(x => x.mt)
            .FirstOrDefault();

        SdkLayout? latestLayout = null;
        if (latest is not null)
        {
            try { latestLayout = AnalyzeSdk(latest.dir); }
            catch { }
        }

        bool? loaded = null;
        string[]? modules = null;
        if (pid is int p && p > 0 && dll is not null)
        {
            var basename = Path.GetFileNameWithoutExtension(dll);
            loaded = Injector.HasModule(p, basename);
            modules = Injector.ListModulesMatching(p, "dumper");
        }

        return Ok(new {
            dumperDll = dll,
            dumperDllResolved = dll is not null,
            dumperDllEnvOverride = Environment.GetEnvironmentVariable("DUMPER7_DLL"),
            outputRootEnvOverride = Environment.GetEnvironmentVariable("DUMPER7_OUTPUT_ROOT"),
            candidateRoots = rootInfo,
            latestSdk = latestLayout,
            target = pid is null ? null : new { pid, dumper7Loaded = loaded, modules },
        });
    }

    static string Ok(object payload) => JsonSerializer.Serialize(new { ok = true, data = payload }, Json);
    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, Json);
}
