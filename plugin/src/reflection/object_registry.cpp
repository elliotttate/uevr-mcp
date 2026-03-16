#include "object_registry.h"
#include "../json_helpers.h"

using namespace uevr;

void ObjectRegistry::register_spawned(API::UObject* obj) {
    std::lock_guard<std::mutex> lock(m_mutex);
    m_spawned.insert(obj);
}

void ObjectRegistry::unregister(API::UObject* obj) {
    std::lock_guard<std::mutex> lock(m_mutex);
    m_spawned.erase(obj);
}

json ObjectRegistry::get_spawned_objects() {
    std::lock_guard<std::mutex> lock(m_mutex);
    json result = json::array();

    for (auto* obj : m_spawned) {
        json entry;
        entry["address"] = JsonHelpers::address_to_string(obj);

        bool alive = API::UObjectHook::exists(obj);
        entry["alive"] = alive;

        if (alive) {
            auto cls = obj->get_class();
            if (cls) {
                auto name = cls->get_fname();
                if (name) entry["class"] = JsonHelpers::fname_to_string(name);
            }
            entry["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());
        }

        result.push_back(entry);
    }

    return json{{"spawned", result}, {"count", result.size()}};
}

void ObjectRegistry::prune() {
    std::lock_guard<std::mutex> lock(m_mutex);
    for (auto it = m_spawned.begin(); it != m_spawned.end(); ) {
        if (!API::UObjectHook::exists(*it)) {
            it = m_spawned.erase(it);
        } else {
            ++it;
        }
    }
}
