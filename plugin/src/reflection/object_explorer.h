#pragma once

#include <nlohmann/json.hpp>
#include <uevr/API.hpp>
#include <string>
#include <vector>

using json = nlohmann::json;

namespace ObjectExplorer {
    // Search GUObjectArray for objects whose full name contains query
    json search_objects(const std::string& query, int limit = 50);

    // Search for UClass objects by name
    json search_classes(const std::string& query, int limit = 50);

    // Get type schema: fields and methods for a UClass/UStruct by name
    json get_type_info(const std::string& type_name);

    // Inspect a live UObject at address: field values + method list
    json inspect_object(uintptr_t address, int depth = 2);

    // Lightweight one-line-per-field summary
    json summarize_object(uintptr_t address);

    // Read a single named field from an object
    json read_field(uintptr_t address, const std::string& field_name);

    // Find instances of a class via UObjectHook / GUObjectArray
    json find_objects_by_class(const std::string& class_name, int limit = 50);

    // Enumerate fields of a UStruct, returning JSON array of {name, type, offset}
    json enumerate_fields(uevr::API::UStruct* ustruct);

    // Enumerate methods of a UStruct, returning JSON array of {name, params, returnType}
    json enumerate_methods(uevr::API::UStruct* ustruct);

    // Chain query: multi-step object graph traversal
    json chain_query(uintptr_t start_address, const json& steps);

    // Get commonly-used singleton objects (engine, game instance, world, game mode, etc.)
    json get_singletons();

    // Find a singleton by type name (first instance of that class)
    json get_singleton(const std::string& type_name);

    // Read array property with pagination (offset/limit)
    json read_array(uintptr_t address, const std::string& field_name, int offset = 0, int limit = 50);

    // Read raw memory as hex dump
    json read_memory(uintptr_t address, int size);

    // Read typed values from memory
    json read_typed(uintptr_t address, const std::string& type, int count = 1, int stride = 0);

    // Get camera state (position, rotation, FOV)
    json get_camera();
}
