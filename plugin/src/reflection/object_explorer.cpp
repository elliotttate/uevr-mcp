#include "object_explorer.h"
#include "property_reader.h"
#include "function_caller.h"
#include "../json_helpers.h"

#include <algorithm>
#include <cctype>
#include <cstring>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

using namespace uevr;

// Case-insensitive substring search
static bool contains_ci(const std::string& haystack, const std::string& needle) {
    auto it = std::search(haystack.begin(), haystack.end(),
        needle.begin(), needle.end(),
        [](char a, char b) { return std::tolower((unsigned char)a) == std::tolower((unsigned char)b); });
    return it != haystack.end();
}

// ── Recursive property tag builder ──────────────────────────────────
//
// Emits a nested JSON tag for a single FProperty, walking inner props for
// arrays/sets/maps/enums so unversioned-asset parsers (FModel, CUE4Parse,
// UAssetAPI) get the structure they need:
//
//   { "type": "ArrayProperty", "inner": { "type": "StructProperty", "structName": "FVector" } }
//
// FMapProperty / FSetProperty key/value/element props are not exposed by
// UEVR's public API (no wrappers for them), so those emit without inner
// children — downstream tools fall back to best-effort parsing.

static nlohmann::json build_property_tag(uevr::API::FProperty* prop) {
    nlohmann::json tag;
    if (!prop) { tag["type"] = "Unknown"; return tag; }

    auto fc = prop->get_class();
    if (!fc) { tag["type"] = "Unknown"; return tag; }

    auto type = JsonHelpers::wide_to_utf8(fc->get_fname()->to_string());
    tag["type"] = type;

    if (type == "StructProperty") {
        auto sp = reinterpret_cast<uevr::API::FStructProperty*>(prop);
        auto ss = sp->get_struct();
        if (ss && ss->get_fname()) {
            tag["structName"] = JsonHelpers::wide_to_utf8(ss->get_fname()->to_string());
        }
    } else if (type == "ArrayProperty") {
        auto ap = reinterpret_cast<uevr::API::FArrayProperty*>(prop);
        auto inner = ap->get_inner();
        if (inner) tag["inner"] = build_property_tag(inner);
    } else if (type == "EnumProperty") {
        auto ep = reinterpret_cast<uevr::API::FEnumProperty*>(prop);
        auto uenum = ep->get_enum();
        if (uenum && uenum->get_fname()) {
            tag["enumName"] = JsonHelpers::wide_to_utf8(uenum->get_fname()->to_string());
        }
        // Underlying numeric prop — parsers need it to know how many bytes to read.
        auto underlying = ep->get_underlying_prop();
        if (underlying) {
            tag["inner"] = build_property_tag(reinterpret_cast<uevr::API::FProperty*>(underlying));
        }
    } else if (type == "BoolProperty") {
        auto bp = reinterpret_cast<uevr::API::FBoolProperty*>(prop);
        tag["fieldSize"]  = bp->get_field_size();
        tag["byteOffset"] = bp->get_byte_offset();
        tag["byteMask"]   = bp->get_byte_mask();
        tag["fieldMask"]  = bp->get_field_mask();
    }
    // FMapProperty / FSetProperty have no UEVR wrappers — caller emits Unknown inners.

    return tag;
}

// ── UEnum entry probe ───────────────────────────────────────────────
//
// UEnum::Names is a TArray<TPair<FName, int64>> at a version-dependent offset
// inside UEnum. UEVR's public API doesn't expose it, so we probe candidate
// offsets and validate heuristically. On success we return the entry list;
// on failure an empty array and the caller treats it as unknown.
//
// Layout tested against UE4.26, UE4.27, UE5.0–5.3. UE4.22- used a TMap instead
// and is not supported.

struct UEnumEntry { std::string name; int64_t value; };
struct TArrayLike { void* data; int32_t count; int32_t capacity; };

static bool probe_mem_readable(void* p, size_t size) noexcept {
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(p, &mbi, sizeof(mbi))) return false;
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD bad = PAGE_NOACCESS | PAGE_GUARD;
    if (mbi.Protect & bad) return false;
    auto end = reinterpret_cast<uint8_t*>(p) + size;
    auto region_end = reinterpret_cast<uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
    return end <= region_end;
}

// SEH-only helpers (no C++ objects with destructors allowed in these bodies).

