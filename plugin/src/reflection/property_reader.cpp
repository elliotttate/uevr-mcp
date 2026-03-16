#include "property_reader.h"
#include "../json_helpers.h"

#include <string>
#include <cstring>

using namespace uevr;

// Helper: check if Vector/Rotator struct and if UE5 (double-based)
static API::UScriptStruct* get_vector_struct() {
    static auto cached = []() -> API::UScriptStruct* {
        auto& api = API::get();
        if (!api) return nullptr;
        auto modern = api->find_uobject<API::UScriptStruct>(L"ScriptStruct /Script/CoreUObject.Vector");
        if (modern) return modern;
        return api->find_uobject<API::UScriptStruct>(L"ScriptStruct /Script/CoreUObject.Object.Vector");
    }();
    return cached;
}

static API::UScriptStruct* get_rotator_struct() {
    static auto cached = []() -> API::UScriptStruct* {
        auto& api = API::get();
        if (!api) return nullptr;
        auto modern = api->find_uobject<API::UScriptStruct>(L"ScriptStruct /Script/CoreUObject.Rotator");
        if (modern) return modern;
        return api->find_uobject<API::UScriptStruct>(L"ScriptStruct /Script/CoreUObject.Object.Rotator");
    }();
    return cached;
}

static bool is_ue5() {
    static auto cached = []() {
        auto vs = get_vector_struct();
        if (!vs) return false;
        // UE5 uses double-based vectors (24 bytes for dvec3)
        return vs->get_struct_size() == 24; // sizeof(double) * 3
    }();
    return cached;
}

// Read fields of a struct into JSON
static json read_struct_fields(void* base, API::UScriptStruct* ustruct, int depth) {
    json result = json::object();

    // Check for Vector/Rotator special cases
    if (ustruct == get_vector_struct() || ustruct == get_rotator_struct()) {
        if (is_ue5()) {
            auto* d = reinterpret_cast<double*>(base);
            result["x"] = d[0];
            result["y"] = d[1];
            result["z"] = d[2];
        } else {
            auto* f = reinterpret_cast<float*>(base);
            result["x"] = f[0];
            result["y"] = f[1];
            result["z"] = f[2];
        }
        return result;
    }

    // Walk all properties of the struct
    for (auto prop = ustruct->get_child_properties(); prop != nullptr; prop = prop->get_next()) {
        auto prop_class = prop->get_class();
        if (!prop_class) continue;

        auto class_name = prop_class->get_fname()->to_string();
        if (class_name.find(L"Property") == std::wstring::npos && class_name != L"Function") continue;

        auto fprop = reinterpret_cast<API::FProperty*>(prop);
        auto name_w = fprop->get_fname()->to_string();
        auto name = JsonHelpers::wide_to_utf8(name_w);

        result[name] = PropertyReader::read_property(base, fprop, depth - 1);
    }

    return result;
}

