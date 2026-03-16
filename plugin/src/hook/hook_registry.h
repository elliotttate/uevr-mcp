#pragma once
#include <nlohmann/json.hpp>
#include <string>
#include <unordered_map>
#include <deque>
#include <mutex>
#include <uevr/API.hpp>

enum class HookAction {
    Log,        // Log all calls to ring buffer
    Block,      // Skip the original function execution
    LogAndBlock // Both
};

struct HookCallLog {
    uint64_t tick;
    uintptr_t object_address;
    std::string object_name;
    std::string function_name;
};

struct HookEntry {
    int id;
    std::string class_name;
    std::string function_name;
    HookAction action;
    uevr::API::UFunction* function{nullptr};
    int call_count{0};
    std::deque<HookCallLog> call_log; // Ring buffer, max 100
    bool active{true};
    static constexpr int MAX_LOG = 100;
};

class HookRegistry {
public:
    static HookRegistry& get();

    nlohmann::json add_hook(const std::string& class_name, const std::string& function_name, HookAction action);
    nlohmann::json remove_hook(int hook_id);
    nlohmann::json list_hooks();
    nlohmann::json get_hook_log(int hook_id, int max_entries = 50);
    nlohmann::json clear_hooks();

    // Called from the global hook callbacks
    bool on_pre_hook(uevr::API::UFunction* fn, uevr::API::UObject* obj, void* frame, void* result);
    void on_post_hook(uevr::API::UFunction* fn, uevr::API::UObject* obj, void* frame, void* result);

private:
    HookRegistry() = default;
    std::mutex m_mutex;
    std::unordered_map<int, HookEntry> m_hooks;
    std::unordered_map<uevr::API::UFunction*, int> m_func_to_hook; // Quick lookup
    int m_next_id{1};
};
