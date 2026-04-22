using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// One-shot "point it at a game and go" setup tools. Resolves the local plugin
/// DLL and UEVR backend DLL, copies the plugin into the per-game UEVR folder,
/// launches the game if needed, and injects UEVRBackend.dll via CreateRemoteThread.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class SetupTools
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── Path resolution ────────────────────────────────────────────────

    static string RepoRoot()
    {
        // Walk up from the assembly looking for the known repo layout.
        var dir = Path.GetDirectoryName(typeof(SetupTools).Assembly.Location);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "plugin")) &&
                Directory.Exists(Path.Combine(dir, "mcp-server")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback: two dirs up (bin/Release/net9.0 -> mcp-server) then parent.
        var asm = Path.GetDirectoryName(typeof(SetupTools).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(asm, "..", "..", "..", ".."));
    }

    static string? FirstExisting(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c))
                return Path.GetFullPath(c);
        return null;
    }

    static string? ResolvePluginDll(string? overridePath)
    {
        var env = Environment.GetEnvironmentVariable("UEVR_MCP_PLUGIN_DLL");
        var root = RepoRoot();
        return FirstExisting(
            overridePath,
            env,
            Path.Combine(root, "plugin", "build", "Release", "uevr_mcp.dll"),
            Path.Combine(root, "plugin", "build_maponly", "Release", "uevr_mcp.dll"));
    }

    static string? ResolveBackendDll(string? overridePath)
    {
        var env = Environment.GetEnvironmentVariable("UEVR_BACKEND_DLL");
        return FirstExisting(
            overridePath,
            env,
            // The common local UEVR build location on this box (see memory).
            @"E:\Github\UEVR\build\bin\uevr\UEVRBackend.dll");
    }

    static string GameNameFromExe(string exePath)
    {
        // UEVR keys per-game plugin folders by the same name it shows in the injector,
        // which is the exe filename without extension — e.g. `RoboQuest-Win64-Shipping`.
        return Path.GetFileNameWithoutExtension(exePath);
    }

    static string PluginDestFor(string gameName)
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appdata, "UnrealVRMod", gameName, "plugins", "uevr_mcp.dll");
    }

    // ─── Process helpers ────────────────────────────────────────────────

    static Process? FindRunningByExe(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            catch { /* access denied on some processes — fall through */ }
            return p; // first by-name match is good enough
        }
        return null;
    }

    static Process LaunchGame(string exePath, string? args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            UseShellExecute = true, // detach so MCP server exit doesn't kill the game
        };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exePath}");
        return p;
    }

    static async Task<Process?> WaitForMainWindow(Process p, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                p.Refresh();
                if (p.HasExited) return null;
                if (p.MainWindowHandle != IntPtr.Zero) return p;
            }
            catch { }
            await Task.Delay(250);
        }
        return p; // return it anyway; caller can inject without a window
    }

    // ─── Plugin install ─────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_install_plugin")]
    [Description("Copy uevr_mcp.dll into %APPDATA%\\UnrealVRMod\\<GameName>\\plugins\\ so UEVR picks it up on next load. Resolves the plugin DLL from the repo's plugin/build/Release by default. GameName is derived from the exe filename if an exe path is given, otherwise pass gameName directly.")]
    public static string InstallPlugin(
        [Description("Path to the game's shipping exe (e.g. C:\\Games\\Foo\\Foo-Win64-Shipping.exe). Either this or gameName is required.")] string? gameExe = null,
        [Description("UEVR game folder name (exe basename, e.g. 'RoboQuest-Win64-Shipping'). Either this or gameExe is required.")] string? gameName = null,
        [Description("Override the plugin DLL source path. Defaults to plugin/build/Release/uevr_mcp.dll in the repo.")] string? pluginDll = null,
        [Description("Overwrite an existing destination plugin (default true).")] bool overwrite = true)
    {
        if (string.IsNullOrEmpty(gameExe) && string.IsNullOrEmpty(gameName))
            return Err("Provide gameExe or gameName.");

        var src = ResolvePluginDll(pluginDll);
        if (src is null)
            return Err("Could not locate uevr_mcp.dll. Build plugin/ (cmake --build plugin/build --config Release) or pass pluginDll.");

        var resolvedName = gameName ?? GameNameFromExe(gameExe!);
        var dst = PluginDestFor(resolvedName);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        bool existed = File.Exists(dst);
        if (existed && !overwrite)
            return Ok(new { gameName = resolvedName, pluginSrc = src, pluginDst = dst, copied = false, reason = "exists; overwrite=false" });

        File.Copy(src, dst, overwrite: true);
        var info = new FileInfo(dst);
        return Ok(new { gameName = resolvedName, pluginSrc = src, pluginDst = dst, copied = true, replaced = existed, bytes = info.Length, modified = info.LastWriteTimeUtc });
    }

    // ─── One-shot setup ─────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_setup_game")]
    [Description("Full setup: (1) copy uevr_mcp.dll into the UEVR per-game plugins folder, (2) launch the game if not already running, (3) inject UEVRBackend.dll via CreateRemoteThread+LoadLibraryA, (4) verify both modules loaded. Point it at a game exe and it handles everything. The plugin opens HTTP on 127.0.0.1:8899 once injected.")]
    public static async Task<string> SetupGame(
        [Description("Absolute path to the game's shipping exe, e.g. C:\\Games\\Foo\\Foo-Win64-Shipping.exe")] string gameExe,
        [Description("If true and the process is not running, launch it. Default true.")] bool launchIfMissing = true,
        [Description("Extra command-line args to pass when launching (e.g. '-windowed').")] string? launchArgs = "-windowed",
        [Description("Override UEVRBackend.dll path. Defaults to $UEVR_BACKEND_DLL or E:\\Github\\UEVR\\build\\bin\\uevr\\UEVRBackend.dll.")] string? backendDll = null,
        [Description("Override uevr_mcp.dll source path. Defaults to plugin/build/Release/uevr_mcp.dll in the repo.")] string? pluginDll = null,
        [Description("How long to wait (ms) for a main window before injecting. Default 20000.")] int windowWaitMs = 20000,
        [Description("Skip the plugin copy step (useful if already installed). Default false.")] bool skipPluginInstall = false,
        [Description("Skip backend injection (only install the plugin). Default false.")] bool skipInject = false)
    {
        if (!OperatingSystem.IsWindows())
            return Err("uevr_setup_game is Windows-only.");

        if (string.IsNullOrWhiteSpace(gameExe))
            return Err("gameExe is required.");
        if (!File.Exists(gameExe))
            return Err($"Game exe not found: {gameExe}");
        gameExe = Path.GetFullPath(gameExe);

        var steps = new List<object>();
        var gameName = GameNameFromExe(gameExe);

        // Step 1: install plugin
        if (!skipPluginInstall)
        {
            var src = ResolvePluginDll(pluginDll);
            if (src is null)
                return Err("Could not locate uevr_mcp.dll. Build plugin/ or pass pluginDll.");
            var dst = PluginDestFor(gameName);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            bool existed = File.Exists(dst);
            File.Copy(src, dst, overwrite: true);
            steps.Add(new { step = "install_plugin", src, dst, replaced = existed });
        }
        else
        {
            steps.Add(new { step = "install_plugin", skipped = true });
        }

        // Step 2: find or launch
        Process? proc = FindRunningByExe(gameExe);
        if (proc is null)
        {
            if (!launchIfMissing)
                return Err($"Process not running and launchIfMissing=false. Start {gameName} first.");

            // Kill any UEVRInjector.exe that would race us on injection (see memory).
            foreach (var inj in Process.GetProcessesByName("UEVRInjector"))
            {
                try { inj.Kill(entireProcessTree: true); } catch { }
            }

            proc = LaunchGame(gameExe, launchArgs);
            steps.Add(new { step = "launch", pid = proc.Id, args = launchArgs });
        }
        else
        {
            steps.Add(new { step = "launch", pid = proc.Id, reused = true });
        }

        // Step 3: inject backend
        if (skipInject)
        {
            steps.Add(new { step = "inject", skipped = true });
            return Ok(new { gameName, pid = proc.Id, steps });
        }

        var backend = ResolveBackendDll(backendDll);
        if (backend is null)
            return Err("Could not locate UEVRBackend.dll. Set UEVR_BACKEND_DLL or pass backendDll. The local build at E:\\Github\\UEVR\\build\\bin\\uevr\\UEVRBackend.dll is preferred — the public UEVR release is too old for uevr_mcp.");

        // Give the target a moment to spin up its main window — injecting too early
        // can race the UE module init.
        var ready = await WaitForMainWindow(proc, windowWaitMs);
        if (ready is null)
            return Err("Game process exited before we could inject.");

        // Already has UEVR? don't double-inject.
        if (Injector.HasModule(proc.Id, "UEVRBackend"))
        {
            steps.Add(new { step = "inject", skipped = true, reason = "UEVRBackend already loaded" });
        }
        else
        {
            var r = Injector.Inject(proc.Id, backend);
            steps.Add(new { step = "inject", backend, result = r.Message, ok = r.Ok, loadLibraryReturn = r.LoadLibraryReturn });
            if (!r.Ok)
                return Ok(new { gameName, pid = proc.Id, success = false, steps });
        }

        // Step 4: verify (poll briefly — the plugin loader runs on a UEVR callback,
        // so uevr_mcp.dll shows up a beat after UEVRBackend does).
        string[] modules = Array.Empty<string>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000)
        {
            modules = Injector.ListModulesMatching(proc.Id, "UEVR", "uevr_mcp");
            if (modules.Any(m => m.Contains("uevr_mcp", StringComparison.OrdinalIgnoreCase))) break;
            await Task.Delay(500);
        }
        bool backendLoaded = modules.Any(m => m.Contains("UEVRBackend", StringComparison.OrdinalIgnoreCase));
        bool pluginLoaded  = modules.Any(m => m.Contains("uevr_mcp",    StringComparison.OrdinalIgnoreCase));
        steps.Add(new { step = "verify", modules, backendLoaded, pluginLoaded });

        return Ok(new
        {
            gameName,
            pid = proc.Id,
            success = backendLoaded && pluginLoaded,
            hint = (backendLoaded && pluginLoaded)
                ? "Plugin is live. Try uevr_get_status or http://127.0.0.1:8899/api/game_info."
                : "UEVRBackend loaded but uevr_mcp didn't appear — plugin may be missing or incompatible. Check %APPDATA%\\UnrealVRMod\\<GameName>\\plugins\\ and UEVR logs.",
            steps
        });
    }

    // ─── Utility ────────────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_setup_paths")]
    [Description("Report the paths the setup tools would use: plugin DLL, backend DLL, and repo root. Useful for debugging before running uevr_setup_game.")]
    public static string SetupPaths()
    {
        var plugin = ResolvePluginDll(null);
        var backend = ResolveBackendDll(null);
        return Ok(new
        {
            repoRoot = RepoRoot(),
            pluginDll = plugin,
            pluginDllResolved = plugin is not null,
            backendDll = backend,
            backendDllResolved = backend is not null,
            pluginDllEnvOverride = Environment.GetEnvironmentVariable("UEVR_MCP_PLUGIN_DLL"),
            backendDllEnvOverride = Environment.GetEnvironmentVariable("UEVR_BACKEND_DLL"),
            uevrModDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnrealVRMod")
        });
    }

    static string Ok(object payload) => JsonSerializer.Serialize(new { ok = true, data = payload }, Json);
    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, Json);
}