static bool seh_read_u64x2(const void* src, uint64_t* dst0, uint64_t* dst1) noexcept {
    __try {
        *dst0 = reinterpret_cast<const uint64_t*>(src)[0];
        *dst1 = reinterpret_cast<const uint64_t*>(src)[1];
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static bool seh_read_tarray(const void* src, TArrayLike* out) noexcept {
    __try {
        std::memcpy(out, src, sizeof(TArrayLike));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static bool seh_read_int64(const void* src, int64_t* out) noexcept {
    __try { *out = *reinterpret_cast<const int64_t*>(src); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// Attempt to resolve an FName; wraps the UEVR call in a guard so a bad probe
// doesn't poison the caller. to_string uses C++ objects internally, so we
// call it outside SEH after we've already validated with the cheap probes.
static std::wstring safe_fname_to_string(uevr::API::FName* fname) {
    // No SEH here — to_string allocates; we just use a C++ try/catch.
    try { return fname->to_string(); } catch (...) { return L""; }
}

static std::vector<UEnumEntry> try_dump_uenum_names(uevr::API::UEnum* uenum) {
    std::vector<UEnumEntry> out;
    if (!uenum) return out;

    // Candidate offsets for UEnum::Names (TArray<TPair<FName, int64>>).
    // Range covers UE4.26–UE5.3; earlier versions used a TMap and are unsupported.
    static constexpr int kCandidateOffsets[] = { 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60 };
    constexpr size_t kEntrySize = 16; // FName(8) + int64(8), 8-byte aligned

    for (int off : kCandidateOffsets) {
        auto base = reinterpret_cast<uint8_t*>(uenum) + off;
        if (!probe_mem_readable(base, sizeof(TArrayLike))) continue;

        TArrayLike arr{};
        if (!seh_read_tarray(base, &arr)) continue;
        if (!arr.data || arr.count <= 0 || arr.count > 4096) continue;
        if (arr.capacity < arr.count) continue;
        if (!probe_mem_readable(arr.data, (size_t)arr.count * kEntrySize)) continue;

        // Validate first up-to-3 entries: FName fields in reasonable range, int64 value readable.
        bool plausible = true;
        int check_n = arr.count < 3 ? arr.count : 3;
        for (int i = 0; i < check_n; ++i) {
            auto e = reinterpret_cast<uint8_t*>(arr.data) + (size_t)i * kEntrySize;
            uint64_t fname_bits = 0, value_bits = 0;
            if (!seh_read_u64x2(e, &fname_bits, &value_bits)) { plausible = false; break; }
            int32_t cmp = (int32_t)(fname_bits & 0xFFFFFFFFu);
            int32_t num = (int32_t)(fname_bits >> 32);
            if (cmp < 0 || cmp > 0x0A000000) { plausible = false; break; }
            if (num < 0 || num > 0x00010000) { plausible = false; break; }
        }
        if (!plausible) continue;

        // Resolve the first entry's FName to sanity-check the offset.
        auto first_name = safe_fname_to_string(reinterpret_cast<uevr::API::FName*>(arr.data));
        if (first_name.empty()) continue;

        // Good offset — collect all entries.
        out.reserve((size_t)arr.count);
        for (int i = 0; i < arr.count; ++i) {
            auto e = reinterpret_cast<uint8_t*>(arr.data) + (size_t)i * kEntrySize;
            auto name_w = safe_fname_to_string(reinterpret_cast<uevr::API::FName*>(e));
            int64_t value = 0;
            seh_read_int64(e + 8, &value);
            out.push_back({ JsonHelpers::wide_to_utf8(name_w), value });
        }
        return out;
    }

    return out;
}

namespace ObjectExplorer {

json search_objects(const std::string& query, int limit) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto array = API::FUObjectArray::get();
    if (!array) return json{{"error", "GUObjectArray not available"}};

    json results = json::array();
    int count = array->get_object_count();

    for (int i = 0; i < count && (int)results.size() < limit; ++i) {
        auto obj = array->get_object(i);
        if (!obj) continue;

        auto full_name = obj->get_full_name();
        auto name_utf8 = JsonHelpers::wide_to_utf8(full_name);

        if (contains_ci(name_utf8, query)) {
            json entry;
            entry["address"] = JsonHelpers::address_to_string(obj);
            entry["fullName"] = name_utf8;

            auto cls = obj->get_class();
            if (cls && cls->get_fname()) {
                entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
            }

            results.push_back(entry);
        }
    }

    return json{{"results", results}, {"count", results.size()}, {"total_scanned", count}};
}

json search_classes(const std::string& query, int limit) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto array = API::FUObjectArray::get();
    if (!array) return json{{"error", "GUObjectArray not available"}};

    json results = json::array();
    int count = array->get_object_count();

    for (int i = 0; i < count && (int)results.size() < limit; ++i) {
        auto obj = array->get_object(i);
        if (!obj) continue;

        auto cls = obj->get_class();
        if (!cls) continue;

        // Check if this is a UClass (class of class should be "Class")
        auto meta_cls = cls->get_class();
        if (!meta_cls) continue;
        auto meta_name = meta_cls->get_fname()->to_string();
        if (meta_name != L"Class") continue;

        auto full_name = obj->get_full_name();
        auto name_utf8 = JsonHelpers::wide_to_utf8(full_name);

        if (contains_ci(name_utf8, query)) {
            json entry;
            entry["address"] = JsonHelpers::address_to_string(obj);
            entry["fullName"] = name_utf8;
            entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
            results.push_back(entry);
        }
    }

    return json{{"results", results}, {"count", results.size()}};
}

json enumerate_fields(API::UStruct* ustruct) {
    json fields = json::array();
    if (!ustruct) return fields;

    for (auto current = ustruct; current != nullptr; current = current->get_super()) {
        for (auto field = current->get_child_properties(); field != nullptr; field = field->get_next()) {
            auto fc = field->get_class();
            if (!fc) continue;

            auto class_name_w = fc->get_fname()->to_string();
            auto class_name = JsonHelpers::wide_to_utf8(class_name_w);

            if (class_name.find("Property") == std::string::npos) continue;

            auto fprop = reinterpret_cast<API::FProperty*>(field);
            auto name = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());

            json f;
            f["name"] = name;
            f["type"] = class_name;
            f["offset"] = fprop->get_offset();

            // Extra info for specific types
            if (class_name == "StructProperty") {
                auto sp = reinterpret_cast<API::FStructProperty*>(fprop);
                auto ss = sp->get_struct();
                if (ss && ss->get_fname()) {
                    f["structType"] = JsonHelpers::wide_to_utf8(ss->get_fname()->to_string());
                }
            } else if (class_name == "ObjectProperty") {
                // ObjectProperty doesn't expose the class in the C API easily
            } else if (class_name == "ArrayProperty") {
                auto ap = reinterpret_cast<API::FArrayProperty*>(fprop);
                auto inner = ap->get_inner();
                if (inner) {
                    auto inner_c = inner->get_class();
                    if (inner_c && inner_c->get_fname()) {
                        f["innerType"] = JsonHelpers::wide_to_utf8(inner_c->get_fname()->to_string());
                    }
                }
            } else if (class_name == "EnumProperty") {
                auto ep = reinterpret_cast<API::FEnumProperty*>(fprop);
                auto uenum = ep->get_enum();
                if (uenum && uenum->get_fname()) {
                    f["enumType"] = JsonHelpers::wide_to_utf8(uenum->get_fname()->to_string());
                }
            }

            // Owner class
            auto owner_name = current->get_fname();
            if (owner_name) f["owner"] = JsonHelpers::wide_to_utf8(owner_name->to_string());

            fields.push_back(f);
        }
    }

    return fields;
}

// Own-only variant: does not walk supers. Used by the bulk reflection dumper where
// supers are emitted as their own types and the SDK/USMAP consumers filter by owner
// anyway — walking supers was 3–5× wasted work on UE hierarchies and pushed per-batch
// game-thread time past its budget on methods=true dumps.
static json enumerate_own_methods(API::UStruct* ustruct) {
    json methods = json::array();
    if (!ustruct) return methods;

    for (auto child = ustruct->get_children(); child != nullptr; child = child->get_next()) {
        auto child_cls = child->get_class();
        if (!child_cls) continue;
        auto child_cls_name = child_cls->get_fname()->to_string();
        if (child_cls_name != L"Function") continue;

        auto func = reinterpret_cast<API::UFunction*>(child);
        json m;
        m["name"] = JsonHelpers::wide_to_utf8(func->get_fname()->to_string());

        json params = json::array();
        std::string return_type;
        for (auto param = func->get_child_properties(); param != nullptr; param = param->get_next()) {
            auto pc = param->get_class();
            if (!pc) continue;
            auto pc_name = JsonHelpers::wide_to_utf8(pc->get_fname()->to_string());
            if (pc_name.find("Property") == std::string::npos) continue;

            auto fparam = reinterpret_cast<API::FProperty*>(param);
            auto pname = JsonHelpers::wide_to_utf8(fparam->get_fname()->to_string());
            if (fparam->is_return_param()) { return_type = pc_name; continue; }

            json p;
            p["name"] = pname;
            p["type"] = pc_name;
            if (fparam->is_out_param()) p["out"] = true;
            params.push_back(p);
        }
        m["params"] = params;
        if (!return_type.empty()) m["returnType"] = return_type;
        if (auto on = ustruct->get_fname()) m["owner"] = JsonHelpers::wide_to_utf8(on->to_string());
        methods.push_back(m);
    }
    return methods;
}

json enumerate_methods(API::UStruct* ustruct) {
    json methods = json::array();
    if (!ustruct) return methods;

    for (auto current = ustruct; current != nullptr; current = current->get_super()) {
        // Walk UField children chain — functions are UField children linked via get_next()
        for (auto child = current->get_children(); child != nullptr; child = child->get_next()) {
            auto child_cls = child->get_class();
            if (!child_cls) continue;

            auto child_cls_name = child_cls->get_fname()->to_string();
            if (child_cls_name != L"Function") continue;

            auto func = reinterpret_cast<API::UFunction*>(child);
            auto name = JsonHelpers::wide_to_utf8(func->get_fname()->to_string());

            json m;
            m["name"] = name;

            // Enumerate parameters
            json params = json::array();
            std::string return_type;

            for (auto param = func->get_child_properties(); param != nullptr; param = param->get_next()) {
                auto pc = param->get_class();
                if (!pc) continue;
                auto pc_name = JsonHelpers::wide_to_utf8(pc->get_fname()->to_string());
                if (pc_name.find("Property") == std::string::npos) continue;

                auto fparam = reinterpret_cast<API::FProperty*>(param);
                auto pname = JsonHelpers::wide_to_utf8(fparam->get_fname()->to_string());

                if (fparam->is_return_param()) {
                    return_type = pc_name;
                    continue;
                }

                json p;
                p["name"] = pname;
                p["type"] = pc_name;
                if (fparam->is_out_param()) p["out"] = true;
                params.push_back(p);
            }

            m["params"] = params;
            if (!return_type.empty()) m["returnType"] = return_type;

            auto owner_name = current->get_fname();
            if (owner_name) m["owner"] = JsonHelpers::wide_to_utf8(owner_name->to_string());

            methods.push_back(m);
        }
    }

    return methods;
}

json get_type_info(const std::string& type_name) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto wname = JsonHelpers::utf8_to_wide(type_name);

    // Try finding as-is first
    auto ustruct = api->find_uobject<API::UStruct>(wname);

    // Try common prefixes
    if (!ustruct) ustruct = api->find_uobject<API::UStruct>(L"Class " + wname);
    if (!ustruct) ustruct = api->find_uobject<API::UStruct>(L"ScriptStruct " + wname);

    if (!ustruct) {
        return json{{"error", "Type not found: " + type_name}};
    }

    json result;
    result["name"] = JsonHelpers::wide_to_utf8(ustruct->get_fname()->to_string());
    result["fullName"] = JsonHelpers::wide_to_utf8(ustruct->get_full_name());
    result["address"] = JsonHelpers::address_to_string(ustruct);
    result["propertiesSize"] = ustruct->get_properties_size();

    auto super = ustruct->get_super();
    if (super && super->get_fname()) {
        result["super"] = JsonHelpers::wide_to_utf8(super->get_fname()->to_string());
    }

    result["fields"] = enumerate_fields(ustruct);
    result["methods"] = enumerate_methods(ustruct);

    return result;
}

json inspect_object(uintptr_t address, int depth) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    // Validate object is alive
    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid at " + JsonHelpers::address_to_string(address)}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    json result;
    result["address"] = JsonHelpers::address_to_string(address);
    result["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
    result["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());

    auto super = cls->get_super();
    if (super && super->get_fname()) {
        result["super"] = JsonHelpers::wide_to_utf8(super->get_fname()->to_string());
    }

    result["fields"] = PropertyReader::read_all_properties(obj, cls, depth);
    result["methods"] = enumerate_methods(cls);

    return result;
}

json summarize_object(uintptr_t address) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    json result;
    result["address"] = JsonHelpers::address_to_string(address);
    result["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
    result["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());

    // One-line summaries of each field
    json fields = json::object();
    for (API::UStruct* current = cls; current != nullptr; current = current->get_super()) {
        for (auto field = current->get_child_properties(); field != nullptr; field = field->get_next()) {
            auto fc = field->get_class();
            if (!fc) continue;
            auto class_name = fc->get_fname()->to_string();
            if (class_name.find(L"Property") == std::wstring::npos) continue;

            auto fprop = reinterpret_cast<API::FProperty*>(field);
            auto name = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());
            auto type = JsonHelpers::wide_to_utf8(class_name);

            try {
                // Read with depth=0 for compact output
                auto val = PropertyReader::read_property(obj, fprop, 0);
                // Summarize: for objects just show address, for structs just show "<struct>"
                if (val.is_object() && val.contains("address")) {
                    fields[name] = type + " -> " + val.value("className", "?") + " @ " + val.value("address", "?");
                } else if (val.is_string()) {
                    auto s = val.get<std::string>();
                    if (s.size() > 80) s = s.substr(0, 80) + "...";
                    fields[name] = type + " = \"" + s + "\"";
                } else if (val.is_number()) {
                    fields[name] = type + " = " + val.dump();
                } else if (val.is_boolean()) {
                    fields[name] = type + " = " + (val.get<bool>() ? "true" : "false");
                } else if (val.is_null()) {
                    fields[name] = type + " = null";
                } else {
                    fields[name] = type;
                }
            } catch (...) {
                fields[name] = type + " = <error>";
            }
        }
    }
    result["fields"] = fields;

    return result;
}

json read_field(uintptr_t address, const std::string& field_name) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    auto wname = JsonHelpers::utf8_to_wide(field_name);
    auto prop = cls->find_property(wname.c_str());
    if (!prop) {
        return json{{"error", "Field '" + field_name + "' not found"}};
    }

    json result;
    result["field"] = field_name;
    result["type"] = PropertyReader::get_property_type_name(prop);
    result["value"] = PropertyReader::read_property(obj, prop, 3);

    return result;
}

json find_objects_by_class(const std::string& class_name, int limit) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto wname = JsonHelpers::utf8_to_wide(class_name);

    // Find the class
    auto cls = api->find_uobject<API::UClass>(L"Class " + wname);
    if (!cls) cls = api->find_uobject<API::UClass>(wname);
    if (!cls) {
        return json{{"error", "Class not found: " + class_name}};
    }

    // Use get_objects_matching to find instances
    auto instances = cls->get_objects_matching<API::UObject>(false);

    json results = json::array();
    int count = 0;
    for (auto inst : instances) {
        if (count >= limit) break;
        if (!inst) continue;

        json entry;
        entry["address"] = JsonHelpers::address_to_string(inst);
        entry["fullName"] = JsonHelpers::wide_to_utf8(inst->get_full_name());
        auto ic = inst->get_class();
        if (ic && ic->get_fname()) {
            entry["className"] = JsonHelpers::wide_to_utf8(ic->get_fname()->to_string());
        }
        results.push_back(entry);
        count++;
    }

    return json{{"className", class_name}, {"results", results}, {"count", results.size()}};
}

json chain_query(uintptr_t start_address, const json& steps) {
    if (!steps.is_array() || steps.empty()) {
        return json{{"error", "Steps must be a non-empty array"}};
    }

    // Working set: list of object addresses we're currently operating on
    std::vector<uintptr_t> current = {start_address};
    static constexpr int MAX_SET_SIZE = 500;
    static constexpr int MAX_STEPS = 20;

    int step_idx = 0;
    for (const auto& step : steps) {
        if (step_idx++ >= MAX_STEPS) {
            return json{{"error", "Too many steps (max 20)"}};
        }

        auto type = step.value("type", "");

        if (type == "field") {
            auto name = step.value("name", "");
            if (name.empty()) return json{{"error", "field step requires 'name'"}};

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                auto result = read_field(addr, name);
                if (result.contains("error")) continue;
                auto& val = result["value"];
                if (val.is_object() && val.contains("address")) {
                    auto child = JsonHelpers::string_to_address(val["address"].get<std::string>());
                    if (child != 0) next.push_back(child);
                }
            }
            current = next;
        }
        else if (type == "method") {
            auto name = step.value("name", "");
            if (name.empty()) return json{{"error", "method step requires 'name'"}};

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                auto result = FunctionCaller::call_getter(addr, name);
                if (result.contains("error")) continue;
                auto& val = result["result"];
                if (val.is_object() && val.contains("address")) {
                    auto child = JsonHelpers::string_to_address(val["address"].get<std::string>());
                    if (child != 0) next.push_back(child);
                }
            }
            current = next;
        }
        else if (type == "array") {
            auto name = step.value("name", "");
            if (name.empty()) return json{{"error", "array step requires 'name' (field name of array)"}};

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                auto result = read_field(addr, name);
                if (result.contains("error")) continue;
                auto& val = result["value"];
                if (val.is_array()) {
                    for (auto& elem : val) {
                        if (elem.is_object() && elem.contains("address")) {
                            auto child = JsonHelpers::string_to_address(elem["address"].get<std::string>());
                            if (child != 0) {
                                next.push_back(child);
                                if ((int)next.size() >= MAX_SET_SIZE) break;
                            }
                        }
                    }
                }
                if ((int)next.size() >= MAX_SET_SIZE) break;
            }
            current = next;
        }
        else if (type == "filter") {
            auto method = step.value("method", "");
            auto field = step.value("field", "");
            auto expected = step.value("value", json(true));

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                bool pass = false;
                if (!method.empty()) {
                    auto result = FunctionCaller::call_getter(addr, method);
                    if (result.contains("result")) {
                        pass = (result["result"] == expected);
                    }
                } else if (!field.empty()) {
                    auto result = read_field(addr, field);
                    if (result.contains("value")) {
                        pass = (result["value"] == expected);
                    }
                }
                if (pass) next.push_back(addr);
            }
            current = next;
        }
        else if (type == "collect") {
            auto fields = step.value("fields", json::array());
            auto methods = step.value("methods", json::array());

            json results = json::array();
            for (auto addr : current) {
                json entry;
                entry["address"] = JsonHelpers::address_to_string(addr);

                // Get class name
                auto obj = reinterpret_cast<API::UObject*>(addr);
                if (API::UObjectHook::exists(obj)) {
                    auto cls = obj->get_class();
                    if (cls && cls->get_fname()) {
                        entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
                    }
                }

                for (const auto& f : fields) {
                    auto fname = f.get<std::string>();
                    auto result = read_field(addr, fname);
                    entry[fname] = result.contains("value") ? result["value"] : result;
                }
                for (const auto& m : methods) {
                    auto mname = m.get<std::string>();
                    auto result = FunctionCaller::call_getter(addr, mname);
                    entry[mname] = result.contains("result") ? result["result"] : result;
                }

                results.push_back(entry);
            }
            return json{{"results", results}, {"count", results.size()}};
        }
        else {
            return json{{"error", "Unknown step type: " + type}};
        }

        if (current.empty()) {
            return json{{"results", json::array()}, {"count", 0}, {"note", "Chain ended at step " + std::to_string(step_idx) + " (no results)"}};
        }
    }

    // No collect step — return final addresses
    json results = json::array();
    for (auto addr : current) {
        json entry;
        entry["address"] = JsonHelpers::address_to_string(addr);
        auto obj = reinterpret_cast<API::UObject*>(addr);
        if (API::UObjectHook::exists(obj)) {
            auto cls = obj->get_class();
            if (cls && cls->get_fname()) {
                entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
            }
            entry["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());
        }
        results.push_back(entry);
    }
    return json{{"addresses", results}, {"count", results.size()}};
}

