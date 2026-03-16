#pragma once

#include <mutex>
#include <unordered_set>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;

class ObjectRegistry {
public:
    static ObjectRegistry& get() {
        static ObjectRegistry instance;
        return instance;
    }

    void register_spawned(uevr::API::UObject* obj);
    void unregister(uevr::API::UObject* obj);
    json get_spawned_objects();
    void prune(); // Remove dead entries

private:
    ObjectRegistry() = default;

    std::mutex m_mutex;
    std::unordered_set<uevr::API::UObject*> m_spawned;
};
