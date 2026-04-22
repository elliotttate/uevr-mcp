using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Wrappers around third-party CLI tools that pair well with a live UEVR-MCP session:
///   - uesave (Rust): GVAS save-file editing — https://github.com/trumank/uesave-rs
///   - patternsleuth: UE symbol/AoB resolver — https://github.com/trumank/patternsleuth
/// The tools themselves aren't bundled; point at installed binaries via env vars
/// (UESAVE_EXE, PATTERNSLEUTH_EXE) or let auto-detect find them on PATH / cargo bin.
/// </summary>
[McpServerToolType]
public static class ExternalTools
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static string? ResolveExe(string envVar, params string[] candidateBasenames)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin")
            });

        foreach (var dir in pathDirs)
        {
            foreach (var name in candidateBasenames)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    record RunResult(int ExitCode, string Stdout, string Stderr, string Command);

    static async Task<RunResult> Run(string exe, IEnumerable<string> args, string? stdin = null, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var exitTask = p.WaitForExitAsync();

        var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs));
        if (completed != exitTask)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new(-1, await outTask, (await errTask) + $"\n[killed after {timeoutMs}ms]",
                exe + " " + string.Join(' ', args));
        }

        return new(p.ExitCode, await outTask, await errTask,
            exe + " " + string.Join(' ', args));
    }

    // ─── uesave ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_uesave")]
    [Description("Run uesave (https://github.com/trumank/uesave-rs) on a GVAS save file. Subcommands: 'to-json' (dump save to JSON), 'from-json' (rebuild save from JSON), 'edit' (JSON roundtrip with inline edits via jq-style path, optional). Auto-locates uesave via $UESAVE_EXE, PATH, or ~/.cargo/bin. Use this to read/modify .sav files between runs — pair with uevr_get_game_info to discover the save directory.")]
    public static async Task<string> UeSave(
        [Description("Subcommand: 'to-json' | 'from-json' | 'version' | 'raw' (pass args directly)")] string subcommand,
        [Description("Input file path (.sav for to-json, .json for from-json)")] string? input = null,
        [Description("Output file path (default: stdout for to-json, autoderived for from-json)")] string? output = null,
        [Description("Extra CLI args forwarded verbatim to uesave (after the subcommand).")] string? extraArgs = null,
        [Description("Timeout in ms (default 60000)")] int timeoutMs = 60000)
    {
        var exe = ResolveExe("UESAVE_EXE", "uesave.exe", "uesave");
        if (exe is null)
            return Err("uesave not found. Install from https://github.com/trumank/uesave-rs (`cargo install --git ...`) or set $UESAVE_EXE.");

        var args = new List<string>();
        if (subcommand == "raw")
        {
            // Split extraArgs by spaces, respecting quotes minimally.
            if (!string.IsNullOrEmpty(extraArgs)) args.AddRange(SplitArgs(extraArgs));
        }
        else
        {
            args.Add(subcommand);
            if (!string.IsNullOrEmpty(input)) args.Add(input);
            if (!string.IsNullOrEmpty(output)) args.Add(output);
            if (!string.IsNullOrEmpty(extraArgs)) args.AddRange(SplitArgs(extraArgs));
        }

        var r = await Run(exe, args, timeoutMs: timeoutMs);
        return JsonSerializer.Serialize(new
        {
            ok = r.ExitCode == 0,
            exe,
            command = r.Command,
            exitCode = r.ExitCode,
            stdout = r.Stdout,
            stderr = r.Stderr
        }, Json);
    }

    // ─── patternsleuth ──────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_patternsleuth")]
    [Description("Run patternsleuth (https://github.com/trumank/patternsleuth) against a UE binary to resolve known engine symbols/offsets (GUObjectArray, GNames, FName::ToString, etc.). Useful when you want to bootstrap offsets without UEVR attached, or cross-check what the injected plugin sees. Auto-locates via $PATTERNSLEUTH_EXE, PATH, or ~/.cargo/bin. Forward any patternsleuth args via 'args' (e.g. 'scan --game RoboQuest-Win64-Shipping.exe').")]
    public static async Task<string> PatternSleuth(
        [Description("Args forwarded to patternsleuth verbatim (e.g. 'scan --game path\\to\\exe')")] string args,
        [Description("Working directory (default: current)")] string? cwd = null,
        [Description("Timeout in ms (default 120000)")] int timeoutMs = 120000)
    {
        var exe = ResolveExe("PATTERNSLEUTH_EXE", "patternsleuth.exe", "patternsleuth");
        if (exe is null)
            return Err("patternsleuth not found. Install from https://github.com/trumank/patternsleuth or set $PATTERNSLEUTH_EXE.");

        var argList = SplitArgs(args).ToList();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        foreach (var a in argList) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var done = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(timeoutMs));
        if (done is Task t && t != p.WaitForExitAsync() && !p.HasExited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return Err($"patternsleuth timed out after {timeoutMs}ms");
        }

        return JsonSerializer.Serialize(new
        {
            ok = p.ExitCode == 0,
            exe,
            args = argList,
            cwd = psi.WorkingDirectory,
            exitCode = p.ExitCode,
            stdout = await outTask,
            stderr = await errTask
        }, Json);
    }

    // ─── uevr_validate_usmap ───────────────────────────────────────────

    [McpServerTool(Name = "uevr_validate_usmap")]
    [Description("Sanity-check a .usmap file. Always: parses the header (magic, version, compression, sizes) locally. Optional: if jmap's `usmap` CLI is on PATH or at $USMAP_CLI / E:\\Github\\jmap\\target\\release\\usmap.exe, round-trips the file through to-json to confirm the body parses. Reports first-error rather than bailing — useful for debugging uevr_dump_usmap output.")]
    public static async Task<string> ValidateUsmap(
        [Description("Path to .usmap file to validate.")] string usmapPath,
        [Description("If true and jmap CLI is available, do a full parse round-trip (default true).")] bool fullParse = true)
    {
        if (!File.Exists(usmapPath))
            return Err($"usmap file not found: {usmapPath}");

        var report = new Dictionary<string, object?>
        {
            ["path"] = Path.GetFullPath(usmapPath),
            ["bytes"] = new FileInfo(usmapPath).Length,
        };

        // Header parse.
        try
        {
            using var fs = File.OpenRead(usmapPath);
            using var br = new BinaryReader(fs);
            ushort magic = br.ReadUInt16();
            if (magic != 0x30C4)
                return ErrReport(report, $"bad magic 0x{magic:X4} (expected 0x30C4)");

            byte version = br.ReadByte();
            report["magic"] = $"0x{magic:X4}";
            report["version"] = version;
            report["versionName"] = version switch {
                0 => "Initial", 1 => "PackageVersioning", 2 => "LongFName",
                3 => "LargeEnums", 4 => "ExplicitEnumValues",
                _ => "Unknown"
            };
            if (version > 4) report["warning"] = $"unrecognized version {version}";

            if (version >= 1)
            {
                int hasVersioning = br.ReadInt32();
                report["hasVersioning"] = hasVersioning > 0;
                if (hasVersioning > 0)
                {
                    br.ReadInt32(); br.ReadInt32(); // file_version_ue4, file_version_ue5
                    int customCount = br.ReadInt32();
                    for (int i = 0; i < customCount; i++) { br.ReadBytes(20); br.ReadInt32(); }
                    br.ReadInt32(); // net_cl
                }
            }

            byte compression = br.ReadByte();
            uint compressedSize = br.ReadUInt32();
            uint decompressedSize = br.ReadUInt32();
            report["compression"] = compression switch {
                0 => "None", 1 => "Oodle", 2 => "Brotli", 3 => "Zstd",
                _ => $"unknown({compression})"
            };
            report["compressedSize"] = compressedSize;
            report["decompressedSize"] = decompressedSize;

            long bodyBytes = fs.Length - fs.Position;
            if (compression == 0 && bodyBytes != compressedSize)
                report["sizeMismatch"] = $"body is {bodyBytes} bytes but header claims {compressedSize}";

            // If uncompressed, peek at name count as another sanity check.
            if (compression == 0 && bodyBytes >= 4)
            {
                uint nameCount = br.ReadUInt32();
                report["nameCountHeader"] = nameCount;
                if (nameCount > 1_000_000) report["suspiciousNameCount"] = nameCount;
            }
        }
        catch (Exception ex)
        {
            return ErrReport(report, "header parse failed: " + ex.Message);
        }

        report["headerOk"] = true;

        // Optional: full parse via jmap's `usmap to-json`.
        if (fullParse)
        {
            var cli = ResolveExe("USMAP_CLI", "usmap.exe", "usmap")
                      ?? FirstExisting(@"E:\Github\jmap\target\release\usmap.exe");
            if (cli is null)
            {
                report["fullParse"] = "skipped: usmap CLI not found (set $USMAP_CLI or build jmap with `cargo build --release -p usmap`)";
            }
            else
            {
                var r = await Run(cli, new[] { "to-json", usmapPath }, timeoutMs: 30000);
                report["fullParse"] = r.ExitCode == 0 ? "ok" : "failed";
                if (r.ExitCode != 0)
                {
                    report["fullParseStderr"] = r.Stderr;
                    report["fullParseExit"] = r.ExitCode;
                }
                else
                {
                    // Count as compact proof-of-parse. jmap emits structs/enums as
                    // KeyValueMap objects (name → body), not arrays.
                    try
                    {
                        using var doc = JsonDocument.Parse(r.Stdout);
                        static int CountMembers(JsonElement el) => el.ValueKind switch {
                            JsonValueKind.Object => el.EnumerateObject().Count(),
                            JsonValueKind.Array  => el.GetArrayLength(),
                            _ => 0,
                        };
                        if (doc.RootElement.TryGetProperty("structs", out var structs))
                            report["parsedStructCount"] = CountMembers(structs);
                        if (doc.RootElement.TryGetProperty("enums", out var enums))
                            report["parsedEnumCount"] = CountMembers(enums);
                    }
                    catch (Exception ex) { report["fullParseJsonInvalid"] = ex.Message; }
                }
            }
        }

        return JsonSerializer.Serialize(new { ok = true, report }, Json);
    }

    static string ErrReport(Dictionary<string, object?> report, string msg)
    {
        report["error"] = msg;
        return JsonSerializer.Serialize(new { ok = false, report }, Json);
    }

    static string? FirstExisting(params string?[] paths)
    {
        foreach (var p in paths)
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return Path.GetFullPath(p);
        return null;
    }

    // ─── helpers ────────────────────────────────────────────────────────

    static IEnumerable<string> SplitArgs(string s)
    {
        // Minimal shell-like split: respects double-quoted segments, strips quotes.
        var cur = new StringBuilder();
        bool inQuote = false;
        foreach (var c in s)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, Json);
}
