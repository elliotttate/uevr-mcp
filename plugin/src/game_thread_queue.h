#pragma once

#include <functional>
#include <future>
#include <mutex>
#include <queue>
#include <nlohmann/json.hpp>

using json = nlohmann::json;

class GameThreadQueue {
public:
    static GameThreadQueue& get() {
        static GameThreadQueue instance;
        return instance;
    }

    // Called from HTTP thread. Queues work for the game thread and waits for the result.
    // Returns the JSON result or a timeout/error JSON.
    json submit_and_wait(std::function<json()> work, int timeout_ms = 5000);

    // Called from game thread (on_pre_engine_tick). Processes up to max_count pending requests.
    void process_pending(int max_count = 16);

    // Number of pending requests in the queue.
    size_t pending_count() const;

private:
    GameThreadQueue() = default;

    struct Request {
        std::function<json()> work;
        std::promise<json> promise;
    };

    mutable std::mutex m_mutex;
    std::queue<std::unique_ptr<Request>> m_queue;
};
