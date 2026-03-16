#include "function_caller.h"
#include "property_reader.h"
#include "property_writer.h"
#include "../json_helpers.h"

#include <vector>
#include <cstring>

using namespace uevr;

namespace FunctionCaller {

json call_getter(uintptr_t address, const std::string& method_name) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    auto wname = JsonHelpers::utf8_to_wide(method_name);
    auto func = cls->find_function(wname.c_str());
    if (!func) {
        return json{{"error", "Method '" + method_name + "' not found"}};
    }

    // Check if function has no required params (only return param allowed)
    for (auto param = func->get_child_properties(); param != nullptr; param = param->get_next()) {
        auto pc = param->get_class();
        if (!pc) continue;
        auto fparam = reinterpret_cast<API::FProperty*>(param);
        if (fparam->is_param() && !fparam->is_return_param()) {
            return json{{"error", "Method '" + method_name + "' has parameters — use invoke_function instead"}};
        }
    }

    // Allocate params buffer
    auto ps = func->get_properties_size();
    auto ma = func->get_min_alignment();

    std::vector<uint8_t> params;
    if (ma > 1) {
        params.resize(((ps + ma - 1) / ma) * ma, 0);
    } else {
        params.resize(ps, 0);
    }

    // Call
    obj->process_event(func, params.data());

    // Find and read return value
    for (auto param = func->get_child_properties(); param != nullptr; param = param->get_next()) {
        auto pc = param->get_class();
        if (!pc) continue;
        auto fparam = reinterpret_cast<API::FProperty*>(param);
        if (!fparam->is_return_param()) continue;

        auto pc_name = pc->get_fname()->to_string();
        if (pc_name == L"BoolProperty") {
            return json{{"result", reinterpret_cast<API::FBoolProperty*>(fparam)->get_value_from_object(params.data())}};
        }
        return json{{"result", PropertyReader::read_property(params.data(), fparam, 2)}};
    }

    return json{{"result", nullptr}};
}

json invoke_function(uintptr_t address, const std::string& method_name, const json& args) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    auto wname = JsonHelpers::utf8_to_wide(method_name);
    auto func = cls->find_function(wname.c_str());
    if (!func) {
        return json{{"error", "Method '" + method_name + "' not found"}};
    }

    // Allocate params buffer
    auto ps = func->get_properties_size();
    auto ma = func->get_min_alignment();

    std::vector<uint8_t> params;
    if (ma > 1) {
        params.resize(((ps + ma - 1) / ma) * ma, 0);
    } else {
        params.resize(ps, 0);
    }

    // Fill parameters from JSON args
    API::FProperty* return_prop = nullptr;
    bool ret_is_bool = false;

    // Track dynamic string allocations to keep them alive during the call
    std::vector<std::unique_ptr<wchar_t[]>> dynamic_strings;

    int arg_index = 0;
    for (auto param = func->get_child_properties(); param != nullptr; param = param->get_next()) {
        auto pc = param->get_class();
        if (!pc) continue;

        auto pc_name = pc->get_fname()->to_string();
        if (pc_name.find(L"Property") == std::wstring::npos) continue;

        auto fparam = reinterpret_cast<API::FProperty*>(param);
        if (!fparam->is_param()) continue;

        if (fparam->is_return_param()) {
            return_prop = fparam;
            ret_is_bool = (pc_name == L"BoolProperty");
            continue;
        }

        // Get the argument value
        auto param_name = JsonHelpers::wide_to_utf8(fparam->get_fname()->to_string());
        json arg_val;

        if (args.is_object() && args.contains(param_name)) {
            arg_val = args[param_name];
        } else if (args.is_array() && arg_index < (int)args.size()) {
            arg_val = args[arg_index];
        } else if (fparam->is_out_param()) {
            arg_index++;
            continue; // Skip unset out params
        } else {
            arg_index++;
            continue; // Use default (zero-initialized)
        }

        // Handle StrProperty specially for dynamic string lifetime
        if (pc_name == L"StrProperty") {
            auto str = arg_val.get<std::string>();
            auto wstr = JsonHelpers::utf8_to_wide(str);
            auto buffer = std::make_unique<wchar_t[]>(wstr.size() + 1);
            std::copy(wstr.begin(), wstr.end(), buffer.get());
            buffer[wstr.size()] = L'\0';

            auto& fstr = *reinterpret_cast<API::TArray<wchar_t>*>(params.data() + fparam->get_offset());
            fstr.count = (int32_t)(wstr.size() + 1);
            fstr.capacity = fstr.count;
            fstr.data = buffer.get();

            dynamic_strings.push_back(std::move(buffer));
        } else {
            std::string write_error;
            if (!PropertyWriter::write_property(params.data(), fparam, arg_val, write_error)) {
                return json{{"error", "Failed to set parameter '" + param_name + "': " + write_error}};
            }
        }

        arg_index++;
    }

    // Call the function
    obj->process_event(func, params.data());

    // Read return value
    json result;
    if (return_prop) {
        if (ret_is_bool) {
            result["result"] = reinterpret_cast<API::FBoolProperty*>(return_prop)->get_value_from_object(params.data());
        } else {
            result["result"] = PropertyReader::read_property(params.data(), return_prop, 2);
        }
    } else {
        result["result"] = nullptr;
    }

    // Read out parameters
    json out_params = json::object();
    for (auto param = func->get_child_properties(); param != nullptr; param = param->get_next()) {
        auto pc = param->get_class();
        if (!pc) continue;
        auto fparam = reinterpret_cast<API::FProperty*>(param);
        if (!fparam->is_param() || !fparam->is_out_param() || fparam->is_return_param()) continue;

        auto pname = JsonHelpers::wide_to_utf8(fparam->get_fname()->to_string());
        auto pc_name = pc->get_fname()->to_string();

        if (pc_name == L"BoolProperty") {
            out_params[pname] = reinterpret_cast<API::FBoolProperty*>(fparam)->get_value_from_object(params.data());
        } else {
            out_params[pname] = PropertyReader::read_property(params.data(), fparam, 2);
        }
    }
    if (!out_params.empty()) {
        result["outParams"] = out_params;
    }

    return result;
}

} // namespace FunctionCaller
