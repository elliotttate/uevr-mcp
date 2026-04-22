using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Raw memory write + AoB-then-patch. Complements uevr_read_memory / uevr_pattern_scan.
/// Backed by the plugin's POST /api/explorer/memory endpoint — SEH-guarded and handles
/// page-protection toggling for code-segment patches.
/// </summary>
[McpServerToolType]
public static class MemoryWriteTools
{
    [McpServerTool(Name = "uevr_write_memory")]
    [Description("Write raw bytes to an arbitrary address in the game process. Accepts hex-string ('DE AD BE EF' or 'DEADBEEF') or a JSON array of integers. SEH-guarded; writable page protection is applied transparently for code patches, then restored. Use this for poking structs, patching shipping binaries, or writing in-game constants. Pair with uevr_pattern_scan to find the target first, or use uevr_patch_bytes for the combined flow.")]
    public static async Task<string> WriteMemory(
        [Description("Target address (0xHEX)")] string address,
        [Description("Bytes to write — hex string (spaces/commas OK) or JSON array of 0..255 ints")] string bytes)
        => await Http.Post("/api/explorer/memory", new { address, bytes = JsonArgs.Parse(bytes) ?? (object)bytes });

    [McpServerTool(Name = "uevr_patch_bytes")]
    [Description("Find an AoB pattern and overwrite the match with new bytes. Combines uevr_pattern_scan + uevr_write_memory into one call. 'replacement' length may differ from 'pattern' length — all written bytes are placed starting at the match. Use '?' wildcards only in the pattern; replacement must be concrete hex.")]
    public static async Task<string> PatchBytes(
        [Description("Hex AoB pattern to search, '?' for wildcards (e.g. '48 89 5C 24 ? 57')")] string pattern,
        [Description("Replacement bytes as hex string (no wildcards)")] string replacement,
        [Description("Module name to scan (default: main exe)")] string? module = null,
        [Description("If the pattern matches more than once, abort unless force=true. Default false.")] bool force = false)
    {
        // Step 1: find the pattern (limit=2 so we can detect ambiguity cheaply).
        var scanRaw = await Http.Post("/api/discovery/pattern_scan", new { pattern, module, limit = 2 });

        JsonElement scan;
        try { scan = JsonDocument.Parse(scanRaw).RootElement; }
        catch (JsonException) {
            return JsonSerializer.Serialize(new { ok = false, error = "pattern_scan returned non-JSON", raw = scanRaw });
        }

        // The scan endpoint returns {results: [...]} or {error: "..."}; forward errors verbatim.
        if (scan.TryGetProperty("error", out var errEl))
            return JsonSerializer.Serialize(new { ok = false, step = "pattern_scan", error = errEl.GetString() ?? errEl.ToString() });
        if (!scan.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
            return JsonSerializer.Serialize(new { ok = false, step = "pattern_scan", error = "no results field", raw = scanRaw });

        var matches = resultsEl.EnumerateArray().ToList();
        if (matches.Count == 0)
            return JsonSerializer.Serialize(new { ok = false, step = "pattern_scan", error = "pattern not found" });
        if (matches.Count > 1 && !force)
            return JsonSerializer.Serialize(new {
                ok = false, step = "pattern_scan",
                error = "pattern matched multiple locations — pass force=true to patch the first, or tighten the pattern",
                matchCount = matches.Count,
                firstMatches = matches.Take(2)
                    .Select(m => m.TryGetProperty("address", out var a) ? a.GetString() : null)
                    .ToArray()
            });

        var addr = matches[0].TryGetProperty("address", out var addrEl) ? addrEl.GetString() : null;
        if (string.IsNullOrEmpty(addr))
            return JsonSerializer.Serialize(new { ok = false, step = "pattern_scan", error = "match had no address field", raw = scanRaw });

        // Step 2: write.
        var writeRaw = await Http.Post("/api/explorer/memory", new { address = addr, bytes = replacement });
        return JsonSerializer.Serialize(new {
            ok = true,
            patched = addr,
            matchCount = matches.Count,
            writeResult = JsonArgs.Parse(writeRaw)
        });
    }
}