json get_singletons() {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    json singletons = json::array();

    // Game Engine
    auto engine = api->get_engine();
    if (engine) {
        json e;
        e["name"] = "GameEngine";
        e["address"] = JsonHelpers::address_to_string(engine);
        auto cls = engine->get_class();
        if (cls && cls->get_fname()) e["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        singletons.push_back(e);

        // Navigate engine to find GameInstance and World
        auto engine_cls = engine->get_class();
        if (engine_cls) {
            // Try GameInstance field
            auto gi_prop = engine_cls->find_property(L"GameInstance");
            if (gi_prop) {
                auto gi_offset = reinterpret_cast<API::FProperty*>(gi_prop)->get_offset();
                auto gi = *reinterpret_cast<API::UObject**>(reinterpret_cast<uintptr_t>(engine) + gi_offset);
                if (gi) {
                    json g;
                    g["name"] = "GameInstance";
                    g["address"] = JsonHelpers::address_to_string(gi);
                    auto gc = gi->get_class();
                    if (gc && gc->get_fname()) g["className"] = JsonHelpers::wide_to_utf8(gc->get_fname()->to_string());
                    singletons.push_back(g);
                }
            }
        }
    }

    // Player Controller
    auto controller = api->get_player_controller(0);
    if (controller) {
        json e;
        e["name"] = "PlayerController";
        e["address"] = JsonHelpers::address_to_string(controller);
        auto cls = controller->get_class();
        if (cls && cls->get_fname()) e["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        singletons.push_back(e);
    }

    // Local Pawn
    auto pawn = api->get_local_pawn(0);
    if (pawn) {
        json e;
        e["name"] = "LocalPawn";
        e["address"] = JsonHelpers::address_to_string(pawn);
        auto cls = pawn->get_class();
        if (cls && cls->get_fname()) e["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        singletons.push_back(e);
    }

    // Search for common singleton types via GUObjectArray
    auto array = API::FUObjectArray::get();
    if (array) {
        int count = array->get_object_count();

        // Types we look for as singletons
        static const std::vector<std::wstring> singleton_types = {
            L"World", L"GameMode", L"GameModeBase",
            L"GameState", L"GameStateBase",
            L"GameSession",
            L"HUD", L"CheatManager"
        };

        for (int i = 0; i < count; ++i) {
            auto obj = array->get_object(i);
            if (!obj) continue;

            auto cls = obj->get_class();
            if (!cls) continue;

            auto cls_name = cls->get_fname()->to_string();
            auto meta_cls = cls->get_class();
            if (!meta_cls) continue;

            // Check if this object's class name matches any of our singleton types
            for (const auto& target : singleton_types) {
                if (cls_name.find(target) != std::wstring::npos) {
                    // Check it's not a CDO (default object)
                    auto full = obj->get_full_name();
                    if (full.find(L"Default__") != std::wstring::npos) continue;

                    json e;
                    e["name"] = JsonHelpers::wide_to_utf8(cls_name);
                    e["address"] = JsonHelpers::address_to_string(obj);
                    e["fullName"] = JsonHelpers::wide_to_utf8(full);
                    e["className"] = JsonHelpers::wide_to_utf8(cls_name);
                    singletons.push_back(e);
                    break;
                }
            }
        }
    }

    return json{{"singletons", singletons}, {"count", singletons.size()}};
}

json get_singleton(const std::string& type_name) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto wname = JsonHelpers::utf8_to_wide(type_name);

    // Try to find the class
    auto cls = api->find_uobject<API::UClass>(L"Class " + wname);
    if (!cls) cls = api->find_uobject<API::UClass>(wname);
    if (!cls) {
        return json{{"error", "Class not found: " + type_name}};
    }

    // Find the first live instance
    auto instances = cls->get_objects_matching<API::UObject>(false);
    for (auto inst : instances) {
        if (!inst) continue;
        // Skip default objects
        auto full = inst->get_full_name();
        if (full.find(L"Default__") != std::wstring::npos) continue;

        json result;
        result["address"] = JsonHelpers::address_to_string(inst);
        result["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        result["fullName"] = JsonHelpers::wide_to_utf8(full);
        return result;
    }

    return json{{"error", "No live instance of " + type_name}};
}

json read_array(uintptr_t address, const std::string& field_name, int offset, int limit) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    auto wname = JsonHelpers::utf8_to_wide(field_name);
    auto prop = cls->find_property(wname.c_str());
    if (!prop) return json{{"error", "Field not found: " + field_name}};

    // Get property type
    auto propc = reinterpret_cast<API::FProperty*>(prop)->get_class();
    if (!propc) return json{{"error", "Property has no class"}};
    auto type_str = propc->get_fname()->to_string();

    if (type_str != L"ArrayProperty") {
        return json{{"error", "Field '" + field_name + "' is not an array"}};
    }

    auto ap = reinterpret_cast<API::FArrayProperty*>(prop);
    auto inner = ap->get_inner();
    if (!inner) return json{{"error", "Array has no inner type"}};

    auto inner_c = inner->get_class();
    if (!inner_c) return json{{"error", "Inner type has no class"}};
    auto inner_type = inner_c->get_fname()->to_string();

    auto fprop = reinterpret_cast<API::FProperty*>(prop);
    auto prop_addr = reinterpret_cast<uintptr_t>(obj) + fprop->get_offset();

    struct TArrayRaw { void* data; int32_t count; int32_t capacity; };
    auto& arr = *reinterpret_cast<TArrayRaw*>(prop_addr);

    int total = arr.count;
    if (!arr.data || total <= 0) {
        return json{{"field", field_name}, {"totalCount", 0}, {"offset", offset}, {"limit", limit}, {"elements", json::array()}};
    }

    if (offset < 0) offset = 0;
    if (offset >= total) {
        return json{{"field", field_name}, {"totalCount", total}, {"offset", offset}, {"limit", limit}, {"elements", json::array()}};
    }

    int end = std::min(offset + limit, total);

    json elements = json::array();
    auto inner_type_utf8 = JsonHelpers::wide_to_utf8(inner_type);

    if (inner_type == L"ObjectProperty" || inner_type == L"InterfaceProperty") {
        auto objs = reinterpret_cast<API::UObject**>(arr.data);
        for (int i = offset; i < end; ++i) {
            if (!objs[i]) { elements.push_back(nullptr); continue; }
            json elem;
            elem["index"] = i;
            elem["address"] = JsonHelpers::address_to_string(objs[i]);
            auto ec = objs[i]->get_class();
            if (ec && ec->get_fname()) elem["className"] = JsonHelpers::wide_to_utf8(ec->get_fname()->to_string());
            elements.push_back(elem);
        }
    } else if (inner_type == L"StructProperty") {
        auto inner_sp = reinterpret_cast<API::FStructProperty*>(inner);
        auto inner_struct = inner_sp->get_struct();
        if (inner_struct) {
            auto elem_size = inner_struct->get_struct_size();
            for (int i = offset; i < end; ++i) {
                auto elem_addr = reinterpret_cast<uintptr_t>(arr.data) + i * elem_size;
                json elem;
                elem["index"] = i;
                elem["value"] = PropertyReader::read_property(reinterpret_cast<void*>(prop_addr), inner, 2);
                // Read individual struct fields
                for (auto sp = inner_struct->get_child_properties(); sp; sp = sp->get_next()) {
                    auto spc = sp->get_class();
                    if (!spc) continue;
                    auto scn = spc->get_fname()->to_string();
                    if (scn.find(L"Property") == std::wstring::npos) continue;
                    auto sfp = reinterpret_cast<API::FProperty*>(sp);
                    auto sname = JsonHelpers::wide_to_utf8(sfp->get_fname()->to_string());
                    elem[sname] = PropertyReader::read_property(reinterpret_cast<void*>(elem_addr), sfp, 2);
                }
                elements.push_back(elem);
            }
        }
    } else if (inner_type == L"FloatProperty") {
        auto vals = reinterpret_cast<float*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"DoubleProperty") {
        auto vals = reinterpret_cast<double*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"IntProperty") {
        auto vals = reinterpret_cast<int32_t*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"ByteProperty") {
        auto vals = reinterpret_cast<uint8_t*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"BoolProperty") {
        auto vals = reinterpret_cast<uint8_t*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i] != 0}});
    } else if (inner_type == L"NameProperty") {
        auto fnames = reinterpret_cast<API::FName*>(arr.data);
        for (int i = offset; i < end; ++i) {
            elements.push_back(json{{"index", i}, {"value", JsonHelpers::wide_to_utf8(fnames[i].to_string())}});
        }
    } else {
        return json{{"field", field_name}, {"totalCount", total}, {"innerType", inner_type_utf8}, {"note", "Unsupported array element type for pagination"}};
    }

    return json{
        {"field", field_name}, {"innerType", inner_type_utf8},
        {"totalCount", total}, {"offset", offset}, {"limit", limit},
        {"returnedCount", elements.size()}, {"elements", elements}
    };
}

