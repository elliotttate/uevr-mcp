#pragma once

#include <thread>
#include <atomic>
#include <mutex>
#include <deque>
#include <string>
#include <chrono>
#include <nlohmann/json.hpp>

using json = nlohmann::json;

class PipeServer {
public:
    static PipeServer& get() {
        static PipeServer instance;
        return instance;
    }

    bool start(const std::string& pipe_name = R"(\\.\pipe\UEVR_MCP)");
    void stop();
    bool is_running() const { return m_running.load(); }

    // Logging ring buffer
    void log(const std::string& message);
    json get_log(int max_entries = 100) const;
    void clear_log();

    // Status info
    void set_game_name(const std::string& name) { m_game_name = name; }
    std::chrono::steady_clock::time_point start_time() const { return m_start_time; }

private:
    PipeServer() = default;
    ~PipeServer();

    void server_thread_func();
    json handle_request(const json& request);

    std::thread m_thread;
    std::atomic<bool> m_running{false};
    std::string m_pipe_name;
    std::chrono::steady_clock::time_point m_start_time;

    // Log ring buffer
    mutable std::mutex m_log_mutex;
    std::deque<std::string> m_log_entries;
    static constexpr size_t MAX_LOG_ENTRIES = 500;

    std::string m_game_name;
};
