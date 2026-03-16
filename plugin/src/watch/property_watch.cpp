#include "property_watch.h"
#include "../reflection/property_reader.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../routes/status_routes.h"

#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;
using namespace uevr;

PropertyWatch& PropertyWatch::get() {
    static PropertyWatch instance;
    return instance;
}

json PropertyWatch::add_watch(uintptr_t address, const std::string& field_name, int interval_ticks) {
    auto obj = reinterpret_cast<API::UObject*>(address);
    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object not found or no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) {
        return json{{"error", "Object has no class"}};
    }

    auto wname = JsonHelpers::utf8_to_wide(field_name);
    auto prop = cls->find_property(wname.c_str());
    if (!prop) {
        return json{{"error", "Field not found: " + field_name}};
    }

    auto fprop = reinterpret_cast<API::FProperty*>(prop);
    json initial_value;
    try {
        initial_value = PropertyReader::read_property(obj, fprop, 1);
    } catch (...) {
        initial_value = nullptr;
    }

    std::lock_guard<std::mutex> lock(m_mutex);

    int id = m_next_watch_id++;
    WatchEntry entry;
    entry.id = id;
    entry.address = address;
    entry.field_name = field_name;
    entry.interval_ticks = (interval_ticks > 0) ? interval_ticks : 1;
    entry.last_check_tick = StatusRoutes::get_tick_count();
    entry.previous_value = nullptr;
    entry.current_value = initial_value;
    entry.change_count = 0;
    entry.active = true;

    m_watches[id] = std::move(entry);

    PipeServer::get().log("Watch: added watch #" + std::to_string(id) + " on " +
                          JsonHelpers::address_to_string(address) + "." + field_name);

    return json{
        {"watchId", id},
        {"address", JsonHelpers::address_to_string(address)},
        {"fieldName", field_name},
        {"intervalTicks", interval_ticks},
        {"initialValue", initial_value}
    };
}

json PropertyWatch::remove_watch(int watch_id) {
    std::lock_guard<std::mutex> lock(m_mutex);

    auto it = m_watches.find(watch_id);
    if (it == m_watches.end()) {
        return json{{"error", "Watch not found: " + std::to_string(watch_id)}};
    }

    m_watches.erase(it);

    PipeServer::get().log("Watch: removed watch #" + std::to_string(watch_id));

    return json{{"success", true}, {"watchId", watch_id}};
}

json PropertyWatch::list_watches() {
    std::lock_guard<std::mutex> lock(m_mutex);

    json result = json::array();
    for (const auto& [id, w] : m_watches) {
        result.push_back(json{
            {"watchId", w.id},
            {"address", JsonHelpers::address_to_string(w.address)},
            {"fieldName", w.field_name},
            {"intervalTicks", w.interval_ticks},
            {"active", w.active},
            {"changeCount", w.change_count},
            {"currentValue", w.current_value},
            {"previousValue", w.previous_value},
            {"lastCheckTick", w.last_check_tick}
        });
    }

    return json{{"watches", result}, {"count", result.size()}};
}

json PropertyWatch::get_changes(int max_count) {
    std::lock_guard<std::mutex> lock(m_mutex);

    json result = json::array();
    int count = 0;

    // Return the most recent changes (from back of deque)
    auto start_it = m_changes.end();
    int available = static_cast<int>(m_changes.size());
    int to_return = (max_count > 0 && max_count < available) ? max_count : available;

    if (to_return > 0) {
        start_it = m_changes.end() - to_return;
    }

    for (auto it = start_it; it != m_changes.end(); ++it) {
        result.push_back(json{
            {"watchId", it->watch_id},
            {"tick", it->tick},
            {"fieldName", it->field_name},
            {"address", JsonHelpers::address_to_string(it->address)},
            {"oldValue", it->old_value},
            {"newValue", it->new_value}
        });
    }

    return json{{"changes", result}, {"count", result.size()}, {"totalBuffered", m_changes.size()}};
}

json PropertyWatch::clear_watches() {
    std::lock_guard<std::mutex> lock(m_mutex);

    int watch_count = static_cast<int>(m_watches.size());
    int change_count = static_cast<int>(m_changes.size());

    m_watches.clear();
    m_changes.clear();

    PipeServer::get().log("Watch: cleared all watches");

    return json{
        {"success", true},
        {"watchesCleared", watch_count},
        {"changesCleared", change_count}
    };
}

json PropertyWatch::take_snapshot(uintptr_t address) {
    auto obj = reinterpret_cast<API::UObject*>(address);
    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object not found or no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) {
        return json{{"error", "Object has no class"}};
    }

    auto class_fname = cls->get_fname();
    std::string class_name = class_fname ? JsonHelpers::wide_to_utf8(class_fname->to_string()) : "Unknown";

    json fields;
    try {
        fields = PropertyReader::read_all_properties(obj, cls, 2);
    } catch (...) {
        return json{{"error", "Failed to read object properties"}};
    }

    std::lock_guard<std::mutex> lock(m_mutex);

    int id = m_next_snapshot_id++;
    Snapshot snap;
    snap.id = id;
    snap.address = address;
    snap.class_name = class_name;
    snap.tick = StatusRoutes::get_tick_count();
    snap.fields = fields;

    m_snapshots[id] = std::move(snap);

    PipeServer::get().log("Watch: snapshot #" + std::to_string(id) + " of " +
                          class_name + " at " + JsonHelpers::address_to_string(address) +
                          " (" + std::to_string(fields.size()) + " fields)");

    return json{
        {"snapshotId", id},
        {"address", JsonHelpers::address_to_string(address)},
        {"className", class_name},
        {"tick", snap.tick},
        {"fieldCount", fields.size()}
    };
}