json read_memory(uintptr_t address, int size) {
    if (size <= 0 || size > 8192) {
        return json{{"error", "Size must be 1-8192 bytes"}};
    }

    std::vector<uint8_t> buffer(size, 0);
    try {
        memcpy(buffer.data(), reinterpret_cast<void*>(address), size);
    } catch (...) {
        return json{{"error", "Access violation reading memory at " + JsonHelpers::address_to_string(address)}};
    }

    // Format as hex dump with ASCII sidebar
    std::string hex_dump;
    for (int row = 0; row < size; row += 16) {
        // Address column
        char addr_buf[32];
        snprintf(addr_buf, sizeof(addr_buf), "%016llX  ", (unsigned long long)(address + row));
        hex_dump += addr_buf;

        // Hex bytes
        for (int col = 0; col < 16; col++) {
            int idx = row + col;
            if (idx < size) {
                char hex[4];
                snprintf(hex, sizeof(hex), "%02X ", buffer[idx]);
                hex_dump += hex;
            } else {
                hex_dump += "   ";
            }
            if (col == 7) hex_dump += " ";
        }

        // ASCII sidebar
        hex_dump += " |";
        for (int col = 0; col < 16 && (row + col) < size; col++) {
            uint8_t b = buffer[row + col];
            hex_dump += (b >= 0x20 && b < 0x7F) ? (char)b : '.';
        }
        hex_dump += "|\n";
    }

    return json{
        {"address", JsonHelpers::address_to_string(address)},
        {"size", size},
        {"dump", hex_dump}
    };
}

