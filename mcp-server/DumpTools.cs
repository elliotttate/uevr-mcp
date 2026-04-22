using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// Bulk reflection dump tools. All three fetch once from the plugin's
/// /api/dump/reflection endpoint and then transform to different output
/// formats locally:
///   - uevr_dump_reflection_json: raw structured JSON (classes/structs/enums + fields + methods)
///   - uevr_dump_sdk_cpp:         one C++ header per type (Dumper-7 style, minimal)
///   - uevr_dump_usmap:           .usmap v0 binary (FModel / CUE4Parse / UAssetAPI mappings)
/// The plugin walks GUObjectArray once and returns everything, so even a big game
/// is a single HTTP call.
/// </summary>
[McpServerToolType]
public static class DumpTools
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── Reflection cache ──────────────────────────────────────────────
    //
    // The reflection walk is the expensive part. Subsequent dump_* tools with
    // the same parameters would repeat it needlessly and, on fragile games
    // like RoboQuest, push the process past its crash window. Cache the raw
    // JSON text and re-parse per caller. TTL is tight because objects come
    // and go, but within a single agent turn multiple dump_* calls hit warm
    // cache and share one walk.

    static readonly object _cacheLock = new();
    static string? _cacheKey;
    static string? _cacheJson;
    static DateTime _cacheAt;
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    static string MakeCacheKey(string? filter, bool methods, bool enums)
        => $"f={filter ?? ""}|m={methods}|e={enums}";

    [McpServerTool(Name = "uevr_dump_cache_clear")]
    [Description("Invalidate the reflection-dump cache. Call after the game loads new content or when you want fresh data. Dumps already in flight are unaffected.")]
    public static string DumpCacheClear()
    {
        lock (_cacheLock) { _cacheKey = null; _cacheJson = null; _cacheAt = default; }
        return JsonSerializer.Serialize(new { ok = true, cleared = true }, JsonOpts);
    }

    [McpServerTool(Name = "uevr_probe_status")]
    [Description("Report which FProperty subclass field offsets have been self-discovered by the plugin probes: FObjectProperty::PropertyClass, FClassProperty::MetaClass, FInterfaceProperty::InterfaceClass, FMapProperty::KeyProp/ValueProp, FSetProperty::ElementProp, and F*DelegateProperty::SignatureFunction. Use this on a new game to verify the probes locked onto the right offsets before trusting the dump's typed references.")]
    public static async Task<string> ProbeStatus()
        => await Http.Get("/api/dump/probe_status");

    [McpServerTool(Name = "uevr_probe_reset")]
    [Description("Clear all property-subclass offset caches so the plugin re-discovers them on the next dump. Rare — useful after game hot-reload (if supported) or cross-game testing with shared plugin processes.")]
    public static async Task<string> ProbeReset()
        => await Http.Post("/api/dump/probe_reset", new { });

    [McpServerTool(Name = "uevr_dump_cache_status")]
    [Description("Report whether a cached reflection walk is warm and how old it is. Useful to decide between cheap reuse and a fresh dump.")]
    public static string DumpCacheStatus()
    {
        lock (_cacheLock)
        {
            if (_cacheKey is null)
                return JsonSerializer.Serialize(new { ok = true, warm = false }, JsonOpts);
            var ageS = (DateTime.UtcNow - _cacheAt).TotalSeconds;
            return JsonSerializer.Serialize(new {
                ok = true, warm = true, key = _cacheKey,
                ageSeconds = Math.Round(ageS, 1),
                ttlSeconds = CacheTtl.TotalSeconds,
                expired = ageS > CacheTtl.TotalSeconds,
                bytes = _cacheJson?.Length ?? 0,
            }, JsonOpts);
        }
    }

    // ─── Fetch — paginated, cached ─────────────────────────────────────
    //
    // Iterates /api/dump/reflection_batch until done=true, accumulating classes,
    // structs, and enums into one synthesized document shaped like the old
    // single-shot /api/dump/reflection response. Each batch runs briefly on the
    // game thread (default 4000 objects / ~200ms) so the game keeps ticking.
    // This was added because a full single-slice walk on AAA UE5 games starves
    // UE's tick and crashes the render thread (observed on RoboQuest).

    static async Task<JsonDocument> FetchReflection(string? filter, bool methods, bool enums)
    {
        var key = MakeCacheKey(filter, methods, enums);
        lock (_cacheLock)
        {
            if (_cacheKey == key && _cacheJson is not null &&
                (DateTime.UtcNow - _cacheAt) < CacheTtl)
            {
                return JsonDocument.Parse(_cacheJson);
            }
        }

        // Smaller batches when methods=true — enumerate_methods is 10–50× more work
        // per class, and we want each batch to fit comfortably inside the plugin's
        // 10s game-thread slice.
        int batchSize = methods ? 1000 : 4000;
        int offset = 0;
        int totalCount = -1;

        var combined = new System.Text.StringBuilder();
        var classes = new System.Text.StringBuilder("[");
        var structs = new System.Text.StringBuilder("[");
        var enumsSb = new System.Text.StringBuilder("[");
        bool firstClass = true, firstStruct = true, firstEnum = true;

        int batches = 0, totalScanned = 0, totalMatched = 0, objectErrors = 0;

        var batchTimings = new List<object>();
        while (true)
        {
            var q = new Dictionary<string, string?>
            {
                ["offset"]  = offset.ToString(),
                ["limit"]   = batchSize.ToString(),
                ["filter"]  = string.IsNullOrEmpty(filter) ? null : filter,
                ["methods"] = methods ? "true" : "false",
                ["enums"]   = enums ? "true" : "false",
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var raw = await Http.Get("/api/dump/reflection_batch", q);
            sw.Stop();
            batchTimings.Add(new { offset, ms = sw.ElapsedMilliseconds });
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                // Preserve full error detail so callers can see whether this was a
                // transport issue, game-thread timeout, or plugin-side exception.
                var detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
                var status = doc.RootElement.TryGetProperty("status", out var st) ? st.GetRawText() : "null";
                var body   = doc.RootElement.TryGetProperty("body",   out var b) ? b.GetString() : null;
                var merged = $"{err} (detail={detail ?? "—"}, status={status}, bodyPreview={(body ?? "").Substring(0, Math.Min(body?.Length ?? 0, 200))})";
                var timingsJson = JsonSerializer.Serialize(batchTimings);
                return JsonDocument.Parse(
                    $"{{\"error\": {JsonSerializer.Serialize(merged)}, \"batchesCompleted\": {batches}, \"offsetReached\": {offset}, \"batchTimings\": {timingsJson}}}");
            }

            batches++;

            void Append(System.Text.StringBuilder sb, ref bool first, JsonElement arr)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    sb.Append(item.GetRawText());
                    first = false;
                }
            }

            if (doc.RootElement.TryGetProperty("classes", out var c))  Append(classes,  ref firstClass,  c);
            if (doc.RootElement.TryGetProperty("structs", out var s))  Append(structs,  ref firstStruct, s);
            if (doc.RootElement.TryGetProperty("enums",   out var e))  Append(enumsSb,  ref firstEnum,   e);

            if (doc.RootElement.TryGetProperty("batchStats", out var bs))
            {
                if (bs.TryGetProperty("scanned", out var sc)) totalScanned += sc.GetInt32();
                if (bs.TryGetProperty("matched", out var m))  totalMatched += m.GetInt32();
                if (bs.TryGetProperty("errors",  out var er)) objectErrors += er.GetInt32();
            }

            if (doc.RootElement.TryGetProperty("totalCount", out var tc)) totalCount = tc.GetInt32();
            if (doc.RootElement.TryGetProperty("nextOffset", out var no)) offset = no.GetInt32();
            bool done = doc.RootElement.TryGetProperty("done", out var dn) && dn.GetBoolean();
            if (done) break;

            // Safety valve: if the batch endpoint isn't making progress (same offset twice),
            // break to avoid spinning forever.
            if (totalCount > 0 && offset >= totalCount) break;
            if (batches > 100_000) break;
        }

        classes.Append(']'); structs.Append(']'); enumsSb.Append(']');

        // Count entries for classCount/structCount/enumCount. Quick brace counting is
        // unreliable with nested objects; instead re-parse each array lazily below
        // and compute counts from doc.
        combined.Append('{');
        combined.Append("\"classes\":"); combined.Append(classes.ToString());
        combined.Append(",\"structs\":"); combined.Append(structs.ToString());
        if (enums) { combined.Append(",\"enums\":"); combined.Append(enumsSb.ToString()); }
        combined.Append(",\"stats\":{");
        combined.Append("\"totalScanned\":").Append(totalScanned);
        combined.Append(",\"totalMatched\":").Append(totalMatched);
        combined.Append(",\"objectErrors\":").Append(objectErrors);
        combined.Append(",\"batches\":").Append(batches);
        if (totalCount >= 0) combined.Append(",\"totalCount\":").Append(totalCount);
        combined.Append('}');
        combined.Append('}');

        var text = combined.ToString();
        lock (_cacheLock)
        {
            _cacheKey = key;
            _cacheJson = text;
            _cacheAt = DateTime.UtcNow;
        }
        return JsonDocument.Parse(text);
    }

    // ─── Tool 1: raw JSON dump ─────────────────────────────────────────

    [McpServerTool(Name = "uevr_dump_reflection_json")]
    [Description("Dump every UClass/UScriptStruct/UEnum with its fields and methods to a JSON file. Single HTTP call — the plugin walks GUObjectArray server-side. Use this to feed external tools (usmap converters, SDK generators, asset parsers) or as a reference snapshot of the game's type system. Returns summary stats; the full data lands in the output file.")]
    public static async Task<string> DumpReflectionJson(
        [Description("Absolute output path for the JSON file (parent dir must exist).")] string outPath,
        [Description("Optional case-insensitive filter on type full names (e.g. '/Game/' to skip engine types).")] string? filter = null,
        [Description("Include method lists (default true).")] bool includeMethods = true,
        [Description("Include enums (default true).")] bool includeEnums = true,
        [Description("Pretty-print JSON (default true).")] bool pretty = true)
    {
        using var doc = await FetchReflection(filter, includeMethods, includeEnums);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            // Surface batch timings if present so the caller can diagnose which slice hung.
            int? batchesCompleted = doc.RootElement.TryGetProperty("batchesCompleted", out var bc) ? bc.GetInt32() : null;
            int? offsetReached    = doc.RootElement.TryGetProperty("offsetReached",   out var ox) ? ox.GetInt32() : null;
            object? timings       = doc.RootElement.TryGetProperty("batchTimings",    out var bt) ? JsonArgs.Parse(bt.GetRawText()) : null;
            return JsonSerializer.Serialize(new {
                ok = false,
                error = "plugin returned error: " + err.ToString(),
                batchesCompleted, offsetReached, batchTimings = timings,
            }, JsonOpts);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
        var text = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = pretty });
        await File.WriteAllTextAsync(outPath, text);

        object? statsObj = null;
        if (doc.RootElement.TryGetProperty("stats", out var s))
            statsObj = JsonArgs.Parse(s.GetRawText());
        return Ok(new {
            outPath = Path.GetFullPath(outPath),
            bytes = new FileInfo(outPath).Length,
            stats = statsObj,
        });
    }

    // ─── Tool 2: C++ SDK emitter (Dumper-7 style lite) ─────────────────

    // Thread-local inheritance map set during DumpSdkCpp so RenderCppFromTag can
    // decide whether to prefix a UClass name with 'A' (Actor subclass) or 'U'
    // (any other UObject) to match UE C++ naming conventions.
    [ThreadStatic] static Dictionary<string, string>? _superMap;

    // A chain reaches Actor if it walks to any of these well-known Actor
    // descendants. The supermap only contains classes in the current filter
    // scope; engine base classes typically aren't there, so we treat reaching
    // one of the stock UE Actor types as conclusive. List is the stock UE
    // inheritance tree as of UE4.22–UE5.4.
    static readonly HashSet<string> _actorBases = new(StringComparer.Ordinal) {
        "Actor", "AActor",
        "Pawn", "APawn",
        "Character", "ACharacter",
        "Controller", "AController",
        "PlayerController", "APlayerController",
        "AIController", "AAIController",
        "HUD", "AHUD",
        "GameMode", "AGameMode", "GameModeBase", "AGameModeBase",
        "GameState", "AGameState", "GameStateBase", "AGameStateBase",
        "PlayerState", "APlayerState",
        "WorldSettings", "AWorldSettings",
        "Info", "AInfo",
        "Volume", "AVolume",
        "StaticMeshActor", "AStaticMeshActor",
        "SkeletalMeshActor", "ASkeletalMeshActor",
        "Light", "ALight", "PointLight", "APointLight", "SpotLight", "ASpotLight", "DirectionalLight", "ADirectionalLight",
        "CameraActor", "ACameraActor",
        "TriggerBox", "ATriggerBox",
        "NavigationData", "ANavigationData",
        "Brush", "ABrush",
    };

    static bool ChainReachesActor(string name, Dictionary<string, string> map)
    {
        // Cap the walk at 32 to dodge cyclic or deeply nested inputs.
        var cur = name;
        for (int i = 0; i < 32; i++)
        {
            if (_actorBases.Contains(cur)) return true;
            if (!map.TryGetValue(cur, out var sup)) return false;
            cur = sup;
        }
        return false;
    }

    static string UePrefix(string className)
    {
        // UE reflection sometimes keeps the C++ prefix on FNames ("AAISkill") and
        // sometimes strips it ("Character_AI"). Try the name as-is and with an
        // 'A' prepended; either form hitting the Actor chain means Actor-derived.
        var map = _superMap;
        if (map is null) return "U";
        if (ChainReachesActor(className, map)) return "A";
        if (!className.StartsWith("A", StringComparison.Ordinal) &&
            ChainReachesActor("A" + className, map)) return "A";
        if (className.StartsWith("A", StringComparison.Ordinal) && className.Length > 1 &&
            ChainReachesActor(className.Substring(1), map)) return "A";
        return "U";
    }

    [McpServerTool(Name = "uevr_dump_sdk_cpp")]
    [Description("Emit a minimal C++ SDK (one .hpp per class/struct) from live reflection. Output mirrors the Dumper-7 shape: struct with field decls at correct offsets, super-class inheritance. Useful for writing C++ mods that share the game's layouts. Methods are OFF by default — they force a much slower reflection walk (per-UFunction param enumeration) and are only useful as comments. Turn on with includeMethods=true if you need them.")]
    public static async Task<string> DumpSdkCpp(
        [Description("Absolute output directory. Will be created if missing; existing .hpp files in it will be overwritten.")] string outDir,
        [Description("Optional case-insensitive filter on type full names.")] string? filter = null,
        [Description("Emit method stub list in comments. Default false — turning on slows the dump significantly.")] bool includeMethods = false,
        [Description("Skip built-in engine types (/Script/Engine, /Script/CoreUObject, etc.) — default false.")] bool skipEngine = false)
    {
        // Fetch with enums=true so we share the cache with uevr_dump_usmap — back-to-
        // back dump tools get one reflection walk instead of two. Enums aren't used
        // by the SDK emitter; loading them costs a tiny amount of extra payload.
        using var doc = await FetchReflection(filter, includeMethods, enums: true);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            // Surface batch timings if present so the caller can diagnose which slice hung.
            int? batchesCompleted = doc.RootElement.TryGetProperty("batchesCompleted", out var bc) ? bc.GetInt32() : null;
            int? offsetReached    = doc.RootElement.TryGetProperty("offsetReached",   out var ox) ? ox.GetInt32() : null;
            object? timings       = doc.RootElement.TryGetProperty("batchTimings",    out var bt) ? JsonArgs.Parse(bt.GetRawText()) : null;
            return JsonSerializer.Serialize(new {
                ok = false,
                error = "plugin returned error: " + err.ToString(),
                batchesCompleted, offsetReached, batchTimings = timings,
            }, JsonOpts);
        }

        Directory.CreateDirectory(outDir);

        // Build a lightweight className -> super map so RenderCppFromTag can choose
        // the correct UE prefix ('A' for Actor-descended classes, 'U' otherwise).
        // Scoped per call via ThreadStatic; cleared in finally below.
        var superMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("classes", out var clsForMap))
            foreach (var c in clsForMap.EnumerateArray())
            {
                var nm = c.GetProperty("name").GetString();
                if (nm is null) continue;
                if (c.TryGetProperty("super", out var sp) && sp.GetString() is string s)
                    superMap[nm] = s;
            }
        _superMap = superMap;
        try {

        var emitted = new List<string>();
        var all = new StringBuilder();
        all.AppendLine("// Auto-generated by uevr_dump_sdk_cpp. One struct per UClass/UScriptStruct.");
        all.AppendLine("// Cast live UObject addresses to these structs to read/write fields at correct offsets.");
        all.AppendLine("#pragma once");
        all.AppendLine("#include <cstdint>");
        all.AppendLine();

        void EmitTypes(JsonElement arr, string kind)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString() ?? "Unnamed";
                var full = t.TryGetProperty("fullName", out var fn) ? fn.GetString() ?? "" : "";
                if (skipEngine && (full.Contains("/Script/Engine") || full.Contains("/Script/CoreUObject")))
                    continue;

                var super = t.TryGetProperty("super", out var sp) ? sp.GetString() : null;
                int propsSize = t.TryGetProperty("propertiesSize", out var ps) ? ps.GetInt32() : 0;

                var sb = new StringBuilder();
                sb.AppendLine($"// {kind}: {full}");
                sb.AppendLine($"// Size: 0x{propsSize:X}");
                sb.Append("struct ").Append(SanitizeIdent(name));
                if (!string.IsNullOrEmpty(super)) sb.Append(" /* : ").Append(SanitizeIdent(super)).Append(" */");
                sb.AppendLine(" {");

                if (t.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                {
                    // Plugin emits own-fields only post-refactor; filter by owner if present for safety.
                    foreach (var f in fields.EnumerateArray())
                    {
                        var owner = f.TryGetProperty("owner", out var ow) ? ow.GetString() : null;
                        if (!string.IsNullOrEmpty(owner) && owner != name) continue;

                        var fname = f.GetProperty("name").GetString() ?? "unnamed";
                        var ftype = f.GetProperty("type").GetString() ?? "Unknown";
                        int offset = f.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

                        // Read richer type info from the recursive `tag` object.
                        JsonElement tag = default;
                        bool hasTag = f.TryGetProperty("tag", out tag) && tag.ValueKind == JsonValueKind.Object;
                        // Prefer the richer probe-aware renderer when we have a tag —
                        // falls back to plain CppTypeFor for legacy flat-field dumps.
                        string cppType = hasTag ? RenderCppFromTag(tag) : CppTypeFor(ftype);
                        string extra = hasTag ? ExtraFromTag(tag) : ExtraFromFlat(f);

                        sb.AppendLine($"    {cppType,-28} {SanitizeIdent(fname)}; // +0x{offset:X} ({ftype}){extra}");
                    }
                }

                if (includeMethods && t.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine("    // Methods (stubs — call via UFunction):");
                    foreach (var m in methods.EnumerateArray())
                    {
                        var owner = m.TryGetProperty("owner", out var ow) ? ow.GetString() : null;
                        if (!string.IsNullOrEmpty(owner) && owner != name) continue;
                        var mname = m.GetProperty("name").GetString() ?? "?";
                        var ret = m.TryGetProperty("returnType", out var r) ? r.GetString() : "void";
                        var paramsStr = m.TryGetProperty("params", out var ps2)
                            ? string.Join(", ", ps2.EnumerateArray().Select(p =>
                                $"{p.GetProperty("type").GetString()} {p.GetProperty("name").GetString()}"))
                            : "";
                        sb.AppendLine($"    // {ret} {mname}({paramsStr});");
                    }
                }

                sb.AppendLine("};");
                sb.AppendLine();

                var fileName = SanitizeIdent(name) + ".hpp";
                File.WriteAllText(Path.Combine(outDir, fileName),
                    "// Auto-generated. " + full + "\n#pragma once\n#include <cstdint>\n\n" + sb.ToString());
                emitted.Add(fileName);
                all.Append(sb);
            }
        }

        if (doc.RootElement.TryGetProperty("classes", out var classes)) EmitTypes(classes, "Class");
        if (doc.RootElement.TryGetProperty("structs", out var structs)) EmitTypes(structs, "ScriptStruct");

        File.WriteAllText(Path.Combine(outDir, "sdk.hpp"), all.ToString());

        return Ok(new {
            outDir = Path.GetFullPath(outDir),
            headerCount = emitted.Count,
            allInOne = Path.Combine(Path.GetFullPath(outDir), "sdk.hpp"),
        });
        } finally { _superMap = null; }
    }

    // ─── Tool 3: .usmap v4 emitter (jmap-compatible) ───────────────────

    [McpServerTool(Name = "uevr_dump_usmap")]
    [Description("Emit a .usmap mappings file from live reflection, matching jmap's v4 (ExplicitEnumValues) format. Drop next to FModel / CUE4Parse / UAssetAPI for parsing unversioned cooked assets. Encodes recursive property tags (array-of-struct inner-struct names, enum underlying types) and UEnum entries (probed from memory — UE4.26+). Compression defaults to 'none' because jmap's own writer notes FModel/UAssetAPI decompressors are flaky; pass compression='brotli' if your tool supports it. Map/Set inner key-value/element tags are emitted as Unknown because UEVR's public API doesn't expose those wrappers.")]
    public static async Task<string> DumpUsmap(
        [Description("Absolute output path for the .usmap file.")] string outPath,
        [Description("Optional case-insensitive filter on type full names.")] string? filter = null,
        [Description("Compression: 'none' (default, most compatible), 'brotli'.")] string compression = "none")
    {
        using var doc = await FetchReflection(filter, methods: false, enums: true);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            // Surface batch timings if present so the caller can diagnose which slice hung.
            int? batchesCompleted = doc.RootElement.TryGetProperty("batchesCompleted", out var bc) ? bc.GetInt32() : null;
            int? offsetReached    = doc.RootElement.TryGetProperty("offsetReached",   out var ox) ? ox.GetInt32() : null;
            object? timings       = doc.RootElement.TryGetProperty("batchTimings",    out var bt) ? JsonArgs.Parse(bt.GetRawText()) : null;
            return JsonSerializer.Serialize(new {
                ok = false,
                error = "plugin returned error: " + err.ToString(),
                batchesCompleted, offsetReached, batchTimings = timings,
            }, JsonOpts);
        }

        return WriteUsmapFile(doc.RootElement, outPath, compression);
    }

    // Pure function: takes a reflection-dump JSON element and writes a .usmap file.
    // Factored out so the self-test tool can drive it with synthetic data, and so the
    // byte layout is in exactly one place.
    static string WriteUsmapFile(JsonElement root, string outPath, string compression)
    {
        var classesEl = root.TryGetProperty("classes", out var c) ? c : default;
        var structsEl = root.TryGetProperty("structs", out var s) ? s : default;
        var enumsEl   = root.TryGetProperty("enums",   out var e) ? e : default;

        // Name interning — shared across names, enum sections, and schema sections.
        var nameTable = new List<string>();
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int Intern(string? n)
        {
            if (string.IsNullOrEmpty(n)) n = "None";
            if (nameIndex.TryGetValue(n, out var idx)) return idx;
            idx = nameTable.Count;
            nameTable.Add(n);
            nameIndex[n] = idx;
            return idx;
        }
        // jmap-equivalent: u32 0xFFFFFFFF in the name-index slot == Option::None.
        const uint OPT_NAME_NONE = 0xFFFFFFFFu;

        // Match jmap's writer: body parts (enums+structs) are written first to a scratch buffer;
        // the names section (which interns all referenced names as a side-effect) is then
        // prepended. This is how the reader layout ends up: [names, enums, structs].
        using var bodyStream = new MemoryStream();
        using var bodyBw = new BinaryWriter(bodyStream, Encoding.UTF8, leaveOpen: true);

        // ── Enums ──
        int enumCount = enumsEl.ValueKind == JsonValueKind.Array ? enumsEl.GetArrayLength() : 0;
        bodyBw.Write((uint)enumCount);
        if (enumCount > 0)
        {
            foreach (var en in enumsEl.EnumerateArray())
            {
                var enumName = en.GetProperty("name").GetString();
                bodyBw.Write((uint)Intern(enumName));
                JsonElement entries = default;
                int entryCount = en.TryGetProperty("entries", out entries) && entries.ValueKind == JsonValueKind.Array
                    ? entries.GetArrayLength() : 0;
                bodyBw.Write((ushort)entryCount); // v>=LargeEnums ⇒ u16
                if (entryCount > 0)
                {
                    string enumPrefix = (enumName ?? "") + "::";
                    foreach (var entry in entries.EnumerateArray())
                    {
                        long value = entry.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                            ? v.GetInt64() : 0;
                        bodyBw.Write((long)value);  // v>=ExplicitEnumValues ⇒ i64 before name
                        // UE's enum entry FNames usually embed the enum name ("ETokenRule::None").
                        // FModel / CUE4Parse / jmap output expect the prefix stripped.
                        var entryName = entry.GetProperty("name").GetString() ?? "";
                        if (enumPrefix.Length > 2 && entryName.StartsWith(enumPrefix, StringComparison.Ordinal))
                            entryName = entryName.Substring(enumPrefix.Length);
                        bodyBw.Write((uint)Intern(entryName));
                    }
                }
            }
        }

        // ── Structs (plus classes — USMAP doesn't distinguish) ──
        var schemaList = new List<JsonElement>();
        if (classesEl.ValueKind == JsonValueKind.Array) schemaList.AddRange(classesEl.EnumerateArray());
        if (structsEl.ValueKind == JsonValueKind.Array) schemaList.AddRange(structsEl.EnumerateArray());

        bodyBw.Write((uint)schemaList.Count);
        foreach (var t in schemaList)
        {
            bodyBw.Write((uint)Intern(t.GetProperty("name").GetString()));
            if (t.TryGetProperty("super", out var sp) && !string.IsNullOrEmpty(sp.GetString()))
                bodyBw.Write((uint)Intern(sp.GetString()));
            else
                bodyBw.Write(OPT_NAME_NONE);

            // Plugin now emits own-fields only (post-refactor). Defend against legacy payloads
            // anyway by filtering with owner == name when owner is populated.
            var ownName = t.GetProperty("name").GetString();
            var ownFields = new List<JsonElement>();
            if (t.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fs.EnumerateArray())
                {
                    var owner = f.TryGetProperty("owner", out var ow) ? ow.GetString() : null;
                    if (!string.IsNullOrEmpty(owner) && owner != ownName) continue;
                    ownFields.Add(f);
                }
            }

            ushort propCount = (ushort)ownFields.Count;             // sum(arrayDim) — we don't expose arrayDim so 1 per prop
            ushort serializablePropCount = (ushort)ownFields.Count;
            bodyBw.Write(propCount);
            bodyBw.Write(serializablePropCount);

            for (int i = 0; i < ownFields.Count; i++)
            {
                var f = ownFields[i];
                bodyBw.Write((ushort)i);    // schema-relative index
                bodyBw.Write((byte)1);      // arrayDim — UEVR public API doesn't expose FProperty::ArrayDim
                bodyBw.Write((uint)Intern(f.GetProperty("name").GetString()));

                // Prefer the recursive `tag` object (new plugin output). Fall back to flat
                // fields (structType/innerType/enumType) for older plugin versions.
                if (f.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.Object)
                    WriteTag(bodyBw, tagEl, Intern);
                else
                    WriteTagFromFlatField(bodyBw, f, Intern);
            }
        }

        // ── Assemble full decompressed body: [names | enums | structs] ──
        bodyBw.Flush();
        byte[] bodyContentBytes = bodyStream.ToArray();

        using var fullStream = new MemoryStream();
        using var fullBw = new BinaryWriter(fullStream, Encoding.UTF8, leaveOpen: true);
        fullBw.Write((uint)nameTable.Count);
        foreach (var n in nameTable)
        {
            var b = Encoding.UTF8.GetBytes(n);
            if (b.Length > 0xFFFF) { Array.Resize(ref b, 0xFFFF); }
            fullBw.Write((ushort)b.Length);  // v>=LongFName ⇒ u16
            fullBw.Write(b);
        }
        fullBw.Write(bodyContentBytes);
        fullBw.Flush();
        byte[] fullBuffer = fullStream.ToArray();

        // ── Compression ──
        byte compressionMethod = 0;
        byte[] writeBytes = fullBuffer;
        if (compression.Equals("brotli", StringComparison.OrdinalIgnoreCase))
        {
            using var comp = new MemoryStream();
            using (var br = new System.IO.Compression.BrotliStream(comp, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                br.Write(fullBuffer, 0, fullBuffer.Length);
            writeBytes = comp.ToArray();
            compressionMethod = 2;
        }

        // ── Header + file ──
        const byte USMAP_V_EXPLICIT_ENUM_VALUES = 4;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
        long bytesWritten;
        using (var outStream = File.Create(outPath))
        using (var outBw = new BinaryWriter(outStream))
        {
            outBw.Write((ushort)0x30C4);                      // magic
            outBw.Write((byte)USMAP_V_EXPLICIT_ENUM_VALUES);  // version 4
            outBw.Write((int)0);                              // has_versioning = 0 (v>=PackageVersioning)
            outBw.Write(compressionMethod);                   // 0 = None, 2 = Brotli
            outBw.Write((uint)writeBytes.Length);             // compressedSize
            outBw.Write((uint)fullBuffer.Length);             // decompressedSize
            outBw.Write(writeBytes);
            outBw.Flush();
            bytesWritten = outStream.Length;
        }

        return Ok(new {
            outPath = Path.GetFullPath(outPath),
            bytes = bytesWritten,
            version = "ExplicitEnumValues (4)",
            compression = compressionMethod == 0 ? "none" : "brotli",
            nameCount = nameTable.Count,
            schemaCount = schemaList.Count,
            enumCount = enumCount,
        });
    }

    // ─── uevr_dump_usmap_selftest ──────────────────────────────────────
    //
    // Drives the USMAP writer with a synthetic reflection payload — no running
    // game needed. Produces a file you can pipe through uevr_validate_usmap to
    // confirm the byte layout matches jmap's reader end-to-end.

    [McpServerTool(Name = "uevr_dump_usmap_selftest")]
    [Description("Write a tiny synthetic .usmap using the same serializer as uevr_dump_usmap. No game required — exercises every tag variant (struct, array-of-struct, enum with entries, map with Unknown inners, bool). Pair with uevr_validate_usmap to round-trip through jmap as a byte-level sanity check.")]
    public static string DumpUsmapSelfTest(
        [Description("Output .usmap path.")] string outPath,
        [Description("Compression: 'none' or 'brotli'.")] string compression = "none")
    {
        // A minimal reflection dump covering the common property-tag shapes.
        const string synth = /*lang=json,strict*/ """
        {
          "enums": [
            {
              "name": "EMySize",
              "fullName": "Enum /Game/EMySize",
              "entries": [
                { "name": "Small",  "value": 0 },
                { "name": "Medium", "value": 1 },
                { "name": "Large",  "value": 2 }
              ]
            }
          ],
          "structs": [
            {
              "name": "FVector2D",
              "fullName": "ScriptStruct /Script/CoreUObject.Vector2D",
              "fields": [
                { "name": "X", "type": "FloatProperty", "offset": 0, "owner": "FVector2D", "tag": { "type": "FloatProperty" } },
                { "name": "Y", "type": "FloatProperty", "offset": 4, "owner": "FVector2D", "tag": { "type": "FloatProperty" } }
              ]
            }
          ],
          "classes": [
            {
              "name": "MyTestActor",
              "fullName": "Class /Game/MyTestActor",
              "super": "Actor",
              "fields": [
                { "name": "Health",   "type": "IntProperty",    "offset": 0,  "owner": "MyTestActor", "tag": { "type": "IntProperty" } },
                { "name": "IsDead",   "type": "BoolProperty",   "offset": 4,  "owner": "MyTestActor", "tag": { "type": "BoolProperty", "fieldSize": 1, "byteOffset": 0, "byteMask": 1, "fieldMask": 1 } },
                { "name": "Name",     "type": "NameProperty",   "offset": 8,  "owner": "MyTestActor", "tag": { "type": "NameProperty" } },
                { "name": "Pos",      "type": "StructProperty", "offset": 16, "owner": "MyTestActor", "tag": { "type": "StructProperty", "structName": "FVector2D" } },
                { "name": "History",  "type": "ArrayProperty",  "offset": 24, "owner": "MyTestActor", "tag": { "type": "ArrayProperty", "inner": { "type": "StructProperty", "structName": "FVector2D" } } },
                { "name": "Size",     "type": "EnumProperty",   "offset": 40, "owner": "MyTestActor", "tag": { "type": "EnumProperty", "enumName": "EMySize", "inner": { "type": "ByteProperty" } } },
                { "name": "LookupMap","type": "MapProperty",    "offset": 48, "owner": "MyTestActor", "tag": { "type": "MapProperty" } }
              ]
            }
          ],
          "stats": { "synthetic": true }
        }
        """;

        using var doc = JsonDocument.Parse(synth);
        return WriteUsmapFile(doc.RootElement, outPath, compression);
    }

    // ─── USMAP property tag serialization ──────────────────────────────

    // EPropertyType numbering — mirrors jmap/UnrealMappingsDumper.
    const byte T_ByteProperty = 0, T_BoolProperty = 1, T_IntProperty = 2, T_FloatProperty = 3,
               T_ObjectProperty = 4, T_NameProperty = 5, T_DelegateProperty = 6, T_DoubleProperty = 7,
               T_ArrayProperty = 8, T_StructProperty = 9, T_StrProperty = 10, T_TextProperty = 11,
               T_InterfaceProperty = 12, T_MulticastDelegateProperty = 13, T_WeakObjectProperty = 14,
               T_LazyObjectProperty = 15, T_AssetObjectProperty = 16, T_SoftObjectProperty = 17,
               T_UInt64Property = 18, T_UInt32Property = 19, T_UInt16Property = 20,
               T_Int64Property = 21, T_Int16Property = 22, T_Int8Property = 23,
               T_MapProperty = 24, T_SetProperty = 25, T_EnumProperty = 26, T_FieldPathProperty = 27,
               T_OptionalProperty = 28, T_Utf8StrProperty = 29, T_AnsiStrProperty = 30,
               T_Unknown = 0xFF;

    static byte TypeEnumFor(string? propType) => propType switch
    {
        "ByteProperty"             => T_ByteProperty,
        "BoolProperty"             => T_BoolProperty,
        "IntProperty"              => T_IntProperty,
        "FloatProperty"            => T_FloatProperty,
        "ObjectProperty"           => T_ObjectProperty,
        "NameProperty"             => T_NameProperty,
        "DelegateProperty"         => T_DelegateProperty,
        "DoubleProperty"           => T_DoubleProperty,
        "ArrayProperty"            => T_ArrayProperty,
        "StructProperty"           => T_StructProperty,
        "StrProperty"              => T_StrProperty,
        "TextProperty"             => T_TextProperty,
        "InterfaceProperty"        => T_InterfaceProperty,
        "MulticastDelegateProperty" or "MulticastInlineDelegateProperty" or "MulticastSparseDelegateProperty"
                                   => T_MulticastDelegateProperty,
        "WeakObjectProperty"       => T_WeakObjectProperty,
        "LazyObjectProperty"       => T_LazyObjectProperty,
        "AssetObjectProperty"      => T_AssetObjectProperty,
        "SoftObjectProperty" or "SoftClassProperty"
                                   => T_SoftObjectProperty,
        "UInt64Property"           => T_UInt64Property,
        "UInt32Property"           => T_UInt32Property,
        "UInt16Property"           => T_UInt16Property,
        "Int64Property"            => T_Int64Property,
        "Int16Property"            => T_Int16Property,
        "Int8Property"             => T_Int8Property,
        "MapProperty"              => T_MapProperty,
        "SetProperty"              => T_SetProperty,
        "EnumProperty"             => T_EnumProperty,
        "FieldPathProperty"        => T_FieldPathProperty,
        "OptionalProperty"         => T_OptionalProperty,
        "Utf8StrProperty"          => T_Utf8StrProperty,
        "AnsiStrProperty"          => T_AnsiStrProperty,
        "ClassProperty"            => T_ObjectProperty,
        _                          => T_Unknown,
    };

    // Writes a property tag from the recursive `tag` JSON object emitted by the updated plugin.
    // Structure mirrors jmap::write_property_inner.
    static void WriteTag(BinaryWriter bw, JsonElement tag, Func<string?, int> intern)
    {
        var type = tag.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        var t = TypeEnumFor(type);
        bw.Write(t);

        switch (t)
        {
            case T_ArrayProperty:
            case T_SetProperty:
            case T_OptionalProperty:
                if (tag.TryGetProperty("inner", out var inner) && inner.ValueKind == JsonValueKind.Object)
                    WriteTag(bw, inner, intern);
                else
                    bw.Write(T_Unknown);
                break;

            case T_StructProperty:
                bw.Write((uint)intern(tag.TryGetProperty("structName", out var sn) ? sn.GetString() : null));
                break;

            case T_MapProperty:
                // Plugin now resolves FMapProperty::KeyProp / ValueProp via raw-
                // memory probe (Phase 1). Emit real inner tags when present.
                if (tag.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object)
                    WriteTag(bw, key, intern);
                else
                    bw.Write(T_Unknown);
                if (tag.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Object)
                    WriteTag(bw, val, intern);
                else
                    bw.Write(T_Unknown);
                break;

            case T_EnumProperty:
                if (tag.TryGetProperty("inner", out var eInner) && eInner.ValueKind == JsonValueKind.Object)
                    WriteTag(bw, eInner, intern);
                else
                    bw.Write(T_ByteProperty);
                bw.Write((uint)intern(tag.TryGetProperty("enumName", out var en) ? en.GetString() : null));
                break;
        }
    }

    // Render a C++ type string from a recursive tag. Uses Phase 1 probe output
    // to emit concrete class names (`AMyActor*`, `TMap<FName, FVector>`) instead
    // of `void*` fallbacks.
    static string RenderCppFromTag(JsonElement tag)
    {
        var type = tag.TryGetProperty("type", out var ty) ? ty.GetString() ?? "Unknown" : "Unknown";
        switch (type)
        {
            case "ArrayProperty":
                return "TArray<" + (tag.TryGetProperty("inner", out var ai) && ai.ValueKind == JsonValueKind.Object
                    ? RenderCppFromTag(ai) : "uint8_t") + ">";
            case "SetProperty":
                return "TSet<" + (tag.TryGetProperty("inner", out var si) && si.ValueKind == JsonValueKind.Object
                    ? RenderCppFromTag(si) : "uint8_t") + ">";
            case "OptionalProperty":
                return "TOptional<" + (tag.TryGetProperty("inner", out var oi) && oi.ValueKind == JsonValueKind.Object
                    ? RenderCppFromTag(oi) : "uint8_t") + ">";
            case "MapProperty":
            {
                string kt = tag.TryGetProperty("key",   out var k) && k.ValueKind == JsonValueKind.Object ? RenderCppFromTag(k) : "void*";
                string vt = tag.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object ? RenderCppFromTag(v) : "void*";
                return $"TMap<{kt}, {vt}>";
            }
            case "StructProperty":
            {
                var sn = tag.TryGetProperty("structName", out var snEl) ? snEl.GetString() : null;
                return string.IsNullOrEmpty(sn) ? "uint8_t[] /*struct*/" : "F" + SanitizeIdent(sn!);
            }
            case "EnumProperty":
            {
                var en = tag.TryGetProperty("enumName", out var enEl) ? enEl.GetString() : null;
                return string.IsNullOrEmpty(en) ? "uint8_t /*enum*/" : SanitizeIdent(en!);
            }
            case "ObjectProperty":
            case "WeakObjectProperty":
            case "LazyObjectProperty":
            case "SoftObjectProperty":
            case "AssetObjectProperty":
            {
                var pc = tag.TryGetProperty("propertyClass", out var pcEl) ? pcEl.GetString() : null;
                if (string.IsNullOrEmpty(pc)) return "void* /*UObject*/";
                // UE prefix convention: 'A' for Actor-descended classes, 'U' for
                // everything else. Resolved via ThreadStatic super-map from the
                // enclosing DumpSdkCpp call.
                return $"{UePrefix(pc!)}{SanitizeIdent(pc!)}* ";
            }
            case "ClassProperty":
            case "SoftClassProperty":
            {
                var mc = tag.TryGetProperty("metaClass", out var mcEl) ? mcEl.GetString() : null;
                return string.IsNullOrEmpty(mc)
                    ? "UClass* "
                    : $"TSubclassOf<{UePrefix(mc!)}{SanitizeIdent(mc!)}>";
            }
            case "InterfaceProperty":
            {
                var ic = tag.TryGetProperty("interfaceClass", out var icEl) ? icEl.GetString() : null;
                // Interfaces always take the 'I' prefix by UE convention.
                return string.IsNullOrEmpty(ic) ? "void* /*interface*/" : $"TScriptInterface<I{SanitizeIdent(ic!)}>";
            }
            case "DelegateProperty":
            case "MulticastDelegateProperty":
            case "MulticastInlineDelegateProperty":
            case "MulticastSparseDelegateProperty":
                return "void* /*delegate*/";
            default:
                return CppTypeFor(type);
        }
    }

    // Fallback for older plugin payloads (no `tag` object, only flat fields).
    static void WriteTagFromFlatField(BinaryWriter bw, JsonElement f, Func<string?, int> intern)
    {
        var type = f.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        var t = TypeEnumFor(type);
        bw.Write(t);

        switch (t)
        {
            case T_StructProperty:
                bw.Write((uint)intern(f.TryGetProperty("structType", out var st) ? st.GetString() : null));
                break;
            case T_ArrayProperty:
            case T_SetProperty:
                var innerTypeStr = f.TryGetProperty("innerType", out var it) ? it.GetString() : "Unknown";
                var innerTag = TypeEnumFor(innerTypeStr);
                bw.Write(innerTag);
                if (innerTag == T_StructProperty) bw.Write((uint)intern(null));
                else if (innerTag == T_EnumProperty) { bw.Write(T_ByteProperty); bw.Write((uint)intern(null)); }
                break;
            case T_MapProperty:
                bw.Write(T_Unknown); bw.Write(T_Unknown); break;
            case T_EnumProperty:
                bw.Write(T_ByteProperty);
                bw.Write((uint)intern(f.TryGetProperty("enumType", out var et) ? et.GetString() : null));
                break;
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────

    static string SanitizeIdent(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    // Plain-type C++ rendering (no tag context). Conservative defaults.
    static string CppTypeFor(string propType) => propType switch
    {
        "BoolProperty"    => "bool",
        "ByteProperty"    => "uint8_t",
        "Int8Property"    => "int8_t",
        "Int16Property"   => "int16_t",
        "UInt16Property"  => "uint16_t",
        "IntProperty"     => "int32_t",
        "UInt32Property"  => "uint32_t",
        "Int64Property"   => "int64_t",
        "UInt64Property"  => "uint64_t",
        "FloatProperty"   => "float",
        "DoubleProperty"  => "double",
        "NameProperty"    => "FName",
        "StrProperty"     => "FString",
        "TextProperty"    => "FText",
        "ObjectProperty"
            or "ClassProperty"
            or "WeakObjectProperty"
            or "LazyObjectProperty"
            or "SoftObjectProperty"
            or "AssetObjectProperty"
            or "InterfaceProperty" => "void* /*UObject*/",
        "ArrayProperty"   => "TArray<uint8_t>",
        "MapProperty"     => "TMap<void*, void*>",
        "SetProperty"     => "TSet<void*>",
        "StructProperty"  => "uint8_t[] /*struct*/",
        "EnumProperty"    => "uint8_t /*enum*/",
        "DelegateProperty" or "MulticastDelegateProperty"
            or "MulticastInlineDelegateProperty"
            or "MulticastSparseDelegateProperty" => "void* /*delegate*/",
        "FieldPathProperty" => "void* /*FieldPath*/",
        _                 => "uint8_t /*unknown*/",
    };

    // Tag-aware rendering — produces TArray<FMyStruct>, TSet<int32_t>, etc. by recursing
    // into the nested `tag` object emitted by the plugin.
    static string CppTypeForTag(JsonElement tag)
    {
        var type = tag.TryGetProperty("type", out var ty) ? ty.GetString() ?? "Unknown" : "Unknown";
        switch (type)
        {
            case "ArrayProperty":
                return "TArray<" + InnerCpp(tag) + ">";
            case "SetProperty":
                return "TSet<" + InnerCpp(tag) + ">";
            case "OptionalProperty":
                return "TOptional<" + InnerCpp(tag) + ">";
            case "MapProperty":
                // Key/value tags aren't populated (no UEVR wrappers) — fall back to generic.
                return "TMap<void*, void*>";
            case "StructProperty":
                var sn = tag.TryGetProperty("structName", out var snEl) ? snEl.GetString() : null;
                return string.IsNullOrEmpty(sn) ? "uint8_t[] /*struct*/" : SanitizeIdent(sn!);
            case "EnumProperty":
                var en = tag.TryGetProperty("enumName", out var enEl) ? enEl.GetString() : null;
                return string.IsNullOrEmpty(en) ? "uint8_t /*enum*/" : SanitizeIdent(en!);
            default:
                return CppTypeFor(type);
        }
    }

    static string InnerCpp(JsonElement tag)
    {
        if (tag.TryGetProperty("inner", out var inner) && inner.ValueKind == JsonValueKind.Object)
            return CppTypeForTag(inner);
        return "uint8_t /*unknown inner*/";
    }

    static string ExtraFromTag(JsonElement tag)
    {
        if (!tag.TryGetProperty("type", out var ty)) return "";
        var type = ty.GetString();
        if (type == "BoolProperty" && tag.TryGetProperty("fieldMask", out var fm))
            return $" /*bit mask=0x{fm.GetUInt32():X} size={tag.GetProperty("fieldSize").GetUInt32()}*/";
        if (type == "StructProperty" && tag.TryGetProperty("structName", out var sn))
            return " /*struct " + sn.GetString() + "*/";
        if (type == "EnumProperty" && tag.TryGetProperty("enumName", out var en))
            return " /*enum " + en.GetString() + "*/";
        // Phase 1: probe-derived concrete class names.
        if ((type == "ObjectProperty" || type == "WeakObjectProperty" || type == "LazyObjectProperty"
           || type == "SoftObjectProperty" || type == "AssetObjectProperty")
            && tag.TryGetProperty("propertyClass", out var pc))
            return " /*-> " + pc.GetString() + "*/";
        if ((type == "ClassProperty" || type == "SoftClassProperty")
            && tag.TryGetProperty("metaClass", out var mc))
            return " /*TSubclassOf<" + mc.GetString() + ">*/";
        if (type == "InterfaceProperty" && tag.TryGetProperty("interfaceClass", out var ic))
            return " /*I" + ic.GetString() + "*/";
        if ((type == "DelegateProperty" || type == "MulticastDelegateProperty"
          || type == "MulticastInlineDelegateProperty" || type == "MulticastSparseDelegateProperty")
            && tag.TryGetProperty("signatureFunction", out var sf))
            return " /*sig " + sf.GetString() + "*/";
        if ((type == "ArrayProperty" || type == "SetProperty" || type == "OptionalProperty")
             && tag.TryGetProperty("inner", out var inner))
        {
            var it = inner.TryGetProperty("type", out var itt) ? itt.GetString() : "Unknown";
            return $" /*inner {it}*/";
        }
        if (type == "MapProperty"
            && (tag.TryGetProperty("key", out var kt) || tag.TryGetProperty("value", out _)))
        {
            var kType = tag.TryGetProperty("key",   out var kEl) && kEl.TryGetProperty("type", out var kt2) ? kt2.GetString() : "?";
            var vType = tag.TryGetProperty("value", out var vEl) && vEl.TryGetProperty("type", out var vt2) ? vt2.GetString() : "?";
            return $" /*key={kType}, value={vType}*/";
        }
        return "";
    }

    static string ExtraFromFlat(JsonElement f) =>
        f.TryGetProperty("structType", out var st) ? " /*struct " + st.GetString() + "*/"
      : f.TryGetProperty("innerType",  out var it) ? " /*inner "  + it.GetString() + "*/"
      : f.TryGetProperty("enumType",   out var et) ? " /*enum "   + et.GetString() + "*/"
      : "";

    static string Ok(object payload) => JsonSerializer.Serialize(new { ok = true, data = payload }, JsonOpts);
    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, JsonOpts);
}