json PropertyWatch::list_snapshots() {
    std::lock_guard<std::mutex> lock(m_mutex);

    json result = json::array();
    for (const auto& [id, s] : m_snapshots) {
        result.push_back(json{
            {"snapshotId", s.id},
            {"address", JsonHelpers::address_to_string(s.address)},
            {"className", s.class_name},
            {"tick", s.tick},
            {"fieldCount", s.fields.size()}
        });
    }

    return json{{"snapshots", result}, {"count", result.size()}};
}

json PropertyWatch::diff_snapshot(int snapshot_id, uintptr_t address) {
    Snapshot snap_copy;
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        auto it = m_snapshots.find(snapshot_id);
        if (it == m_snapshots.end()) {
            return json{{"error", "Snapshot not found: " + std::to_string(snapshot_id)}};
        }
        snap_copy = it->second;
    }

    // Use the original address if none specified
    uintptr_t target_addr = (address != 0) ? address : snap_copy.address;

    auto obj = reinterpret_cast<API::UObject*>(target_addr);
    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object not found or no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) {
        return json{{"error", "Object has no class"}};
    }

    json current_fields;
    try {
        current_fields = PropertyReader::read_all_properties(obj, cls, 2);
    } catch (...) {
        return json{{"error", "Failed to read current object properties"}};
    }

    // Compare snapshot fields to current fields
    json differences = json::array();

    // Check fields that existed in the snapshot
    for (auto it = snap_copy.fields.begin(); it != snap_copy.fields.end(); ++it) {
        const auto& field_name = it.key();
        const auto& snapshot_value = it.value();

        if (current_fields.contains(field_name)) {
            const auto& current_value = current_fields[field_name];
            if (snapshot_value != current_value) {
                differences.push_back(json{
                    {"field", field_name},
                    {"snapshotValue", snapshot_value},
                    {"currentValue", current_value}
                });
            }
        } else {
            // Field was in snapshot but not in current (removed or class changed)
            differences.push_back(json{
                {"field", field_name},
                {"snapshotValue", snapshot_value},
                {"currentValue", nullptr},
                {"note", "field no longer present"}
            });
        }
    }

    // Check for fields that exist now but weren't in the snapshot
    for (auto it = current_fields.begin(); it != current_fields.end(); ++it) {
        const auto& field_name = it.key();
        if (!snap_copy.fields.contains(field_name)) {
            differences.push_back(json{
                {"field", field_name},
                {"snapshotValue", nullptr},
                {"currentValue", it.value()},
                {"note", "new field not in snapshot"}
            });
        }
    }

    auto class_fname = cls->get_fname();
    std::string class_name = class_fname ? JsonHelpers::wide_to_utf8(class_fname->to_string()) : "Unknown";

    return json{
        {"snapshotId", snapshot_id},
        {"snapshotTick", snap_copy.tick},
        {"currentTick", StatusRoutes::get_tick_count()},
        {"address", JsonHelpers::address_to_string(target_addr)},
        {"className", class_name},
        {"differenceCount", differences.size()},
        {"differences", differences}
    };
}

json PropertyWatch::delete_snapshot(int snapshot_id) {
    std::lock_guard<std::mutex> lock(m_mutex);

    auto it = m_snapshots.find(snapshot_id);
    if (it == m_snapshots.end()) {
        return json{{"error", "Snapshot not found: " + std::to_string(snapshot_id)}};
    }

    m_snapshots.erase(it);

    PipeServer::get().log("Watch: deleted snapshot #" + std::to_string(snapshot_id));

    return json{{"success", true}, {"snapshotId", snapshot_id}};
}

void PropertyWatch::tick(uint64_t current_tick) {
    std::lock_guard<std::mutex> lock(m_mutex);

    for (auto& [id, watch] : m_watches) {
        if (!watch.active) continue;

        // Check if it's time to poll this watch
        if (current_tick - watch.last_check_tick < static_cast<uint64_t>(watch.interval_ticks)) {
            continue;
        }

        watch.last_check_tick = current_tick;

        // Validate the object is still alive
        auto obj = reinterpret_cast<API::UObject*>(watch.address);
        if (!API::UObjectHook::exists(obj)) {
            watch.active = false;
            continue;
        }

        auto cls = obj->get_class();
        if (!cls) continue;

        auto wname = JsonHelpers::utf8_to_wide(watch.field_name);
        auto prop = cls->find_property(wname.c_str());
        if (!prop) continue;

        auto fprop = reinterpret_cast<API::FProperty*>(prop);

        json value;
        try {
            value = PropertyReader::read_property(obj, fprop, 1);
        } catch (...) {
            continue;
        }

        if (value != watch.current_value) {
            // Change detected
            watch.previous_value = watch.current_value;
            watch.current_value = value;
            watch.change_count++;

            ChangeEvent evt;
            evt.watch_id = watch.id;
            evt.tick = current_tick;
            evt.field_name = watch.field_name;
            evt.address = watch.address;
            evt.old_value = watch.previous_value;
            evt.new_value = watch.current_value;

            m_changes.push_back(std::move(evt));
            if (static_cast<int>(m_changes.size()) > MAX_CHANGES) {
                m_changes.pop_front();
            }
        }
    }
}
