#pragma once

#include <nlohmann/json.hpp>
#include <string>
#include <vector>
#include <unordered_map>
#include <mutex>
#include <deque>

struct WatchEntry {
    int id;
    uintptr_t address;
    std::string field_name;
    int interval_ticks; // Check every N ticks
    uint64_t last_check_tick{0};
    nlohmann::json previous_value;
    nlohmann::json current_value;
    int change_count{0};
    bool active{true};
    std::string lua_script; // Optional Lua code to execute on change
};

struct ChangeEvent {
    int watch_id;
    uint64_t tick;
    std::string field_name;
    uintptr_t address;
    nlohmann::json old_value;
    nlohmann::json new_value;
};

struct Snapshot {
    int id;
    uintptr_t address;
    std::string class_name;
    uint64_t tick;
    nlohmann::json fields; // {fieldName: value, ...}
};

class PropertyWatch {
public:
    static PropertyWatch& get();

    // Watch management
    nlohmann::json add_watch(uintptr_t address, const std::string& field_name, int interval_ticks = 1, const std::string& lua_script = "");
    nlohmann::json remove_watch(int watch_id);
    nlohmann::json list_watches();
    nlohmann::json get_changes(int max_count = 100);
    nlohmann::json clear_watches();

    // Snapshot/diff
    nlohmann::json take_snapshot(uintptr_t address);
    nlohmann::json list_snapshots();
    nlohmann::json diff_snapshot(int snapshot_id, uintptr_t address = 0); // 0 = use original address
    nlohmann::json delete_snapshot(int snapshot_id);

    // Called from game thread every tick
    void tick(uint64_t current_tick);

private:
    PropertyWatch() = default;
    std::mutex m_mutex;
    std::unordered_map<int, WatchEntry> m_watches;
    std::deque<ChangeEvent> m_changes; // Ring buffer, max 1000
    std::unordered_map<int, Snapshot> m_snapshots;
    int m_next_watch_id{1};
    int m_next_snapshot_id{1};
    static constexpr int MAX_CHANGES = 1000;
};
