using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Stability tools for dumping on fragile (DX12 AAA) games where UEVR's
/// stereo-render rehook loop crashes the process. Two layered defenses:
///
/// 1. Pre-launch config (uevr_write_stability_config): writes a conservative
///    config.txt that disables stereo rendering, enables ExtremeCompatibility,
///    and sets every Skip* flag we can safely set. Should be written BEFORE
///    the game launches so UEVR reads it during init.
///
/// 2. Post-inject D3D-monitor suppressor (uevr_suppress_d3d_monitor): enumerates
///    threads in the target, finds the one whose start address sits inside
///    UEVRBackend.dll's range, and suspends it. UEVR's D3D monitor thread is
///    the one that triggers the "Last chance encountered for hooking" retry
///    cycle; suspending it breaks the crash loop without otherwise affecting
///    UEVR's reflection access.
///
/// Together they let live `uevr_dump_*` pipeline run on DX12 AAA games that
/// otherwise crash ~7s after injection.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class StabilityTools
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static string ConfigPathFor(string gameName)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnrealVRMod", gameName, "config.txt");

    // ─── Config-based hardening ────────────────────────────────────────

    // Stability preset — each key maps to a value we think is safest for
    // reflection-only dumping. Comments note the rationale so future games
    // can tune individual entries if needed.
    //
    // The preset targets three failure modes we've seen on DX12 AAA games
    // (Stellar Blade, etc.):
    //   1. UEVR's D3D monitor thread retries hook installation every ~10s,
    //      crashing the D3D device mid-render.
    //   2. UEVR installs a FFakeStereoRenderingHook on FViewport/SceneView,
    //      which races with the game thread on heavy reflection walks.
    //   3. UEVR's VR runtime init (OpenXR/OpenVR) holds references that
    //      don't release cleanly under memory pressure.
    //
    // (1) is addressed by uevr_suppress_d3d_monitor at runtime.
    // (2) is addressed by VR_Compatibility_SceneView + VR_2DScreenMode +
    //     VR_RenderingMethod=2 (alternating mode, least invasive).
    // (3) is addressed by Frontend_RequestedRuntime pointing at a non-
    //     existent DLL so UEVR skips runtime init entirely.
    static readonly (string Key, string Value, string Why)[] StabilityPresets = new[]
    {
        ("VR_ExtremeCompatibilityMode",        "true",                  "UEVR's own 'make this work on broken games' flag"),
        ("VR_2DScreenMode",                    "true",                  "render mono — no stereo hook churn"),
        ("VR_Compatibility_SceneView",         "true",                  "skip FSceneView / SceneViewExtension hook install"),
        ("VR_Compatibility_SkipPostInitProperties", "true",             "skip the PostInitProperties hook that fires on every UObject"),
        ("VR_Compatibility_AHUD",              "true",                  "skip the HUD-specific hook path"),
        ("VR_RenderingMethod",                 "2",                     "alternating (0=native stereo, 1=synced, 2=alternating) — least intrusive on DX12"),
        ("VR_AsynchronousScan",                "false",                 "deterministic init order, less race with D3D hook"),
        ("VR_LoadBlueprintCode",               "false",                 "no BP injection, fewer moving parts"),
        ("VR_EnableGUI",                       "false",                 "no imgui overlay, fewer D3D interactions"),
        ("VR_ShowFPSOverlay",                  "false",                 "same"),
        ("VR_ShowStatsOverlay",                "false",                 "same"),
        ("VR_UseFMallocSceneViewExtensions",   "false",                 "skip engine-level scene view extension patching"),
        ("VR_PassDepthToRuntime",              "false",                 "don't send depth to VR runtime (not loaded anyway)"),
        ("UObjectHook_EnabledAtStartup",       "false",                 "user can enable later; our plugin accesses UObject via UEVR API directly"),
        ("FrameworkConfig_MenuOpen",           "false",                 "don't pop the menu"),
        // NOTE: Frontend_RequestedRuntime is deliberately NOT set here. Pointing it
        // at a non-existent DLL makes UEVR retry runtime loading and hurts stability
        // on the games it was supposed to help. Leave the user's existing value
        // (typically openxr_loader.dll) alone.
    };

    [McpServerTool(Name = "uevr_write_stability_config")]
    [Description("Write a conservative UEVR config.txt for a game before launch. Sets ExtremeCompatibilityMode, 2DScreenMode, disables async scanning / GUI / FPS overlay, and toggles off UObjectHook startup. Reduces the chance that UEVR's stereo-render rehook loop crashes DX12 AAA games during our dump. Does NOT modify already-running games — the config is read at UEVR init time, so write this BEFORE uevr_setup_game.")]
    public static string WriteStabilityConfig(
        [Description("Game folder name (exe basename, e.g. 'SB-Win64-Shipping'). Config writes to %APPDATA%\\UnrealVRMod\\<gameName>\\config.txt.")] string gameName,
        [Description("Keep the existing config's other fields (default true). False = clobber to preset only.")] bool preserveOthers = true,
        [Description("Extra key=value pairs to set (comma-separated 'KEY=VALUE'). Override or add to the preset.")] string? extra = null)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return Err("gameName is required.");

        var path = ConfigPathFor(gameName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        if (preserveOthers && File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq);
                var v = line.Substring(eq + 1);
                fields[k] = v;
            }
        }

        // Apply preset.
        foreach (var (k, v, _) in StabilityPresets)
            fields[k] = v;

        // Apply user overrides.
        if (!string.IsNullOrEmpty(extra))
        {
            foreach (var pair in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                fields[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
            }
        }

        using var sw = new StreamWriter(path, append: false, Encoding.ASCII);
        foreach (var k in fields.Keys.OrderBy(x => x, StringComparer.Ordinal))
            sw.WriteLine($"{k}={fields[k]}");

        return JsonSerializer.Serialize(new {
            ok = true,
            data = new {
                configPath = path,
                keysWritten = fields.Count,
                presetKeys = StabilityPresets.Select(p => new { key = p.Key, value = p.Value, why = p.Why }),
            }
        }, JsonOpts);
    }

    // ─── Runtime D3D-monitor suppressor ────────────────────────────────
    //
    // Enumerates all threads in the target process; for each thread's start
    // address (via NtQueryInformationThread / Win32StartAddress) checks if it
    // falls inside UEVRBackend.dll's mapped range. The D3D monitor thread is
    // the one that waits 10s then triggers rehooks; suspending it stops the
    // crash loop. We look up the backend's base+size via Process.Modules.
    //
    // Trade-off: this blunts UEVR's own self-healing for real VR use cases
    // (HMD disconnect/reconnect), but for reflection dumps that don't care
    // about VR rendering, it's exactly what we want.

    const uint THREAD_ALL_ACCESS = 0x001F03FF;
    const int  ThreadQuerySetWin32StartAddress = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    const uint TH32CS_SNAPTHREAD = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int  tpBasePri;
        public int  tpDeltaPri;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Thread32First(IntPtr hSnap, ref THREADENTRY32 lpte);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Thread32Next(IntPtr hSnap, ref THREADENTRY32 lpte);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint dwAccess, bool bInherit, uint dwThreadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SuspendThread(IntPtr hThread);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr hThread);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationThread(
        IntPtr ThreadHandle,
        int ThreadInformationClass,
        out IntPtr ThreadInformation,
        uint ThreadInformationLength,
        IntPtr ReturnLength);

    [McpServerTool(Name = "uevr_suppress_d3d_monitor")]
    [Description("Suspend UEVR's D3D monitor thread inside a running target. The monitor thread is what triggers UEVR's 'Last chance encountered for hooking' retry cycle, which crashes DX12 games during their render loop. Finds all threads whose start address sits inside UEVRBackend.dll's mapped range and suspends them. Reflection access via the UEVR API is unaffected (our plugin runs on the game thread, not UEVR's worker threads). Call this AFTER uevr_setup_game so the backend is loaded and UE reflection init has already completed.")]
    public static string SuppressD3DMonitor(
        [Description("Target process id.")] int pid,
        [Description("Also suspend UEVR's command thread (handles frontend commands — unnecessary for dumping). Default false.")] bool includeCommandThread = false,
        [Description("Dry-run: report threads that would be suspended without actually doing it.")] bool dryRun = false)
    {
        Process proc;
        try { proc = Process.GetProcessById(pid); }
        catch { return Err($"pid {pid} not found"); }

        // Locate UEVRBackend.dll's address range inside the target.
        IntPtr backendBase = IntPtr.Zero;
        long backendSize = 0;
        string? backendPath = null;
        try
        {
            foreach (ProcessModule m in proc.Modules)
            {
                if (m.ModuleName?.Equals("UEVRBackend.dll", StringComparison.OrdinalIgnoreCase) == true)
                {
                    backendBase = m.BaseAddress;
                    backendSize = m.ModuleMemorySize;
                    backendPath = m.FileName;
                    break;
                }
            }
        }
        catch (Exception ex) { return Err($"failed to enumerate modules: {ex.Message}"); }

        if (backendBase == IntPtr.Zero)
            return Err("UEVRBackend.dll is not loaded in the target. Run uevr_setup_game first.");

        long backendLow  = backendBase.ToInt64();
        long backendHigh = backendLow + backendSize;

        // Enumerate threads in the target process.
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap == (IntPtr)(-1) || snap == IntPtr.Zero)
            return Err("CreateToolhelp32Snapshot failed");

        var candidates = new List<(uint tid, long start, string? hint)>();
        try
        {
            var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snap, ref te)) return Err("Thread32First failed");
            do
            {
                if (te.th32OwnerProcessID != pid) continue;

                var hThread = OpenThread(THREAD_ALL_ACCESS, false, te.th32ThreadID);
                if (hThread == IntPtr.Zero) continue;
                try
                {
                    if (NtQueryInformationThread(hThread, ThreadQuerySetWin32StartAddress,
                            out var startAddr, (uint)IntPtr.Size, IntPtr.Zero) != 0)
                        continue;
                    long sv = startAddr.ToInt64();
                    if (sv < backendLow || sv >= backendHigh) continue;

                    // Thread start is inside UEVRBackend's range — candidate worker.
                    long rva = sv - backendLow;
                    candidates.Add((te.th32ThreadID, sv, $"rva=0x{rva:X}"));
                }
                finally { CloseHandle(hThread); }
            } while (Thread32Next(snap, ref te));
        }
        finally { CloseHandle(snap); }

        if (candidates.Count == 0)
        {
            return JsonSerializer.Serialize(new {
                ok = true,
                data = new {
                    pid,
                    backendBase = $"0x{backendLow:X}",
                    backendSize,
                    found = 0,
                    note = "No UEVRBackend worker threads found. Either the backend just loaded (threads not spawned yet — wait a second and retry) or UEVR is using a different start-address pattern for its workers.",
                }
            }, JsonOpts);
        }

        var suspended = new List<object>();
        if (!dryRun)
        {
            foreach (var (tid, start, hint) in candidates)
            {
                var hThread = OpenThread(THREAD_ALL_ACCESS, false, tid);
                if (hThread == IntPtr.Zero) continue;
                try
                {
                    var prev = SuspendThread(hThread);
                    if (prev != uint.MaxValue)
                        suspended.Add(new { tid, start = $"0x{start:X}", prevSuspendCount = prev, hint });
                }
                finally { CloseHandle(hThread); }
            }
        }

        return JsonSerializer.Serialize(new {
            ok = true,
            data = new {
                pid,
                backendBase = $"0x{backendLow:X}",
                backendSize,
                backendPath,
                candidates = candidates.Select(c => new {
                    tid = c.tid,
                    start = $"0x{c.start:X}",
                    hint = c.hint,
                }),
                suspended,
                dryRun,
                hint = dryRun
                    ? "Dry run complete. Call again with dryRun=false to actually suspend these threads."
                    : includeCommandThread
                        ? $"Suspended {suspended.Count} UEVR worker thread(s). This includes the D3D monitor and command thread."
                        : $"Suspended {suspended.Count} UEVR worker thread(s) — the D3D monitor's rehook loop should be frozen. UEVR reflection API continues to work.",
            }
        }, JsonOpts);
    }

    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, JsonOpts);
}
