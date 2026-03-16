#pragma once

#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;

namespace PropertyReader {
    // Read a single property value from an object, returning JSON.
    // base = pointer to the UObject (or struct base)
    // prop = the FProperty describing the field
    // depth = recursion limit for struct/object references
    json read_property(void* base, uevr::API::FProperty* prop, int depth = 3);

    // Read all properties of an object into a JSON object.
    json read_all_properties(uevr::API::UObject* obj, uevr::API::UStruct* type, int depth = 2);

    // Get the type name string for a property (e.g. "FloatProperty", "ObjectProperty")
    std::string get_property_type_name(uevr::API::FProperty* prop);
}
