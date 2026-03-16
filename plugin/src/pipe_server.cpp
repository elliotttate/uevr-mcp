#include "pipe_server.h"
#include "game_thread_queue.h"
#include "json_helpers.h"

#include <Windows.h>
#include <uevr/API.hpp>
#include <sstream>
#include <iomanip>

PipeServer::~PipeServer() {
    stop();
}

bool PipeServer::start(const std::string& pipe_name) {
    if (m_running.load()) return false;
    m_pipe_name = pipe_name;
    m_start_time = std::chrono::steady_clock::now();
    m_thread = std::thread(&PipeServer::server_thread_func, this);
    return true;
}

void PipeServer::stop() {
    m_running.store(false);
    // Create a dummy connection to unblock ConnectNamedPipe
    auto wname = JsonHelpers::utf8_to_wide(m_pipe_name);
    auto handle = CreateFileW(wname.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (handle != INVALID_HANDLE_VALUE) {
        CloseHandle(handle);
    }
    if (m_thread.joinable()) {
        m_thread.join();
    }
}

void PipeServer::log(const std::string& message) {
    // Timestamp the message
    auto now = std::chrono::system_clock::now();
    auto t = std::chrono::system_clock::to_time_t(now);
    std::ostringstream oss;
    oss << std::put_time(std::localtime(&t), "%H:%M:%S") << " " << message;

    std::lock_guard<std::mutex> lock(m_log_mutex);
    m_log_entries.push_back(oss.str());
    while (m_log_entries.size() > MAX_LOG_ENTRIES) {
        m_log_entries.pop_front();
    }
}

json PipeServer::get_log(int max_entries) const {
    std::lock_guard<std::mutex> lock(m_log_mutex);
    json entries = json::array();
    int start = (int)m_log_entries.size() - max_entries;
    if (start < 0) start = 0;
    for (int i = start; i < (int)m_log_entries.size(); ++i) {
        entries.push_back(m_log_entries[i]);
    }
    return entries;
}

void PipeServer::clear_log() {
    std::lock_guard<std::mutex> lock(m_log_mutex);
    m_log_entries.clear();
}

json PipeServer::handle_request(const json& request) {
    std::string cmd = request.value("command", "");

    if (cmd == "get_status") {
        auto& api = uevr::API::get();
        auto elapsed = std::chrono::steady_clock::now() - m_start_time;
        auto seconds = std::chrono::duration_cast<std::chrono::seconds>(elapsed).count();

        json result;
        result["status"] = "running";
        result["uptime_seconds"] = seconds;
        result["game"] = m_game_name;
        result["queue_depth"] = GameThreadQueue::get().pending_count();

        if (api) {
            auto vr = api->param()->vr;
            result["vr_runtime"] = vr->is_openvr() ? "OpenVR" : (vr->is_openxr() ? "OpenXR" : "Unknown");
            result["hmd_active"] = (bool)vr->is_hmd_active();
        }

        return result;
    }

    if (cmd == "get_log") {
        int max_entries = request.value("max_entries", 100);
        return json{{"entries", get_log(max_entries)}};
    }

    if (cmd == "clear_log") {
        clear_log();
        return json{{"status", "cleared"}};
    }

    if (cmd == "game_info") {
        json result;
        char exe_path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, exe_path, MAX_PATH);
        std::string exe(exe_path);
        result["gamePath"] = exe;

        auto slash = exe.find_last_of("\\/");
        if (slash != std::string::npos) {
            result["gameDirectory"] = exe.substr(0, slash + 1);
            result["gameName"] = exe.substr(slash + 1);
        }

        auto& api = uevr::API::get();
        if (api) {
            auto vr = api->param()->vr;
            result["vrRuntime"] = vr->is_openvr() ? "OpenVR" : (vr->is_openxr() ? "OpenXR" : "Unknown");
            result["hmdActive"] = (bool)vr->is_hmd_active();
        }

        auto elapsed = std::chrono::steady_clock::now() - m_start_time;
        result["uptimeSeconds"] = std::chrono::duration_cast<std::chrono::seconds>(elapsed).count();
        result["game"] = m_game_name;

        return result;
    }

    return json{{"error", "Unknown command: " + cmd}};
}

void PipeServer::server_thread_func() {
    m_running.store(true);

    auto& api = uevr::API::get();
    if (api) {
        api->log_info("UEVR-MCP: Pipe server starting on %s", m_pipe_name.c_str());
    }

    auto wname = JsonHelpers::utf8_to_wide(m_pipe_name);

    while (m_running.load()) {
        HANDLE pipe = CreateNamedPipeW(
            wname.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            4096, 4096, 0, nullptr
        );

        if (pipe == INVALID_HANDLE_VALUE) {
            if (api) api->log_error("UEVR-MCP: Failed to create named pipe");
            Sleep(1000);
            continue;
        }

        BOOL connected = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);

        if (!connected || !m_running.load()) {
            CloseHandle(pipe);
            continue;
        }

        // Read request
        char buffer[4096]{};
        DWORD bytes_read = 0;
        if (ReadFile(pipe, buffer, sizeof(buffer) - 1, &bytes_read, nullptr) && bytes_read > 0) {
            try {
                auto request = json::parse(std::string(buffer, bytes_read));
                log("Pipe: " + request.value("command", "unknown"));

                auto response = handle_request(request);
                auto response_str = response.dump();

                DWORD bytes_written = 0;
                WriteFile(pipe, response_str.c_str(), (DWORD)response_str.size(), &bytes_written, nullptr);
                FlushFileBuffers(pipe);
            } catch (const std::exception& e) {
                auto err = json{{"error", std::string("Parse error: ") + e.what()}}.dump();
                DWORD bw = 0;
                WriteFile(pipe, err.c_str(), (DWORD)err.size(), &bw, nullptr);
                FlushFileBuffers(pipe);
            }
        }

        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}
