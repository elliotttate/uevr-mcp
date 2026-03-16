#include "game_thread_queue.h"
#include <chrono>

json GameThreadQueue::submit_and_wait(std::function<json()> work, int timeout_ms) {
    auto req = std::make_unique<Request>();
    req->work = std::move(work);
    auto future = req->promise.get_future();

    {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_queue.push(std::move(req));
    }

    auto status = future.wait_for(std::chrono::milliseconds(timeout_ms));
    if (status == std::future_status::timeout) {
        return json{{"error", "Game thread request timed out"}};
    }

    try {
        return future.get();
    } catch (const std::exception& e) {
        return json{{"error", std::string("Exception: ") + e.what()}};
    }
}

void GameThreadQueue::process_pending(int max_count) {
    for (int i = 0; i < max_count; ++i) {
        std::unique_ptr<Request> req;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            if (m_queue.empty()) break;
            req = std::move(m_queue.front());
            m_queue.pop();
        }

        try {
            auto result = req->work();
            req->promise.set_value(std::move(result));
        } catch (const std::exception& e) {
            req->promise.set_value(json{{"error", std::string("Game thread exception: ") + e.what()}});
        } catch (...) {
            req->promise.set_value(json{{"error", "Unknown game thread exception"}});
        }
    }
}

size_t GameThreadQueue::pending_count() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_queue.size();
}
