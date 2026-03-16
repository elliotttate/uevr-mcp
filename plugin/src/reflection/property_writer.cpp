#include "property_writer.h"
#include "../json_helpers.h"

#include <cstring>

using namespace uevr;

namespace PropertyWriter {

bool write_property(void* base, API::FProperty* prop, const json& value, std::string& error) {
    if (!base || !prop) {
        error = "Null base or property";
        return false;
    }

    auto propc = prop->get_class();
    if (!propc) {
        error = "Property has no class";
        return false;
    }

    auto type_name = propc->get_fname()->to_string();
    auto offset = prop->get_offset();
    auto addr = reinterpret_cast<uintptr_t>(base) + offset;

    try {
        if (type_name == L"BoolProperty") {
            auto fbp = reinterpret_cast<API::FBoolProperty*>(prop);
            fbp->set_value_in_object(base, value.get<bool>());
            return true;
        }
        if (type_name == L"FloatProperty") {
            *reinterpret_cast<float*>(addr) = value.get<float>();
            return true;
        }
        if (type_name == L"DoubleProperty") {
            *reinterpret_cast<double*>(addr) = value.get<double>();
            return true;
        }
        if (type_name == L"ByteProperty") {
            *reinterpret_cast<uint8_t*>(addr) = value.get<uint8_t>();
            return true;
        }
        if (type_name == L"Int8Property") {
            *reinterpret_cast<int8_t*>(addr) = value.get<int8_t>();
            return true;
        }
        if (type_name == L"Int16Property") {
            *reinterpret_cast<int16_t*>(addr) = value.get<int16_t>();
            return true;
        }
        if (type_name == L"UInt16Property") {
            *reinterpret_cast<uint16_t*>(addr) = value.get<uint16_t>();
            return true;
        }
        if (type_name == L"IntProperty") {
            *reinterpret_cast<int32_t*>(addr) = value.get<int32_t>();
            return true;
        }
        if (type_name == L"UIntProperty" || type_name == L"UInt32Property") {
            *reinterpret_cast<uint32_t*>(addr) = value.get<uint32_t>();
            return true;
        }
        if (type_name == L"Int64Property") {
            *reinterpret_cast<int64_t*>(addr) = value.get<int64_t>();
            return true;
        }
        if (type_name == L"UInt64Property") {
            *reinterpret_cast<uint64_t*>(addr) = value.get<uint64_t>();
            return true;
        }
        if (type_name == L"NameProperty") {
            auto str = value.get<std::string>();
            auto wstr = JsonHelpers::utf8_to_wide(str);
            *reinterpret_cast<API::FName*>(addr) = API::FName{wstr};
            return true;
        }
        if (type_name == L"ObjectProperty" || type_name == L"InterfaceProperty") {
            if (value.is_null()) {
                *reinterpret_cast<API::UObject**>(addr) = nullptr;
                return true;
            }
            auto addr_str = value.is_string() ? value.get<std::string>() : value.value("address", "");
            auto ptr = JsonHelpers::string_to_address(addr_str);
            if (ptr == 0) {
                error = "Invalid address for object property";
                return false;
            }
            *reinterpret_cast<API::UObject**>(addr) = reinterpret_cast<API::UObject*>(ptr);
            return true;
        }
        if (type_name == L"EnumProperty") {
            auto ep = reinterpret_cast<API::FEnumProperty*>(prop);
            auto np = ep->get_underlying_prop();
            if (!np) { error = "Enum has no underlying property"; return false; }

            auto np_c = np->get_class();
            if (!np_c) { error = "Enum underlying has no class"; return false; }
            auto np_type = np_c->get_fname()->to_string();

            int64_t val = value.is_object() ? value.value("value", 0) : value.get<int64_t>();

            if (np_type == L"ByteProperty") *reinterpret_cast<uint8_t*>(addr) = (uint8_t)val;
            else if (np_type == L"IntProperty") *reinterpret_cast<int32_t*>(addr) = (int32_t)val;
            else if (np_type == L"Int64Property") *reinterpret_cast<int64_t*>(addr) = val;
            else if (np_type == L"UInt32Property" || np_type == L"UIntProperty") *reinterpret_cast<uint32_t*>(addr) = (uint32_t)val;
            else *reinterpret_cast<int32_t*>(addr) = (int32_t)val;

            return true;
        }
        if (type_name == L"StructProperty") {
            if (!value.is_object()) {
                error = "Struct property requires JSON object";
                return false;
            }

            auto sp = reinterpret_cast<API::FStructProperty*>(prop);
            auto ustruct = sp->get_struct();
            if (!ustruct) { error = "StructProperty has no struct descriptor"; return false; }

            // Set individual fields within the struct
            for (auto& [key, val] : value.items()) {
                auto wkey = JsonHelpers::utf8_to_wide(key);
                auto field_prop = ustruct->find_property(wkey.c_str());
                if (!field_prop) {
                    error = "Field '" + key + "' not found in struct";
                    return false;
                }
                std::string field_error;
                if (!write_property(reinterpret_cast<void*>(addr), field_prop, val, field_error)) {
                    error = "Error writing struct field '" + key + "': " + field_error;
                    return false;
                }
            }
            return true;
        }

        error = "Unsupported property type: " + JsonHelpers::wide_to_utf8(type_name);
        return false;

    } catch (const json::exception& e) {
        error = std::string("JSON type mismatch: ") + e.what();
        return false;
    } catch (const std::exception& e) {
        error = std::string("Exception: ") + e.what();
        return false;
    }
}

} // namespace PropertyWriter
