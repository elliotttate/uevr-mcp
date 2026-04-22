using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

[McpServerToolType]
public static class ReverseEngineeringTools
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    enum ReKind
    {
        Class,
        Struct,
        Enum,
    }

    sealed record ReField(
        string Name,
        int Offset,
        ulong PropertyFlags,
        string FlatType,
        JsonElement Tag);

    sealed record ReType(
        string Path,
        string RawName,
        string FullName,
        ReKind Kind,
        int Size,
        int Alignment,
        string? SuperPath,
        ulong ClassFlags,
        List<ReField> Fields,
        List<(string Name, long Value)> EnumEntries);

    sealed class BundleModel
    {
        public Dictionary<string, ReType> TypesByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AliasToPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> RawSuperByName { get; } = new(StringComparer.Ordinal);
    }

    static readonly HashSet<string> ActorBases = new(StringComparer.Ordinal)
    {
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
        "Light", "ALight",
        "PointLight", "APointLight",
        "SpotLight", "ASpotLight",
        "DirectionalLight", "ADirectionalLight",
        "CameraActor", "ACameraActor",
        "TriggerBox", "ATriggerBox",
        "NavigationData", "ANavigationData",
        "Brush", "ABrush",
    };

    const ulong CastClassActor = 0x0000001000000000UL;

    [McpServerTool(Name = "uevr_dump_bn_ida_bundle")]
    [Description("Write a reverse-engineering bundle for Binary Ninja and IDA Pro. The bundle contains a Binary Ninja-friendly `.jmap` export, a plain C/C++ header for local type import, import scripts for both tools, and a short README. The `.jmap` is designed for jmap/ue_binja-style class and struct reconstruction; the header/import scripts preserve field offsets and fall back to opaque byte arrays when live reflection does not expose a field's exact storage layout.")]
    public static async Task<string> DumpBinaryNinjaIdaBundle(
        [Description("Absolute output directory for the bundle. Created if missing; existing files may be overwritten.")] string outDir,
        [Description("Optional case-insensitive filter on type full names. Use this sparingly — referenced dependency types are not auto-expanded.")] string? filter = null,
        [Description("Pretty-print the emitted `.jmap` JSON (default true).")] bool pretty = true)
    {
        using var doc = await DumpTools.FetchReflectionPublic(filter, methods: false, enums: true);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            int? batchesCompleted = doc.RootElement.TryGetProperty("batchesCompleted", out var bc) ? bc.GetInt32() : null;
            int? offsetReached = doc.RootElement.TryGetProperty("offsetReached", out var ox) ? ox.GetInt32() : null;
            object? timings = doc.RootElement.TryGetProperty("batchTimings", out var bt) ? JsonArgs.Parse(bt.GetRawText()) : null;
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "plugin returned error: " + err,
                batchesCompleted,
                offsetReached,
                batchTimings = timings,
            }, JsonOpts);
        }

        var model = BuildModel(doc.RootElement);
        if (model.TypesByPath.Count == 0)
            return Err("reflection dump contained no classes, structs, or enums");

        Directory.CreateDirectory(outDir);

        var bundleDir = Path.GetFullPath(outDir);
        var jmapPath = Path.Combine(bundleDir, "uevr_types.jmap");
        var headerPath = Path.Combine(bundleDir, "uevr_types.hpp");
        var idaScriptPath = Path.Combine(bundleDir, "import_ida_types.py");
        var bnScriptPath = Path.Combine(bundleDir, "import_binary_ninja_types.py");
        var readmePath = Path.Combine(bundleDir, "README.txt");

        var jmapJson = JsonSerializer.Serialize(BuildJmap(model), new JsonSerializerOptions
        {
            WriteIndented = pretty,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        await File.WriteAllTextAsync(jmapPath, jmapJson, Encoding.UTF8);
        await File.WriteAllTextAsync(headerPath, BuildHeader(model), Encoding.UTF8);
        await File.WriteAllTextAsync(idaScriptPath, BuildIdaImporter(Path.GetFileName(headerPath)), Encoding.UTF8);
        await File.WriteAllTextAsync(bnScriptPath, BuildBinaryNinjaImporter(Path.GetFileName(headerPath)), Encoding.UTF8);
        await File.WriteAllTextAsync(readmePath, BuildReadme(
            Path.GetFileName(jmapPath),
            Path.GetFileName(headerPath),
            Path.GetFileName(idaScriptPath),
            Path.GetFileName(bnScriptPath)), Encoding.UTF8);

        int classCount = model.TypesByPath.Values.Count(t => t.Kind == ReKind.Class);
        int structCount = model.TypesByPath.Values.Count(t => t.Kind == ReKind.Struct);
        int enumCount = model.TypesByPath.Values.Count(t => t.Kind == ReKind.Enum);

        return Ok(new
        {
            outDir = bundleDir,
            jmapPath,
            headerPath,
            idaScriptPath,
            binaryNinjaScriptPath = bnScriptPath,
            readmePath,
            classCount,
            structCount,
            enumCount,
        });
    }

    static BundleModel BuildModel(JsonElement root)
    {
        var model = new BundleModel();

        void AddAliases(ReType type)
        {
            var raw = type.RawName;
            var display = DisplayName(type, model.RawSuperByName);
            foreach (var alias in EnumerateAliases(raw, display, type.Kind))
            {
                if (!model.AliasToPath.ContainsKey(alias))
                    model.AliasToPath[alias] = type.Path;
            }
        }

        if (root.TryGetProperty("classes", out var classes) && classes.ValueKind == JsonValueKind.Array)
        {
            foreach (var cls in classes.EnumerateArray())
            {
                var fullName = cls.GetProperty("fullName").GetString() ?? "";
                var path = NormalizeObjectPath(fullName);
                if (string.IsNullOrEmpty(path)) continue;

                var rawName = cls.GetProperty("name").GetString() ?? Path.GetFileName(path);
                var superPath = ResolvePathFromNameOrFullName(
                    cls.TryGetProperty("super", out var sp) ? sp.GetString() : null,
                    model.AliasToPath);
                if (!string.IsNullOrEmpty(superPath))
                    model.RawSuperByName[rawName] = Path.GetFileNameWithoutExtension(superPath);

                var size = cls.TryGetProperty("propertiesSize", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 0;
                var align = cls.TryGetProperty("minAlignment", out var ma) && ma.ValueKind == JsonValueKind.Number
                    ? ma.GetInt32()
                    : InferAlignment(size);

                model.TypesByPath[path] = new ReType(
                    path,
                    rawName,
                    fullName,
                    ReKind.Class,
                    size,
                    align,
                    superPath,
                    ReadUInt64(cls, "classFlags"),
                    ReadFields(cls),
                    new());
            }
        }

        if (root.TryGetProperty("structs", out var structs) && structs.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in structs.EnumerateArray())
            {
                var fullName = st.GetProperty("fullName").GetString() ?? "";
                var path = NormalizeObjectPath(fullName);
                if (string.IsNullOrEmpty(path)) continue;

                var rawName = st.GetProperty("name").GetString() ?? Path.GetFileName(path);
                var superPath = ResolvePathFromNameOrFullName(
                    st.TryGetProperty("super", out var sp) ? sp.GetString() : null,
                    model.AliasToPath);
                var size = st.TryGetProperty("propertiesSize", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 0;
                var align = st.TryGetProperty("minAlignment", out var ma) && ma.ValueKind == JsonValueKind.Number
                    ? ma.GetInt32()
                    : InferAlignment(size);

                model.TypesByPath[path] = new ReType(
                    path,
                    rawName,
                    fullName,
                    ReKind.Struct,
                    size,
                    align,
                    superPath,
                    0,
                    ReadFields(st),
                    new());
            }
        }

        if (root.TryGetProperty("enums", out var enums) && enums.ValueKind == JsonValueKind.Array)
        {
            foreach (var en in enums.EnumerateArray())
            {
                var fullName = en.GetProperty("fullName").GetString() ?? "";
                var path = NormalizeObjectPath(fullName);
                if (string.IsNullOrEmpty(path)) continue;

                var rawName = en.GetProperty("name").GetString() ?? Path.GetFileName(path);
                var entries = new List<(string Name, long Value)>();
                if (en.TryGetProperty("entries", out var ee) && ee.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in ee.EnumerateArray())
                    {
                        var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "Unnamed" : "Unnamed";
                        var value = entry.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                        entries.Add((name, value));
                    }
                }

                model.TypesByPath[path] = new ReType(
                    path,
                    rawName,
                    fullName,
                    ReKind.Enum,
                    GuessEnumSize(entries),
                    GuessEnumSize(entries),
                    null,
                    0,
                    new(),
                    entries);
            }
        }

        foreach (var type in model.TypesByPath.Values.OrderBy(t => t.Path, StringComparer.Ordinal))
            AddAliases(type);

        return model;
    }

    static List<ReField> ReadFields(JsonElement typeElement)
    {
        var fields = new List<ReField>();
        if (!typeElement.TryGetProperty("fields", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return fields;

        foreach (var field in arr.EnumerateArray())
        {
            var name = field.TryGetProperty("name", out var n) ? n.GetString() ?? "unnamed" : "unnamed";
            var offset = field.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
            var propertyFlags = ReadUInt64(field, "propertyFlags");
            var flatType = field.TryGetProperty("type", out var ft) ? ft.GetString() ?? "Unknown" : "Unknown";
            var tag = field.TryGetProperty("tag", out var tg) && tg.ValueKind == JsonValueKind.Object
                ? tg.Clone()
                : JsonDocument.Parse("""{"type":"Unknown"}""").RootElement.Clone();

            fields.Add(new ReField(name, offset, propertyFlags, flatType, tag));
        }

        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return fields;
    }

    static object BuildJmap(BundleModel model)
    {
        var objects = new SortedDictionary<string, object>(StringComparer.Ordinal);

        foreach (var type in model.TypesByPath.Values.OrderBy(t => t.Path, StringComparer.Ordinal))
        {
            objects[type.Path] = type.Kind switch
            {
                ReKind.Class => BuildJmapClass(type, model),
                ReKind.Struct => BuildJmapStruct(type, model),
                ReKind.Enum => BuildJmapEnum(type),
                _ => throw new InvalidOperationException("unexpected type kind"),
            };
        }

        return new
        {
            metadata = new
            {
                tool = "UEVR-MCP",
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                source = "live reflection",
                engine_version = new { major = 0, minor = 0 }
            },
            image_base_address = "0x0",
            objects,
            vtables = new Dictionary<string, object?>()
        };
    }

    static object BuildJmapClass(ReType type, BundleModel model)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "Class",
            ["address"] = "0x0",
            ["vtable"] = "0x0",
            ["object_flags"] = 0,
            ["outer"] = OuterPath(type.Path),
            ["class"] = "/Script/CoreUObject.Class",
            ["children"] = Array.Empty<string>(),
            ["property_values"] = new Dictionary<string, object?>(),
            ["super_struct"] = type.SuperPath,
            ["properties"] = type.Fields.Select(f => BuildJmapProperty(type, f, model)).ToList(),
            ["properties_size"] = type.Size,
            ["min_alignment"] = type.Alignment,
            ["script"] = "",
            ["class_flags"] = type.ClassFlags,
            ["class_cast_flags"] = IsActorDerived(type, model.RawSuperByName) ? CastClassActor : 0UL,
            ["class_default_object"] = null,
            ["instance_vtable"] = null,
        };
    }

    static object BuildJmapStruct(ReType type, BundleModel model)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "ScriptStruct",
            ["address"] = "0x0",
            ["vtable"] = "0x0",
            ["object_flags"] = 0,
            ["outer"] = OuterPath(type.Path),
            ["class"] = "/Script/CoreUObject.ScriptStruct",
            ["children"] = Array.Empty<string>(),
            ["property_values"] = new Dictionary<string, object?>(),
            ["super_struct"] = type.SuperPath,
            ["properties"] = type.Fields.Select(f => BuildJmapProperty(type, f, model)).ToList(),
            ["properties_size"] = type.Size,
            ["min_alignment"] = type.Alignment,
            ["script"] = "",
            ["struct_flags"] = 0,
        };
    }

    static object BuildJmapEnum(ReType type)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "Enum",
            ["address"] = "0x0",
            ["vtable"] = "0x0",
            ["object_flags"] = 0,
            ["outer"] = OuterPath(type.Path),
            ["class"] = "/Script/CoreUObject.Enum",
            ["children"] = Array.Empty<string>(),
            ["property_values"] = new Dictionary<string, object?>(),
            ["cpp_type"] = type.Size > 1 ? "uint32" : "uint8",
            ["enum_flags"] = null,
            ["cpp_form"] = "EnumClass",
            ["names"] = type.EnumEntries.Select(e => new object[] { e.Name, e.Value }).ToList(),
        };
    }

    static Dictionary<string, object?> BuildJmapProperty(ReType owner, ReField field, BundleModel model)
    {
        int size = GuessFieldSize(field, owner, model, null);
        var prop = new Dictionary<string, object?>
        {
            ["address"] = "0x0",
            ["name"] = field.Name,
            ["offset"] = field.Offset,
            ["array_dim"] = 1,
            ["size"] = Math.Max(size, 0),
            ["flags"] = field.PropertyFlags
        };

        EmitPropertyType(prop, field.Tag, field.FlatType, model);
        return prop;
    }

    static void EmitPropertyType(Dictionary<string, object?> dst, JsonElement tag, string flatType, BundleModel model)
    {
        string type = tag.TryGetProperty("type", out var tt) ? tt.GetString() ?? flatType : flatType;
        switch (type)
        {
            case "StructProperty":
                dst["type"] = "StructProperty";
                dst["struct"] = ResolvePathFromNameOrFullName(
                    tag.TryGetProperty("structName", out var sn) ? sn.GetString() : null,
                    model.AliasToPath) ?? UnknownObjectPath(tag.TryGetProperty("structName", out sn) ? sn.GetString() : "UnknownStruct");
                break;
            case "StrProperty":
            case "NameProperty":
            case "TextProperty":
            case "FloatProperty":
            case "DoubleProperty":
            case "ByteProperty":
            case "UInt16Property":
            case "UInt32Property":
            case "UInt64Property":
            case "Int8Property":
            case "Int16Property":
            case "IntProperty":
            case "Int64Property":
            case "FieldPathProperty":
            case "FUtf8StrProperty":
            case "AnsiStrProperty":
                dst["type"] = type;
                break;
            case "MulticastInlineDelegateProperty":
            case "MulticastSparseDelegateProperty":
            case "MulticastDelegateProperty":
            case "DelegateProperty":
                dst["type"] = type;
                dst["signature_function"] = ResolvePathFromNameOrFullName(
                    tag.TryGetProperty("signatureFunction", out var sig) ? sig.GetString() : null,
                    model.AliasToPath);
                break;
            case "BoolProperty":
                dst["type"] = "BoolProperty";
                dst["field_size"] = tag.TryGetProperty("fieldSize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt32() : 1;
                dst["byte_offset"] = tag.TryGetProperty("byteOffset", out var bo) && bo.ValueKind == JsonValueKind.Number ? bo.GetInt32() : 0;
                dst["byte_mask"] = tag.TryGetProperty("byteMask", out var bm) && bm.ValueKind == JsonValueKind.Number ? bm.GetInt32() : 1;
                dst["field_mask"] = tag.TryGetProperty("fieldMask", out var fm) && fm.ValueKind == JsonValueKind.Number ? fm.GetInt32() : 1;
                break;
            case "ArrayProperty":
                dst["type"] = "ArrayProperty";
                dst["inner"] = BuildSyntheticNestedProperty(
                    tag.TryGetProperty("inner", out var ai) ? ai : default,
                    model);
                break;
            case "EnumProperty":
                dst["type"] = "EnumProperty";
                dst["container"] = BuildSyntheticNestedProperty(
                    tag.TryGetProperty("inner", out var enumInner) ? enumInner : default,
                    model);
                dst["enum"] = ResolvePathFromNameOrFullName(
                    tag.TryGetProperty("enumName", out var en) ? en.GetString() : null,
                    model.AliasToPath);
                break;
            case "MapProperty":
                dst["type"] = "MapProperty";
                dst["key_prop"] = BuildSyntheticNestedProperty(
                    tag.TryGetProperty("key", out var mk) ? mk : default,
                    model);
                dst["value_prop"] = BuildSyntheticNestedProperty(
                    tag.TryGetProperty("value", out var mv) ? mv : default,
                    model);
                break;
            case "SetProperty":
                dst["type"] = "SetProperty";
                dst["key_prop"] = BuildSyntheticNestedProperty(
                    tag.TryGetProperty("inner", out var si) ? si : default,
                    model);
                break;
            case "OptionalProperty":
                dst["type"] = "OptionalProperty";
                dst["inner"] = BuildSyntheticNestedProperty(
                    tag.TryGetProperty("inner", out var opt) ? opt : default,
                    model);
                break;
            case "ObjectProperty":
            case "WeakObjectProperty":
            case "SoftObjectProperty":
            case "LazyObjectProperty":
            case "ClassProperty":
            case "SoftClassProperty":
                dst["type"] = type;
                dst["property_class"] = ResolvePathFromNameOrFullName(
                    tag.TryGetProperty("propertyClass", out var pc) ? pc.GetString() : null,
                    model.AliasToPath) ?? "/Script/CoreUObject.Object";
                if (type is "ClassProperty" or "SoftClassProperty")
                {
                    dst["meta_class"] = ResolvePathFromNameOrFullName(
                        tag.TryGetProperty("metaClass", out var mc) ? mc.GetString() : null,
                        model.AliasToPath) ?? "/Script/CoreUObject.Object";
                }
                break;
            case "InterfaceProperty":
                dst["type"] = "InterfaceProperty";
                dst["interface_class"] = ResolvePathFromNameOrFullName(
                    tag.TryGetProperty("interfaceClass", out var ic) ? ic.GetString() : null,
                    model.AliasToPath);
                break;
            default:
                dst["type"] = flatType;
                break;
        }
    }

    static Dictionary<string, object?> BuildSyntheticNestedProperty(JsonElement tag, BundleModel model)
    {
        var nested = new Dictionary<string, object?>
        {
            ["address"] = "0x0",
            ["name"] = "__inner",
            ["offset"] = 0,
            ["array_dim"] = 1,
            ["size"] = 0,
            ["flags"] = 0
        };

        if (tag.ValueKind != JsonValueKind.Object)
        {
            nested["type"] = "ByteProperty";
            nested["size"] = 1;
            return nested;
        }

        EmitPropertyType(nested, tag, tag.TryGetProperty("type", out var tt) ? tt.GetString() ?? "ByteProperty" : "ByteProperty", model);
        return nested;
    }

    static string BuildHeader(BundleModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated by uevr_dump_bn_ida_bundle.");
        sb.AppendLine("// This header is intentionally parser-friendly for Binary Ninja and IDA.");
        sb.AppendLine("// When live reflection cannot prove a field's exact storage shape, the field");
        sb.AppendLine("// degrades to an opaque byte array sized from the next known offset.");
        sb.AppendLine("#pragma once");
        sb.AppendLine();
        sb.AppendLine("typedef signed char int8_t;");
        sb.AppendLine("typedef unsigned char uint8_t;");
        sb.AppendLine("typedef short int16_t;");
        sb.AppendLine("typedef unsigned short uint16_t;");
        sb.AppendLine("typedef int int32_t;");
        sb.AppendLine("typedef unsigned int uint32_t;");
        sb.AppendLine("typedef long long int64_t;");
        sb.AppendLine("typedef unsigned long long uint64_t;");
        sb.AppendLine();
        sb.AppendLine("struct FName { uint32_t ComparisonIndex; uint32_t Number; };");
        sb.AppendLine("struct FString { void* Data; int32_t Num; int32_t Max; };");
        sb.AppendLine("struct UE_TArray { void* Data; int32_t Num; int32_t Max; };");
        sb.AppendLine("struct UE_TWeakObjectPtr { int32_t ObjectIndex; int32_t ObjectSerialNumber; };");
        sb.AppendLine("struct UE_TScriptInterface { void* ObjectPointer; void* InterfacePointer; };");
        sb.AppendLine();

        foreach (var enumType in OrderedEnums(model))
            sb.AppendLine($"enum {DisplayName(enumType, model.RawSuperByName)};");
        if (model.TypesByPath.Values.Any(t => t.Kind == ReKind.Enum))
            sb.AppendLine();

        foreach (var type in OrderedStructLikes(model).OrderBy(t => DisplayName(t, model.RawSuperByName), StringComparer.Ordinal))
            sb.AppendLine($"struct {DisplayName(type, model.RawSuperByName)};");
        sb.AppendLine();

        foreach (var enumType in OrderedEnums(model))
        {
            var enumName = DisplayName(enumType, model.RawSuperByName);
            var underlying = enumType.Size > 1 ? "uint32_t" : "uint8_t";
            sb.AppendLine($"enum {enumName} : {underlying} {{");
            if (enumType.EnumEntries.Count == 0)
            {
                sb.AppendLine($"    {enumName}_Unknown = 0");
            }
            else
            {
                for (int i = 0; i < enumType.EnumEntries.Count; i++)
                {
                    var entry = enumType.EnumEntries[i];
                    var comma = i + 1 == enumType.EnumEntries.Count ? "" : ",";
                    sb.AppendLine($"    {SanitizeIdentifier(entry.Name)} = {entry.Value}{comma}");
                }
            }
            sb.AppendLine("};");
            sb.AppendLine();
        }

        foreach (var type in OrderedByDependencies(model))
        {
            var typeName = DisplayName(type, model.RawSuperByName);
            sb.AppendLine($"// {type.FullName}");
            sb.AppendLine($"// Size: 0x{type.Size:X}");
            sb.AppendLine($"struct {typeName}");
            sb.AppendLine("{");

            int cursor = 0;
            if (!string.IsNullOrEmpty(type.SuperPath) && model.TypesByPath.TryGetValue(type.SuperPath, out var superType))
            {
                var superName = DisplayName(superType, model.RawSuperByName);
                sb.AppendLine($"    {superName} __super; // +0x0 base");
                cursor = Math.Max(cursor, superType.Size);
            }

            for (int i = 0; i < type.Fields.Count; i++)
            {
                var field = type.Fields[i];
                int nextOffset = i + 1 < type.Fields.Count ? type.Fields[i + 1].Offset : type.Size;
                if (field.Offset < cursor)
                    continue;

                if (field.Offset > cursor)
                {
                    sb.AppendLine($"    uint8_t _pad_{cursor:X4}[0x{field.Offset - cursor:X}];");
                    cursor = field.Offset;
                }

                var rendered = RenderHeaderField(field, type, nextOffset, model);
                sb.Append("    ").Append(rendered.Declaration).Append("; // +0x")
                    .Append(field.Offset.ToString("X"))
                    .Append(" (").Append(field.FlatType).Append(')');
                if (!string.IsNullOrEmpty(rendered.Comment))
                    sb.Append(' ').Append(rendered.Comment);
                sb.AppendLine();

                cursor = field.Offset + rendered.ConsumedSize;
                if (cursor < nextOffset)
                {
                    sb.AppendLine($"    uint8_t _pad_{cursor:X4}[0x{nextOffset - cursor:X}];");
                    cursor = nextOffset;
                }
            }

            if (type.Size > cursor)
                sb.AppendLine($"    uint8_t _pad_{cursor:X4}[0x{type.Size - cursor:X}];");

            sb.AppendLine("};");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    sealed record FieldRender(string Declaration, int ConsumedSize, string? Comment);

    static FieldRender RenderHeaderField(ReField field, ReType owner, int nextOffset, BundleModel model)
    {
        int delta = Math.Max(nextOffset - field.Offset, 0);
        var tag = field.Tag;
        var typeName = tag.TryGetProperty("type", out var tt) ? tt.GetString() ?? field.FlatType : field.FlatType;
        var actualType = RenderHeaderType(typeName, tag, model, out int? knownSize, out bool trustworthy);
        int consumed = trustworthy && knownSize.HasValue && knownSize.Value > 0 && (delta == 0 || knownSize.Value <= delta)
            ? knownSize.Value
            : delta;

        if (!trustworthy || !knownSize.HasValue || knownSize.Value <= 0 || (delta > 0 && knownSize.Value > delta))
        {
            if (consumed <= 0)
                consumed = Math.Max(GuessFieldSize(field, owner, model, nextOffset), 1);
            return new FieldRender($"uint8_t {SanitizeIdentifier(field.Name)}[0x{consumed:X}]", consumed, $"/* {actualType} */");
        }

        return new FieldRender($"{actualType} {SanitizeIdentifier(field.Name)}", knownSize.Value, null);
    }

    static string RenderHeaderType(string typeName, JsonElement tag, BundleModel model, out int? knownSize, out bool trustworthy)
    {
        trustworthy = true;
        switch (typeName)
        {
            case "BoolProperty":
                knownSize = tag.TryGetProperty("fieldSize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt32() : 1;
                return "uint8_t";
            case "ByteProperty":
            case "Int8Property":
                knownSize = 1;
                return typeName == "Int8Property" ? "int8_t" : "uint8_t";
            case "UInt16Property":
            case "Int16Property":
                knownSize = 2;
                return typeName == "Int16Property" ? "int16_t" : "uint16_t";
            case "UInt32Property":
            case "IntProperty":
                knownSize = 4;
                return typeName == "IntProperty" ? "int32_t" : "uint32_t";
            case "UInt64Property":
            case "Int64Property":
                knownSize = 8;
                return typeName == "Int64Property" ? "int64_t" : "uint64_t";
            case "FloatProperty":
                knownSize = 4;
                return "float";
            case "DoubleProperty":
                knownSize = 8;
                return "double";
            case "NameProperty":
                knownSize = 8;
                return "FName";
            case "StrProperty":
                knownSize = 16;
                return "FString";
            case "TextProperty":
                knownSize = TryResolveBuiltinStructSize("Text", model);
                trustworthy = knownSize.HasValue;
                return "FText";
            case "FieldPathProperty":
                knownSize = TryResolveBuiltinStructSize("FieldPath", model) ?? 32;
                return "FFieldPath";
            case "ArrayProperty":
                knownSize = 16;
                return "UE_TArray";
            case "WeakObjectProperty":
                knownSize = 8;
                return "UE_TWeakObjectPtr";
            case "InterfaceProperty":
                knownSize = 16;
                return "UE_TScriptInterface";
            case "ObjectProperty":
            case "ClassProperty":
                knownSize = 8;
                return ResolveHeaderPointerType(
                    tag.TryGetProperty("propertyClass", out var pc) ? pc.GetString() : null,
                    model);
            case "StructProperty":
                {
                    var path = ResolvePathFromNameOrFullName(
                        tag.TryGetProperty("structName", out var sn) ? sn.GetString() : null,
                        model.AliasToPath);
                    if (path is not null && model.TypesByPath.TryGetValue(path, out var referenced))
                    {
                        knownSize = referenced.Size;
                        return DisplayName(referenced, model.RawSuperByName);
                    }
                    knownSize = null;
                    trustworthy = false;
                    return "UnknownStruct";
                }
            case "EnumProperty":
                {
                    var path = ResolvePathFromNameOrFullName(
                        tag.TryGetProperty("enumName", out var en) ? en.GetString() : null,
                        model.AliasToPath);
                    if (path is not null && model.TypesByPath.TryGetValue(path, out var referenced))
                    {
                        knownSize = referenced.Size;
                        return DisplayName(referenced, model.RawSuperByName);
                    }

                    knownSize = tag.TryGetProperty("inner", out var inner)
                        ? GuessTagSize(inner, null, model, null)
                        : 1;
                    trustworthy = false;
                    return "uint32_t";
                }
            case "SoftObjectProperty":
            case "SoftClassProperty":
            case "LazyObjectProperty":
            case "MulticastInlineDelegateProperty":
            case "MulticastSparseDelegateProperty":
            case "MulticastDelegateProperty":
            case "DelegateProperty":
            case "OptionalProperty":
            case "FUtf8StrProperty":
            case "AnsiStrProperty":
            case "MapProperty":
            case "SetProperty":
                knownSize = null;
                trustworthy = false;
                return typeName;
            default:
                knownSize = null;
                trustworthy = false;
                return typeName;
        }
    }

    static int GuessFieldSize(ReField field, ReType owner, BundleModel model, int? nextOffset)
        => GuessTagSize(field.Tag, owner, model, nextOffset);

    static int GuessTagSize(JsonElement tag, ReType? owner, BundleModel model, int? nextOffset)
    {
        var typeName = tag.TryGetProperty("type", out var tt) ? tt.GetString() ?? "Unknown" : "Unknown";
        return typeName switch
        {
            "BoolProperty" => tag.TryGetProperty("fieldSize", out var fs) && fs.ValueKind == JsonValueKind.Number ? Math.Max(fs.GetInt32(), 1) : 1,
            "ByteProperty" or "Int8Property" => 1,
            "UInt16Property" or "Int16Property" => 2,
            "UInt32Property" or "IntProperty" or "FloatProperty" => 4,
            "UInt64Property" or "Int64Property" or "DoubleProperty" => 8,
            "NameProperty" => 8,
            "StrProperty" => 16,
            "TextProperty" => TryResolveBuiltinStructSize("Text", model) ?? DeltaFallback(owner, nextOffset),
            "FieldPathProperty" => TryResolveBuiltinStructSize("FieldPath", model) ?? 32,
            "ArrayProperty" => 16,
            "WeakObjectProperty" => 8,
            "InterfaceProperty" => 16,
            "ObjectProperty" or "ClassProperty" => 8,
            "StructProperty" => ResolveReferencedSize(
                tag.TryGetProperty("structName", out var sn) ? sn.GetString() : null,
                model),
            "EnumProperty" => tag.TryGetProperty("inner", out var inner)
                ? GuessTagSize(inner, owner, model, nextOffset)
                : 1,
            _ => DeltaFallback(owner, nextOffset)
        };
    }

    static int DeltaFallback(ReType? owner, int? nextOffset)
    {
        if (owner is not null && nextOffset.HasValue)
            return Math.Max(nextOffset.Value, 1);
        return 1;
    }

    static int ResolveReferencedSize(string? nameOrPath, BundleModel model)
    {
        var path = ResolvePathFromNameOrFullName(nameOrPath, model.AliasToPath);
        return path is not null && model.TypesByPath.TryGetValue(path, out var type) ? Math.Max(type.Size, 1) : 0;
    }

    static int? TryResolveBuiltinStructSize(string rawName, BundleModel model)
    {
        var path = ResolvePathFromNameOrFullName(rawName, model.AliasToPath);
        if (path is not null && model.TypesByPath.TryGetValue(path, out var type))
            return Math.Max(type.Size, 1);
        return null;
    }

    static string ResolveHeaderPointerType(string? rawName, BundleModel model)
    {
        var path = ResolvePathFromNameOrFullName(rawName, model.AliasToPath);
        if (path is not null && model.TypesByPath.TryGetValue(path, out var referenced))
            return DisplayName(referenced, model.RawSuperByName) + "*";
        return "void*";
    }

    static IEnumerable<ReType> OrderedEnums(BundleModel model)
        => model.TypesByPath.Values
            .Where(t => t.Kind == ReKind.Enum)
            .OrderBy(t => DisplayName(t, model.RawSuperByName), StringComparer.Ordinal);

    static IEnumerable<ReType> OrderedStructLikes(BundleModel model)
        => model.TypesByPath.Values.Where(t => t.Kind is ReKind.Class or ReKind.Struct);

    static List<ReType> OrderedByDependencies(BundleModel model)
    {
        var structLikes = OrderedStructLikes(model).ToDictionary(t => t.Path, t => t, StringComparer.Ordinal);
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var path in structLikes.Keys)
        {
            incoming[path] = 0;
            edges[path] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var type in structLikes.Values)
        {
            foreach (var dep in EnumerateFullDependencies(type, model))
            {
                if (dep == type.Path || !structLikes.ContainsKey(dep)) continue;
                if (edges[dep].Add(type.Path))
                    incoming[type.Path]++;
            }
        }

        var ready = new SortedSet<string>(incoming.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var ordered = new List<ReType>();

        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            ordered.Add(structLikes[next]);

            foreach (var child in edges[next].OrderBy(x => x, StringComparer.Ordinal))
            {
                incoming[child]--;
                if (incoming[child] == 0)
                    ready.Add(child);
            }
        }

        foreach (var remaining in structLikes.Keys.Except(ordered.Select(t => t.Path), StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            ordered.Add(structLikes[remaining]);

        return ordered;
    }

    static IEnumerable<string> EnumerateFullDependencies(ReType type, BundleModel model)
    {
        if (!string.IsNullOrEmpty(type.SuperPath))
            yield return type.SuperPath;

        foreach (var field in type.Fields)
        {
            var tag = field.Tag;
            var tagType = tag.TryGetProperty("type", out var tt) ? tt.GetString() ?? field.FlatType : field.FlatType;
            if (tagType == "StructProperty")
            {
                var dep = ResolvePathFromNameOrFullName(tag.TryGetProperty("structName", out var sn) ? sn.GetString() : null, model.AliasToPath);
                if (dep is not null) yield return dep;
            }
            else if (tagType == "EnumProperty")
            {
                var dep = ResolvePathFromNameOrFullName(tag.TryGetProperty("enumName", out var en) ? en.GetString() : null, model.AliasToPath);
                if (dep is not null) yield return dep;
            }
        }
    }

    static string BuildIdaImporter(string headerFileName)
    {
        var escaped = headerFileName.Replace("\\", "\\\\").Replace("'", "\\'");
        return $$"""
import os
import ida_kernwin
import idc

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
HEADER_PATH = os.path.join(BASE_DIR, '{{escaped}}')

with open(HEADER_PATH, 'r', encoding='utf-8') as f:
    source = f.read()

flags = idc.PT_TYP | idc.PT_REPLACE
errors = idc.parse_decls(source, flags)

message = f'Imported local types from {HEADER_PATH} with {errors} parse error(s).'
print(message)
try:
    ida_kernwin.info(message)
except Exception:
    pass
""";
    }

    static string BuildBinaryNinjaImporter(string headerFileName)
    {
        var escaped = headerFileName.Replace("\\", "\\\\").Replace("'", "\\'");
        return $$"""
import os
from binaryninja import log_error, log_info

HEADER_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), '{{escaped}}')

def run(bv):
    with open(HEADER_PATH, 'r', encoding='utf-8') as f:
        source = f.read()

    result = bv.parse_types_from_string(source)
    type_defs = [(name, type_obj) for name, type_obj in result.types.items()]
    if not type_defs:
        log_error(f'No types parsed from {HEADER_PATH}')
        return

    bv.define_user_types(type_defs)
    log_info(f'Imported {len(type_defs)} user types from {HEADER_PATH}')

run(bv)
""";
    }

    static string BuildReadme(string jmapFileName, string headerFileName, string idaScriptFileName, string bnScriptFileName)
    {
        return
$@"UEVR reverse-engineering bundle

Files:
  - {jmapFileName}
    Minimal jmap-compatible JSON for Binary Ninja. Intended for trumank/jmap's ue_binja importer.
    Current limitation: live reflection does not expose instance-vtable tables, so this export focuses on class/struct reconstruction rather than vtable/exec symbol recovery.

  - {headerFileName}
    Parser-friendly local type header for IDA Pro and Binary Ninja.
    When the reflection dump cannot prove a field's exact storage type, the field is emitted as an opaque byte array sized from the next known offset.

  - {idaScriptFileName}
    IDAPython helper. Run it from File -> Script file... to import the header into Local Types.

  - {bnScriptFileName}
    Binary Ninja helper. Run it in the scripting console with an open BinaryView (`bv` must exist) to import the header as user types.

Binary Ninja paths:
  1. Preferred: use ue_binja from https://github.com/trumank/jmap and import {jmapFileName}.
  2. Fallback: run {bnScriptFileName} to import the plain header instead.

IDA Pro path:
  1. Open the target database.
  2. Run {idaScriptFileName}.
  3. Review Local Types and apply them manually where needed.
";
    }

    static string DisplayName(ReType type, Dictionary<string, string> rawSuperByName)
    {
        if (type.Kind == ReKind.Enum)
            return EnsurePrefixed(type.RawName, 'E');
        if (type.Kind == ReKind.Struct)
            return EnsurePrefixed(type.RawName, 'F');
        if (IsInterface(type))
            return EnsurePrefixed(type.RawName, 'I');
        return IsActorDerived(type, rawSuperByName)
            ? EnsurePrefixed(type.RawName, 'A')
            : EnsurePrefixed(type.RawName, 'U');
    }

    static bool IsInterface(ReType type)
        => type.Kind == ReKind.Class && string.Equals(type.SuperPath, "/Script/CoreUObject.Interface", StringComparison.Ordinal);

    static bool IsActorDerived(ReType type, Dictionary<string, string> rawSuperByName)
    {
        if (type.Kind != ReKind.Class) return false;
        var current = type.RawName;
        for (int i = 0; i < 64 && !string.IsNullOrEmpty(current); i++)
        {
            if (ActorBases.Contains(current))
                return true;
            if (!rawSuperByName.TryGetValue(current, out current!))
                return false;
        }
        return false;
    }

    static IEnumerable<string> EnumerateAliases(string rawName, string displayName, ReKind kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new List<string>();

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                aliases.Add(value);
        }

        Add(rawName);
        Add(displayName);
        Add(StripSinglePrefix(rawName));
        Add(StripSinglePrefix(displayName));

        if (kind == ReKind.Class)
        {
            var core = StripSinglePrefix(rawName);
            Add("A" + core);
            Add("U" + core);
            Add("I" + core);
        }
        else if (kind == ReKind.Struct)
        {
            Add("F" + StripSinglePrefix(rawName));
        }
        else if (kind == ReKind.Enum)
        {
            Add("E" + StripSinglePrefix(rawName));
        }

        return aliases;
    }

    static string StripSinglePrefix(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        char first = value[0];
        if ((first is 'A' or 'U' or 'I' or 'F' or 'E') && value.Length > 1 && char.IsUpper(value[1]))
            return value.Substring(1);
        return value;
    }

    static string EnsurePrefixed(string value, char prefix)
    {
        value = SanitizeIdentifier(value);
        if (string.IsNullOrEmpty(value)) return prefix.ToString();
        if (value[0] == prefix) return value;
        return prefix + StripSinglePrefix(value);
    }

    static string SanitizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value!)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (!char.IsLetter(sb[0]) && sb[0] != '_')
            sb.Insert(0, '_');
        return sb.ToString();
    }

    static string? ResolvePathFromNameOrFullName(string? nameOrFullName, Dictionary<string, string> aliasToPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrFullName)) return null;
        var normalized = NormalizeObjectPath(nameOrFullName!);
        if (!string.IsNullOrEmpty(normalized))
            return normalized;
        return aliasToPath.TryGetValue(nameOrFullName!, out var path) ? path : null;
    }

    static string NormalizeObjectPath(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        var trimmed = fullName.Trim();
        if (trimmed.StartsWith('/'))
            return trimmed;
        int firstSlash = trimmed.IndexOf('/');
        return firstSlash >= 0 ? trimmed.Substring(firstSlash) : string.Empty;
    }

    static string OuterPath(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot > 0 ? path.Substring(0, dot) : "/Script";
    }

    static string UnknownObjectPath(string? name)
        => "/Script/Unknown." + SanitizeIdentifier(name);

    static int GuessEnumSize(List<(string Name, long Value)> entries)
    {
        if (entries.Count == 0) return 1;
        long min = entries.Min(e => e.Value);
        long max = entries.Max(e => e.Value);
        return min >= byte.MinValue && max <= byte.MaxValue ? 1 : 4;
    }

    static ulong ReadUInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return 0;
        if (prop.TryGetUInt64(out var u)) return u;
        if (prop.TryGetInt64(out var s) && s >= 0) return (ulong)s;
        return 0;
    }

    static int InferAlignment(int size)
    {
        if (size <= 0) return 1;
        if (size % 8 == 0) return 8;
        if (size % 4 == 0) return 4;
        if (size % 2 == 0) return 2;
        return 1;
    }

    static string Ok(object payload) => JsonSerializer.Serialize(new { ok = true, data = payload }, JsonOpts);
    static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, error = msg }, JsonOpts);
}
