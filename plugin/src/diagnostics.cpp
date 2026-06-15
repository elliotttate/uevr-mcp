#include "diagnostics.h"

#include <Windows.h>
#include <fstream>
#include <iomanip>
#include <sstream>

#include <uevr/API.hpp>

namespace {

constexpr auto BREADCRUMB_FLUSH_INTERVAL = std::chrono::milliseconds(750);

std::filesystem::path get_fallback_breadcrumb_path() {
    char exe_path[MAX_PATH]{};
    GetModuleFileNameA(nullptr, exe_path, MAX_PATH);
    std::filesystem::path exe(exe_path);
    return exe.parent_path() / "uevr_mcp_last_breadcrumb.json";
}

} // namespace

Diagnostics::ScopedCallback::ScopedCallback(std::string name, std::string renderer, nlohmann::json extra)
    : m_name(std::move(name)) {
    Diagnostics::get().callback_enter(m_name, renderer, extra);
}

Diagnostics::ScopedCallback::~ScopedCallback() {
    if (!m_resolved) {
        Diagnostics::get().callback_success(m_name);
    }
}

void Diagnostics::ScopedCallback::fail(const std::string& error) {
    if (m_resolved) {
        return;
    }

    Diagnostics::get().callback_failure(m_name, error);
    m_resolved = true;
}

Diagnostics& Diagnostics::get() {
    static Diagnostics instance;
    return instance;
}

void Diagnostics::initialize() {
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_initialized) {
        return;
    }

    auto& api = uevr::API::get();
    if (api) {
        auto candidate = api->get_persistent_dir(L"uevr_mcp_last_breadcrumb.json");
        if (!candidate.empty()) {
            m_breadcrumb_path = std::move(candidate);
        }
    }

    if (m_breadcrumb_path.empty()) {
        m_breadcrumb_path = get_fallback_breadcrumb_path();
    }

    m_breadcrumb_state = {
        {"status", "idle"},
        {"callback", ""},
        {"detail", ""},
        {"timestamp", now_local_timestamp()},
        {"threadId", 0},
        {"sequence", 0}
    };

    m_initialized = true;
    write_breadcrumb_locked(true);
}

void Diagnostics::callback_enter(const std::string& name, const std::string& renderer, const nlohmann::json& extra) {
    if (!m_initialized) {
        initialize();
    }

    std::lock_guard<std::mutex> lock(m_mutex);
    auto& stats = m_callback_stats[name];
    stats.invocations++;
    stats.last_thread_id = GetCurrentThreadId();
    if (!renderer.empty()) {
        stats.renderer = renderer;
    }

    m_active_callbacks[name] = ActiveCallback{std::chrono::steady_clock::now(), renderer};

    m_breadcrumb_state = {
        {"status", "active"},
        {"callback", name},
        {"detail", ""},
        {"timestamp", now_local_timestamp()},
        {"threadId", GetCurrentThreadId()},
        {"sequence", ++m_breadcrumb_seq}
    };

    if (!renderer.empty()) {
        m_breadcrumb_state["renderer"] = renderer;
    }

    if (!extra.is_null() && !extra.empty()) {
        m_breadcrumb_state["extra"] = extra;
    }

    write_breadcrumb_locked(false);
}

void Diagnostics::callback_success(const std::string& name) {
    if (!m_initialized) {
        initialize();
    }

    std::lock_guard<std::mutex> lock(m_mutex);
    auto& stats = m_callback_stats[name];
    stats.successes++;
    stats.last_thread_id = GetCurrentThreadId();
    stats.last_success_at = now_local_timestamp();

    const auto active = m_active_callbacks.find(name);
    if (active != m_active_callbacks.end()) {
        stats.last_duration_us = static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::microseconds>(
                std::chrono::steady_clock::now() - active->second.started_at
            ).count());
        if (!active->second.renderer.empty()) {
            stats.renderer = active->second.renderer;
        }
        m_active_callbacks.erase(active);
    }

    m_breadcrumb_state = {
        {"status", "idle"},
        {"callback", name},
        {"detail", ""},
        {"timestamp", now_local_timestamp()},
        {"threadId", GetCurrentThreadId()},
        {"sequence", ++m_breadcrumb_seq}
    };

    if (!stats.renderer.empty()) {
        m_breadcrumb_state["renderer"] = stats.renderer;
    }

    write_breadcrumb_locked(false);
}

