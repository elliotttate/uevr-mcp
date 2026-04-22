using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UevrMcp;

/// <summary>
/// Windows DLL injector: CreateRemoteThread + LoadLibraryA. Pure kernel32 p/invoke,
/// identical semantics to tools/inject_uevr.ps1 but native so the MCP server can do
/// the whole setup flow (plugin install + backend injection) in-process.
/// </summary>
[SupportedOSPlatform("windows")]
static class Injector
{
    const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    const uint MEM_COMMIT_RESERVE = 0x3000;
    const uint MEM_RELEASE        = 0x8000;
    const uint PAGE_READWRITE     = 0x04;
    const uint WAIT_TIMEOUT       = 0x102;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr hProc, IntPtr addr, uint size, uint alloc, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr hProc, IntPtr addr, uint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr hProc, IntPtr addr, byte[] buf, uint size, out uint written);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr hProc, IntPtr attrs, uint stack, IntPtr startAddr, IntPtr param, uint flags, out uint tid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeThread(IntPtr h, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    public readonly record struct Result(bool Ok, string Message, uint LoadLibraryReturn);

    public static Result Inject(int pid, string dllPath, int waitMs = 30000)
    {
        if (!File.Exists(dllPath))
            return new(false, $"DLL not found: {dllPath}", 0);
        dllPath = Path.GetFullPath(dllPath);

        var hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (hProc == IntPtr.Zero)
            return new(false, $"OpenProcess failed (pid={pid}, err={Marshal.GetLastWin32Error()})", 0);

        IntPtr remotePath = IntPtr.Zero;
        uint allocSize = 0;
        try
        {
            var bytes = Encoding.ASCII.GetBytes(dllPath + '\0');
            allocSize = (uint)bytes.Length;
            remotePath = VirtualAllocEx(hProc, IntPtr.Zero, allocSize, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (remotePath == IntPtr.Zero)
                return new(false, $"VirtualAllocEx failed (err={Marshal.GetLastWin32Error()})", 0);

            if (!WriteProcessMemory(hProc, remotePath, bytes, allocSize, out _))
                return new(false, $"WriteProcessMemory failed (err={Marshal.GetLastWin32Error()})", 0);

            var kernel = GetModuleHandleA("kernel32.dll");
            var loadLib = GetProcAddress(kernel, "LoadLibraryA");
            if (loadLib == IntPtr.Zero)
                return new(false, "GetProcAddress(LoadLibraryA) failed", 0);

            var hThread = CreateRemoteThread(hProc, IntPtr.Zero, 0, loadLib, remotePath, 0, out var tid);
            if (hThread == IntPtr.Zero)
                return new(false, $"CreateRemoteThread failed (err={Marshal.GetLastWin32Error()})", 0);

            try
            {
                var wait = WaitForSingleObject(hThread, (uint)waitMs);
                if (wait == WAIT_TIMEOUT)
                    return new(false, $"Remote thread did not return in {waitMs}ms", 0);

                GetExitCodeThread(hThread, out var code);
                // Note: return is truncated to 32-bit; 0 means LoadLibraryA failed.
                var ok = code != 0;
                return new(ok, ok
                    ? $"LoadLibraryA returned 0x{code:X} (tid={tid})"
                    : $"LoadLibraryA returned 0 — likely load failure inside target", code);
            }
            finally
            {
                CloseHandle(hThread);
            }
        }
        finally
        {
            if (remotePath != IntPtr.Zero)
                VirtualFreeEx(hProc, remotePath, 0, MEM_RELEASE);
            CloseHandle(hProc);
        }
    }

    public static bool HasModule(int pid, string moduleNameSubstr)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            p.Refresh();
            foreach (ProcessModule m in p.Modules)
            {
                if (m.ModuleName is string name &&
                    name.Contains(moduleNameSubstr, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static string[] ListModulesMatching(int pid, params string[] substrs)
    {
        var hits = new List<string>();
        try
        {
            var p = Process.GetProcessById(pid);
            p.Refresh();
            foreach (ProcessModule m in p.Modules)
            {
                if (m.ModuleName is string name &&
                    substrs.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    hits.Add(name);
            }
        }
        catch { }
        return hits.ToArray();
    }
}
