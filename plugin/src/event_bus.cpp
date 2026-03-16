#include "event_bus.h"

using json = nlohmann::json;

EventBus& EventBus::get() {
    static EventBus instance;
    return instance;
}

void EventBus::publish(const std::string& type, const json& data) {
    std::lock_guard<std::mutex> lock(m_mutex);

    Event evt;
    evt.seq = m_next_seq++;
    evt.type = type;
    evt.data = data;

    m_events.push_back(std::move(evt));
    while (static_cast<int>(m_events.size()) > MAX_EVENTS) {
        m_events.pop_front();
    }

    m_cv.notify_all();
}

std::pair<std::vector<Event>, uint64_t> EventBus::poll(uint64_t since_seq, int max_count) {
    std::lock_guard<std::mutex> lock(m_mutex);

    std::vector<Event> result;
    uint64_t new_seq = since_seq;

    for (const auto& evt : m_events) {
        if (evt.seq > since_seq) {
            result.push_back(evt);
            new_seq = evt.seq;
            if (static_cast<int>(result.size()) >= max_count) break;
        }
    }

    if (result.empty()) {
        new_seq = m_next_seq - 1;
    }

    return {result, new_seq};
}

bool EventBus::wait_for_events(uint64_t since_seq, int timeout_ms) {
    std::unique_lock<std::mutex> lock(m_mutex);
    return m_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [&]() {
        return m_next_seq > since_seq + 1;
    });
}

uint64_t EventBus::current_sequence() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_next_seq - 1;
}
