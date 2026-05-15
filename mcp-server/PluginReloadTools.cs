using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Plugin rebuild + install flow. UEVR plugin DLLs are loaded once per game
/// session; the DLL file is locked while loaded. The closest we get to hot-reload
/// without UEVR itself supporting plugin unload is:
///   1. Build the updated plugin
///   2. Stage the new DLL next to the active one (*.pending)
///   3. On next game launch, the installer swaps pending → active
/// For live swap inside a running game, UEVR has to initiate the unload — this
/// tool exposes the rebuild + stage pipeline so agents can iterate quickly and
/// only need to restart the game to pick up new builds.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class PluginReloadTools
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static string RepoRoot()
    {
        // Single-file release exe has empty Assembly.Location — fall back to ProcessPath.
        // Release-zip users can't rebuild the plugin from source (no plugin/ dir), so
        // the caller will get a clean "plugin/ not found" error rather than crashing.
        var asm = typeof(PluginReloadTools).Assembly.Location;
        string? dir;
        if (!string.IsNullOrEmpty(asm))
        {
            dir = Path.GetDirectoryName(asm);
        }
        else
        {
            try { dir = Path.GetDirectoryName(Environment.ProcessPath); }
            catch { dir = AppContext.BaseDirectory; }
        }
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "plugin")) &&
                Directory.Exists(Path.Combine(dir, "mcp-server")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }

    static string? ResolveCmake()
    {
        // Prefer system PATH cmake; fall back to common Visual Studio installs.
        foreach (var c in new[] { "cmake" })
        {
            var p = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = c,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            try
            {
                using var proc = Process.Start(p)!;
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                var line = stdout.Split('\n').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                if (!string.IsNullOrEmpty(line)) return line.Trim();
            }
            catch { }
        }
        foreach (var probe in new[] {
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
            @"C:\Program Files\CMake\bin\cmake.exe",
        })
            if (File.Exists(probe)) return probe;
        return null;
    }

    [McpServerTool(Name = "uevr_plugin_rebuild")]
    [Description("Build the plugin DLL from plugin/CMakeLists.txt in Release mode. Runs `cmake --build plugin/build --config Release --target uevr_mcp`. If the build directory does not exist, configures it first with `cmake -S plugin -B plugin/build`. Returns build output + path to new DLL. The DLL is not installed anywhere — call uevr_plugin_stage or uevr_install_plugin to deploy.")]
    public static async Task<string> PluginRebuild(
        [Description("Build configuration. Release is strongly preferred (game DLLs must match UEVR backend's MT runtime).")] string config = "Release",
        [Description("Build directory under plugin/. Defaults to 'build'.")] string buildDir = "build",
        [Description("Timeout for the build in seconds. Defaults to 600.")] int timeoutSec = 600)
    {
        try
        {
            var root = RepoRoot();
            var pluginDir = Path.Combine(root, "plugin");
            if (!Directory.Exists(pluginDir)) return Err($"plugin/ not found under {root}");
            var buildPath = Path.Combine(pluginDir, buildDir);
            var cmake = ResolveCmake();
            if (cmake is null) return Err("cmake not found on PATH or in common VS install dirs");

            var outLog = new StringBuilder();

            async Task<int> Run(string args)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmake,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = pluginDir,
                };
                psi.Arguments = args;
                var p = new Process { StartInfo = psi };
                p.OutputDataReceived += (_, e) => { if (e.Data != null) outLog.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) outLog.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return -1; }
                return p.ExitCode;
            }

            if (!File.Exists(Path.Combine(buildPath, "CMakeCache.txt")))
            {
                outLog.AppendLine("[configure]");
                int cfgRc = await Run($"-S \"{pluginDir}\" -B \"{buildPath}\"");
                if (cfgRc != 0)
                    return JsonSerializer.Serialize(new {
                        ok = false,
                        error = "cmake configure failed",
                        data = new { exitCode = cfgRc, log = outLog.ToString() }
                    }, Json);
            }

            outLog.AppendLine("[build]");
            int rc = await Run($"--build \"{buildPath}\" --config {config} --target uevr_mcp");
            if (rc != 0)
            {
                var tail = outLog.Length > 8000 ? outLog.ToString()[^8000..] : outLog.ToString();
                return JsonSerializer.Serialize(new {
                    ok = false,
                    error = "cmake build failed",
                    data = new { exitCode = rc, logTail = tail }
                }, Json);
            }

            var dll = Path.Combine(buildPath, config, "uevr_mcp.dll");
            if (!File.Exists(dll))
                return Err($"Build succeeded but DLL not found at expected path: {dll}");
            var info = new FileInfo(dll);
            return JsonSerializer.Serialize(new {
                ok = true,
                data = new {
                    dll,
                    size = info.Length,
                    mtime = info.LastWriteTimeUtc,
                    logTail = outLog.Length > 2000 ? outLog.ToString()[^2000..] : outLog.ToString(),
                }
            }, Json);
        }
        catch (Exception ex) { return Err(ex.ToString()); }
    }

    [McpServerTool(Name = "uevr_plugin_stage")]
    [Description("Stage a freshly-built plugin DLL next to the installed one as uevr_mcp.dll.pending. On next game launch, uevr_install_plugin will swap it in. Use when the game is running and the installed DLL is file-locked — this is the hot-reload escape hatch.")]
    public static string PluginStage(
        [Description("Game exe path or game name. Used to find %APPDATA%\\UnrealVRMod\\<GameName>\\plugins.")] string gameExe,
        [Description("Source DLL. Defaults to plugin/build/Release/uevr_mcp.dll.")] string? sourceDll = null)
    {
        try
        {
            var root = RepoRoot();
            var src = sourceDll ?? Path.Combine(root, "plugin", "build", "Release", "uevr_mcp.dll");
            if (!File.Exists(src)) return Err($"Source DLL not found: {src}");
            var gameName = Path.GetFileNameWithoutExtension(gameExe);
            var dstDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnrealVRMod", gameName, "plugins");
            Directory.CreateDirectory(dstDir);
            var pending = Path.Combine(dstDir, "uevr_mcp.dll.pending");
            File.Copy(src, pending, overwrite: true);
            return JsonSerializer.Serialize(new {
                ok = true,
                data = new {
                    staged = pending,
                    active = Path.Combine(dstDir, "uevr_mcp.dll"),
                    note = "On next launch, rename .pending to .dll (or call uevr_install_plugin again) to activate."
                }
            }, Json);
        }
        catch (Exception ex) { return Err(ex.ToString()); }
    }

    [McpServerTool(Name = "uevr_plugin_info")]
    [Description("Report which plugin DLL is currently installed per-game and its mtime vs the fresh build in plugin/build/Release. Tells you if a rebuild+install is needed. Calls http://127.0.0.1:8899/api/dump/probe_status to check if the running plugin is responding.")]
    public static async Task<string> PluginInfo(
        [Description("Game exe path or game name.")] string gameExe)
    {
        try
        {
            var root = RepoRoot();
            var repoBuilt = Path.Combine(root, "plugin", "build", "Release", "uevr_mcp.dll");
            // Release-zip layout: uevr_mcp.dll sits next to UevrMcpServer.exe.
            string? exeDir = null;
            try { exeDir = Path.GetDirectoryName(Environment.ProcessPath); } catch { }
            var releaseBuilt = exeDir is null ? null : Path.Combine(exeDir, "uevr_mcp.dll");
            // Prefer the repo build (it's the one a dev would rebuild), fall back to the bundled DLL.
            var built = File.Exists(repoBuilt) ? repoBuilt
                      : (releaseBuilt != null && File.Exists(releaseBuilt) ? releaseBuilt : repoBuilt);
            var gameName = Path.GetFileNameWithoutExtension(gameExe);
            var installedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnrealVRMod", gameName, "plugins");
            var installed = Path.Combine(installedDir, "uevr_mcp.dll");
            var pending = Path.Combine(installedDir, "uevr_mcp.dll.pending");

            bool reachable = false;
            string? versionHint = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = await http.GetAsync("http://127.0.0.1:8899/api/dump/probe_status");
                if (resp.IsSuccessStatusCode)
                {
                    reachable = true;
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("engineVersionHint", out var ve))
                        versionHint = ve.GetString();
                }
            }
            catch { }

            return JsonSerializer.Serialize(new {
                ok = true,
                data = new {
                    built = File.Exists(built) ? new { path = built, mtime = File.GetLastWriteTimeUtc(built), size = new FileInfo(built).Length } : null,
                    installed = File.Exists(installed) ? new { path = installed, mtime = File.GetLastWriteTimeUtc(installed), size = new FileInfo(installed).Length } : null,
                    pending = File.Exists(pending) ? new { path = pending, mtime = File.GetLastWriteTimeUtc(pending), size = new FileInfo(pending).Length } : null,
                    pluginReachable = reachable,
                    engineVersionHint = versionHint,
                    needsInstall = File.Exists(built) && (!File.Exists(installed) || File.GetLastWriteTimeUtc(built) > File.GetLastWriteTimeUtc(installed)),
                }
            }, Json);
        }
        catch (Exception ex) { return Err(ex.ToString()); }
    }

    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, Json);
}
