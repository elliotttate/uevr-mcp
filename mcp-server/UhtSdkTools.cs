using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace UevrMcp;

/// <summary>
/// UHT-style C++ SDK emitter — UCLASS / USTRUCT / UPROPERTY macros with
/// forward declarations, includes, and decoded property flags. Output matches
/// what jmap_to_uht.py produces, so the generated tree drops into a UE4/UE5
/// editor project as Source/ and compiles through UnrealHeaderTool. Pairs
/// with uevr_dump_sdk_cpp (cast-style offsets, used for runtime workflows).
///
/// Not exhaustive vs jmap — UCLASS() flags and UPROPERTY specifiers are
/// derived from known CPF_* / CLASS_* bits, covering the common subset
/// (EditAnywhere / BlueprintReadWrite / Transient / Config / Replicated /
/// etc.). Custom-UFUNCTION metadata, USTRUCT specifiers, and ExposeOnSpawn
/// refinement can be added incrementally.
/// </summary>
[McpServerToolType]
public static class UhtSdkTools
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── Engine-type exclusion list ───────────────────────────────────
    //
    // USMAPs from cooked games (and Dumper-7's /Script/Unknown.* naming)
    // don't tag package origin, so the emitter can't tell whether a
    // class is game code or just UE's built-in. When we emit e.g. AAIController
    // into the SDK module, UHT fails with "shares engine name" because UE's
    // own AAIController is already in AIModule.
    //
    // This list is the set of engine type names that have historically
    // collided during real emits. Names are matched against the *base* type
    // name as it appears in USMAP (without the A/U/F/E prefix on classes,
    // because the emitter re-prefixes). Kept conservative — add more as
    // compile-check reveals them.
    static readonly HashSet<string> EngineTypes = new(StringComparer.Ordinal)
    {
        // Actors / controllers
        "Actor", "Pawn", "Character", "Controller", "PlayerController", "AIController",
        "GameMode", "GameModeBase", "GameState", "GameStateBase",
        "PlayerState", "HUD", "SpectatorPawn", "DefaultPawn", "CameraActor",
        "StaticMeshActor", "SkeletalMeshActor", "Light", "DirectionalLight",
        "PointLight", "SpotLight", "SkyLight", "TriggerBox", "TriggerSphere",
        "PlayerStart", "PlayerCameraManager",
        // Components
        "ActorComponent", "SceneComponent", "PrimitiveComponent", "StaticMeshComponent",
        "SkeletalMeshComponent", "MeshComponent", "InstancedStaticMeshComponent",
        "HierarchicalInstancedStaticMeshComponent", "CameraComponent",
        "AudioComponent", "LightComponent", "PointLightComponent", "DirectionalLightComponent",
        "SpotLightComponent", "BoxComponent", "SphereComponent", "CapsuleComponent",
        "ShapeComponent", "CharacterMovementComponent", "ProjectileMovementComponent",
        "MovementComponent", "PawnMovementComponent", "NavMovementComponent",
        "ArrowComponent", "BillboardComponent", "DecalComponent", "ParticleSystemComponent",
        "NiagaraComponent", "ChildActorComponent", "RotatingMovementComponent",
        "SplineComponent", "InputComponent",
        // Anim
        "AnimInstance", "AnimBlueprintGeneratedClass", "AnimSequence", "AnimMontage",
        "BlendSpace", "SkeletalMesh",
        // Object / Engine core
        "Object", "Package", "Class", "Struct", "Function", "Interface",
        "AssetManager", "Engine", "GameEngine", "World", "Level", "GameInstance",
        "SaveGame", "DataAsset", "DataTable", "CurveTable", "Blueprint",
        "BlueprintGeneratedClass", "UserDefinedEnum", "UserDefinedStruct",
        "Texture", "Texture2D", "TextureCube", "Material", "MaterialInterface",
        "MaterialInstance", "MaterialInstanceConstant", "MaterialInstanceDynamic",
        "StaticMesh", "PhysicalMaterial", "SoundBase", "SoundCue", "SoundWave",
        // Widgets
        "UserWidget", "Widget", "PanelWidget", "ContentWidget", "WidgetTree",
        "WidgetBlueprintGeneratedClass",
        // Struct collisions (from UHT: "Class 'UA2Pose' shares engine name 'A2Pose'
        // with struct 'FA2Pose'")
        "2Pose", "2CSPose", "TimerHandle", "Vector", "Vector2D", "Vector4",
        "Rotator", "Quat", "Transform", "Color", "LinearColor", "IntPoint",
        "IntVector", "Box", "Box2D", "Sphere", "Plane", "Matrix",
    };

    static bool IsEngineType(string name)
    {
        // Try raw name first — USMAP may already have stripped the prefix
        // (e.g. "A2Pose" where the leading A is part of the name, not a prefix).
        if (EngineTypes.Contains(name)) return true;
        // Then try stripping a single prefix letter if the next char is upper
        // (AAIController → AIController) or a digit (A2Pose → 2Pose — some
        // engine structs like FA2Pose live under this shape).
        if (name.Length >= 2 && (name[0] == 'A' || name[0] == 'U' || name[0] == 'F' || name[0] == 'E')
            && (char.IsUpper(name[1]) || char.IsDigit(name[1])))
            if (EngineTypes.Contains(name.Substring(1))) return true;
        return false;
    }

    // ─── CLASS_* flag bits (UE4.22+/UE5) ──────────────────────────────
    [Flags]
    enum CLASS : uint
    {
        Abstract                = 0x00000001,
        DefaultConfig           = 0x00000002,
        Config                  = 0x00000004,
        Transient               = 0x00000008,
        ProjectUserConfig       = 0x00000040,
        Native                  = 0x00000080,
        NotPlaceable            = 0x00000200,
        PerObjectConfig         = 0x00000400,
        EditInlineNew           = 0x00001000,
        CollapseCategories      = 0x00002000,
        Interface               = 0x00004000,
        Const                   = 0x00010000,
        CompiledFromBlueprint   = 0x00040000,
        MinimalAPI              = 0x00080000,
        DefaultToInstanced      = 0x00200000,
        Hidden                  = 0x01000000,
        Deprecated              = 0x02000000,
        HideDropDown            = 0x04000000,
        GlobalUserConfig        = 0x08000000,
    }

    // ─── FUNC_* flag bits ──────────────────────────────────────────────
    [Flags]
    enum FUNC : uint
    {
        Final                  = 0x00000001,
        RequiredAPI            = 0x00000002,
        BlueprintAuthorityOnly = 0x00000004,
        BlueprintCosmetic      = 0x00000008,
        Net                    = 0x00000040,
        NetReliable            = 0x00000080,
        Exec                   = 0x00000200,
        Native                 = 0x00000400,
        Event                  = 0x00000800,
        Static                 = 0x00002000,
        NetMulticast           = 0x00004000,
        NetServer              = 0x00200000,
        NetClient              = 0x01000000,
        BlueprintCallable      = 0x04000000,
        BlueprintEvent         = 0x08000000,
        BlueprintPure          = 0x10000000,
        EditorOnly             = 0x20000000,
        Const                  = 0x40000000,
    }

    // ─── CPF_* flag bits ────────────────────────────────────────────────
    // Stable across UE4.22–UE5.4 (new flags appended, existing flags unchanged).
    [Flags]
    enum CPF : ulong
    {
        Edit                        = 0x0000000000000001,
        ConstParm                   = 0x0000000000000002,
        BlueprintVisible            = 0x0000000000000004,
        ExportObject                = 0x0000000000000008,
        BlueprintReadOnly           = 0x0000000000000010,
        Net                         = 0x0000000000000020,
        EditFixedSize               = 0x0000000000000040,
        Parm                        = 0x0000000000000080,
        OutParm                     = 0x0000000000000100,
        ReturnParm                  = 0x0000000000000400,
        DisableEditOnTemplate       = 0x0000000000000800,
        Transient                   = 0x0000000000002000,
        Config                      = 0x0000000000004000,
        DisableEditOnInstance       = 0x0000000000010000,
        EditConst                   = 0x0000000000020000,
        GlobalConfig                = 0x0000000000040000,
        InstancedReference          = 0x0000000000080000,
        DuplicateTransient          = 0x0000000000200000,
        SaveGame                    = 0x0000000001000000,
        NoClear                     = 0x0000000002000000,
        ReferenceParm               = 0x0000000008000000,
        BlueprintAssignable         = 0x0000000010000000,
        Deprecated                  = 0x0000000020000000,
        RepSkip                     = 0x0000000080000000,
        RepNotify                   = 0x0000000100000000,
        Interp                      = 0x0000000200000000,
        NonTransactional            = 0x0000000400000000,
        EditorOnly                  = 0x0000000800000000,
        AssetRegistrySearchable     = 0x0000010000000000,
        SimpleDisplay               = 0x0000020000000000,
        AdvancedDisplay             = 0x0000040000000000,
        Protected                   = 0x0000080000000000,
        BlueprintCallable           = 0x0000100000000000,
        ExposeOnSpawn               = 0x0001000000000000,
    }

    // ─── UPROPERTY specifier derivation ────────────────────────────────
    //
    // Mirrors what UE's own UHT emits. Visibility is "Visible*" for const-ish
    // fields and "Edit*" otherwise; similarly BlueprintReadOnly vs ReadWrite
    // depends on BPReadOnly plus BPVisible.

    static string UPropertySpecifier(CPF flags)
    {
        var parts = new List<string>();

        bool edit         = (flags & CPF.Edit) != 0;
        bool editConst    = (flags & CPF.EditConst) != 0;
        bool bpVisible    = (flags & CPF.BlueprintVisible) != 0;
        bool bpReadOnly   = (flags & CPF.BlueprintReadOnly) != 0;
        bool hideOnTmpl   = (flags & CPF.DisableEditOnTemplate) != 0;
        bool hideOnInst   = (flags & CPF.DisableEditOnInstance) != 0;

        {
            // Always emit a visibility specifier so UHT has something to work with —
            // jmap does the same. Editable fields get Edit*, const-editable get
            // Visible*, everything else defaults to VisibleAnywhere.
            string scope = (hideOnInst && !hideOnTmpl) ? "DefaultsOnly"
                         : (!hideOnInst && hideOnTmpl) ? "InstanceOnly"
                         : "Anywhere";
            bool visibleOnly = editConst || !edit;
            parts.Add((visibleOnly ? "VisibleAnywhere" : "EditAnywhere")
                .Replace("Anywhere", scope));
        }

        if (bpVisible)
            parts.Add(bpReadOnly ? "BlueprintReadOnly" : "BlueprintReadWrite");

        if ((flags & CPF.Transient)             != 0) parts.Add("Transient");
        if ((flags & CPF.GlobalConfig)          != 0) parts.Add("GlobalConfig");
        else if ((flags & CPF.Config)           != 0) parts.Add("Config");
        if ((flags & CPF.SaveGame)              != 0) parts.Add("SaveGame");
        if ((flags & CPF.Interp)                != 0) parts.Add("Interp");
        if ((flags & CPF.EditorOnly)            != 0) parts.Add("EditorOnly");
        if ((flags & CPF.AdvancedDisplay)       != 0) parts.Add("AdvancedDisplay");
        else if ((flags & CPF.SimpleDisplay)    != 0) parts.Add("SimpleDisplay");
        if ((flags & CPF.ExposeOnSpawn)         != 0) parts.Add("ExposeOnSpawn");
        if ((flags & CPF.BlueprintAssignable)   != 0) parts.Add("BlueprintAssignable");
        if ((flags & CPF.BlueprintCallable)     != 0) parts.Add("BlueprintCallable");
        if ((flags & CPF.Net)                   != 0)
            parts.Add((flags & CPF.RepNotify) != 0 ? "ReplicatedUsing" : "Replicated");
        if ((flags & CPF.AssetRegistrySearchable) != 0) parts.Add("AssetRegistrySearchable");
        if ((flags & CPF.Deprecated)            != 0) parts.Add("Deprecated");

        return parts.Count == 0 ? "UPROPERTY()" : "UPROPERTY(" + string.Join(", ", parts) + ")";
    }

    // ─── UCLASS specifier derivation ───────────────────────────────────
    //
    // Builds "UCLASS(Abstract, BlueprintType, Config=Game, ...)" from the
    // raw ClassFlags bits plus ClassConfigName/ClassWithin/interfaces.

    static string UClassSpecifier(CLASS flags, string? configName)
    {
        var parts = new List<string>();
        if ((flags & CLASS.Abstract)             != 0) parts.Add("Abstract");
        if ((flags & CLASS.Deprecated)           != 0) parts.Add("Deprecated");
        if ((flags & CLASS.Hidden)               != 0) parts.Add("Hidden");
        if ((flags & CLASS.HideDropDown)         != 0) parts.Add("HideDropDown");
        if ((flags & CLASS.NotPlaceable)         != 0) parts.Add("NotPlaceable");
        if ((flags & CLASS.EditInlineNew)        != 0) parts.Add("EditInlineNew");
        if ((flags & CLASS.DefaultToInstanced)   != 0) parts.Add("DefaultToInstanced");
        if ((flags & CLASS.CollapseCategories)   != 0) parts.Add("CollapseCategories");
        if ((flags & CLASS.Transient)            != 0) parts.Add("Transient");
        if ((flags & CLASS.PerObjectConfig)      != 0) parts.Add("PerObjectConfig");
        if ((flags & CLASS.MinimalAPI)           != 0) parts.Add("MinimalAPI");
        // Config modifier family — pick the most specific one UHT expects.
        if ((flags & CLASS.GlobalUserConfig)     != 0) parts.Add("GlobalUserConfig");
        else if ((flags & CLASS.ProjectUserConfig) != 0) parts.Add("ProjectUserConfig");
        else if ((flags & CLASS.DefaultConfig)   != 0) parts.Add("DefaultConfig");
        if ((flags & CLASS.Config) != 0 && !string.IsNullOrEmpty(configName) && configName != "None")
            parts.Add($"Config={configName}");
        // Always emit BlueprintType so BP can see this class (matches jmap's
        // default). Users can strip manually if a class really shouldn't expose.
        parts.Add("BlueprintType");
        return "UCLASS(" + string.Join(", ", parts) + ")";
    }

    // ─── UFUNCTION specifier derivation ────────────────────────────────

    static string UFunctionSpecifier(FUNC flags)
    {
        var parts = new List<string>();
        // BP access decoder tree — mirrors UHT's precedence.
        bool bpEvent       = (flags & FUNC.BlueprintEvent) != 0;
        bool isNative      = (flags & FUNC.Native)         != 0;
        bool isEvent       = (flags & FUNC.Event)          != 0;
        if (bpEvent && !isNative)       parts.Add("BlueprintImplementableEvent");
        else if (bpEvent && isNative)   parts.Add("BlueprintNativeEvent");
        if ((flags & FUNC.BlueprintPure)     != 0) parts.Add("BlueprintPure");
        else if ((flags & FUNC.BlueprintCallable) != 0) parts.Add("BlueprintCallable");
        if ((flags & FUNC.BlueprintAuthorityOnly) != 0) parts.Add("BlueprintAuthorityOnly");
        if ((flags & FUNC.BlueprintCosmetic) != 0) parts.Add("BlueprintCosmetic");
        if ((flags & FUNC.Exec)         != 0) parts.Add("Exec");
        if ((flags & FUNC.EditorOnly)   != 0) parts.Add("CallInEditor");
        // Networking.
        if ((flags & FUNC.NetServer)    != 0) parts.Add("Server");
        if ((flags & FUNC.NetClient)    != 0) parts.Add("Client");
        if ((flags & FUNC.NetMulticast) != 0) parts.Add("NetMulticast");
        if ((flags & FUNC.Net) != 0 && (flags & FUNC.NetReliable) != 0) parts.Add("Reliable");
        if ((flags & FUNC.Net) != 0 && (flags & FUNC.NetReliable) == 0) parts.Add("Unreliable");
        return parts.Count == 0 ? "UFUNCTION()" : "UFUNCTION(" + string.Join(", ", parts) + ")";
    }

    // ─── Type rendering (UHT flavor) ───────────────────────────────────
    //
    // Uses UE4-style short names: int32, uint8, bool, FString, FName, FText.
    // Object refs get the correct A/U/I prefix from the super map built for
    // the current dump.

    [ThreadStatic] static Dictionary<string, string>? _superMap;
    [ThreadStatic] static HashSet<string>? _referencedClasses;
    [ThreadStatic] static HashSet<string>? _referencedStructs;
    [ThreadStatic] static HashSet<string>? _referencedEnums;

    static readonly HashSet<string> ActorBases = new(StringComparer.Ordinal) {
        "Actor","AActor","Pawn","APawn","Character","ACharacter",
        "Controller","AController","PlayerController","APlayerController",
        "AIController","AAIController","HUD","AHUD",
        "GameMode","AGameMode","GameModeBase","AGameModeBase",
        "GameState","AGameState","GameStateBase","AGameStateBase",
        "PlayerState","APlayerState","WorldSettings","AWorldSettings",
        "Info","AInfo","Volume","AVolume","Brush","ABrush",
        "StaticMeshActor","AStaticMeshActor","SkeletalMeshActor","ASkeletalMeshActor",
        "Light","ALight","CameraActor","ACameraActor","TriggerBox","ATriggerBox",
    };

    static bool IsActorChain(string name)
    {
        var map = _superMap;
        if (map is null) return false;
        var cur = name;
        for (int i = 0; i < 32; i++)
        {
            if (ActorBases.Contains(cur)) return true;
            if (!map.TryGetValue(cur, out var sup)) return false;
            cur = sup;
        }
        return false;
    }

    static string Prefix(string name)
        => IsActorChain(name) || IsActorChain("A" + name) ? "A" : "U";

    static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    // Map a raw FProperty class name ("IntProperty", "FloatProperty", etc.) to
    // a UHT-style C++ type string. Used for method parameter/return types
    // when we don't have a recursive tag (methods come from enumerate_methods
    // which flattens type info).
    // Render a CDO default value as a C++ literal where it makes sense, otherwise
    // return null (caller emits a /*default: …*/ comment instead).
    //
    // Matches UHT-style initializers: `= 0.5f`, `= 12`, `= true`, `= FName("X")`,
    // `= TEXT("X")`. UE4.26–UE5 standards.
    static string? RenderCppLiteral(JsonElement val, string cppType)
    {
        try
        {
            switch (val.ValueKind)
            {
                case JsonValueKind.True:  return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Number:
                {
                    // Choose literal suffix from the declared cpp type so round-trip
                    // via cl.exe doesn't lose precision. For floats we ensure a
                    // decimal point is present — "0f" isn't valid C++ float syntax,
                    // needs "0.0f".
                    static string Floaty(double d)
                    {
                        var s = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
                        return s;
                    }
                    if (cppType == "float")  return Floaty(val.GetDouble()) + "f";
                    if (cppType == "double") return Floaty(val.GetDouble());
                    if (cppType == "int32" || cppType == "int16" || cppType == "int8" || cppType == "int64")
                        return val.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (cppType == "uint8" || cppType == "uint16" || cppType == "uint32" || cppType == "uint64")
                        return val.GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return val.GetRawText();
                }
                case JsonValueKind.String:
                {
                    var s = val.GetString() ?? "";
                    if (cppType == "FName")   return $"FName(TEXT(\"{EscapeCppString(s)}\"))";
                    if (cppType == "FString") return $"TEXT(\"{EscapeCppString(s)}\")";
                    if (cppType == "FText")   return $"FText::FromString(TEXT(\"{EscapeCppString(s)}\"))";
                    // Enum default comes as "EType::Member" or bare "Member" — both safe as identifiers.
                    if (cppType.StartsWith("E") && cppType.Length > 1 && char.IsUpper(cppType[1]) && !s.Contains('"'))
                        return s.Contains("::") ? s : $"{cppType}::{Sanitize(s)}";
                    return null;  // unknown string-to-cpp mapping — comment fallback
                }
                default: return null;
            }
        }
        catch { return null; }
    }

    static string EscapeCppString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    static string CompactJson(JsonElement e)
    {
        var s = e.GetRawText();
        return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
    }

    static string MapPropertyTypeToUht(string? raw) => raw switch
    {
        null            => "void",
        "IntProperty"   => "int32",
        "FloatProperty" => "float",
        "DoubleProperty"=> "double",
        "BoolProperty"  => "bool",
        "ByteProperty"  => "uint8",
        "Int8Property"  => "int8",
        "Int16Property" => "int16",
        "UInt16Property"=> "uint16",
        "UInt32Property"=> "uint32",
        "Int64Property" => "int64",
        "UInt64Property"=> "uint64",
        "NameProperty"  => "FName",
        "StrProperty"   => "FString",
        "TextProperty"  => "FText",
        "ObjectProperty" or "WeakObjectProperty" or "LazyObjectProperty"
            or "SoftObjectProperty" or "AssetObjectProperty" or "ClassProperty"
            or "SoftClassProperty" or "InterfaceProperty"
                          => "UObject*",
        "ArrayProperty" => "TArray<uint8>",
        "MapProperty"   => "TMap<FName, uint8>",
        "SetProperty"   => "TSet<uint8>",
        "StructProperty" => "uint8 /*struct*/",
        "EnumProperty"   => "uint8 /*enum*/",
        "DelegateProperty" or "MulticastDelegateProperty"
            or "MulticastInlineDelegateProperty" or "MulticastSparseDelegateProperty"
                          => "void*",
        _                => "uint8",
    };

    // Strip a UE C++ type prefix (A/U/I/F/E) if and only if the second char
    // is uppercase. "AActor" -> "Actor" but "AnimMontage" stays "AnimMontage"
    // (it's just a class whose name starts with A).
    static string StripUePrefix(string name)
    {
        if (name.Length < 2) return name;
        char c0 = name[0], c1 = name[1];
        bool prefixChar = c0 == 'A' || c0 == 'U' || c0 == 'I' || c0 == 'F' || c0 == 'E';
        if (prefixChar && char.IsUpper(c1)) return name.Substring(1);
        return name;
    }

    static string RenderUhtType(JsonElement tag)
    {
        var type = tag.TryGetProperty("type", out var ty) ? ty.GetString() ?? "Unknown" : "Unknown";
        switch (type)
        {
            case "BoolProperty":   return "bool";
            case "ByteProperty":   return "uint8";
            case "Int8Property":   return "int8";
            case "Int16Property":  return "int16";
            case "UInt16Property": return "uint16";
            case "IntProperty":    return "int32";
            case "UInt32Property": return "uint32";
            case "Int64Property":  return "int64";
            case "UInt64Property": return "uint64";
            case "FloatProperty":  return "float";
            case "DoubleProperty": return "double";
            case "NameProperty":   return "FName";
            case "StrProperty":    return "FString";
            case "TextProperty":   return "FText";
            case "ArrayProperty":
                return "TArray<" + (tag.TryGetProperty("inner", out var ai) && ai.ValueKind == JsonValueKind.Object
                    ? RenderUhtType(ai) : "uint8") + ">";
            case "SetProperty":
                return "TSet<" + (tag.TryGetProperty("inner", out var si) && si.ValueKind == JsonValueKind.Object
                    ? RenderUhtType(si) : "uint8") + ">";
            case "MapProperty":
                {
                    string kt = tag.TryGetProperty("key",   out var k) && k.ValueKind == JsonValueKind.Object ? RenderUhtType(k) : "FName";
                    string vt = tag.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object ? RenderUhtType(v) : "FString";
                    return $"TMap<{kt}, {vt}>";
                }
            case "StructProperty":
                {
                    var sn = tag.TryGetProperty("structName", out var snEl) ? snEl.GetString() : null;
                    if (string.IsNullOrEmpty(sn)) return "uint8[] /*struct*/";
                    _referencedStructs?.Add(sn!);
                    return "F" + Sanitize(sn!);
                }
            case "EnumProperty":
                {
                    var en = tag.TryGetProperty("enumName", out var enEl) ? enEl.GetString() : null;
                    if (string.IsNullOrEmpty(en)) return "uint8 /*enum*/";
                    _referencedEnums?.Add(en!);
                    return "E" + Sanitize(en!).TrimStart('E');
                }
            case "ObjectProperty":
            case "WeakObjectProperty":
            case "LazyObjectProperty":
            case "SoftObjectProperty":
            case "AssetObjectProperty":
                {
                    var pc = tag.TryGetProperty("propertyClass", out var pcEl) ? pcEl.GetString() : null;
                    if (string.IsNullOrEmpty(pc)) return "UObject*";
                    _referencedClasses?.Add(pc!);
                    var core = Sanitize(StripUePrefix(pc!));
                    var p = Prefix(core);
                    if (type == "WeakObjectProperty")   return $"TWeakObjectPtr<{p}{core}>";
                    if (type == "LazyObjectProperty")   return $"TLazyObjectPtr<{p}{core}>";
                    if (type == "SoftObjectProperty")   return $"TSoftObjectPtr<{p}{core}>";
                    return $"{p}{core}*";
                }
            case "ClassProperty":
                {
                    var mc = tag.TryGetProperty("metaClass", out var mcEl) ? mcEl.GetString() : null;
                    if (string.IsNullOrEmpty(mc)) return "UClass*";
                    _referencedClasses?.Add(mc!);
                    var core = Sanitize(mc!);
                    return $"TSubclassOf<{Prefix(core)}{core}>";
                }
            case "SoftClassProperty":
                {
                    var mc = tag.TryGetProperty("metaClass", out var mcEl) ? mcEl.GetString() : null;
                    if (string.IsNullOrEmpty(mc)) return "TSoftClassPtr<UObject>";
                    _referencedClasses?.Add(mc!);
                    var core = Sanitize(mc!);
                    return $"TSoftClassPtr<{Prefix(core)}{core}>";
                }
            case "InterfaceProperty":
                {
                    var ic = tag.TryGetProperty("interfaceClass", out var icEl) ? icEl.GetString() : null;
                    if (string.IsNullOrEmpty(ic)) return "TScriptInterface<IInterface>";
                    _referencedClasses?.Add(ic!);
                    return $"TScriptInterface<I{Sanitize(ic!)}>";
                }
            case "DelegateProperty":                    return "FScriptDelegate";
            case "MulticastDelegateProperty":
            case "MulticastInlineDelegateProperty":     return "FMulticastScriptDelegate";
            case "MulticastSparseDelegateProperty":     return "FSparseDelegate";
            case "FieldPathProperty":                   return "FFieldPath";
            default:                                    return "uint8 /*unknown*/";
        }
    }

    // ─── UPROPERTY = meta(Category="...") tweaks ───────────────────────

    static string MakeCategory(string className) => "\"" + className + "\"";

    // ─── Render a single UHT header (shared by DumpUhtSdk + DumpUeProject) ──
    //
    // Takes a class-or-struct JSON element (from the reflection dump), the kind
    // tag, and the current module's name (used for the *_API export macro).
    // Returns the full header text. Caller decides where to write it.
    // Expects _superMap / _referencedClasses / _referencedStructs / _referencedEnums
    // to already be set up (do it once per DumpUhtSdk / DumpUeProject call).

    static string RenderUhtHeader(JsonElement t, string kind, string moduleApi)
    {
        var name = t.GetProperty("name").GetString() ?? "Unnamed";
        var full = t.TryGetProperty("fullName", out var fn) ? fn.GetString() ?? "" : "";
        var super = t.TryGetProperty("super", out var sp) ? sp.GetString() : null;

        _referencedClasses = new HashSet<string>(StringComparer.Ordinal);
        _referencedStructs = new HashSet<string>(StringComparer.Ordinal);
        _referencedEnums   = new HashSet<string>(StringComparer.Ordinal);

        var body = new StringBuilder();
        body.AppendLine($"// {kind}: {full}");
        string apiMacro = string.IsNullOrEmpty(moduleApi) ? "" : moduleApi + " ";

        if (kind == "Class")
        {
            var core = Sanitize(StripUePrefix(name));
            var pref = Prefix(core);
            var superCore = string.IsNullOrEmpty(super) ? "Object" : Sanitize(StripUePrefix(super!));
            var superPref = string.IsNullOrEmpty(super) ? "U" : Prefix(superCore);

            // Phase 4: UCLASS flags + ClassConfigName + interfaces.
            uint cflags = t.TryGetProperty("classFlags", out var cf) && cf.ValueKind == JsonValueKind.Number
                ? cf.GetUInt32() : 0u;
            string? configName = t.TryGetProperty("classConfigName", out var cn) ? cn.GetString() : null;
            body.AppendLine(UClassSpecifier((CLASS)cflags, configName));

            // Build inheritance list: super + interfaces.
            var inherits = new List<string> { $"public {superPref}{superCore}" };
            if (t.TryGetProperty("interfaces", out var ifacesEl) && ifacesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var i in ifacesEl.EnumerateArray())
                {
                    var iname = i.GetString();
                    if (string.IsNullOrEmpty(iname)) continue;
                    _referencedClasses?.Add(iname);
                    // Interface UE classes are named with a "U"-prefix UClass + "I"-prefix C++ class.
                    // The C++ inheritance target is the I-prefix form.
                    var icore = Sanitize(StripUePrefix(iname));
                    inherits.Add($"public I{icore}");
                }
            }
            body.AppendLine($"class {apiMacro}{pref}{core} : {string.Join(", ", inherits)} {{");
            body.AppendLine("    GENERATED_BODY()");
            body.AppendLine("public:");
        }
        else
        {
            body.AppendLine("USTRUCT(BlueprintType)");
            body.AppendLine($"struct {apiMacro}F{Sanitize(StripUePrefix(name))} {{");
            body.AppendLine("    GENERATED_BODY()");
        }

        if (t.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fields.EnumerateArray())
            {
                var owner = f.TryGetProperty("owner", out var ow) ? ow.GetString() : null;
                if (!string.IsNullOrEmpty(owner) && owner != name) continue;

                var fname = f.GetProperty("name").GetString() ?? "unnamed";
                int offset = f.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                ulong flags = f.TryGetProperty("propertyFlags", out var pf) && pf.ValueKind == JsonValueKind.Number
                    ? pf.GetUInt64() : 0;

                JsonElement tag = default;
                bool hasTag = f.TryGetProperty("tag", out tag) && tag.ValueKind == JsonValueKind.Object;
                var uhtType = hasTag ? RenderUhtType(tag) : "uint8";
                var spec = UPropertySpecifier((CPF)flags);

                // Phase 4C: CDO default-value initializer when it renders as a
                // scalar C++ literal. Compound types (structs, arrays, objects)
                // carry through as a comment so the human reader can see them
                // but we don't try to synthesize a C++ initializer.
                string initializer = "";
                string defComment  = "";
                if (f.TryGetProperty("defaultValue", out var dv))
                {
                    var lit = RenderCppLiteral(dv, uhtType);
                    if (lit is not null) initializer = " = " + lit;
                    else defComment = "  // default: " + CompactJson(dv);
                }
                body.AppendLine($"    {spec}");
                body.AppendLine($"    {uhtType} {Sanitize(fname)}{initializer};  // +0x{offset:X}{defComment}");
                body.AppendLine();
            }
        }

        // Phase 4: emit method declarations with UFUNCTION specifiers if the
        // dump included methods. Parameters are rendered with their raw type
        // names (UE's FName for each param's property class) — good enough
        // for UHT to compile when the types are in scope.
        if (t.TryGetProperty("methods", out var methodsEl) && methodsEl.ValueKind == JsonValueKind.Array
            && methodsEl.GetArrayLength() > 0)
        {
            body.AppendLine();
            body.AppendLine("    // ── Methods ──");
            foreach (var m in methodsEl.EnumerateArray())
            {
                var owner = m.TryGetProperty("owner", out var mo) ? mo.GetString() : null;
                if (!string.IsNullOrEmpty(owner) && owner != name) continue;

                var mname = m.GetProperty("name").GetString() ?? "Unnamed";
                uint fflags = m.TryGetProperty("functionFlags", out var ff) && ff.ValueKind == JsonValueKind.Number
                    ? ff.GetUInt32() : 0u;
                var spec = UFunctionSpecifier((FUNC)fflags);
                var ret = m.TryGetProperty("returnType", out var rt) ? rt.GetString() : "void";
                // Map raw "IntProperty" -> "int32" via our existing helper.
                ret = MapPropertyTypeToUht(ret);

                var paramsStr = new List<string>();
                if (m.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in ps.EnumerateArray())
                    {
                        var pn = p.GetProperty("name").GetString() ?? "p";
                        var pt = MapPropertyTypeToUht(p.GetProperty("type").GetString() ?? "uint8");
                        bool outParam = p.TryGetProperty("out", out var op) && op.GetBoolean();
                        paramsStr.Add((outParam ? pt + "& " : pt + " ") + Sanitize(pn));
                    }
                }
                // Kismet bytecode preview: for Blueprint-hosted functions (non-native
                // with real Script bytes), emit a one-line comment listing the first
                // few opcodes. Native functions skip this. Lets readers see what a
                // Blueprint function actually does at the opcode level instead of
                // staring at an empty signature.
                if (m.TryGetProperty("scriptBytes", out var sb) && sb.ValueKind == JsonValueKind.Number)
                {
                    int bytes = sb.GetInt32();
                    if (bytes > 0)
                    {
                        var opList = new List<string>();
                        if (m.TryGetProperty("scriptOps", out var so) && so.ValueKind == JsonValueKind.Array)
                            foreach (var op in so.EnumerateArray())
                                if (op.ValueKind == JsonValueKind.String)
                                    opList.Add(op.GetString() ?? "?");
                        var preview = opList.Count > 0 ? string.Join(", ", opList) : "(no ops)";
                        body.AppendLine($"    // @kismet {bytes} bytes: {preview}");
                    }
                }
                body.AppendLine($"    {spec}");
                body.AppendLine($"    {ret} {Sanitize(mname)}({string.Join(", ", paramsStr)});");
            }
        }

        body.AppendLine("};");
        body.AppendLine();

        var hdr = new StringBuilder();
        hdr.AppendLine($"// Auto-generated UHT-style header from UEVR-MCP. Source type: {full}");
        hdr.AppendLine("// This file is intended for use inside a UE4/UE5 editor project. Drop into");
        hdr.AppendLine("// Source/<Module>/Public/ and let UnrealHeaderTool compile it.");
        hdr.AppendLine("#pragma once");
        hdr.AppendLine();
        hdr.AppendLine("#include \"CoreMinimal.h\"");
        hdr.AppendLine("#include \"UObject/NoExportTypes.h\"");
        hdr.AppendLine("#include \"UObject/ObjectMacros.h\"");
        hdr.AppendLine();

        foreach (var rc in _referencedClasses!.OrderBy(x => x, StringComparer.Ordinal))
        {
            var rcCore = Sanitize(StripUePrefix(rc));
            var rcPref = Prefix(rcCore);
            hdr.AppendLine($"class {rcPref}{rcCore};");
        }
        if (_referencedStructs!.Count > 0) hdr.AppendLine();
        foreach (var rs in _referencedStructs!.OrderBy(x => x, StringComparer.Ordinal))
            hdr.AppendLine($"struct F{Sanitize(StripUePrefix(rs))};");
        if (_referencedEnums!.Count > 0) hdr.AppendLine();
        foreach (var re in _referencedEnums!.OrderBy(x => x, StringComparer.Ordinal))
            hdr.AppendLine($"enum class E{Sanitize(StripUePrefix(re))} : uint8;");

        hdr.AppendLine();
        hdr.AppendLine($"#include \"{Sanitize(name)}.generated.h\"");
        hdr.AppendLine();
        hdr.Append(body);
        return hdr.ToString();
    }

    static string RenderUhtEnum(JsonElement eObj)
    {
        var name = eObj.GetProperty("name").GetString() ?? "Unnamed";
        var core = Sanitize(StripUePrefix(name));
        var sb = new StringBuilder();
        sb.AppendLine($"// Auto-generated UHT enum. Source: Enum {name}");
        sb.AppendLine("#pragma once");
        sb.AppendLine();
        sb.AppendLine("#include \"CoreMinimal.h\"");
        sb.AppendLine();
        sb.AppendLine($"#include \"E{core}.generated.h\"");
        sb.AppendLine();
        sb.AppendLine("UENUM(BlueprintType)");
        sb.AppendLine($"enum class E{core} : uint8 {{");

        // UHT requires a 0 entry on BlueprintType enums so default-init is
        // valid. Bitflag enums starting at 1 trip this; we pre-emit a `None`
        // entry when no zero value is found in the source.
        bool hasZero = false;
        var rowsToEmit = new List<(string Sanitized, long V, string Display)>();
        if (eObj.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var ent in entries.EnumerateArray())
            {
                var en = ent.GetProperty("name").GetString() ?? "None";
                long v = ent.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.Number
                    ? vv.GetInt64() : 0;
                if (en.StartsWith(name + "::", StringComparison.Ordinal))
                    en = en.Substring(name.Length + 2);
                if (v == 0) hasZero = true;
                rowsToEmit.Add((Sanitize(en), v, en));
            }
        }
        if (!hasZero)
            sb.AppendLine("    None = 0 UMETA(DisplayName=\"None\"),");
        foreach (var r in rowsToEmit)
            sb.AppendLine($"    {r.Sanitized} = {r.V} UMETA(DisplayName=\"{r.Display}\"),");

        sb.AppendLine("};");
        return sb.ToString();
    }

    // ─── Module extraction ─────────────────────────────────────────────
    //
    // fullName is like "Class /Script/<Module>.<Type>" for native types,
    // "Class /Script/<Module>.<Type>_C" for BP-generated ones, or
    // "Class /Game/..." for /Game/ BP assets. For project scaffolding we
    // care about Script modules; /Game content goes into a separate "Game"
    // module or is skipped.

    // Loose "is this a stock UE engine module?" predicate. Covers the modules
    // that ship with UnrealEngine 4.22–5.4 plus common first-party plugins.
    // Conservative false-positive risk: if a game ships a module whose name
    // collides with an engine one, it'll get filtered out (rare).
    static readonly HashSet<string> _engineExact = new(StringComparer.Ordinal) {
        "Core","CoreUObject","Engine","InputCore","ApplicationCore","Slate","SlateCore",
        "UMG","Niagara","AIModule","GameplayTags","GameplayTasks","UnrealEd",
        "Foliage","Landscape","LevelSequence","MovieScene","MovieSceneTracks","MovieSceneCapture",
        "PhysicsCore","Paper2D","ApexDestruction","ChaosCloth","ChaosSolverEngine","ChaosSolvers",
        "Json","JsonUtilities","HTTP","XmlParser","PerfCounters","TraceLog","Stats",
        "NavigationSystem","AugmentedReality","ClothingSystemRuntime",
        "ClothingSystemRuntimeNv","ClothingSystemRuntimeCommon",
        "AnimGraphRuntime","AnimationCore","AnimationSharing",
        "ImageWrapper","MediaAssets","ActorSequence","ActorLayerUtilities",
        "AssetRegistry","AudioMixer","AudioMixerCore","AudioExtensions","Audio",
        "ArchVisCharacter","AssetTags","AutomationController","AutomationMessages","AutomationTest",
        "AutomationUtils","AutomationWorker","BuildPatchServices","Cbor","BuildSettings",
        "CinematicCamera","CableComponent","CrashTracker","CustomMeshComponent",
        "D3D11RHI","D3D12RHI","RHI","RenderCore","Renderer","Niagara","NiagaraCore","NiagaraShader",
        "GameplayAbilities","GameplayCameras","GameplayDebugger","GeometryCacheTracks",
        "GeometryCollectionTracks","GooglePAD","HeadMountedDisplay","HotReload","ImageCore",
        "Landscape","LauncherCheck","LevelSequenceEditor","LightPropagationVolumeRuntime",
        "MagicLeapAR","MagicLeapEyeTracker","MagicLeapIdentity","MagicLeapMedia",
        "Messaging","MessagingCommon","MessagingRpc","MobilePatchingUtils",
        "MovieSceneAudioTracks","MovieSceneCapture","NavigationCore","NetCore","Networking",
        "OnlineSubsystem","OnlineSubsystemUtils","Oodle","OpenXRHMD","OpenXRInput",
        "PacketHandler","PakFile","PerceptualColor","PerformanceMonitor","PoseAI",
        "ProceduralMeshComponent","PropertyPath","ReplicationGraph","SessionFrontend",
        "SessionMessages","SessionServices","SignalProcessing","SlateReflector","SoundFieldRendering",
        "SourceControl","SpatialAccelerator","Subversion","SunPosition","SynthBenchmark",
        "Synthesis","TargetPlatform","TextureEditor","ToolMenus","TraceDataVisualization",
        "UnrealGameSync","UnrealInsights","Voice","VorbisAudioDecoder",
        "Qos","Voice","Voip","WebBrowser","WebBrowserWidget","WebSockets","WindowsDeviceProfileSelector",
        "WindowsTargetPlatform","XmpExif","WmfMedia","WmfMediaEditor","WmfMediaFactory",
        "MediaIOCore","MediaIOCoreEditor","MediaPlate","LiveLinkInterface","LiveLinkMessageBusFramework",
        "AvfMediaFactory","ImgMedia","MediaCompositing","MediaCompositingEditor",
        "CableComponent","CharacterAI","Collision","Combat","CoreOnline","CoreUObjectBP",
        // Additional UE4/UE5 engine plugin modules frequently present in shipped games.
        "DatasmithContent","DatasmithImporter","VariantManagerContent","VariantManager",
        "InteractiveToolsFramework","ModelingComponents","ModelingOperators","MeshConversion",
        "EditableMesh","GeometryCollectionTracks","GeometryCache","GeometryCacheTracks",
        "FieldSystemEngine","StaticMeshDescription","MeshDescription","MeshUtilities",
        "MaterialShaderQualitySettings","PhysXVehicles","LevelSequenceEditor",
        "TemplateSequence","MotoSynth","PrefabAsset","Overlay","Serialization","VectorVM",
        "PropertyAccess","TimeManagement","SoundFields","SoundUtilities","EngineSettings",
        "MoviePlayer","LocationServicesBPLibrary","UObjectPlugin","SignificanceManager",
        "FacialAnimation","DeveloperSettings","TcpMessaging","UdpMessaging","ImgMediaFactory",
        "EngineMessages","Hotfix","Rejoin","Lobby","Party","EyeTracker","MRMesh","ImageWriteQueue",
        "PortalRpc","PortalServices","PlatformCrypto","PlatformCryptoOpenSSL","PlatformCryptoTypes",
        "HairStrandsCore","HairStrandsEditor","HairStrandsMeshProjection",
    };

    static bool IsEngineModule(string module)
    {
        if (_engineExact.Contains(module)) return true;
        // Prefix heuristics for the long tail of Online/Anim/Audio/Chaos subsystem modules.
        if (module.StartsWith("OnlineSubsystem", StringComparison.Ordinal) && module != "OnlineSubsystemGOG"
            && module != "OnlineSubsystemRedpointEOS" && module != "OnlineSubsystemSteam") return true;
        if (module.StartsWith("Anim",  StringComparison.Ordinal)) return true;
        if (module.StartsWith("Audio", StringComparison.Ordinal)) return true;
        if (module.StartsWith("Chaos", StringComparison.Ordinal)) return true;
        if (module.StartsWith("Media", StringComparison.Ordinal)) return true;
        if (module.StartsWith("Niagara", StringComparison.Ordinal)) return true;
        if (module.StartsWith("GeometryCollection", StringComparison.Ordinal)) return true;
        return false;
    }

    static string? ModuleFromFullName(string fullName)
    {
        const string marker = "/Script/";
        int idx = fullName.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + marker.Length;
        int end = fullName.IndexOf('.', start);
        if (end < 0) return null;
        return fullName.Substring(start, end - start);
    }

    // /Game/ BP content uses a different fullName shape:
    //   "BlueprintGeneratedClass /Game/UI/Common/MenuCursor/UI_BP_BaseCursor.UI_BP_BaseCursor_C"
    // Return a synthetic module name for these so the emit can group them
    // under a single "Game" pseudo-module. When includeGameContent=false
    // these classes are skipped entirely (the default — game content isn't
    // usable as a UE C++ editor project module without BP reinstancing).
    static string? GameModuleFromFullName(string fullName)
    {
        const string marker = "/Game/";
        int idx = fullName.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        return "Game";
    }

    // ─── Main tool ─────────────────────────────────────────────────────

    [McpServerTool(Name = "uevr_dump_uht_sdk")]
    [Description("Emit UHT-style C++ headers (UCLASS / USTRUCT / UPROPERTY with decoded specifiers) from live reflection. Output matches jmap_to_uht.py: one header per type under outDir, include graph derived from referenced types, UE4 project can drop these under Source/. Complements uevr_dump_sdk_cpp (cast-style, for runtime workflows); this flavor is for building UE C++ mods. Shares the reflection cache with dump_usmap / dump_sdk_cpp so running all three is one walk.")]
    public static async Task<string> DumpUhtSdk(
        [Description("Absolute output directory.")] string outDir,
        [Description("Optional case-insensitive filter on type full names.")] string? filter = null,
        [Description("Skip built-in engine types.")] bool skipEngine = false,
        [Description("Emit UFUNCTION stubs for methods. Default false — matches dump_sdk_cpp to avoid the slow methods=true reflection walk.")] bool includeMethods = false)
    {
        using var doc = await DumpTools.FetchReflectionPublic(filter, includeMethods, enums: true);
        if (doc.RootElement.TryGetProperty("error", out var err))
            return JsonSerializer.Serialize(new { ok = false, error = err.ToString() }, JsonOpts);

        Directory.CreateDirectory(outDir);

        // Pre-pass: build super-map for A/U-prefix resolution.
        var superMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("classes", out var clsArr))
            foreach (var c in clsArr.EnumerateArray())
                if (c.TryGetProperty("super", out var sp) && sp.GetString() is string s)
                    superMap[c.GetProperty("name").GetString() ?? ""] = s;
        _superMap = superMap;

        var enumNames = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("enums", out var enArr))
            foreach (var e in enArr.EnumerateArray())
                if (e.GetProperty("name").GetString() is string en) enumNames.Add(en);

        int classCount = 0, structCount = 0, enumCount = 0;

        try
        {
            void EmitType(JsonElement t, string kind)
            {
                var name = t.GetProperty("name").GetString() ?? "Unnamed";
                var full = t.TryGetProperty("fullName", out var fn) ? fn.GetString() ?? "" : "";
                if (skipEngine && (full.Contains("/Script/Engine") || full.Contains("/Script/CoreUObject")))
                    return;
                var header = RenderUhtHeader(t, kind, moduleApi: "");
                File.WriteAllText(Path.Combine(outDir, Sanitize(name) + ".h"), header);
                if (kind == "Class") classCount++; else structCount++;
            }

            if (doc.RootElement.TryGetProperty("classes", out var c2))
                foreach (var cls in c2.EnumerateArray()) EmitType(cls, "Class");
            if (doc.RootElement.TryGetProperty("structs", out var s2))
                foreach (var s in s2.EnumerateArray()) EmitType(s, "ScriptStruct");

            foreach (var eObj in enArr.ValueKind == JsonValueKind.Array ? enArr.EnumerateArray() : default)
            {
                var name = eObj.GetProperty("name").GetString() ?? "Unnamed";
                var core = StripUePrefix(name);
                var sb = new StringBuilder(RenderUhtEnum(eObj));
                File.WriteAllText(Path.Combine(outDir, "E" + Sanitize(core) + ".h"), sb.ToString());
                enumCount++;
            }
        }
        finally
        {
            _superMap = null;
            _referencedClasses = null;
            _referencedStructs = null;
            _referencedEnums = null;
        }

        return JsonSerializer.Serialize(new {
            ok = true,
            data = new {
                outDir = Path.GetFullPath(outDir),
                classCount,
                structCount,
                enumCount,
            },
        }, JsonOpts);
    }

    // ─── uevr_dump_ue_project ──────────────────────────────────────────

    // Internal seam for USMAP-driven project scaffolding. Takes a pre-built
    // reflection JSON (adapted from jmap's USMAP output) and emits the same
    // project tree that DumpUeProject produces against live reflection.
    // All types collapse into one module since USMAP doesn't carry the
    // /Script/<Module> origin.
    internal static string EmitUhtProjectFromReflection(JsonElement root,
        string outDir, string projectName, string moduleName, string engineAssociation)
    {
        var superMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("classes", out var clsArr))
            foreach (var c in clsArr.EnumerateArray())
                if (c.TryGetProperty("super", out var sp) && sp.ValueKind == JsonValueKind.String
                    && sp.GetString() is string s)
                    superMap[c.GetProperty("name").GetString() ?? ""] = s;
        _superMap = superMap;

        Directory.CreateDirectory(outDir);
        var modDir = Path.Combine(outDir, "Source", moduleName);
        var pubDir = Path.Combine(modDir, "Public");
        var prvDir = Path.Combine(modDir, "Private");
        Directory.CreateDirectory(pubDir);
        Directory.CreateDirectory(prvDir);
        string moduleApi = moduleName.ToUpperInvariant() + "_API";

        int classes = 0, structs = 0, enums = 0, skipped = 0, skippedDup = 0;
        // Track stripped-basename we've already emitted. UHT errors with
        // "shares engine name" when two of our own types produce the same
        // stripped name (e.g. `AHoleSpawner` class + `FHoleSpawner` struct
        // both collapse to "HoleSpawner"). First emit wins.
        var emittedStripped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            void EmitHeader(JsonElement t, string kind)
            {
                var name = t.GetProperty("name").GetString() ?? "Unnamed";
                if (IsEngineType(name)) { skipped++; return; }
                // Internal collision check — collapsed-name must be unique.
                var collapsed = name;
                if (collapsed.Length >= 2
                    && (collapsed[0] == 'A' || collapsed[0] == 'U' || collapsed[0] == 'F' || collapsed[0] == 'E' || collapsed[0] == 'I')
                    && (char.IsUpper(collapsed[1]) || char.IsDigit(collapsed[1])))
                    collapsed = collapsed.Substring(1);
                if (!emittedStripped.Add(collapsed)) { skippedDup++; return; }
                var hdr = RenderUhtHeader(t, kind, moduleApi);
                File.WriteAllText(Path.Combine(pubDir, Sanitize(name) + ".h"), hdr);
                if (kind == "Class") classes++; else structs++;
            }
            if (root.TryGetProperty("classes", out var c2))
                foreach (var cls in c2.EnumerateArray()) EmitHeader(cls, "Class");
            if (root.TryGetProperty("structs", out var s2))
                foreach (var st in s2.EnumerateArray()) EmitHeader(st, "ScriptStruct");
            if (root.TryGetProperty("enums", out var eArr))
            {
                foreach (var e in eArr.EnumerateArray())
                {
                    var name = e.GetProperty("name").GetString() ?? "Unnamed";
                    if (IsEngineType(name)) { skipped++; continue; }
                    var core = StripUePrefix(name);
                    if (!emittedStripped.Add(core)) { skippedDup++; continue; }
                    File.WriteAllText(Path.Combine(pubDir, "E" + Sanitize(core) + ".h"),
                        RenderUhtEnum(e));
                    enums++;
                }
            }

            // Minimal Build.cs, module stub, target + uproject — same templates
            // as DumpUeProject but with a single module.
            var build = new StringBuilder();
            build.AppendLine("using UnrealBuildTool;");
            build.AppendLine();
            build.AppendLine($"public class {moduleName} : ModuleRules {{");
            build.AppendLine($"    public {moduleName}(ReadOnlyTargetRules Target) : base(Target) {{");
            build.AppendLine("        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;");
            build.AppendLine();
            build.AppendLine("        PublicDependencyModuleNames.AddRange(new string[] {");
            build.AppendLine("            \"Core\", \"CoreUObject\", \"Engine\", \"InputCore\", \"UMG\",");
            build.AppendLine("            \"SlateCore\", \"Slate\", \"AIModule\", \"GameplayTags\"");
            build.AppendLine("        });");
            build.AppendLine("    }");
            build.AppendLine("}");
            File.WriteAllText(Path.Combine(modDir, moduleName + ".Build.cs"), build.ToString());

            var modCpp = new StringBuilder();
            modCpp.AppendLine($"// Auto-generated module stub for {moduleName}.");
            modCpp.AppendLine("#include \"Modules/ModuleManager.h\"");
            modCpp.AppendLine();
            modCpp.AppendLine($"IMPLEMENT_MODULE(FDefaultGameModuleImpl, {moduleName});");
            File.WriteAllText(Path.Combine(prvDir, moduleName + ".cpp"), modCpp.ToString());

            string targetCs(string name, string type) => $$"""
                using UnrealBuildTool;
                using System.Collections.Generic;

                public class {{name}} : TargetRules
                {
                    public {{name}}(TargetInfo Target) : base(Target)
                    {
                        Type = TargetType.{{type}};
                        DefaultBuildSettings = BuildSettingsVersion.V2;
                        ExtraModuleNames.AddRange(new string[] { "{{moduleName}}" });
                    }
                }
                """;
            File.WriteAllText(Path.Combine(outDir, "Source", projectName + ".Target.cs"),
                targetCs(projectName + "Target", "Game"));
            File.WriteAllText(Path.Combine(outDir, "Source", projectName + "Editor.Target.cs"),
                targetCs(projectName + "EditorTarget", "Editor"));

            var uproject = $$"""
            {
                "FileVersion": 3,
                "EngineAssociation": "{{engineAssociation}}",
                "Category": "",
                "Description": "Generated from USMAP via UEVR-MCP uevr_dump_uht_from_usmap.",
                "Modules": [
                    {
                        "Name": "{{moduleName}}",
                        "Type": "Runtime",
                        "LoadingPhase": "Default",
                        "AdditionalDependencies": [ "Engine", "CoreUObject" ]
                    }
                ]
            }
            """;
            File.WriteAllText(Path.Combine(outDir, projectName + ".uproject"), uproject);
        }
        finally
        {
            _superMap = null;
            _referencedClasses = null;
            _referencedStructs = null;
            _referencedEnums = null;
        }

        return JsonSerializer.Serialize(new {
            ok = true,
            data = new {
                projectRoot = Path.GetFullPath(outDir),
                projectName,
                moduleName,
                classes, structs, enums,
                totalHeaders = classes + structs + enums,
                skippedEngineTypes = skipped,
                skippedDuplicates = skippedDup,
            },
        }, JsonOpts);
    }

    [McpServerTool(Name = "uevr_dump_ue_project")]
    [Description("Scaffold a buildable UE4/UE5 editor project from live reflection. Groups types by their /Script/<Module>/ module name, emits per-module Source/<Module>/{Public,Private}/ with UHT headers + .Build.cs + module stub .cpp, plus a root .uproject and two .Target.cs files. Matches jmap_to_uht.py's project shape. Types whose fullName doesn't start with /Script/ (BP assets under /Game/) are skipped. Opens a ready-to-compile mirror project — drop into UE editor.")]
    public static async Task<string> DumpUeProject(
        [Description("Absolute output directory — the project root. Created if missing; existing files may be overwritten.")] string outDir,
        [Description("Project name (affects .uproject name and Target.cs class names). Default: inferred from first discovered module.")] string? projectName = null,
        [Description("Only emit these modules (comma-separated). Default: every /Script/ module with at least one type.")] string? modules = null,
        [Description("Engine association written into .uproject (e.g. '4.26', '5.3'). Default: '4.26'.")] string engineAssociation = "4.26",
        [Description("Skip CoreUObject/Engine/UMG engine modules in the Source tree (but keep them in dependency lists). Default true.")] bool skipEngineModules = true,
        [Description("Include UFunction enumeration + Kismet bytecode previews. 2-3× slower per batch. Default false.")] bool methods = false,
        [Description("Include BP-generated classes from /Game/ content. Emitted into a synthetic 'Game' module. Brings in BP widgets, BP actors, UberGraph functions with Kismet bytecode — but the output is NOT a valid UE C++ editor project for those classes (Blueprints can't be reinstanced from headers alone). Best used with methods=true for Kismet preview capture.")] bool includeGameContent = false)
    {
        using var doc = await DumpTools.FetchReflectionPublic(filter: null, methods: methods, enums: true);
        if (doc.RootElement.TryGetProperty("error", out var err))
            return JsonSerializer.Serialize(new { ok = false, error = err.ToString() }, JsonOpts);

        // Build super-map once for the whole project.
        var superMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("classes", out var clsArr))
            foreach (var c in clsArr.EnumerateArray())
                if (c.TryGetProperty("super", out var sp) && sp.GetString() is string s)
                    superMap[c.GetProperty("name").GetString() ?? ""] = s;
        _superMap = superMap;

        // Group types by module.
        var moduleTypes = new Dictionary<string, List<(JsonElement el, string kind)>>(StringComparer.Ordinal);

        void AddToModule(JsonElement t, string kind)
        {
            var full = t.TryGetProperty("fullName", out var fn) ? fn.GetString() : null;
            if (string.IsNullOrEmpty(full)) return;
            var mod = ModuleFromFullName(full!);
            if (mod is null && includeGameContent)
                mod = GameModuleFromFullName(full!);
            if (mod is null) return;
            if (skipEngineModules && IsEngineModule(mod)) return;
            if (!moduleTypes.TryGetValue(mod, out var list))
                moduleTypes[mod] = list = new List<(JsonElement, string)>();
            list.Add((t, kind));
        }

        foreach (var c in (clsArr.ValueKind == JsonValueKind.Array ? clsArr.EnumerateArray() : default))
            AddToModule(c, "Class");
        if (doc.RootElement.TryGetProperty("structs", out var structsArr))
            foreach (var s in structsArr.EnumerateArray()) AddToModule(s, "ScriptStruct");
        if (doc.RootElement.TryGetProperty("enums", out var enumsArr))
            foreach (var e in enumsArr.EnumerateArray()) AddToModule(e, "Enum");

        if (moduleTypes.Count == 0)
        {
            _superMap = null;
            return JsonSerializer.Serialize(new { ok = false, error = "no /Script/ modules found in reflection dump" }, JsonOpts);
        }

        // Filter modules by user request if set.
        if (!string.IsNullOrWhiteSpace(modules))
        {
            var keep = modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            moduleTypes = moduleTypes
                .Where(kv => keep.Contains(kv.Key, StringComparer.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        projectName ??= moduleTypes.Keys.OrderBy(x => x, StringComparer.Ordinal).First();

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "Source"));

        int totalHeaders = 0;
        var perModuleStats = new List<object>();

        try
        {
            foreach (var (module, types) in moduleTypes.Select(kv => (kv.Key, kv.Value)))
            {
                var modDir = Path.Combine(outDir, "Source", module);
                var pubDir = Path.Combine(modDir, "Public");
                var prvDir = Path.Combine(modDir, "Private");
                Directory.CreateDirectory(pubDir);
                Directory.CreateDirectory(prvDir);

                int classes = 0, structs = 0, enums = 0;
                string moduleApi = module.ToUpperInvariant() + "_API";

                foreach (var (t, kind) in types)
                {
                    var name = t.GetProperty("name").GetString() ?? "Unnamed";
                    if (kind == "Enum")
                    {
                        var core = StripUePrefix(name);
                        var hdr = RenderUhtEnum(t);
                        File.WriteAllText(Path.Combine(pubDir, "E" + Sanitize(core) + ".h"), hdr);
                        enums++;
                    }
                    else
                    {
                        var hdr = RenderUhtHeader(t, kind, moduleApi);
                        File.WriteAllText(Path.Combine(pubDir, Sanitize(name) + ".h"), hdr);
                        if (kind == "Class") classes++; else structs++;
                    }
                }
                totalHeaders += classes + structs + enums;

                // Build.cs
                var build = new StringBuilder();
                build.AppendLine("using UnrealBuildTool;");
                build.AppendLine();
                build.AppendLine($"public class {module} : ModuleRules {{");
                build.AppendLine($"    public {module}(ReadOnlyTargetRules Target) : base(Target) {{");
                build.AppendLine("        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;");
                build.AppendLine();
                build.AppendLine("        PublicDependencyModuleNames.AddRange(new string[] {");
                build.AppendLine("            \"Core\", \"CoreUObject\", \"Engine\", \"InputCore\",");
                build.AppendLine("            \"UMG\", \"SlateCore\", \"Slate\", \"AIModule\",");
                // cross-module deps: add sibling modules as private deps so inter-module
                // references resolve without a manual edit.
                if (moduleTypes.Count > 1)
                {
                    build.AppendLine("        });");
                    build.AppendLine();
                    build.AppendLine("        PrivateDependencyModuleNames.AddRange(new string[] {");
                    foreach (var other in moduleTypes.Keys.Where(k => k != module).OrderBy(x => x, StringComparer.Ordinal))
                        build.AppendLine($"            \"{other}\",");
                }
                build.AppendLine("        });");
                build.AppendLine("    }");
                build.AppendLine("}");
                File.WriteAllText(Path.Combine(modDir, module + ".Build.cs"), build.ToString());

                // Module stub .cpp
                var modCpp = new StringBuilder();
                modCpp.AppendLine($"// Auto-generated module stub for {module}.");
                modCpp.AppendLine("#include \"Modules/ModuleManager.h\"");
                modCpp.AppendLine();
                modCpp.AppendLine($"IMPLEMENT_MODULE(FDefaultGameModuleImpl, {module});");
                File.WriteAllText(Path.Combine(prvDir, module + ".cpp"), modCpp.ToString());

                perModuleStats.Add(new { module, classes, structs, enums, dir = modDir });
            }

            // Target.cs files
            string targetCs(string name, string type)
            {
                var ord = moduleTypes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var extras = string.Join(", ", ord.Select(m => "\"" + m + "\""));
                return $$"""
                using UnrealBuildTool;
                using System.Collections.Generic;

                public class {{name}} : TargetRules
                {
                    public {{name}}(TargetInfo Target) : base(Target)
                    {
                        Type = TargetType.{{type}};
                        DefaultBuildSettings = BuildSettingsVersion.V2;
                        ExtraModuleNames.AddRange(new string[] { {{extras}} });
                    }
                }
                """;
            }
            File.WriteAllText(Path.Combine(outDir, "Source", projectName + ".Target.cs"),
                targetCs(projectName + "Target", "Game"));
            File.WriteAllText(Path.Combine(outDir, "Source", projectName + "Editor.Target.cs"),
                targetCs(projectName + "EditorTarget", "Editor"));

            // .uproject
            var moduleEntries = moduleTypes.Keys.OrderBy(x => x, StringComparer.Ordinal)
                .Select(m => $$"""
                        {
                            "Name": "{{m}}",
                            "Type": "Runtime",
                            "LoadingPhase": "Default",
                            "AdditionalDependencies": [ "Engine", "CoreUObject" ]
                        }
                """);
            var uproject = $$"""
            {
                "FileVersion": 3,
                "EngineAssociation": "{{engineAssociation}}",
                "Category": "",
                "Description": "Generated mirror project from UEVR-MCP uevr_dump_ue_project.",
                "Modules": [
            {{string.Join(",\n", moduleEntries)}}
                ]
            }
            """;
            File.WriteAllText(Path.Combine(outDir, projectName + ".uproject"), uproject);
        }
        finally
        {
            _superMap = null;
            _referencedClasses = null;
            _referencedStructs = null;
            _referencedEnums = null;
        }

        return JsonSerializer.Serialize(new {
            ok = true,
            data = new {
                projectRoot = Path.GetFullPath(outDir),
                projectName,
                moduleCount = moduleTypes.Count,
                totalHeaders,
                modules = perModuleStats,
            },
        }, JsonOpts);
    }
}