json read_typed(uintptr_t address, const std::string& type, int count, int stride) {
    if (count <= 0) count = 1;
    if (count > 50) count = 50;

    // Auto-calculate stride from type if not specified
    if (stride <= 0) {
        if (type == "u8" || type == "i8") stride = 1;
        else if (type == "u16" || type == "i16") stride = 2;
        else if (type == "u32" || type == "i32" || type == "f32") stride = 4;
        else if (type == "u64" || type == "i64" || type == "f64" || type == "ptr") stride = 8;
        else return json{{"error", "Unknown type: " + type + ". Valid: u8, i8, u16, i16, u32, i32, u64, i64, f32, f64, ptr"}};
    }

    json values = json::array();
    for (int i = 0; i < count; i++) {
        uintptr_t addr = address + (uintptr_t)i * stride;
        try {
            if (type == "u8") values.push_back(*reinterpret_cast<uint8_t*>(addr));
            else if (type == "i8") values.push_back(*reinterpret_cast<int8_t*>(addr));
            else if (type == "u16") values.push_back(*reinterpret_cast<uint16_t*>(addr));
            else if (type == "i16") values.push_back(*reinterpret_cast<int16_t*>(addr));
            else if (type == "u32") values.push_back(*reinterpret_cast<uint32_t*>(addr));
            else if (type == "i32") values.push_back(*reinterpret_cast<int32_t*>(addr));
            else if (type == "u64") values.push_back(*reinterpret_cast<uint64_t*>(addr));
            else if (type == "i64") values.push_back(*reinterpret_cast<int64_t*>(addr));
            else if (type == "f32") values.push_back(*reinterpret_cast<float*>(addr));
            else if (type == "f64") values.push_back(*reinterpret_cast<double*>(addr));
            else if (type == "ptr") values.push_back(JsonHelpers::address_to_string(*reinterpret_cast<uintptr_t*>(addr)));
        } catch (...) {
            values.push_back(json{{"error", "access violation at " + JsonHelpers::address_to_string(addr)}});
        }
    }

    return json{
        {"address", JsonHelpers::address_to_string(address)},
        {"type", type}, {"count", count}, {"stride", stride},
        {"values", values}
    };
}

