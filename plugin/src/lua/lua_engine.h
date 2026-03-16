#pragma once

#include <string>
#include <memory>
#include <mutex>
#include <vector>
#include <cstdint>
#include <nlohmann/json.hpp>

using json = nlohmann::json;

// Opaque pointer to avoid sol2 header in every translation unit
struct LuaStateWrapper;

class LuaEngine {
public:
    static LuaEngine& get();

    // Create Lua state with UEVR API bindings. Call from on_initialize().
    void initialize();

    // Execute Lua code. Returns {success, result, output[], execTime_ms} or {success:false, error, output[]}.
    json execute(const std::string& code);

    // Execute a Lua callback with context variables injected into the environment.
    // context keys become local variables accessible in the script.
    json execute_callback(const std::string& code, const json& context);

    // Destroy and recreate the Lua state — clears all globals, callbacks, etc.
    json reset();

    // Hot-reload: execute a script file in the existing state (preserves globals/callbacks).
    json reload_script(const std::string& filepath);

    // Diagnostics: initialized, execCount, frameCallbackCount
    json get_state_info();

    // Inspect top-level Lua globals (names, types, values).
    json get_globals();

    // Run registered frame callbacks. Call from on_pre_engine_tick().
    void on_frame(float delta);

    bool is_initialized() const { return m_initialized; }

private:
    LuaEngine();
    ~LuaEngine();

    // Non-copyable
    LuaEngine(const LuaEngine&) = delete;
    LuaEngine& operator=(const LuaEngine&) = delete;

    void setup_bindings();
    void setup_uevr_api_bindings();
    void setup_module_loader();
    void setup_coroutine_scheduler();

    LuaStateWrapper* m_state{nullptr};  // Opaque wrapper around sol::state
    std::recursive_mutex m_mutex;
    std::vector<std::string> m_output_buffer;
    uint64_t m_exec_count{0};
    bool m_initialized{false};
    int m_next_callback_id{1};
    int m_next_timer_id{1};
    int m_next_coroutine_id{1};
};
