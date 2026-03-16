#pragma once

#include <nlohmann/json.hpp>
#include <deque>
#include <mutex>
#include <condition_variable>
#include <string>
#include <vector>
#include <utility>

struct Event {
    uint64_t seq;
    std::string type;  // "hook_fire", "watch_change", "lua_output", "timer_fire"
    nlohmann::json data;
};

class EventBus {
public:
    static EventBus& get();

    void publish(const std::string& type, const nlohmann::json& data);

    // Get events since a given sequence number. Returns {events, new_seq}.
    std::pair<std::vector<Event>, uint64_t> poll(uint64_t since_seq, int max_count = 100);

    // Block until new events arrive or timeout. Returns true if events available.
    bool wait_for_events(uint64_t since_seq, int timeout_ms = 5000);

    uint64_t current_sequence() const;

private:
    EventBus() = default;
    mutable std::mutex m_mutex;
    std::condition_variable m_cv;
    std::deque<Event> m_events;
    uint64_t m_next_seq{1};
    static constexpr int MAX_EVENTS = 5000;
};