json get_camera() {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto controller = api->get_player_controller(0);
    if (!controller) return json{{"error", "No player controller"}};

    auto cls = controller->get_class();
    if (!cls) return json{{"error", "Controller has no class"}};

    // Find PlayerCameraManager
    auto cam_prop = cls->find_property(L"PlayerCameraManager");
    if (!cam_prop) return json{{"error", "PlayerCameraManager property not found"}};

    auto cam_fprop = reinterpret_cast<API::FProperty*>(cam_prop);
    auto cam_addr = reinterpret_cast<uintptr_t>(controller) + cam_fprop->get_offset();
    auto cam_manager = *reinterpret_cast<API::UObject**>(cam_addr);

    if (!cam_manager) return json{{"error", "PlayerCameraManager is null"}};

    json result;
    result["cameraManager"]["address"] = JsonHelpers::address_to_string(cam_manager);
    auto cam_cls = cam_manager->get_class();
    if (cam_cls && cam_cls->get_fname()) {
        result["cameraManager"]["className"] = JsonHelpers::wide_to_utf8(cam_cls->get_fname()->to_string());
    }

    if (!cam_cls) return result;

    // Helper to call a getter method on the camera manager
    auto try_getter = [&](const wchar_t* method_name) -> json {
        auto func = cam_cls->find_function(method_name);
        if (!func) return nullptr;

        // Check for 0 required params
        for (auto p = func->get_child_properties(); p; p = p->get_next()) {
            auto fp = reinterpret_cast<API::FProperty*>(p);
            if (fp->is_param() && !fp->is_return_param()) return nullptr;
        }

        auto ps = func->get_properties_size();
        auto ma = func->get_min_alignment();
        std::vector<uint8_t> params;
        if (ma > 1) params.resize(((ps + ma - 1) / ma) * ma, 0);
        else params.resize(ps, 0);

        cam_manager->process_event(func, params.data());

        for (auto p = func->get_child_properties(); p; p = p->get_next()) {
            auto fp = reinterpret_cast<API::FProperty*>(p);
            if (!fp->is_return_param()) continue;
            auto pc = fp->get_class();
            if (pc && pc->get_fname()->to_string() == L"BoolProperty") {
                return json(reinterpret_cast<API::FBoolProperty*>(fp)->get_value_from_object(params.data()));
            }
            return PropertyReader::read_property(params.data(), fp, 2);
        }
        return nullptr;
    };

    // Try standard UE camera methods
    auto loc = try_getter(L"GetCameraLocation");
    if (!loc.is_null()) result["position"] = loc;

    auto rot = try_getter(L"GetCameraRotation");
    if (!rot.is_null()) result["rotation"] = rot;

    auto fov = try_getter(L"GetFOVAngle");
    if (!fov.is_null()) result["fov"] = fov;

    // Also try K2_ variants (Blueprint-exposed versions)
    if (loc.is_null()) {
        loc = try_getter(L"K2_GetActorLocation");
        if (!loc.is_null()) result["position"] = loc;
    }

    // Read useful fields from CameraManager if methods didn't work
    if (result.find("fov") == result.end()) {
        auto fov_prop = cam_cls->find_property(L"DefaultFOV");
        if (fov_prop) {
            auto fp = reinterpret_cast<API::FProperty*>(fov_prop);
            result["fov"] = PropertyReader::read_property(cam_manager, fp, 0);
        }
    }

    return result;
}