namespace PropertyReader {

std::string get_property_type_name(API::FProperty* prop) {
    if (!prop) return "Unknown";
    auto pc = prop->get_class();
    if (!pc) return "Unknown";
    auto fn = pc->get_fname();
    if (!fn) return "Unknown";
    return JsonHelpers::wide_to_utf8(fn->to_string());
}

json read_property(void* base, API::FProperty* prop, int depth) {
    if (!base || !prop) return nullptr;

    auto propc = prop->get_class();
    if (!propc) return nullptr;

    auto type_name = propc->get_fname()->to_string();
    auto offset = prop->get_offset();
    auto addr = reinterpret_cast<uintptr_t>(base) + offset;

    // Match type by name
    if (type_name == L"BoolProperty") {
        auto fbp = reinterpret_cast<API::FBoolProperty*>(prop);
        return fbp->get_value_from_object(base);
    }
    if (type_name == L"FloatProperty") {
        return *reinterpret_cast<float*>(addr);
    }
    if (type_name == L"DoubleProperty") {
        return *reinterpret_cast<double*>(addr);
    }
    if (type_name == L"ByteProperty") {
        return *reinterpret_cast<uint8_t*>(addr);
    }
    if (type_name == L"Int8Property") {
        return *reinterpret_cast<int8_t*>(addr);
    }
    if (type_name == L"Int16Property") {
        return *reinterpret_cast<int16_t*>(addr);
    }
    if (type_name == L"UInt16Property") {
        return *reinterpret_cast<uint16_t*>(addr);
    }
    if (type_name == L"IntProperty") {
        return *reinterpret_cast<int32_t*>(addr);
    }
    if (type_name == L"UIntProperty" || type_name == L"UInt32Property") {
        return *reinterpret_cast<uint32_t*>(addr);
    }
    if (type_name == L"Int64Property") {
        return *reinterpret_cast<int64_t*>(addr);
    }
    if (type_name == L"UInt64Property") {
        return *reinterpret_cast<uint64_t*>(addr);
    }
    if (type_name == L"NameProperty") {
        auto fname = reinterpret_cast<API::FName*>(addr);
        auto wstr = fname->to_string();
        return JsonHelpers::wide_to_utf8(wstr);
    }
    if (type_name == L"StrProperty") {
        auto& fstr = *reinterpret_cast<API::TArray<wchar_t>*>(addr);
        if (fstr.data == nullptr || fstr.count == 0) return "";
        // FString count includes null terminator
        int len = fstr.count;
        if (len > 0 && fstr.data[len - 1] == L'\0') len--;
        return JsonHelpers::wide_to_utf8(fstr.data, len);
    }
    if (type_name == L"TextProperty") {
        // FText is complex, just indicate it exists
        return "<FText>";
    }
    if (type_name == L"ObjectProperty" || type_name == L"InterfaceProperty") {
        auto obj = *reinterpret_cast<API::UObject**>(addr);
        if (!obj) return nullptr;

        json result;
        result["address"] = JsonHelpers::address_to_string(obj);
        auto cls = obj->get_class();
        if (cls) {
            auto fn = cls->get_fname();
            if (fn) result["className"] = JsonHelpers::wide_to_utf8(fn->to_string());
        }

        if (depth > 0) {
            // Optionally include full name
            auto full = obj->get_full_name();
            result["fullName"] = JsonHelpers::wide_to_utf8(full);
        }
        return result;
    }
    if (type_name == L"ClassProperty") {
        auto cls = *reinterpret_cast<API::UClass**>(addr);
        if (!cls) return nullptr;

        json result;
        result["address"] = JsonHelpers::address_to_string(cls);
        auto fn = cls->get_fname();
        if (fn) result["className"] = JsonHelpers::wide_to_utf8(fn->to_string());
        return result;
    }
    if (type_name == L"WeakObjectProperty" || type_name == L"SoftObjectProperty" || type_name == L"LazyObjectProperty") {
        // These have complex internal representations; just report address area
        return json{{"type", JsonHelpers::wide_to_utf8(type_name)}, {"offset", offset}};
    }
    if (type_name == L"StructProperty") {
        if (depth <= 0) return "<struct>";

        auto sp = reinterpret_cast<API::FStructProperty*>(prop);
        auto ustruct = sp->get_struct();
        if (!ustruct) return nullptr;

        return read_struct_fields(reinterpret_cast<void*>(addr), ustruct, depth);
    }
    if (type_name == L"ArrayProperty") {
        auto ap = reinterpret_cast<API::FArrayProperty*>(prop);
        auto inner = ap->get_inner();
        if (!inner) return json::array();

        auto inner_c = inner->get_class();
        if (!inner_c) return json::array();

        auto inner_type = inner_c->get_fname()->to_string();
        auto inner_offset = inner->get_offset();

        // TArray layout: { T* data, int32_t count, int32_t capacity }
        struct TArrayRaw { void* data; int32_t count; int32_t capacity; };
        auto& arr = *reinterpret_cast<TArrayRaw*>(addr);

        if (!arr.data || arr.count <= 0) return json::array();

        json result = json::array();
        int limit = (arr.count > 100) ? 100 : arr.count; // Cap at 100 elements

        if (inner_type == L"ObjectProperty" || inner_type == L"InterfaceProperty") {
            auto objs = reinterpret_cast<API::UObject**>(arr.data);
            for (int i = 0; i < limit; ++i) {
                if (!objs[i]) { result.push_back(nullptr); continue; }
                json elem;
                elem["address"] = JsonHelpers::address_to_string(objs[i]);
                auto cls = objs[i]->get_class();
                if (cls && cls->get_fname()) elem["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
                result.push_back(elem);
            }
        } else if (inner_type == L"StructProperty") {
            if (depth <= 0) {
                return json{{"type", "array"}, {"count", arr.count}, {"innerType", "struct"}};
            }
            auto inner_sp = reinterpret_cast<API::FStructProperty*>(inner);
            auto inner_struct = inner_sp->get_struct();
            if (inner_struct) {
                auto elem_size = inner_struct->get_struct_size();
                for (int i = 0; i < limit; ++i) {
                    auto elem_addr = reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(arr.data) + i * elem_size);
                    result.push_back(read_struct_fields(elem_addr, inner_struct, depth - 1));
                }
            }
        } else if (inner_type == L"FloatProperty") {
            auto vals = reinterpret_cast<float*>(arr.data);
            for (int i = 0; i < limit; ++i) result.push_back(vals[i]);
        } else if (inner_type == L"DoubleProperty") {
            auto vals = reinterpret_cast<double*>(arr.data);
            for (int i = 0; i < limit; ++i) result.push_back(vals[i]);
        } else if (inner_type == L"IntProperty") {
            auto vals = reinterpret_cast<int32_t*>(arr.data);
            for (int i = 0; i < limit; ++i) result.push_back(vals[i]);
        } else if (inner_type == L"ByteProperty") {
            auto vals = reinterpret_cast<uint8_t*>(arr.data);
            for (int i = 0; i < limit; ++i) result.push_back(vals[i]);
        } else if (inner_type == L"BoolProperty") {
            // Bools in arrays are stored as full bytes
            auto vals = reinterpret_cast<uint8_t*>(arr.data);
            for (int i = 0; i < limit; ++i) result.push_back(vals[i] != 0);
        } else {
            return json{{"type", "array"}, {"count", arr.count}, {"innerType", JsonHelpers::wide_to_utf8(inner_type)}};
        }

        if (arr.count > 100) {
            // Indicate truncation
            result.push_back(json{{"truncated", true}, {"totalCount", arr.count}});
        }
        return result;
    }
    if (type_name == L"EnumProperty") {
        auto ep = reinterpret_cast<API::FEnumProperty*>(prop);
        auto np = ep->get_underlying_prop();
        if (!np) return nullptr;

        auto np_c = np->get_class();
        if (!np_c) return nullptr;

        auto np_type = np_c->get_fname()->to_string();
        int64_t numeric_val = 0;

        if (np_type == L"ByteProperty") numeric_val = *reinterpret_cast<uint8_t*>(addr);
        else if (np_type == L"IntProperty") numeric_val = *reinterpret_cast<int32_t*>(addr);
        else if (np_type == L"Int64Property") numeric_val = *reinterpret_cast<int64_t*>(addr);
        else if (np_type == L"UInt32Property" || np_type == L"UIntProperty") numeric_val = *reinterpret_cast<uint32_t*>(addr);
        else numeric_val = *reinterpret_cast<int32_t*>(addr); // fallback

        json result;
        result["value"] = numeric_val;

        auto uenum = ep->get_enum();
        if (uenum) {
            auto efn = uenum->get_fname();
            if (efn) result["enumType"] = JsonHelpers::wide_to_utf8(efn->to_string());
        }
        return result;
    }
    if (type_name == L"DelegateProperty" || type_name == L"MulticastDelegateProperty" ||
        type_name == L"MulticastInlineDelegateProperty" || type_name == L"MulticastSparseDelegateProperty") {
        return json{{"type", JsonHelpers::wide_to_utf8(type_name)}};
    }
    if (type_name == L"MapProperty" || type_name == L"SetProperty") {
        return json{{"type", JsonHelpers::wide_to_utf8(type_name)}, {"note", "not yet supported"}};
    }

    // Fallback
    return json{{"type", JsonHelpers::wide_to_utf8(type_name)}, {"offset", offset}};
}

json read_all_properties(API::UObject* obj, API::UStruct* type, int depth) {
    if (!obj || !type) return json::object();

    json result = json::object();

    // Walk up the class hierarchy
    for (auto current = type; current != nullptr; current = current->get_super()) {
        for (auto field = current->get_child_properties(); field != nullptr; field = field->get_next()) {
            auto fc = field->get_class();
            if (!fc) continue;

            auto class_name = fc->get_fname()->to_string();
            if (class_name.find(L"Property") == std::wstring::npos) continue;

            auto fprop = reinterpret_cast<API::FProperty*>(field);
            auto name = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());

            try {
                result[name] = read_property(obj, fprop, depth);
            } catch (...) {
                result[name] = "<error reading>";
            }
        }
    }

    return result;
}

} // namespace PropertyReader
