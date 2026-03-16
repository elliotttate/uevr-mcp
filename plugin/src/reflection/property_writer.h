#pragma once

#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;

namespace PropertyWriter {
    // Write a JSON value to a property on an object.
    // Returns true on success, false on failure (sets error string).
    bool write_property(void* base, uevr::API::FProperty* prop, const json& value, std::string& error);
}
