#pragma once

#include <nlohmann/json.hpp>
#include <uevr/API.hpp>
#include <string>

using json = nlohmann::json;

namespace FunctionCaller {
    // Call a 0-parameter getter method on an object
    json call_getter(uintptr_t address, const std::string& method_name);

    // Call a UFunction with JSON arguments on an object
    json invoke_function(uintptr_t address, const std::string& method_name, const json& args);
}
