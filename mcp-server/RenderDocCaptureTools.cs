using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Host-side controls for UEVRJ's embedded RenderDoc path. These tools do not
/// require the in-game HTTP plugin to be live: they launch through
/// UEVRRenderDocLauncher.exe, write the capture sentinel watched by UEVR, and
/// validate the resulting .rdc with renderdoccmd.exe.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class RenderDocCaptureTools
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static string Ok(object payload) => JsonSerializer.Serialize(new { ok = true, data = payload }, Json);
    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, Json);

    static string? FirstExisting(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(c));
        }
        return null;
    }

    static string? FirstExistingDir(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(c));
        }
        return null;
    }

    static string? ResolveUevrRoot(string? overrideRoot)
        => FirstExistingDir(
            overrideRoot,
            Environment.GetEnvironmentVariable("UEVRJ_ROOT"),
            Environment.GetEnvironmentVariable("UEVR_RENDERDOC_UEVRJ_ROOT"),
            @"E:\Github\UEVRJ",
            @"E:\github\uevrj",
            @"E:\github\uevrj-renderdoc");

    static string? ResolveRenderDocRoot(string? overrideRoot)
        => FirstExistingDir(
            overrideRoot,
            Environment.GetEnvironmentVariable("RENDERDOC_ROOT"),
            Environment.GetEnvironmentVariable("UEVR_RENDERDOC_ROOT"),
            @"E:\Github\renderdoc");

    static string? ResolveLauncher(string? overridePath, string? uevrRoot)
        => FirstExisting(
            overridePath,
            Environment.GetEnvironmentVariable("UEVR_RENDERDOC_LAUNCHER"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build", "bin", "uevr", "UEVRRenderDocLauncher.exe"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build-renderdoc", "bin", "uevr", "UEVRRenderDocLauncher.exe"));

    static string? ResolveSmoke(string? overridePath, string? uevrRoot)
        => FirstExisting(
            overridePath,
            Environment.GetEnvironmentVariable("UEVR_RENDERDOC_SMOKE"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build", "bin", "uevr", "UEVRRenderDocSmoke.exe"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build-renderdoc", "bin", "uevr", "UEVRRenderDocSmoke.exe"));

    static string? ResolveBackend(string? overridePath, string? uevrRoot)
        => FirstExisting(
            overridePath,
            Environment.GetEnvironmentVariable("UEVR_RENDERDOC_BACKEND_DLL"),
            Environment.GetEnvironmentVariable("UEVR_BACKEND_DLL"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build", "bin", "uevr", "UEVRBackend.dll"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build-renderdoc", "bin", "uevr", "UEVRBackend.dll"));

    static string? ResolveRenderDocDll(string? overridePath, string? uevrRoot, string? renderDocRoot)
        => FirstExisting(
            overridePath,
            Environment.GetEnvironmentVariable("UEVR_RENDERDOC_DLL"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build", "bin", "uevr", "renderdoc.dll"),
            uevrRoot is null ? null : Path.Combine(uevrRoot, "build-renderdoc", "bin", "uevr", "renderdoc.dll"),
            renderDocRoot is null ? null : Path.Combine(renderDocRoot, "x64", "Development", "renderdoc.dll"),
            renderDocRoot is null ? null : Path.Combine(renderDocRoot, "x64", "Release", "renderdoc.dll"));

    static string? ResolveRenderDocCmd(string? overridePath, string? renderDocRoot)
    {
        var direct = FirstExisting(
            overridePath,
            Environment.GetEnvironmentVariable("RENDERDOCCMD_EXE"),
            Environment.GetEnvironmentVariable("UEVR_RENDERDOC_CMD"),
            renderDocRoot is null ? null : Path.Combine(renderDocRoot, "x64", "Development", "renderdoccmd.exe"),
            renderDocRoot is null ? null : Path.Combine(renderDocRoot, "x64", "Release", "renderdoccmd.exe"),
            @"C:\Program Files\RenderDoc\renderdoccmd.exe",
            @"C:\Program Files (x86)\RenderDoc\renderdoccmd.exe");
        if (direct is not null)
            return direct;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "renderdoccmd.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }

    static string DefaultCaptureTemplate(string label)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(Path.GetTempPath(), "uevr_renderdoc_live", $"{Slug(label)}_{stamp}");
    }

    static string Slug(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        var s = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(s) ? "capture" : s;
    }

    static string CaptureSentinelPath()
        => Path.Combine(Path.GetTempPath(), "uevr_renderdoc_capture.req");

    static void WriteCaptureRequest(string captureTemplate, int frames)
    {
        File.WriteAllLines(CaptureSentinelPath(), new[]
        {
            captureTemplate,
            $"frames={Math.Max(1, frames)}"
        });
    }

    static FileInfo? FindNewestCapture(string captureTemplate)
    {
        var dir = Path.GetDirectoryName(captureTemplate);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return null;
        var prefix = Path.GetFileName(captureTemplate);
        return new DirectoryInfo(dir)
            .GetFiles(prefix + "*.rdc", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    static async Task<FileInfo?> WaitForCapture(string captureTemplate, int frames, int timeoutSeconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        var nextRetry = DateTimeOffset.UtcNow.AddSeconds(2);
        FileInfo? newest = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            newest = FindNewestCapture(captureTemplate);
            if (newest is not null)
                break;

            if (DateTimeOffset.UtcNow >= nextRetry)
            {
                WriteCaptureRequest(captureTemplate, frames);
                nextRetry = DateTimeOffset.UtcNow.AddSeconds(2);
            }

            await Task.Delay(500);
        }

        if (newest is null)
            return null;

        long lastLength = -1;
        while (DateTimeOffset.UtcNow < deadline)
        {
            newest.Refresh();
            if (newest.Exists && newest.Length > 0)
            {
                var readable = false;
                try
                {
                    using var _ = File.Open(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    readable = true;
                }
                catch { }

                if (readable && newest.Length == lastLength)
                    return newest;
                lastLength = newest.Length;
            }

            await Task.Delay(500);
        }

        return newest.Exists && newest.Length > 0 ? newest : null;
    }

    static int? ParseCreatedPid(string text)
    {
        var match = Regex.Match(text, @"Created suspended process pid=(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var pid) ? pid : null;
    }

    static JsonNode? ParseJson(string raw)
    {
        try { return JsonNode.Parse(raw); }
        catch { return null; }
    }

    static long CountLinesIfSmall(string path, long maxBytes = 128L * 1024 * 1024)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > maxBytes)
                return -1;
            return File.ReadLines(path).LongCount();
        }
        catch { return -1; }
    }

    [McpServerTool(Name = "uevr_renderdoc_paths")]
    [Description("Resolve the host-side paths used by the embedded RenderDoc flow: UEVRJ root, UEVRRenderDocLauncher.exe, UEVRBackend.dll, renderdoc.dll, renderdoccmd.exe, smoke app, and the capture sentinel file. Call this first when setting up MCP-driven .rdc capture.")]
    public static string Paths(
        [Description("Optional UEVRJ checkout root. Defaults to $UEVRJ_ROOT, $UEVR_RENDERDOC_UEVRJ_ROOT, E:\\Github\\UEVRJ, E:\\github\\uevrj, then E:\\github\\uevrj-renderdoc.")] string? uevrRoot = null,
        [Description("Optional RenderDoc checkout root. Defaults to $RENDERDOC_ROOT, $UEVR_RENDERDOC_ROOT, then E:\\Github\\renderdoc.")] string? renderDocRoot = null)
    {
        var root = ResolveUevrRoot(uevrRoot);
        var rdRoot = ResolveRenderDocRoot(renderDocRoot);
        var launcher = ResolveLauncher(null, root);
        var backend = ResolveBackend(null, root);
        var rdDll = ResolveRenderDocDll(null, root, rdRoot);
        var rdCmd = ResolveRenderDocCmd(null, rdRoot);
        var smoke = ResolveSmoke(null, root);
        return Ok(new
        {
            uevrRoot = root,
            renderDocRoot = rdRoot,
            launcher,
            launcherExists = launcher is not null,
            backend,
            backendExists = backend is not null,
            renderdocDll = rdDll,
            renderdocDllExists = rdDll is not null,
            renderdoccmd = rdCmd,
            renderdoccmdExists = rdCmd is not null,
            smoke,
            smokeExists = smoke is not null,
            captureSentinel = CaptureSentinelPath()
        });
    }

    [McpServerTool(Name = "uevr_renderdoc_launch_game")]
    [Description("Launch a game through UEVRJ's suspended RenderDoc launcher. This injects renderdoc.dll first, UEVRBackend.dll second, waits until UEVR prehooks D3D12, then resumes the game. Use this instead of normal late UEVR injection when you need a 1:1-compatible .rdc.")]
    public static async Task<string> LaunchGame(
        [Description("Absolute path to the game's shipping exe.")] string gameExe,
        [Description("Game command-line args, e.g. '--dx12' or '-d3d12'.")] string? gameArgs = null,
        [Description("Working directory for the game. Defaults to the exe directory.")] string? cwd = null,
        [Description("Optional XR runtime JSON path, assigned to XR_RUNTIME_JSON for the launched process.")] string? xrRuntimeJson = null,
        [Description("Optional UEVRJ checkout root.")] string? uevrRoot = null,
        [Description("Optional launcher exe path.")] string? launcher = null,
        [Description("Optional UEVRBackend.dll path.")] string? backendDll = null,
        [Description("Optional renderdoc.dll path.")] string? renderdocDll = null,
        [Description("Optional RenderDoc checkout root.")] string? renderDocRoot = null,
        [Description("Milliseconds the launcher waits for UEVR's early-ready event. Default 30000.")] int readyTimeoutMs = 30000,
        [Description("If true, pass --wait so the launcher stays alive until the game exits. Default false.")] bool waitForGameExit = false,
        [Description("Timeout for the launcher process itself in milliseconds. Default 60000; ignored only if waitForGameExit=true, where it is clamped to at least 1 hour.")] int timeoutMs = 60000)
    {
        if (!OperatingSystem.IsWindows())
            return Err("RenderDoc launching is Windows-only.");
        if (string.IsNullOrWhiteSpace(gameExe) || !File.Exists(gameExe))
            return Err($"Game exe not found: {gameExe}");

        var root = ResolveUevrRoot(uevrRoot);
        var rdRoot = ResolveRenderDocRoot(renderDocRoot);
        var launcherPath = ResolveLauncher(launcher, root);
        var backend = ResolveBackend(backendDll, root);
        var rdDll = ResolveRenderDocDll(renderdocDll, root, rdRoot);
        if (launcherPath is null) return Err("UEVRRenderDocLauncher.exe not found. Build UEVRJ or pass launcher.");
        if (backend is null) return Err("UEVRBackend.dll not found. Build UEVRJ or pass backendDll.");
        if (rdDll is null) return Err("renderdoc.dll not found. Build UEVRJ/RenderDoc or pass renderdocDll.");

        var args = new List<string>
        {
            "--exe", Path.GetFullPath(gameExe),
            "--cwd", string.IsNullOrWhiteSpace(cwd) ? Path.GetDirectoryName(Path.GetFullPath(gameExe))! : Path.GetFullPath(cwd),
            "--backend", backend,
            "--renderdoc", rdDll,
            "--ready-timeout-ms", Math.Max(1000, readyTimeoutMs).ToString()
        };
        if (waitForGameExit)
            args.Add("--wait");
        if (!string.IsNullOrWhiteSpace(gameArgs))
        {
            args.Add("--");
            args.AddRange(ExternalTools.SplitArgs(gameArgs));
        }

        var env = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(xrRuntimeJson))
            env["XR_RUNTIME_JSON"] = Path.GetFullPath(xrRuntimeJson);

        var runTimeout = waitForGameExit ? Math.Max(timeoutMs, 60 * 60 * 1000) : Math.Max(timeoutMs, 1000);
        var r = await ExternalTools.Run(launcherPath, args, timeoutMs: runTimeout, cwd: Path.GetDirectoryName(launcherPath), env: env);
        var combined = r.Stdout + "\n" + r.Stderr;
        return Ok(new
        {
            launcher = launcherPath,
            backend,
            renderdoc = rdDll,
            gameExe = Path.GetFullPath(gameExe),
            gameArgs,
            cwd = args[3],
            xrRuntimeJson = string.IsNullOrWhiteSpace(xrRuntimeJson) ? null : Path.GetFullPath(xrRuntimeJson),
            exitCode = r.ExitCode,
            launched = r.ExitCode == 0,
            gamePid = ParseCreatedPid(combined),
            stdout = r.Stdout,
            stderr = r.Stderr,
            command = r.Command
        });
    }

    [McpServerTool(Name = "uevr_renderdoc_request_capture")]
    [Description("Request a RenderDoc capture from an already-launched embedded UEVR process by writing the sentinel file that UEVR watches. Waits for the .rdc to appear, then optionally validates it with renderdoccmd index-capture and extracts a thumbnail.")]
    public static async Task<string> RequestCapture(
        [Description("Capture path prefix without .rdc. Defaults under %TEMP%\\uevr_renderdoc_live.")] string? captureTemplate = null,
        [Description("Number of frames to capture. Default 1.")] int frames = 1,
        [Description("Seconds to wait for the .rdc to appear and become readable. Default 90.")] int timeoutSeconds = 90,
        [Description("If true, run renderdoccmd index-capture after the .rdc is written. Default true.")] bool validate = true,
        [Description("If true, extract thumbnail.png with renderdoccmd thumb. Default true.")] bool thumbnail = true,
        [Description("Validation timeout in milliseconds. Default 600000.")] int validateTimeoutMs = 600000,
        [Description("Optional RenderDoc checkout root.")] string? renderDocRoot = null,
        [Description("Optional renderdoccmd.exe path.")] string? renderDocCmd = null)
    {
        var template = string.IsNullOrWhiteSpace(captureTemplate)
            ? DefaultCaptureTemplate("mcp")
            : Path.GetFullPath(captureTemplate);
        var dir = Path.GetDirectoryName(template);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        WriteCaptureRequest(template, frames);
        var capture = await WaitForCapture(template, frames, timeoutSeconds);
        if (capture is null)
            return Err($"No .rdc appeared for template '{template}' within {timeoutSeconds}s. Launch the game with uevr_renderdoc_launch_game so UEVR's RenderDoc watcher is active.");

        JsonNode? validation = null;
        if (validate || thumbnail)
        {
            var raw = await ValidateCapture(
                capture.FullName,
                outDir: null,
                renderDocRoot: renderDocRoot,
                renderDocCmd: renderDocCmd,
                runIndex: validate,
                extractThumbnail: thumbnail,
                maxThumbnailSize: 512,
                timeoutMs: validateTimeoutMs);
            validation = ParseJson(raw) ?? JsonValue.Create(raw);
        }

        return Ok(new
        {
            capture = capture.FullName,
            bytes = capture.Length,
            captureTemplate = template,
            frames = Math.Max(1, frames),
            sentinel = CaptureSentinelPath(),
            validation
        });
    }

    [McpServerTool(Name = "uevr_renderdoc_capture_game")]
    [Description("Full MCP-driven capture flow: launch the game through UEVRRenderDocLauncher.exe, wait for startup, request a RenderDoc .rdc, validate/index it with renderdoccmd, and optionally stop the game afterwards. This is the highest-level one-call path.")]
    public static async Task<string> CaptureGame(
        [Description("Absolute path to the game's shipping exe.")] string gameExe,
        [Description("Game command-line args, e.g. '--dx12' or '-d3d12'.")] string? gameArgs = null,
        [Description("Working directory for the game. Defaults to the exe directory.")] string? cwd = null,
        [Description("Capture path prefix without .rdc. Defaults under %TEMP%\\uevr_renderdoc_live.")] string? captureTemplate = null,
        [Description("Seconds to wait after launch before requesting capture. Default 45.")] int startupDelaySeconds = 45,
        [Description("Seconds to wait for the capture file. Default 120.")] int captureTimeoutSeconds = 120,
        [Description("Optional XR runtime JSON path, assigned to XR_RUNTIME_JSON for launch.")] string? xrRuntimeJson = null,
        [Description("Optional UEVRJ checkout root.")] string? uevrRoot = null,
        [Description("Optional RenderDoc checkout root.")] string? renderDocRoot = null,
        [Description("Optional launcher exe path.")] string? launcher = null,
        [Description("Optional UEVRBackend.dll path.")] string? backendDll = null,
        [Description("Optional renderdoc.dll path.")] string? renderdocDll = null,
        [Description("Optional renderdoccmd.exe path.")] string? renderDocCmd = null,
        [Description("If true, stop the launched game process after validation. Default false.")] bool stopAfterCapture = false,
        [Description("Validation/index timeout in milliseconds. Default 900000.")] int validateTimeoutMs = 900000)
    {
        var launchRaw = await LaunchGame(
            gameExe,
            gameArgs,
            cwd,
            xrRuntimeJson,
            uevrRoot,
            launcher,
            backendDll,
            renderdocDll,
            renderDocRoot,
            readyTimeoutMs: 30000,
            waitForGameExit: false,
            timeoutMs: 60000);
        var launch = ParseJson(launchRaw);
        if (launch?["ok"]?.GetValue<bool>() != true)
            return launchRaw;

        var pid = launch?["data"]?["gamePid"]?.GetValue<int?>();
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, startupDelaySeconds)));

        var template = string.IsNullOrWhiteSpace(captureTemplate)
            ? DefaultCaptureTemplate(Path.GetFileNameWithoutExtension(gameExe))
            : Path.GetFullPath(captureTemplate);
        var captureRaw = await RequestCapture(
            template,
            frames: 1,
            timeoutSeconds: captureTimeoutSeconds,
            validate: true,
            thumbnail: true,
            validateTimeoutMs: validateTimeoutMs,
            renderDocRoot: renderDocRoot,
            renderDocCmd: renderDocCmd);

        bool stopped = false;
        if (stopAfterCapture && pid is int gamePid)
        {
            try
            {
                var proc = Process.GetProcessById(gamePid);
                proc.Kill(entireProcessTree: true);
                stopped = true;
            }
            catch { }
        }

        return Ok(new
        {
            launch,
            capture = ParseJson(captureRaw) ?? JsonValue.Create(captureRaw),
            stoppedGame = stopped
        });
    }

    [McpServerTool(Name = "uevr_renderdoc_validate_capture")]
    [Description("Validate an existing .rdc with renderdoccmd. By default runs index-capture into a JSONL directory and extracts thumbnail.png. Returns counts for actions/events/state/resources when index files are present.")]
    public static async Task<string> ValidateCapture(
        [Description("Absolute path to a .rdc file.")] string capture,
        [Description("Output directory for index-capture JSONL files. Defaults under %TEMP%\\uevr_renderdoc_validate.")] string? outDir = null,
        [Description("Optional RenderDoc checkout root.")] string? renderDocRoot = null,
        [Description("Optional renderdoccmd.exe path.")] string? renderDocCmd = null,
        [Description("If true, run renderdoccmd index-capture. Default true.")] bool runIndex = true,
        [Description("If true, run renderdoccmd thumb and write thumbnail.png. Default true.")] bool extractThumbnail = true,
        [Description("Max thumbnail dimension. Default 512.")] int maxThumbnailSize = 512,
        [Description("Timeout for each renderdoccmd operation in milliseconds. Default 600000.")] int timeoutMs = 600000)
    {
        if (string.IsNullOrWhiteSpace(capture) || !File.Exists(capture))
            return Err($"Capture not found: {capture}");
        if (!capture.EndsWith(".rdc", StringComparison.OrdinalIgnoreCase))
            return Err($"Expected a .rdc capture, got: {capture}");

        var rdRoot = ResolveRenderDocRoot(renderDocRoot);
        var exe = ResolveRenderDocCmd(renderDocCmd, rdRoot);
        if (exe is null)
            return Err("renderdoccmd.exe not found. Pass renderDocCmd or renderDocRoot, or set RENDERDOCCMD_EXE.");

        capture = Path.GetFullPath(capture);
        if (string.IsNullOrWhiteSpace(outDir))
        {
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
            outDir = Path.Combine(Path.GetTempPath(), "uevr_renderdoc_validate", $"{Path.GetFileNameWithoutExtension(capture)}_{stamp}");
        }
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        ExternalTools.RunResult? index = null;
        if (runIndex)
        {
            index = await ExternalTools.Run(
                exe,
                new[] { "index-capture", "--out", outDir, capture },
                timeoutMs: Math.Max(1000, timeoutMs),
                cwd: Path.GetDirectoryName(exe));
            await File.WriteAllTextAsync(Path.Combine(outDir, "renderdoccmd_index_capture.log"), index.Stdout + index.Stderr);
        }

        ExternalTools.RunResult? thumb = null;
        string? thumbPath = null;
        if (extractThumbnail)
        {
            thumbPath = Path.Combine(outDir, "thumbnail.png");
            var args = new List<string> { "thumb", "--out", thumbPath };
            if (maxThumbnailSize > 0)
                args.AddRange(new[] { "--max-size", maxThumbnailSize.ToString() });
            args.Add(capture);
            thumb = await ExternalTools.Run(exe, args, timeoutMs: Math.Max(1000, timeoutMs), cwd: Path.GetDirectoryName(exe));
            await File.WriteAllTextAsync(Path.Combine(outDir, "renderdoccmd_thumb.log"), thumb.Stdout + thumb.Stderr);
        }

        var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .ToArray();
        object FileSummary(string name)
        {
            var p = Path.Combine(outDir, name);
            var fi = new FileInfo(p);
            return new
            {
                exists = fi.Exists,
                bytes = fi.Exists ? fi.Length : 0,
                lines = fi.Exists && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? CountLinesIfSmall(p) : null as long?
            };
        }

        var ok = (!runIndex || index?.ExitCode == 0) &&
                 (!extractThumbnail || (thumb?.ExitCode == 0 && thumbPath is not null && File.Exists(thumbPath)));

        return Ok(new
        {
            capture,
            captureBytes = new FileInfo(capture).Length,
            renderdoccmd = exe,
            outDir,
            ok,
            index = index is null ? null : new { exitCode = index.ExitCode, stdout = index.Stdout, stderr = index.Stderr },
            thumbnail = thumb is null ? null : new { exitCode = thumb.ExitCode, path = thumbPath, exists = thumbPath is not null && File.Exists(thumbPath), stdout = thumb.Stdout, stderr = thumb.Stderr },
            outputs = new
            {
                meta = FileSummary("meta.json"),
                actions = FileSummary("actions.jsonl"),
                events = FileSummary("events.jsonl"),
                state = FileSummary("state.jsonl"),
                resources = FileSummary("resources.json"),
                fileCount = files.Length,
                totalBytes = files.Sum(f => f.Length)
            }
        });
    }

    [McpServerTool(Name = "uevr_renderdoc_list_captures")]
    [Description("List .rdc captures on disk, newest first. Defaults to the common UEVR RenderDoc temp directories. Pass rootDir to inspect a specific capture folder.")]
    public static string ListCaptures(
        [Description("Directory to search. If omitted, searches %TEMP%\\uevr_renderdoc_live, _smoke, and _sn2.")] string? rootDir = null,
        [Description("Maximum capture rows to return. Default 20.")] int max = 20,
        [Description("If true, search recursively. Default false for explicit rootDir, true for default temp roots.")] bool? recursive = null)
    {
        var roots = string.IsNullOrWhiteSpace(rootDir)
            ? new[]
            {
                Path.Combine(Path.GetTempPath(), "uevr_renderdoc_live"),
                Path.Combine(Path.GetTempPath(), "uevr_renderdoc_smoke"),
                Path.Combine(Path.GetTempPath(), "uevr_renderdoc_sn2")
            }
            : new[] { Path.GetFullPath(rootDir) };
        var option = recursive ?? string.IsNullOrWhiteSpace(rootDir)
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var captures = roots
            .Where(Directory.Exists)
            .SelectMany(root =>
            {
                try { return Directory.GetFiles(root, "*.rdc", option); }
                catch { return Array.Empty<string>(); }
            })
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(Math.Max(1, max))
            .Select(f => new { path = f.FullName, bytes = f.Length, lastWriteUtc = f.LastWriteTimeUtc })
            .ToArray();

        return Ok(new { roots, count = captures.Length, captures });
    }
}