// ── Bulk reflection dump ────────────────────────────────────────────
//
// Walks GUObjectArray once, classifies every object as UClass / UScriptStruct / UEnum,
// and emits a consolidated JSON schema. Drives MCP tools that produce USMAPs,
// C++ SDK headers, or raw JSON dumps — done server-side so clients don't need
// thousands of round-trips.
//
// Core worker: walks [begin, end) of GUObjectArray into the provided json arrays.
// Kept as a free function so dump_reflection (unbounded) and dump_reflection_batch
// (paginated) share exactly the same per-object logic.
// Pointer-compare gate: look up the well-known meta-type UClass pointers once per
// dump and check object->get_class() == one of them. A pointer compare is O(1);
// the old path called FName::to_string per object (~1ms each × 4000 = 4s of
// per-batch game-thread time spent just classifying).
struct KindGate {
    uevr::API::UStruct* c_class = nullptr;
    uevr::API::UStruct* c_bp_class = nullptr;
    uevr::API::UStruct* c_anim_bp_class = nullptr;
    uevr::API::UStruct* c_widget_bp_class = nullptr;
    uevr::API::UStruct* c_script_struct = nullptr;
    uevr::API::UStruct* c_user_struct = nullptr;
    uevr::API::UStruct* c_enum = nullptr;
    uevr::API::UStruct* c_user_enum = nullptr;
};

static KindGate build_kind_gate() {
    auto& api = uevr::API::get();
    KindGate g{};
    if (!api) return g;
    g.c_class            = api->find_uobject<uevr::API::UStruct>(L"Class /Script/CoreUObject.Class");
    g.c_bp_class         = api->find_uobject<uevr::API::UStruct>(L"Class /Script/Engine.BlueprintGeneratedClass");
    g.c_anim_bp_class    = api->find_uobject<uevr::API::UStruct>(L"Class /Script/Engine.AnimBlueprintGeneratedClass");
    g.c_widget_bp_class  = api->find_uobject<uevr::API::UStruct>(L"Class /Script/UMG.WidgetBlueprintGeneratedClass");
    g.c_script_struct    = api->find_uobject<uevr::API::UStruct>(L"Class /Script/CoreUObject.ScriptStruct");
    g.c_user_struct      = api->find_uobject<uevr::API::UStruct>(L"Class /Script/Engine.UserDefinedStruct");
    g.c_enum             = api->find_uobject<uevr::API::UStruct>(L"Class /Script/CoreUObject.Enum");
    g.c_user_enum        = api->find_uobject<uevr::API::UStruct>(L"Class /Script/Engine.UserDefinedEnum");
    return g;
}