void Diagnostics::callback_failure(const std::string& name, const std::string& error) {
    if (!m_initialized) {
        initialize();
    }

    std::lock_guard<std::mutex> lock(m_mutex);
    auto& stats = m_callback_stats[name];
    stats.failures++;
    stats.last_error = error;
    stats.last_failure_at = now_local_timestamp();
    stats.last_thread_id = GetCurrentThreadId();

    const auto active = m_active_callbacks.find(name);
    if (active != m_active_callbacks.end()) {
        stats.last_duration_us = static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::microseconds>(
                std::chrono::steady_clock::now() - active->second.started_at
            ).count());
        if (!active->second.renderer.empty()) {
            stats.renderer = active->second.renderer;
        }
        m_active_callbacks.erase(active);
    }

    m_breadcrumb_state = {
        {"status", "failed"},
        {"callback", name},
        {"detail", error},
        {"timestamp", now_local_timestamp()},
        {"threadId", GetCurrentThreadId()},
        {"sequence", ++m_breadcrumb_seq}
    };

    if (!stats.renderer.empty()) {
        m_breadcrumb_state["renderer"] = stats.renderer;
    }

    write_breadcrumb_locked(true);
}

nlohmann::json Diagnostics::get_callback_health() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    nlohmann::json callbacks = nlohmann::json::object();

    for (const auto& [name, stats] : m_callback_stats) {
        nlohmann::json entry{
            {"invocations", stats.invocations},
            {"successes", stats.successes},
            {"failures", stats.failures},
            {"lastDurationUs", stats.last_duration_us},
            {"lastThreadId", stats.last_thread_id}
        };
        if (!stats.renderer.empty()) entry["renderer"] = stats.renderer;
        if (!stats.last_error.empty()) entry["lastError"] = stats.last_error;
        if (!stats.last_success_at.empty()) entry["lastSuccessAt"] = stats.last_success_at;
        if (!stats.last_failure_at.empty()) entry["lastFailureAt"] = stats.last_failure_at;
        callbacks[name] = std::move(entry);
    }

    return nlohmann::json{{"callbacks", std::move(callbacks)}};
}

nlohmann::json Diagnostics::get_breadcrumb() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    auto breadcrumb = m_breadcrumb_state;
    breadcrumb["path"] = m_breadcrumb_path.string();
    return breadcrumb;
}

std::filesystem::path Diagnostics::breadcrumb_path() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_breadcrumb_path;
}

std::string Diagnostics::describe_current_exception() {
    try {
        throw;
    } catch (const std::exception& e) {
        return e.what();
    } catch (...) {
        return "unknown C++ exception";
    }
}

void Diagnostics::write_breadcrumb_locked(bool force) {
    if (m_breadcrumb_path.empty()) {
        return;
    }

    const auto callback = m_breadcrumb_state.value("callback", std::string{});
    const auto status = m_breadcrumb_state.value("status", std::string{});
    const auto now = std::chrono::steady_clock::now();

    if (!force && now - m_last_breadcrumb_flush < BREADCRUMB_FLUSH_INTERVAL) {
        return;
    }

    std::error_code ec;
    std::filesystem::create_directories(m_breadcrumb_path.parent_path(), ec);

    std::ofstream out(m_breadcrumb_path, std::ios::binary | std::ios::trunc);
    if (!out.good()) {
        return;
    }

    out << m_breadcrumb_state.dump(2);
    out.flush();

    m_last_breadcrumb_flush = now;
    m_last_breadcrumb_callback = callback;
    m_last_breadcrumb_status = status;
}

std::string Diagnostics::now_local_timestamp() {
    auto now = std::chrono::system_clock::now();
    auto tt = std::chrono::system_clock::to_time_t(now);
    std::tm tm{};
    localtime_s(&tm, &tt);

    std::ostringstream oss;
    oss << std::put_time(&tm, "%Y-%m-%d %H:%M:%S");
    return oss.str();
}
