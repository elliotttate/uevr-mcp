using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Readiness + lifecycle tools. Cheap status checks the agent can poll before
/// committing to expensive operations, plus graceful shutdown and Steam game
/// discovery to round out the "point at a game and go" surface.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class ReadinessTools
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── uevr_is_ready ──────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_is_ready")]
    [Description("One-call health check. Returns a compact JSON verdict: is any UE shipping process running, does it have UEVRBackend + uevr_mcp loaded, and does HTTP /api/game_info respond? Use this to gate any other uevr_* call after setup — cheap, no side effects, works even when HTTP is down.")]
    public static async Task<string> IsReady(
        [Description("Optional: restrict to a specific process name or PID. Defaults to any *-Shipping.exe with uevr_mcp loaded.")] string? process = null)
    {
        var state = new Dictionary<string, object?>();
        var proc = FindInjectedGameProcess(process);
        if (proc is null)
        {
            state["processFound"] = false;
            state["backendLoaded"] = false;
            state["pluginLoaded"] = false;
            state["httpAlive"] = false;
            state["ready"] = false;
            state["hint"] = "No UE shipping process found with uevr_mcp loaded. Run uevr_setup_game to launch and inject.";
            return JsonSerializer.Serialize(state, Json);
        }

        state["processFound"] = true;
        state["pid"] = proc.Id;
        state["processName"] = proc.ProcessName;

        var modules = Injector.ListModulesMatching(proc.Id, "UEVR", "uevr_mcp");
        bool backend = modules.Any(m => m.Contains("UEVRBackend", StringComparison.OrdinalIgnoreCase));
        bool plugin  = modules.Any(m => m.Contains("uevr_mcp",    StringComparison.OrdinalIgnoreCase));
        state["backendLoaded"] = backend;
        state["pluginLoaded"]  = plugin;
        state["modules"] = modules;

        bool httpAlive = false;
        object? gameInfo = null;
        try
        {
            var raw = await Http.Get("/api/game_info");
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("error", out _))
            {
                httpAlive = true;
                gameInfo = JsonArgs.Parse(raw);
            }
        }
        catch { /* HTTP down → httpAlive remains false */ }

        state["httpAlive"] = httpAlive;
        if (gameInfo is not null) state["gameInfo"] = gameInfo;
        state["ready"] = backend && plugin && httpAlive;
        if (!(bool)state["ready"]!)
        {
            state["hint"] = !plugin ? "Plugin DLL not loaded — check %APPDATA%\\UnrealVRMod\\<Game>\\plugins\\ and UEVR log."
                          : !backend ? "UEVRBackend not loaded — injection may have failed."
                          : "HTTP not responding yet — plugin may still be initializing. Retry in a second.";
        }
        return JsonSerializer.Serialize(state, Json);
    }

    // ─── uevr_wait_for_plugin ──────────────────────────────────────────

    [McpServerTool(Name = "uevr_wait_for_plugin")]
    [Description("Block until the plugin's HTTP endpoint responds, or the timeout expires. Use after uevr_setup_game — the plugin needs a few seconds to open port 8899 after UEVRBackend finishes loading callbacks. Polls every ~300ms.")]
    public static async Task<string> WaitForPlugin(
        [Description("Max wait in ms (default 30000).")] int timeoutMs = 30000,
        [Description("Optional process name/PID filter.")] string? process = null)
    {
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 500);
        int attempts = 0;

        while (Environment.TickCount64 < deadline)
        {
            attempts++;
            try
            {
                var raw = await Http.Get("/api/game_info");
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("error", out _))
                {
                    return JsonSerializer.Serialize(new {
                        ok = true, attempts,
                        gameInfo = JsonArgs.Parse(raw)
                    }, Json);
                }
            }
            catch { /* not up yet */ }

            // If the game process itself died, bail immediately — no point polling.
            if (FindInjectedGameProcess(process) is null &&
                attempts > 5) // allow a couple early misses before processes register
            {
                // Could be we're polling before injection is done — but after 5 tries
                // (~1.5s) with no matching process, it's really not there.
                break;
            }
            await Task.Delay(300);
        }
        return JsonSerializer.Serialize(new { ok = false, attempts, error = "timed out waiting for plugin HTTP" }, Json);
    }

    // ─── uevr_uevr_log ─────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_uevr_log")]
    [Description("Tail the UEVR host log. UEVR writes per-game logs at %APPDATA%\\UnrealVRMod\\<GameName>\\log.txt. By default we pick the most recently modified log across all game folders (the currently-injected one). This is UEVR's OWN log — plugin-load failures, backend init errors, render-api issues, access violations land here. Distinct from uevr_get_log which is the uevr_mcp plugin ring buffer.")]
    public static string UevrLog(
        [Description("Number of lines to return from the tail (default 200, max 2000).")] int tail = 200,
        [Description("Optional substring filter (case-insensitive).")] string? filter = null,
        [Description("Optional game folder name, e.g. 'RoboQuest-Win64-Shipping'. Default: most-recent log across all games.")] string? gameName = null)
    {
        tail = Math.Clamp(tail, 1, 2000);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnrealVRMod");

        string? logPath = null;
        if (!string.IsNullOrEmpty(gameName))
        {
            logPath = Path.Combine(root, gameName, "log.txt");
        }
        else
        {
            // Most recently written log wins — that's the active game.
            DateTime best = DateTime.MinValue;
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var candidate = Path.Combine(dir, "log.txt");
                    if (!File.Exists(candidate)) continue;
                    var t = File.GetLastWriteTimeUtc(candidate);
                    if (t > best) { best = t; logPath = candidate; }
                }
            }
        }

        if (logPath is null || !File.Exists(logPath))
            return JsonSerializer.Serialize(new { ok = false, error = "UEVR log not found", searched = root }, Json);

        string[] allLines;
        try
        {
            // UEVR rotates the log on each launch; we just want the current file.
            // Use shared-read so we don't collide with UEVR's writer handle.
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            allLines = sr.ReadToEnd().Split('\n');
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message, path = logPath }, Json);
        }

        IEnumerable<string> selected = allLines;
        if (!string.IsNullOrEmpty(filter))
            selected = selected.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var tailLines = selected.TakeLast(tail).ToList();
        return JsonSerializer.Serialize(new {
            ok = true,
            path = logPath,
            totalLines = allLines.Length,
            returned = tailLines.Count,
            lines = tailLines,
        }, Json);
    }

    // ─── uevr_stop_game ────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_stop_game")]
    [Description("Shut down the injected game process. Tries a graceful WM_CLOSE first; falls back to Process.Kill on timeout. Use this between plugin-dev iterations so you can re-build, re-inject, and re-launch cleanly.")]
    public static async Task<string> StopGame(
        [Description("Optional process name/PID filter. Default: find any *-Shipping.exe with uevr_mcp loaded.")] string? process = null,
        [Description("Ms to wait for graceful close before force-kill (default 5000).")] int gracefulMs = 5000,
        [Description("Skip graceful close and kill immediately.")] bool force = false)
    {
        var proc = FindInjectedGameProcess(process) ?? FindFirstUeShippingProcess(process);
        if (proc is null)
            return JsonSerializer.Serialize(new { ok = false, error = "no matching process found" }, Json);

        int pid = proc.Id;
        string name = proc.ProcessName;

        if (!force)
        {
            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                    proc.CloseMainWindow();
            }
            catch { /* ignore; we'll kill */ }

            var waited = 0;
            while (waited < gracefulMs)
            {
                try { proc.Refresh(); if (proc.HasExited) break; }
                catch { break; }
                await Task.Delay(200);
                waited += 200;
            }
        }

        try
        {
            proc.Refresh();
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, pid, name, error = ex.Message }, Json);
        }

        return JsonSerializer.Serialize(new { ok = true, pid, name, forced = force }, Json);
    }

    // ─── uevr_find_steam_game ──────────────────────────────────────────

    [McpServerTool(Name = "uevr_find_steam_game")]
    [Description("Locate UE-based games in the user's Steam libraries. Scans the Steam install, reads libraryfolders.vdf, walks each library's steamapps/common for *-Win64-Shipping.exe / *-WinGDK-Shipping.exe. Returns a list of (name, exePath, libraryPath) triples. Optional query filters by substring on directory/exe name.")]
    public static string FindSteamGame(
        [Description("Optional case-insensitive substring to filter results by game folder or exe name.")] string? query = null,
        [Description("Max results (default 50).")] int limit = 50)
    {
        var steamPath = ResolveSteamPath();
        if (steamPath is null)
            return JsonSerializer.Serialize(new { ok = false, error = "Steam not found in registry (HKCU\\Software\\Valve\\Steam\\SteamPath)" }, Json);

        var libs = new List<string>();
        libs.Add(steamPath);
        var libVdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libVdf))
        {
            foreach (var line in File.ReadAllLines(libVdf))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, "\"path\"\\s+\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        libs = libs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var hits = new List<object>();
        foreach (var lib in libs)
        {
            var common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (var gameDir in Directory.EnumerateDirectories(common))
            {
                var gameName = Path.GetFileName(gameDir);
                if (!string.IsNullOrEmpty(query) &&
                    !gameName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Search up to 3 levels deep — UE often nests Binaries/Win64 under a project subdir.
                string[] exes;
                try
                {
                    exes = Directory.GetFiles(gameDir, "*-Shipping.exe", new EnumerationOptions {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 5,
                        IgnoreInaccessible = true,
                    });
                }
                catch { continue; }

                foreach (var exe in exes)
                {
                    if (!string.IsNullOrEmpty(query) &&
                        !gameName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(exe).Contains(query, StringComparison.OrdinalIgnoreCase))
                        continue;
                    hits.Add(new {
                        gameName,
                        exe,
                        libraryPath = lib,
                    });
                    if (hits.Count >= limit) goto done;
                }
            }
        }
    done:
        return JsonSerializer.Serialize(new { ok = true, steamPath, libraries = libs, hits }, Json);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    static Process? FindInjectedGameProcess(string? filter)
    {
        IEnumerable<Process> candidates;

        if (!string.IsNullOrEmpty(filter))
        {
            if (int.TryParse(filter, out var pid))
            {
                try { candidates = new[] { Process.GetProcessById(pid) }; }
                catch { return null; }
            }
            else
            {
                var name = filter.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
                candidates = Process.GetProcessesByName(name);
            }
        }
        else
        {
            // Any *-Shipping process. Gameplay exes always end in -Shipping on retail builds.
            candidates = Process.GetProcesses().Where(p => {
                try { return p.ProcessName.Contains("Shipping", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }

        foreach (var p in candidates)
        {
            try
            {
                if (Injector.HasModule(p.Id, "uevr_mcp")) return p;
            }
            catch { }
        }
        return null;
    }

    static Process? FindFirstUeShippingProcess(string? filter)
    {
        if (!string.IsNullOrEmpty(filter))
        {
            if (int.TryParse(filter, out var pid))
                try { return Process.GetProcessById(pid); } catch { return null; }
            var name = filter.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            return Process.GetProcessesByName(name).FirstOrDefault();
        }
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Contains("Shipping", StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch { }
        }
        return null;
    }

    static string? ResolveSteamPath()
    {
        // HKCU\Software\Valve\Steam\SteamPath — the canonical location. Fall back to
        // HKLM registry keys, then a couple common install paths.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p.Replace('/', '\\');
        }
        catch { }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p;
        }
        catch { }
        foreach (var p in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            if (Directory.Exists(p)) return p;
        return null;
    }
}