static void dump_range(int begin, int end,
                       const std::string& filter,
                       bool include_methods, bool include_enums,
                       json& classes, json& structs, json& enums,
                       int& total_scanned, int& total_matched, int& object_errors)
{
    auto array = API::FUObjectArray::get();
    if (!array) return;

    auto gate = build_kind_gate();

    for (int i = begin; i < end; ++i) {
        // Whole-object guard: GUObjectArray can contain objects in weird intermediate
        // states (half-destructed, being GC'd, non-standard vtables). One bad entry
        // must not abort the whole dump.
        try {
        auto obj = array->get_object(i);
        if (!obj) continue;
        ++total_scanned;

        auto cls = reinterpret_cast<uevr::API::UStruct*>(obj->get_class());
        if (!cls) continue;
        // Pointer compare vs cached meta-class UClass objects. O(1) per check vs
        // O(FName-to-string) for the previous path.
        bool is_class  = (cls == gate.c_class || cls == gate.c_bp_class
                       || cls == gate.c_anim_bp_class || cls == gate.c_widget_bp_class);
        bool is_struct = (cls == gate.c_script_struct || cls == gate.c_user_struct);
        bool is_enum   = (cls == gate.c_enum || cls == gate.c_user_enum);
        if (!is_class && !is_struct && !(include_enums && is_enum)) continue;

        auto full_name_utf8 = JsonHelpers::wide_to_utf8(obj->get_full_name());
        if (!filter.empty() && !contains_ci(full_name_utf8, filter)) continue;
        ++total_matched;

        if (is_enum) {
            json e;
            auto uenum = reinterpret_cast<API::UEnum*>(obj);
            e["name"] = JsonHelpers::wide_to_utf8(uenum->get_fname()->to_string());
            e["fullName"] = full_name_utf8;
            e["address"] = JsonHelpers::address_to_string(uenum);

            // Memory-probe UEnum::Names — UEVR's public API doesn't expose it, but
            // the TArray layout at a version-dependent offset is stable enough to
            // detect with validation. Failure returns empty and downstream tools
            // treat the enum as opaque.
            auto entries = try_dump_uenum_names(uenum);
            if (!entries.empty()) {
                json arr = json::array();
                for (auto& en : entries) arr.push_back({{"name", en.name}, {"value", en.value}});
                e["entries"] = arr;
            }
            enums.push_back(e);
            continue;
        }

        auto ustruct = reinterpret_cast<API::UStruct*>(obj);
        json t;
        auto struct_name_utf8 = JsonHelpers::wide_to_utf8(ustruct->get_fname()->to_string());
        t["name"] = struct_name_utf8;
        t["fullName"] = full_name_utf8;
        t["address"] = JsonHelpers::address_to_string(ustruct);
        t["propertiesSize"] = ustruct->get_properties_size();
        if (auto super = ustruct->get_super()) {
            if (super->get_fname()) {
                t["super"] = JsonHelpers::wide_to_utf8(super->get_fname()->to_string());
            }
        }

        // Emit "own" fields only (not walking supers) so USMAP schemas are correctly
        // scoped. Attach the full recursive property tag — this is what USMAP / FModel
        // / CUE4Parse need to parse unversioned assets. Per-field try/catch so a single
        // corrupt property doesn't poison the whole type or the rest of the dump.
        json own_fields = json::array();
        int field_errors = 0;
        try {
            for (auto field = ustruct->get_child_properties(); field != nullptr; field = field->get_next()) {
                try {
                    auto fc = field->get_class();
                    if (!fc) continue;
                    auto class_name = JsonHelpers::wide_to_utf8(fc->get_fname()->to_string());
                    if (class_name.find("Property") == std::string::npos) continue;

                    auto fprop = reinterpret_cast<API::FProperty*>(field);
                    json f;
                    f["name"] = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());
                    f["type"] = class_name;
                    f["offset"] = fprop->get_offset();
                    f["owner"] = struct_name_utf8;
                    f["tag"] = build_property_tag(fprop);
                    own_fields.push_back(f);
                } catch (...) {
                    ++field_errors;
                }
            }
        } catch (...) {
            t["fieldsError"] = "exception while walking child_properties chain";
        }
        t["fields"] = own_fields;
        if (field_errors > 0) t["fieldErrors"] = field_errors;
        if (include_methods) {
            try { t["methods"] = enumerate_own_methods(ustruct); }
            catch (...) { t["methodsError"] = "exception while enumerating methods"; }
        }

        if (is_class) classes.push_back(t);
        else structs.push_back(t);
        } // try
        catch (...) { ++object_errors; }
    }
}

json dump_reflection(const std::string& filter, bool include_methods, bool include_enums) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};
    auto array = API::FUObjectArray::get();
    if (!array) return json{{"error", "GUObjectArray not available"}};

    int count = array->get_object_count();
    json classes = json::array(), structs = json::array(), enums = json::array();
    int total_scanned = 0, total_matched = 0, object_errors = 0;

    dump_range(0, count, filter, include_methods, include_enums,
               classes, structs, enums,
               total_scanned, total_matched, object_errors);

    json result;
    result["classes"] = classes;
    result["structs"] = structs;
    if (include_enums) result["enums"] = enums;
    result["stats"] = {
        {"totalScanned", total_scanned},
        {"totalMatched", total_matched},
        {"classCount",  classes.size()},
        {"structCount", structs.size()},
        {"enumCount",   enums.size()},
        {"objectErrors", object_errors}
    };
    return result;
}

json dump_reflection_batch(int offset, int limit,
                           const std::string& filter,
                           bool include_methods, bool include_enums)
{
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};
    auto array = API::FUObjectArray::get();
    if (!array) return json{{"error", "GUObjectArray not available"}};

    int count = array->get_object_count();
    if (offset < 0) offset = 0;
    if (limit <= 0) limit = 500;
    int end = offset + limit;
    if (end > count) end = count;

    json classes = json::array(), structs = json::array(), enums = json::array();
    int total_scanned = 0, total_matched = 0, object_errors = 0;

    dump_range(offset, end, filter, include_methods, include_enums,
               classes, structs, enums,
               total_scanned, total_matched, object_errors);

    json result;
    result["classes"] = classes;
    result["structs"] = structs;
    if (include_enums) result["enums"] = enums;
    result["offset"] = offset;
    result["nextOffset"] = end;   // caller terminates when nextOffset == totalCount
    result["totalCount"] = count;
    result["done"] = (end >= count);
    result["batchStats"] = {
        {"scanned",  total_scanned},
        {"matched",  total_matched},
        {"errors",   object_errors},
        {"classes",  classes.size()},
        {"structs",  structs.size()},
        {"enums",    enums.size()}
    };
    return result;
}

// ── Raw memory write ────────────────────────────────────────────────
//
// SEH-guarded memcpy into an arbitrary process address. Callers are trusted —
// the MCP server is localhost-only and already exposes arbitrary reads, so
// exposing writes is symmetric. VirtualProtect is NOT called: the MCP is a
// debugging/modding surface, not a generic patcher. For code patches use a
// Lua helper that toggles page protection itself.

static bool seh_memcpy(void* dst, const void* src, size_t n) noexcept {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

json write_memory(uintptr_t address, const std::vector<uint8_t>& bytes) {
    if (!address) return json{{"error", "Null address"}};
    if (bytes.empty()) return json{{"error", "Empty byte buffer"}};
    if (bytes.size() > (1u << 20)) return json{{"error", "Refusing to write > 1MiB in a single call"}};

    void* dst = reinterpret_cast<void*>(address);

    // Query page protection; if not writable, flip it temporarily. Use PAGE_EXECUTE_READWRITE
    // so code pages also work (common use case: NOP-out a signature).
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(dst, &mbi, sizeof(mbi))) {
        return json{{"error", "VirtualQuery failed"}, {"err", (uint64_t)GetLastError()}};
    }

    DWORD old_protect = 0;
    const DWORD writable_mask = PAGE_READWRITE | PAGE_WRITECOPY
                              | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    bool changed = false;
    if ((mbi.Protect & writable_mask) == 0) {
        if (!VirtualProtect(dst, bytes.size(), PAGE_EXECUTE_READWRITE, &old_protect)) {
            return json{{"error", "VirtualProtect failed"}, {"err", (uint64_t)GetLastError()}};
        }
        changed = true;
    }

    bool ok = seh_memcpy(dst, bytes.data(), bytes.size());

    if (changed) {
        DWORD ignore = 0;
        VirtualProtect(dst, bytes.size(), old_protect, &ignore);
        FlushInstructionCache(GetCurrentProcess(), dst, bytes.size());
    }

    if (!ok) return json{{"error", "Access violation during write"}};
    return json{{"ok", true}, {"address", JsonHelpers::address_to_string(address)}, {"bytesWritten", bytes.size()}};
}

} // namespace ObjectExplorer
