using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// "USMAP → full UHT project" converter. Takes any valid USMAP file — whether
/// produced by our own uevr_dump_usmap, by Dumper-7, or by jmap_dumper — and
/// drives the same UHT emitter + project scaffolder that uevr_dump_uht_sdk /
/// uevr_dump_ue_project use against live reflection.
///
/// Why: on fragile games (DX12 AAA titles where UEVR's stereo render hook
/// crashes the process), Dumper-7 reliably produces a USMAP without any D3D
/// hooking. We can take that USMAP and still generate a buildable UE4/UE5
/// project, just without the UEVR-only extras (property offsets, ClassFlags,
/// UFUNCTION flags, interfaces — USMAP doesn't carry those fields).
///
/// Shelling to jmap's usmap CLI for the parse keeps the format handling in
/// the single well-tested place (jmap/usmap crate).
/// </summary>
[McpServerToolType]
public static class UsmapSdkTools
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── Parse USMAP via jmap ──────────────────────────────────────────

    static string? FindUsmapCli()
    {
        var env = Environment.GetEnvironmentVariable("USMAP_CLI");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in path)
        {
            var c = Path.Combine(d, "usmap.exe");
            if (File.Exists(c)) return c;
        }
        // Common local fallback — the jmap repo's built binary on this box.
        var local = @"E:\Github\jmap\target\release\usmap.exe";
        if (File.Exists(local)) return local;
        return null;
    }

    static JsonDocument ParseUsmap(string usmapPath)
    {
        var cli = FindUsmapCli()
            ?? throw new InvalidOperationException(
                "usmap CLI not found. Set $USMAP_CLI or build jmap's usmap crate: " +
                "`cargo build --release -p usmap --manifest-path E:/Github/jmap/Cargo.toml`.");

        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("to-json");
        psi.ArgumentList.Add(usmapPath);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start usmap CLI");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"usmap CLI exited {p.ExitCode}: {stderr}");

        return JsonDocument.Parse(stdout);
    }

    // ─── jmap USMAP JSON → our reflection schema ───────────────────────
    //
    // jmap emits:   {structs: {Name: {super_struct, properties: [{name, array_dim, index, inner}]}}, enums: {Name: {entries: {value: entryName}}}}
    // we consume:   {classes: [...], structs: [...], enums: [{name, entries: [{name, value}]}]}
    //
    // USMAP doesn't distinguish UClass from UScriptStruct — it's all one schema
    // array. We pick Class vs Struct by name prefix ('A' or 'U' for classes,
    // 'F' for structs, plus a falls-back-to-struct rule).

    static JsonDocument AdaptJmapToReflection(JsonElement jmapRoot,
        Dictionary<string, Dictionary<string, int>>? offsetMap = null)
    {
        var classes = new List<object>();
        var structs = new List<object>();
        var enums = new List<object>();

        bool LooksLikeClass(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            char c0 = name[0];
            // UE convention: A* actors, U* uobjects, I* interfaces -> UClass
            // F* structs, E* enums -> non-class. Stretch heuristic for game names
            // that don't follow convention: names ending in "Component" / "Actor"
            // / "Controller" / "Manager" / etc. are almost always classes.
            if (c0 == 'F' || c0 == 'E') return false;
            if (c0 == 'A' || c0 == 'U' || c0 == 'I') return true;
            if (name.EndsWith("Component", StringComparison.Ordinal)) return true;
            if (name.EndsWith("Actor",     StringComparison.Ordinal)) return true;
            if (name.EndsWith("Controller",StringComparison.Ordinal)) return true;
            if (name.EndsWith("Manager",   StringComparison.Ordinal)) return true;
            if (name.EndsWith("Settings",  StringComparison.Ordinal)) return true;
            // Default to struct — minimizes bad-cast fallout in generated headers.
            return false;
        }

        if (jmapRoot.TryGetProperty("structs", out var jmapStructs)
            && jmapStructs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in jmapStructs.EnumerateObject())
            {
                var name = p.Name;
                var body = p.Value;
                var super = body.TryGetProperty("super_struct", out var s) && s.ValueKind != JsonValueKind.Null
                    ? s.GetString() : null;

                var fields = new List<object>();
                if (body.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fp in props.EnumerateArray())
                    {
                        var fname = fp.TryGetProperty("name", out var fn) ? fn.GetString() : null;
                        if (string.IsNullOrEmpty(fname)) continue;
                        var innerRaw = fp.GetProperty("inner");
                        var tag = InnerToTag(innerRaw);
                        string typeName = tag.TryGetValue("type", out var t) && t is string ts ? ts : "Unknown";
                        // Try to backfill offset from Dumper-7's text dump. Look up by
                        // the class's own name or the "A"-prefixed variant (Dumper-7
                        // uses C++ prefixes where jmap's USMAP uses stripped names).
                        int offset = 0;
                        if (offsetMap is not null)
                        {
                            foreach (var key in new[] { name, "A" + name, "U" + name, "F" + name, "E" + name })
                            {
                                if (offsetMap.TryGetValue(key, out var classOffsets)
                                    && classOffsets.TryGetValue(fname, out var found))
                                {
                                    offset = found;
                                    break;
                                }
                            }
                        }
                        fields.Add(new {
                            name  = fname,
                            type  = typeName,
                            offset,
                            owner = name,
                            tag   = tag,
                        });
                    }
                }

                var entry = new {
                    name,
                    fullName = $"Class /Script/Unknown.{name}", // USMAP lacks package origin
                    super,
                    fields,
                };
                if (LooksLikeClass(name)) classes.Add(entry);
                else structs.Add(entry);
            }

            // Second pass: demote classes whose super is a known STRUCT (either
            // another USMAP struct or a hardcoded engine struct). USMAP doesn't
            // distinguish class vs struct and our first-pass heuristic (name
            // prefix + common suffixes) misclassifies game-specific names like
            // `ActRow` (Act = act of the game, not A-prefix for actor) as
            // classes even when they inherit from FTableRowBase (a struct).
            // Iterate until stable — demotion cascades.
            var structNames = new HashSet<string>(structs.Select(x => (string)x.GetType().GetProperty("name")!.GetValue(x)!), StringComparer.Ordinal);
            // Known engine structs (canonical, no F-prefix) that game code
            // commonly inherits from. Keeps demotion working even when the
            // engine super doesn't appear in the USMAP.
            var engineStructSupers = new HashSet<string>(new[] {
                "TableRowBase", "InputActionKeyMapping", "InputAxisKeyMapping",
                "DataTableRowHandle", "SoftObjectPath", "SoftClassPath",
                "GameplayTag", "GameplayTagContainer", "GameplayAbilityActorInfo",
                "AttributeCapture", "Vector", "Vector2D", "Vector4", "Rotator",
                "Quat", "Transform", "Color", "LinearColor", "IntPoint",
                "IntVector", "Box", "Box2D", "Sphere", "Plane", "Matrix",
            }, StringComparer.Ordinal);
            bool StructSuper(string? s)
            {
                if (string.IsNullOrEmpty(s)) return false;
                return structNames.Contains(s!) || engineStructSupers.Contains(s!);
            }
            bool anyDemoted = true;
            while (anyDemoted)
            {
                anyDemoted = false;
                for (int i = classes.Count - 1; i >= 0; i--)
                {
                    var c = classes[i];
                    var supr = (string?)c.GetType().GetProperty("super")!.GetValue(c);
                    if (StructSuper(supr))
                    {
                        structs.Add(c);
                        structNames.Add((string)c.GetType().GetProperty("name")!.GetValue(c)!);
                        classes.RemoveAt(i);
                        anyDemoted = true;
                    }
                }
            }

            // Third pass: demote classes that are referenced as StructProperty
            // by any field. USMAP property tags carry "structName" — a
            // definitive type signal that outranks the name heuristic. E.g.
            // `AreaDelay` reads like a class (A-prefix) but if any field holds
            // `TArray<FAreaDelay>` (StructProperty→structName=AreaDelay), the
            // real type is a struct. Walk all collected types' field tags,
            // collect referenced struct names, demote any class matching.
            var referencedStructNames = new HashSet<string>(StringComparer.Ordinal);
            void CollectStructRefs(System.Text.Json.JsonElement tag)
            {
                if (tag.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                if (tag.TryGetProperty("type", out var tEl) && tEl.GetString() == "StructProperty"
                    && tag.TryGetProperty("structName", out var sEl) && sEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    referencedStructNames.Add(sEl.GetString()!);
                if (tag.TryGetProperty("inner", out var inEl)) CollectStructRefs(inEl);
                if (tag.TryGetProperty("key", out var keyEl))  CollectStructRefs(keyEl);
                if (tag.TryGetProperty("value", out var vEl))  CollectStructRefs(vEl);
            }
            // The entries carry `fields` as List<object> — we built them from
            // Dictionary<string, object?> wrapped in anonymous objects. Walk
            // back through jmapStructs directly to re-read the inner tags.
            foreach (var p in jmapStructs.EnumerateObject())
            {
                if (!p.Value.TryGetProperty("properties", out var props)
                    || props.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var fp in props.EnumerateArray())
                {
                    if (!fp.TryGetProperty("inner", out var inner)) continue;
                    // Re-serialize the Dictionary-shape our adapter produces
                    // by calling InnerToTag. But we only need struct names —
                    // walk the raw jmap shape recursively for "Struct": {name}.
                    void WalkRaw(System.Text.Json.JsonElement el)
                    {
                        if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foreach (var kv in el.EnumerateObject())
                            {
                                if (kv.Name == "Struct" && kv.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                                    && kv.Value.TryGetProperty("name", out var n)
                                    && n.ValueKind == System.Text.Json.JsonValueKind.String)
                                    referencedStructNames.Add(n.GetString()!);
                                WalkRaw(kv.Value);
                            }
                        }
                        else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var c in el.EnumerateArray()) WalkRaw(c);
                    }
                    WalkRaw(inner);
                }
            }
            bool anyDemoted2 = true;
            while (anyDemoted2)
            {
                anyDemoted2 = false;
                for (int i = classes.Count - 1; i >= 0; i--)
                {
                    var c = classes[i];
                    var nm = (string)c.GetType().GetProperty("name")!.GetValue(c)!;
                    if (referencedStructNames.Contains(nm))
                    {
                        structs.Add(c);
                        structNames.Add(nm);
                        classes.RemoveAt(i);
                        anyDemoted2 = true;
                    }
                }
            }
            // Cascade: also demote classes whose super is now a struct.
            bool anyDemoted3 = true;
            while (anyDemoted3)
            {
                anyDemoted3 = false;
                for (int i = classes.Count - 1; i >= 0; i--)
                {
                    var c = classes[i];
                    var supr = (string?)c.GetType().GetProperty("super")!.GetValue(c);
                    if (StructSuper(supr))
                    {
                        structs.Add(c);
                        structNames.Add((string)c.GetType().GetProperty("name")!.GetValue(c)!);
                        classes.RemoveAt(i);
                        anyDemoted3 = true;
                    }
                }
            }
        }

        if (jmapRoot.TryGetProperty("enums", out var jmapEnums)
            && jmapEnums.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in jmapEnums.EnumerateObject())
            {
                var name = p.Name;
                var body = p.Value;
                var entries = new List<object>();
                if (body.TryGetProperty("entries", out var entriesEl)
                    && entriesEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var e in entriesEl.EnumerateObject())
                    {
                        if (!long.TryParse(e.Name, out var value)) continue;
                        entries.Add(new { name = e.Value.GetString(), value });
                    }
                }
                enums.Add(new { name, fullName = $"Enum /Script/Unknown.{name}", entries });
            }
        }

        // Serialize with our expected schema.
        var shaped = new
        {
            classes,
            structs,
            enums,
            stats = new {
                totalScanned = classes.Count + structs.Count + enums.Count,
                totalMatched = classes.Count + structs.Count + enums.Count,
                classCount = classes.Count,
                structCount = structs.Count,
                enumCount = enums.Count,
                objectErrors = 0,
                source = "usmap",
            },
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(shaped));
    }

    // Convert jmap's inner property tag (strings + nested objects) into the
    // object-shape our emitter already consumes. Cases mirror jmap's
    // PropertyInner enum.
    static Dictionary<string, object?> InnerToTag(JsonElement inner)
    {
        // Scalar string forms: "Int", "Bool", "Name", ...
        if (inner.ValueKind == JsonValueKind.String)
        {
            return new Dictionary<string, object?> {
                ["type"] = ScalarToPropertyType(inner.GetString()!)
            };
        }
        // Object forms: {"Array": {"inner": ...}}, {"Struct": {"name": ...}}, ...
        if (inner.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in inner.EnumerateObject())
            {
                var kind = kv.Name;
                var body = kv.Value;
                switch (kind)
                {
                    case "Array": return new Dictionary<string, object?> {
                        ["type"] = "ArrayProperty",
                        ["inner"] = InnerToTag(body.GetProperty("inner"))
                    };
                    case "Set": return new Dictionary<string, object?> {
                        ["type"] = "SetProperty",
                        ["inner"] = InnerToTag(body.GetProperty("key"))
                    };
                    case "Map": return new Dictionary<string, object?> {
                        ["type"] = "MapProperty",
                        ["key"]   = InnerToTag(body.GetProperty("key")),
                        ["value"] = InnerToTag(body.GetProperty("value"))
                    };
                    case "Struct": return new Dictionary<string, object?> {
                        ["type"] = "StructProperty",
                        ["structName"] = body.GetProperty("name").GetString()
                    };
                    case "Enum": return new Dictionary<string, object?> {
                        ["type"] = "EnumProperty",
                        ["enumName"] = body.GetProperty("name").GetString(),
                        ["inner"] = InnerToTag(body.GetProperty("inner"))
                    };
                    case "Optional": return new Dictionary<string, object?> {
                        ["type"] = "OptionalProperty",
                        ["inner"] = InnerToTag(body.GetProperty("inner"))
                    };
                }
            }
        }
        return new Dictionary<string, object?> { ["type"] = "Unknown" };
    }

    static string ScalarToPropertyType(string s) => s switch
    {
        "Byte"              => "ByteProperty",
        "Bool"              => "BoolProperty",
        "Int"               => "IntProperty",
        "Float"             => "FloatProperty",
        "Object"            => "ObjectProperty",
        "Name"              => "NameProperty",
        "Delegate"          => "DelegateProperty",
        "Double"            => "DoubleProperty",
        "Str"               => "StrProperty",
        "Text"              => "TextProperty",
        "Interface"         => "InterfaceProperty",
        "MulticastDelegate" => "MulticastDelegateProperty",
        "WeakObject"        => "WeakObjectProperty",
        "LazyObject"        => "LazyObjectProperty",
        "AssetObject"       => "AssetObjectProperty",
        "SoftObject"        => "SoftObjectProperty",
        "UInt64"            => "UInt64Property",
        "UInt32"            => "UInt32Property",
        "UInt16"            => "UInt16Property",
        "Int64"             => "Int64Property",
        "Int16"             => "Int16Property",
        "Int8"              => "Int8Property",
        "FieldPath"         => "FieldPathProperty",
        "Utf8Str"           => "Utf8StrProperty",
        "AnsiStr"           => "AnsiStrProperty",
        _                   => "Unknown",
    };

    // ─── Tool: USMAP → UHT project ─────────────────────────────────────

    [McpServerTool(Name = "uevr_dump_uht_from_usmap")]
    [Description("Convert any USMAP (Dumper-7, jmap, or our uevr_dump_usmap output) into a full UHT-style UE4/UE5 project scaffold. Shells to jmap's usmap CLI to parse, then runs the same UHT emitter + project generator used by uevr_dump_ue_project against live reflection. Use this when UEVR injection is unsafe and you've already dumped a USMAP via Dumper-7. Pass `gObjectsPath` pointing at Dumper-7's `GObjects-Dump-WithProperties.txt` to backfill real per-property offsets into the output (closes USMAP's biggest quality gap). Output matches uevr_dump_ue_project but without the UEVR-only fields (UCLASS/UFUNCTION flags, interface lists, ClassConfigName).")]
    public static string DumpUhtFromUsmap(
        [Description("Absolute path to the input .usmap file.")] string usmapPath,
        [Description("Absolute output directory for the generated project.")] string outDir,
        [Description("Project name for the .uproject / Target.cs. Defaults to the usmap file's base name.")] string? projectName = null,
        [Description("Module name to place all types in (USMAP doesn't carry package origin). Default 'SDK'.")] string moduleName = "SDK",
        [Description("Engine association written into .uproject (default '4.26').")] string engineAssociation = "4.26",
        [Description("Optional path to Dumper-7's GObjects-Dump-WithProperties.txt — if provided, per-property offsets are backfilled from it (USMAP itself doesn't carry memory offsets). Big quality win for the fallback path.")] string? gObjectsPath = null)
    {
        if (!File.Exists(usmapPath))
            return JsonSerializer.Serialize(new { ok = false, error = $"usmap not found: {usmapPath}" }, JsonOpts);

        // Optional offset map from Dumper-7's text dump.
        Dictionary<string, Dictionary<string, int>>? offsetMap = null;
        if (!string.IsNullOrEmpty(gObjectsPath) && File.Exists(gObjectsPath))
        {
            try { offsetMap = ParseDumper7Offsets(gObjectsPath); }
            catch { /* best effort; absence is not fatal */ }
        }

        JsonDocument reflection;
        try
        {
            using var jmapDoc = ParseUsmap(usmapPath);
            reflection = AdaptJmapToReflection(jmapDoc.RootElement, offsetMap);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOpts);
        }

        using (reflection)
        {
            projectName ??= Path.GetFileNameWithoutExtension(usmapPath);
            return UhtSdkTools.EmitUhtProjectFromReflection(reflection.RootElement,
                outDir, projectName, moduleName, engineAssociation);
        }
    }

    // ─── Dumper-7 offset parser ────────────────────────────────────────
    //
    // Dumper-7 writes GObjects-Dump-WithProperties.txt with tab-indented
    // property lines directly under each class header:
    //
    //   [00000000] {0x1a168620} Package CoreUObject
    //   [00000001] {0xace4300} Class CoreUObject.Object
    //   [00000028] {0x73bf3260}	ClassProperty NativeClass
    //
    // The [offset] bracket is the memory offset of that property inside its
    // containing UStruct. Parse into a className → {propName → offset} map
    // so the adapter can inject real offsets into the USMAP-derived fields.

    static readonly Regex ReClassHeader = new(
        @"^\[[\da-fA-F]+\]\s*\{0x[\da-fA-F]+\}\s*(?:Class|ScriptStruct)\s+[\w/]+\.(\w+)",
        RegexOptions.Compiled);
    static readonly Regex RePropertyLine = new(
        @"^\s*\[([\da-fA-F]+)\]\s*\{0x[\da-fA-F]+\}\s*\w+Property\s+(\w+)",
        RegexOptions.Compiled);

    static Dictionary<string, Dictionary<string, int>> ParseDumper7Offsets(string path)
    {
        var map = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        string? currentClass = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] != '[' && line[0] != ' ' && line[0] != '\t') continue;

            // Class/struct header at column 0.
            if (line.Length > 0 && line[0] == '[')
            {
                var hm = ReClassHeader.Match(line);
                if (hm.Success)
                {
                    currentClass = hm.Groups[1].Value;
                    continue;
                }
                // Fall through — could be package/enum line we don't care about.
            }

            // Tab-indented property line.
            if (currentClass is null) continue;
            var pm = RePropertyLine.Match(line);
            if (!pm.Success) continue;
            if (!int.TryParse(pm.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int offset))
                continue;
            if (!map.TryGetValue(currentClass, out var fields))
                map[currentClass] = fields = new Dictionary<string, int>(StringComparer.Ordinal);
            fields[pm.Groups[2].Value] = offset;
        }
        return map;
    }
}
