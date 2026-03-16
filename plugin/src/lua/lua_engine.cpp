#include "lua_engine.h"
#include "../pipe_server.h"
#include "../json_helpers.h"
#include "../event_bus.h"

#define SOL_ALL_SAFETIES_ON 1
#include <sol/sol.hpp>

#include <uevr/API.hpp>
#include <chrono>
#include <sstream>
#include <fstream>
#include <filesystem>
#include <unordered_set>

using namespace uevr;

// ── Opaque wrapper so sol::state doesn't leak into the header ───────

struct LuaStateWrapper {
    sol::state lua;
};

// ── Instruction count hook for infinite loop protection ─────────────

static constexpr int LUA_INSTRUCTION_LIMIT = 10'000'000;

static void instruction_count_hook(lua_State* L, lua_Debug*) {
    luaL_error(L, "Execution exceeded instruction limit (%d instructions) — possible infinite loop", LUA_INSTRUCTION_LIMIT);
}

// ── lua_to_json helper ──────────────────────────────────────────────

static json sol_to_json(const sol::object& obj) {
    switch (obj.get_type()) {
    case sol::type::nil:
    case sol::type::none:
        return nullptr;

    case sol::type::boolean:
        return obj.as<bool>();

    case sol::type::number: {
        double d = obj.as<double>();
        int64_t i = static_cast<int64_t>(d);
        if (static_cast<double>(i) == d && d >= -9007199254740992.0 && d <= 9007199254740992.0) {
            return i;
        }
        return d;
    }

    case sol::type::string:
        return obj.as<std::string>();

    case sol::type::table: {
        sol::table tbl = obj.as<sol::table>();
        // Check if it's array-like (sequential integer keys starting at 1)
        bool is_array = true;
        size_t count = 0;
        tbl.for_each([&](const sol::object& k, const sol::object&) {
            count++;
            if (k.get_type() != sol::type::number) {
                is_array = false;
                return;
            }
            double d = k.as<double>();
            if (d != static_cast<double>(count)) {
                is_array = false;
            }
        });

        if (is_array && count > 0) {
            json arr = json::array();
            for (size_t i = 1; i <= count; i++) {
                arr.push_back(sol_to_json(tbl[i]));
            }
            return arr;
        }

        json obj_json = json::object();
        tbl.for_each([&](const sol::object& k, const sol::object& v) {
            std::string key;
            if (k.get_type() == sol::type::string) {
                key = k.as<std::string>();
            } else if (k.get_type() == sol::type::number) {
                key = std::to_string(k.as<int64_t>());
            } else {
                return;
            }
            obj_json[key] = sol_to_json(v);
        });
        return obj_json;
    }

    case sol::type::userdata:
    case sol::type::lightuserdata: {
        void* ptr = obj.as<void*>();
        return json{{"_type", "userdata"}, {"address", JsonHelpers::address_to_string(ptr)}};
    }

    case sol::type::function:
        return json{{"_type", "function"}};

    case sol::type::thread:
        return json{{"_type", "thread"}};

    default:
        return json{{"_type", "unknown"}};
    }
}

// ── LuaEngine implementation ────────────────────────────────────────

LuaEngine& LuaEngine::get() {
    static LuaEngine instance;
    return instance;
}

LuaEngine::LuaEngine() = default;

LuaEngine::~LuaEngine() {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    delete m_state;
    m_state = nullptr;
}

void LuaEngine::initialize() {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    if (m_initialized) return;

    m_state = new LuaStateWrapper();
    auto& lua = m_state->lua;

    lua.open_libraries(
        sol::lib::base,
        sol::lib::string,
        sol::lib::math,
        sol::lib::table,
        sol::lib::package,
        sol::lib::coroutine,
        sol::lib::utf8,
        sol::lib::os,
        sol::lib::bit32
    );

    // Restrict dangerous os functions
    sol::table os_table = lua["os"];
    os_table["execute"] = sol::nil;
    os_table["exit"] = sol::nil;
    os_table["remove"] = sol::nil;
    os_table["rename"] = sol::nil;
    os_table["getenv"] = sol::nil;

    // Restrict dangerous io/file functions
    lua["io"] = sol::nil;
    lua["dofile"] = sol::nil;
    lua["loadfile"] = sol::nil;

    setup_bindings();
    setup_uevr_api_bindings();
    setup_module_loader();
    setup_coroutine_scheduler();

    m_initialized = true;
    PipeServer::get().log("LuaEngine: initialized");
}

void LuaEngine::setup_bindings() {
    auto& lua = m_state->lua;

    // Override print() to capture output
    lua["print"] = [this](sol::variadic_args va) {
        std::ostringstream oss;
        bool first = true;
        for (const auto& arg : va) {
            if (!first) oss << "\t";
            first = false;

            sol::object obj = arg;
            switch (obj.get_type()) {
            case sol::type::nil: oss << "nil"; break;
            case sol::type::boolean: oss << (obj.as<bool>() ? "true" : "false"); break;
            case sol::type::number: oss << obj.as<double>(); break;
            case sol::type::string: oss << obj.as<std::string>(); break;
            default: oss << sol::type_name(m_state->lua.lua_state(), obj.get_type()); break;
            }
        }
        m_output_buffer.push_back(oss.str());
    };

    // mcp namespace for callback management
    sol::table mcp = lua.create_named_table("mcp");

    // Frame callback storage lives as a Lua table
    lua["_mcp_frame_callbacks"] = lua.create_table();

    mcp["on_frame"] = [this](sol::protected_function fn) -> int {
        sol::table callbacks = m_state->lua["_mcp_frame_callbacks"];
        int id = m_next_callback_id++;
        callbacks[id] = fn;
        return id;
    };

    mcp["remove_callback"] = [this](int id) {
        sol::table callbacks = m_state->lua["_mcp_frame_callbacks"];
        callbacks[id] = sol::nil;
    };

    mcp["clear_callbacks"] = [this]() {
        m_state->lua["_mcp_frame_callbacks"] = m_state->lua.create_table();
        m_next_callback_id = 1;
    };

    // mcp.log — log to pipe server
    mcp["log"] = [](const std::string& msg) {
        PipeServer::get().log("Lua: " + msg);
    };

    // ── Timer / scheduler system ─────────────────────────────────────
    // _mcp_timers is a table keyed by timer id. Each entry is a table:
    //   { id=int, delay=float, remaining=float, looping=bool, callback=function }
    lua["_mcp_timers"] = lua.create_table();

    // mcp.set_timer(delay_seconds, callback, looping?) -> timer_id
    mcp["set_timer"] = [this](float delay, sol::protected_function fn, sol::optional<bool> looping) -> int {
        int id = m_next_timer_id++;
        sol::table timers = m_state->lua["_mcp_timers"];
        sol::table entry = m_state->lua.create_table();
        entry["id"] = id;
        entry["delay"] = delay;
        entry["remaining"] = delay;
        entry["looping"] = looping.value_or(false);
        entry["callback"] = fn;
        timers[id] = entry;
        return id;
    };

    // mcp.clear_timer(timer_id)
    mcp["clear_timer"] = [this](int id) {
        sol::table timers = m_state->lua["_mcp_timers"];
        timers[id] = sol::nil;
    };

    // mcp.clear_all_timers()
    mcp["clear_all_timers"] = [this]() {
        m_state->lua["_mcp_timers"] = m_state->lua.create_table();
        m_next_timer_id = 1;
    };
}

void LuaEngine::setup_uevr_api_bindings() {
    auto& lua = m_state->lua;

    // ── uevr namespace ──────────────────────────────────────────────
    sol::table uevr = lua.create_named_table("uevr");

    // ── UObject usertype ────────────────────────────────────────────
    lua.new_usertype<API::UObject>("UObject", sol::no_constructor,
        "get_address", [](API::UObject* self) -> uintptr_t {
            return reinterpret_cast<uintptr_t>(self);
        },
        "get_fname", [](API::UObject* self) -> std::string {
            auto fname = self->get_fname();
            if (!fname) return "";
            return JsonHelpers::wide_to_utf8(fname->to_string());
        },
        "get_full_name", [](API::UObject* self) -> std::string {
            return JsonHelpers::wide_to_utf8(self->get_full_name());
        },
        "get_class", [](API::UObject* self) -> API::UClass* {
            return self->get_class();
        },
        "get_outer", [](API::UObject* self) -> API::UObject* {
            return self->get_outer();
        },
        "is_a", [](API::UObject* self, API::UClass* cls) -> bool {
            if (!cls) return false;
            return self->is_a(cls);
        },
        "get_bool_property", [](API::UObject* self, const std::string& name) -> bool {
            return self->get_bool_property(JsonHelpers::utf8_to_wide(name));
        },
        "set_bool_property", [](API::UObject* self, const std::string& name, bool value) {
            self->set_bool_property(JsonHelpers::utf8_to_wide(name), value);
        },
        "get_property_data", [](API::UObject* self, const std::string& name) -> uintptr_t {
            void* data = self->get_property_data(JsonHelpers::utf8_to_wide(name));
            return reinterpret_cast<uintptr_t>(data);
        },
        "call_function", [](API::UObject* self, const std::string& name) {
            self->call_function(JsonHelpers::utf8_to_wide(name), nullptr);
        },
        "process_event", [](API::UObject* self, API::UFunction* func) {
            if (!func) return;
            auto ps = func->get_properties_size();
            std::vector<uint8_t> params(ps, 0);
            self->process_event(func, params.data());
        }
    );

    // ── UStruct usertype ────────────────────────────────────────────
    lua.new_usertype<API::UStruct>("UStruct", sol::no_constructor,
        sol::base_classes, sol::bases<API::UObject, API::UField>(),
        "get_super", [](API::UStruct* self) -> API::UStruct* {
            return self->get_super();
        },
        "find_function", [](API::UStruct* self, const std::string& name) -> API::UFunction* {
            return self->find_function(JsonHelpers::utf8_to_wide(name));
        },
        "find_property", [](API::UStruct* self, const std::string& name) -> API::FProperty* {
            return reinterpret_cast<API::FProperty*>(self->find_property(JsonHelpers::utf8_to_wide(name)));
        },
        "get_child_properties", [](API::UStruct* self) -> API::FField* {
            return self->get_child_properties();
        },
        "get_properties_size", [](API::UStruct* self) -> int32_t {
            return self->get_properties_size();
        }
    );

    // ── UClass usertype ─────────────────────────────────────────────
    lua.new_usertype<API::UClass>("UClass", sol::no_constructor,
        sol::base_classes, sol::bases<API::UStruct, API::UObject, API::UField>(),
        "get_class_default_object", [](API::UClass* self) -> API::UObject* {
            return self->get_class_default_object();
        },
        "get_objects_matching", [](API::UClass* self, sol::optional<bool> allow_default) -> sol::as_table_t<std::vector<API::UObject*>> {
            return sol::as_table(self->get_objects_matching(allow_default.value_or(false)));
        },
        "get_first_object_matching", [](API::UClass* self, sol::optional<bool> allow_default) -> API::UObject* {
            return self->get_first_object_matching(allow_default.value_or(false));
        }
    );

    // ── UFunction usertype ──────────────────────────────────────────
    lua.new_usertype<API::UFunction>("UFunction", sol::no_constructor,
        sol::base_classes, sol::bases<API::UStruct, API::UObject, API::UField>(),
        "get_native_function", [](API::UFunction* self) -> uintptr_t {
            return reinterpret_cast<uintptr_t>(self->get_native_function());
        },
        "get_function_flags", [](API::UFunction* self) -> uint32_t {
            return self->get_function_flags();
        },
        "call", [](API::UFunction* self, API::UObject* obj) {
            if (!obj) return;
            auto ps = self->get_properties_size();
            std::vector<uint8_t> params(ps, 0);
            obj->process_event(self, params.data());
        }
    );

    // ── UField usertype ─────────────────────────────────────────────
    lua.new_usertype<API::UField>("UField", sol::no_constructor,
        sol::base_classes, sol::bases<API::UObject>(),
        "get_next", [](API::UField* self) -> API::UField* {
            return self->get_next();
        }
    );

    // ── FField usertype ─────────────────────────────────────────────
    lua.new_usertype<API::FField>("FField", sol::no_constructor,
        "get_next", [](API::FField* self) -> API::FField* {
            return self->get_next();
        },
        "get_fname", [](API::FField* self) -> std::string {
            auto fname = self->get_fname();
            if (!fname) return "";
            return JsonHelpers::wide_to_utf8(fname->to_string());
        }
    );

    // ── FProperty usertype ──────────────────────────────────────────
    lua.new_usertype<API::FProperty>("FProperty", sol::no_constructor,
        sol::base_classes, sol::bases<API::FField>(),
        "get_offset", [](API::FProperty* self) -> int32_t {
            return self->get_offset();
        },
        "is_param", [](API::FProperty* self) -> bool {
            return self->is_param();
        },
        "is_return_param", [](API::FProperty* self) -> bool {
            return self->is_return_param();
        }
    );

    // ── uevr.api — top-level API functions ──────────────────────────
    uevr["api"] = lua.create_table_with(
        "find_uobject", [](const std::string& name) -> API::UObject* {
            auto& api = API::get();
            return api->find_uobject(JsonHelpers::utf8_to_wide(name));
        },
        "get_engine", []() -> API::UObject* {
            auto& api = API::get();
            return reinterpret_cast<API::UObject*>(api->get_engine());
        },
        "get_player_controller", [](sol::optional<int> index) -> API::UObject* {
            auto& api = API::get();
            return api->get_player_controller(index.value_or(0));
        },
        "get_local_pawn", [](sol::optional<int> index) -> API::UObject* {
            auto& api = API::get();
            return api->get_local_pawn(index.value_or(0));
        },
        "spawn_object", [](API::UClass* cls, API::UObject* outer) -> API::UObject* {
            auto& api = API::get();
            if (!cls || !outer) return nullptr;
            return api->spawn_object(cls, outer);
        },
        "add_component_by_class", [](API::UObject* actor, API::UClass* cls, sol::optional<bool> deferred) -> API::UObject* {
            auto& api = API::get();
            if (!actor || !cls) return nullptr;
            return api->add_component_by_class(actor, cls, deferred.value_or(false));
        },
        "execute_command", [](const std::string& cmd) {
            auto& api = API::get();
            api->execute_command(JsonHelpers::utf8_to_wide(cmd));
        },
        "get_persistent_dir", []() -> std::string {
            auto& api = API::get();
            return api->get_persistent_dir().string();
        },
        "dispatch_lua_event", [](const std::string& name, const std::string& data) {
            auto& api = API::get();
            api->dispatch_lua_event(name, data);
        }
    );

    // ── uevr.uobject_hook ───────────────────────────────────────────
    uevr["uobject_hook"] = lua.create_table_with(
        "exists", [](API::UObject* obj) -> bool {
            if (!obj) return false;
            return API::UObjectHook::exists(obj);
        },
        "get_first_object_by_class", [](API::UClass* cls, sol::optional<bool> allow_default) -> API::UObject* {
            if (!cls) return nullptr;
            return API::UObjectHook::get_first_object_by_class(cls, allow_default.value_or(false));
        },
        "get_objects_by_class", [](API::UClass* cls, sol::optional<bool> allow_default) -> sol::as_table_t<std::vector<API::UObject*>> {
            if (!cls) return sol::as_table(std::vector<API::UObject*>{});
            return sol::as_table(API::UObjectHook::get_objects_by_class(cls, allow_default.value_or(false)));
        },
        "get_or_add_motion_controller_state", [](API::UObject* obj) -> API::UObjectHook::MotionControllerState* {
            if (!obj) return nullptr;
            return API::UObjectHook::get_or_add_motion_controller_state(obj);
        },
        "get_motion_controller_state", [](API::UObject* obj) -> API::UObjectHook::MotionControllerState* {
            if (!obj) return nullptr;
            return API::UObjectHook::get_motion_controller_state(obj);
        },
        "remove_motion_controller_state", [](API::UObject* obj) {
            if (obj) API::UObjectHook::remove_motion_controller_state(obj);
        }
    );

    // ── MotionControllerState usertype ──────────────────────────────
    lua.new_usertype<API::UObjectHook::MotionControllerState>("MotionControllerState", sol::no_constructor,
        "set_hand", [](API::UObjectHook::MotionControllerState* self, uint32_t hand) {
            self->set_hand(hand);
        },
        "set_permanent", [](API::UObjectHook::MotionControllerState* self, bool permanent) {
            self->set_permanent(permanent);
        },
        "set_rotation_offset", [](API::UObjectHook::MotionControllerState* self, float x, float y, float z, float w) {
            UEVR_Quaternionf q{x, y, z, w};
            self->set_rotation_offset(&q);
        },
        "set_location_offset", [](API::UObjectHook::MotionControllerState* self, float x, float y, float z) {
            UEVR_Vector3f v{x, y, z};
            self->set_location_offset(&v);
        }
    );

    // ── VR bindings ─────────────────────────────────────────────────
    uevr["vr"] = lua.create_table_with(
        "is_runtime_ready", []() -> bool {
            return API::VR::is_runtime_ready();
        },
        "is_openvr", []() -> bool {
            return API::VR::is_openvr();
        },
        "is_openxr", []() -> bool {
            return API::VR::is_openxr();
        },
        "is_hmd_active", []() -> bool {
            return API::VR::is_hmd_active();
        },
        "get_standing_origin", []() -> sol::as_table_t<std::vector<float>> {
            auto o = API::VR::get_standing_origin();
            return sol::as_table(std::vector<float>{o.x, o.y, o.z});
        },
        "get_hmd_index", []() -> uint32_t {
            return API::VR::get_hmd_index();
        },
        "get_left_controller_index", []() -> uint32_t {
            return API::VR::get_left_controller_index();
        },
        "get_right_controller_index", []() -> uint32_t {
            return API::VR::get_right_controller_index();
        },
        "get_pose", [](uint32_t index) -> sol::as_table_t<std::vector<float>> {
            auto p = API::VR::get_pose(index);
            return sol::as_table(std::vector<float>{
                p.position.x, p.position.y, p.position.z,
                p.rotation.x, p.rotation.y, p.rotation.z, p.rotation.w
            });
        },
        "recenter_view", []() {
            API::VR::recenter_view();
        },
        "get_aim_method", []() -> int {
            return static_cast<int>(API::VR::get_aim_method());
        },
        "set_aim_method", [](int method) {
            API::VR::set_aim_method(static_cast<API::VR::AimMethod>(method));
        },
        "is_snap_turn_enabled", []() -> bool {
            return API::VR::is_snap_turn_enabled();
        },
        "set_snap_turn_enabled", [](bool enabled) {
            API::VR::set_snap_turn_enabled(enabled);
        },
        "set_decoupled_pitch_enabled", [](bool enabled) {
            API::VR::set_decoupled_pitch_enabled(enabled);
        },
        "set_mod_value", [](const std::string& key, const std::string& value) {
            // Call the C function pointer directly to avoid template deduction issues
            auto fn = API::get()->param()->vr->set_mod_value;
            fn(key.data(), value.c_str());
        },
        "get_mod_value", [](const std::string& key) -> std::string {
            return API::VR::get_mod_value<std::string>(key);
        },
        "save_config", []() {
            API::VR::save_config();
        },
        "reload_config", []() {
            API::VR::reload_config();
        },
        "trigger_haptic_vibration", [](float seconds_from_now, float amplitude, float frequency, float duration, sol::optional<uint64_t> source) {
            API::VR::trigger_haptic_vibration(seconds_from_now, amplitude, frequency, duration,
                reinterpret_cast<UEVR_InputSourceHandle>(source.value_or(0)));
        }
    );

    // ── Console bindings ────────────────────────────────────────────
    lua.new_usertype<API::IConsoleVariable>("IConsoleVariable", sol::no_constructor,
        "set_int", [](API::IConsoleVariable* self, int value) {
            self->set(value);
        },
        "set_float", [](API::IConsoleVariable* self, float value) {
            self->set(value);
        },
        "set_string", [](API::IConsoleVariable* self, const std::string& value) {
            self->set(JsonHelpers::utf8_to_wide(value));
        },
        "get_int", [](API::IConsoleVariable* self) -> int {
            return self->get_int();
        },
        "get_float", [](API::IConsoleVariable* self) -> float {
            return self->get_float();
        }
    );

    uevr["console"] = lua.create_table_with(
        "find_variable", [](const std::string& name) -> API::IConsoleVariable* {
            auto& api = API::get();
            auto mgr = api->get_console_manager();
            if (!mgr) return nullptr;
            return mgr->find_variable(JsonHelpers::utf8_to_wide(name));
        },
        "find_command", [](const std::string& name) -> API::IConsoleCommand* {
            auto& api = API::get();
            auto mgr = api->get_console_manager();
            if (!mgr) return nullptr;
            return mgr->find_command(JsonHelpers::utf8_to_wide(name));
        }
    );

    lua.new_usertype<API::IConsoleCommand>("IConsoleCommand", sol::no_constructor,
        "execute", [](API::IConsoleCommand* self, sol::optional<std::string> args) {
            self->execute(JsonHelpers::utf8_to_wide(args.value_or("")));
        }
    );
}

json LuaEngine::execute(const std::string& code) {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    if (!m_initialized || !m_state) {
        return json{{"success", false}, {"error", "Lua engine not initialized"}};
    }

    m_output_buffer.clear();
    m_exec_count++;

    auto& lua = m_state->lua;
    auto start = std::chrono::high_resolution_clock::now();

    // Set instruction count hook for infinite loop protection
    lua_sethook(lua.lua_state(), instruction_count_hook, LUA_MASKCOUNT, LUA_INSTRUCTION_LIMIT);

    auto result = lua.safe_script(code, sol::script_pass_on_error);

    // Remove the hook
    lua_sethook(lua.lua_state(), nullptr, 0, 0);

    auto end = std::chrono::high_resolution_clock::now();
    double exec_ms = std::chrono::duration<double, std::milli>(end - start).count();

    json response;
    response["output"] = m_output_buffer;
    response["execTime_ms"] = exec_ms;

    if (result.valid()) {
        sol::object val = result;
        response["success"] = true;
        response["result"] = sol_to_json(val);
    } else {
        sol::error err = result;
        response["success"] = false;
        response["error"] = err.what();
    }

    return response;
}

json LuaEngine::reset() {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    if (!m_initialized) {
        return json{{"success", false}, {"error", "Lua engine not initialized"}};
    }

    delete m_state;
    m_state = nullptr;
    m_exec_count = 0;
    m_next_callback_id = 1;
    m_next_timer_id = 1;
    m_next_coroutine_id = 1;
    m_output_buffer.clear();
    m_initialized = false;

    // Re-initialize
    initialize();

    PipeServer::get().log("LuaEngine: state reset");
    return json{{"success", true}, {"message", "Lua state reset"}};
}

json LuaEngine::get_state_info() {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    json info;
    info["initialized"] = m_initialized;
    info["execCount"] = m_exec_count;

    if (m_initialized && m_state) {
        auto& lua = m_state->lua;

        // Count frame callbacks
        sol::table callbacks = lua["_mcp_frame_callbacks"];
        int count = 0;
        callbacks.for_each([&](const sol::object&, const sol::object& v) {
            if (v.get_type() == sol::type::function) count++;
        });
        info["frameCallbackCount"] = count;

        // Count active timers
        sol::table timers = lua["_mcp_timers"];
        int timer_count = 0;
        timers.for_each([&](const sol::object&, const sol::object& v) {
            if (v.get_type() == sol::type::table) timer_count++;
        });
        info["timerCount"] = timer_count;

        // Count active coroutines
        sol::table coroutines = lua["_mcp_coroutines"];
        int co_count = 0;
        if (coroutines.valid()) {
            coroutines.for_each([&](const sol::object&, const sol::object& v) {
                if (v.get_type() == sol::type::table) co_count++;
            });
        }
        info["coroutineCount"] = co_count;

        // Lua memory usage
        info["memoryKB"] = lua_gc(lua.lua_state(), LUA_GCCOUNT, 0);
    } else {
        info["frameCallbackCount"] = 0;
        info["timerCount"] = 0;
        info["coroutineCount"] = 0;
        info["memoryKB"] = 0;
    }

    return info;
}

void LuaEngine::on_frame(float delta) {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    if (!m_initialized || !m_state) return;

    auto& lua = m_state->lua;
    sol::table callbacks = lua["_mcp_frame_callbacks"];
    if (!callbacks.valid()) return;

    callbacks.for_each([&](const sol::object& key, const sol::object& val) {
        if (val.get_type() != sol::type::function) return;

        sol::protected_function fn = val.as<sol::protected_function>();
        auto result = fn(delta);
        if (!result.valid()) {
            sol::error err = result;
            PipeServer::get().log("LuaEngine: frame callback error: " + std::string(err.what()));
        }
    });

    // ── Process timers ───────────────────────────────────────────────
    sol::table timers = lua["_mcp_timers"];
    if (!timers.valid()) return;

    // Collect expired timer ids so we can modify the table after iteration
    std::vector<int> to_remove;

    timers.for_each([&](const sol::object& key, const sol::object& val) {
        if (val.get_type() != sol::type::table) return;

        sol::table entry = val.as<sol::table>();
        float remaining = entry["remaining"].get_or(0.0f);
        remaining -= delta;

        if (remaining <= 0.0f) {
            sol::optional<sol::protected_function> cb = entry["callback"];
            if (cb.has_value()) {
                auto result = cb.value()();
                if (!result.valid()) {
                    sol::error err = result;
                    PipeServer::get().log("LuaEngine: timer callback error: " + std::string(err.what()));
                }
            }

            bool looping = entry["looping"].get_or(false);
            if (looping) {
                float delay = entry["delay"].get_or(0.0f);
                entry["remaining"] = delay;
            } else {
                int id = entry["id"].get_or(0);
                to_remove.push_back(id);
            }
        } else {
            entry["remaining"] = remaining;
        }
    });

    // Remove expired non-looping timers
    for (int id : to_remove) {
        timers[id] = sol::nil;
    }

    // ── Process async coroutines ────────────────────────────────────────
    sol::table coroutines = lua["_mcp_coroutines"];
    if (!coroutines.valid()) return;

    std::vector<int> co_remove;

    coroutines.for_each([&](const sol::object& key, const sol::object& val) {
        if (val.get_type() != sol::type::table) return;
        sol::table entry = val.as<sol::table>();

        std::string wait_type = entry["wait_type"].get_or<std::string>("none");
        bool ready = false;

        if (wait_type == "seconds") {
            float remaining = entry["remaining"].get_or(0.0f);
            remaining -= delta;
            if (remaining <= 0.0f) {
                ready = true;
            } else {
                entry["remaining"] = remaining;
            }
        } else if (wait_type == "predicate") {
            sol::optional<sol::protected_function> pred = entry["predicate"];
            if (pred.has_value()) {
                auto pred_result = pred.value()();
                if (pred_result.valid()) {
                    sol::object r = pred_result;
                    if (r.get_type() == sol::type::boolean && r.as<bool>()) {
                        ready = true;
                    }
                }
            }
        } else {
            // "none" — resume immediately (first tick after mcp.async)
            ready = true;
        }

        if (ready) {
            sol::optional<sol::thread> thread_opt = entry["thread"];
            if (!thread_opt.has_value()) {
                int id = entry["id"].get_or(0);
                co_remove.push_back(id);
                return;
            }

            sol::thread& thread = thread_opt.value();
            sol::state_view thread_state = thread.state();
            sol::object fn_obj = thread_state["_mcp_co_fn"];
            sol::coroutine co(thread_state.lua_state(), fn_obj);

            if (!co) {
                int id = entry["id"].get_or(0);
                co_remove.push_back(id);
                return;
            }

            auto result = co();
            if (!result.valid()) {
                sol::error err = result;
                PipeServer::get().log("LuaEngine: coroutine error: " + std::string(err.what()));
                int id = entry["id"].get_or(0);
                co_remove.push_back(id);
                return;
            }

            // Check if coroutine finished
            auto status = thread.status();
            if (status == sol::thread_status::dead || status == sol::thread_status::ok) {
                int id = entry["id"].get_or(0);
                co_remove.push_back(id);
            }
            // If yielded, the coroutine set its own wait_type via mcp.wait/wait_until
        }
    });

    for (int id : co_remove) {
        coroutines[id] = sol::nil;
    }
}

// ── Module loader (require() support) ──────────────────────────────

void LuaEngine::setup_module_loader() {
    auto& lua = m_state->lua;

    // Custom searcher that loads from the UEVR scripts directory
    lua.safe_script(R"(
        table.insert(package.searchers, 2, function(modname)
            local base = _mcp_scripts_dir
            if not base then return "\n\tno _mcp_scripts_dir set" end
            local path = base .. "/" .. modname:gsub("%.", "/") .. ".lua"
            local f = io.open(path, "r")
            if not f then
                return "\n\tno file '" .. path .. "'"
            end
            local content = f:read("*a")
            f:close()
            local fn, err = load(content, "@" .. path)
            if not fn then
                error("error loading module '" .. modname .. "' from file '" .. path .. "':\n\t" .. err)
            end
            return fn, path
        end)
    )", sol::script_pass_on_error);

    // Re-enable io.open (read-only, for the module loader) but keep other io functions disabled
    lua.safe_script(R"(
        local _original_open = _G._io_open_backup
        if not _original_open then
            -- io was set to nil in initialization, so we need a minimal open
            -- We'll use loadfile-style loading instead
        end
    )", sol::script_pass_on_error);

    // Set the scripts directory path
    auto& api = uevr::API::get();
    if (api) {
        auto scripts_dir = api->get_persistent_dir() / "scripts";
        lua["_mcp_scripts_dir"] = scripts_dir.string();

        // Provide a minimal io.open for the module loader only
        // We create a sandboxed version that only allows reading from scripts dir
        std::string sd = scripts_dir.string();
        lua["io"] = lua.create_table();
        lua["io"]["open"] = [sd](const std::string& path, sol::optional<std::string> mode) -> sol::object {
            // Only allow read mode
            std::string m = mode.value_or("r");
            if (m.find('w') != std::string::npos || m.find('a') != std::string::npos) {
                return sol::nil;
            }
            // Must be under scripts dir (basic check)
            std::string normalized = path;
            if (normalized.find("..") != std::string::npos) {
                return sol::nil;
            }
            // Actually open the file — if it fails, return nil
            // Note: we return nil since we can't return a full Lua file handle easily
            // The module searcher above uses io.open, so we need this to work
            return sol::nil; // Fall through — we handle it differently
        };
    }

    // Actually, the cleanest approach: use loadfile directly in the searcher
    // Re-enable loadfile (sandboxed to scripts dir only)
    if (api) {
        auto scripts_dir = api->get_persistent_dir() / "scripts";
        std::string sd = scripts_dir.string();

        // Replace the searcher with one that uses loadfile-style loading via C++
        lua["_mcp_load_module"] = [this, sd](const std::string& modname) -> sol::object {
            // Convert module name to path
            std::string relpath = modname;
            for (auto& c : relpath) { if (c == '.') c = '/'; }
            relpath += ".lua";

            std::filesystem::path filepath = std::filesystem::path(sd) / relpath;

            // Also check autorun subdirectory
            if (!std::filesystem::exists(filepath)) {
                filepath = std::filesystem::path(sd) / "autorun" / relpath;
            }

            if (!std::filesystem::exists(filepath)) {
                return sol::make_object(m_state->lua, sol::nil);
            }

            std::ifstream in(filepath, std::ios::binary);
            if (!in) return sol::make_object(m_state->lua, sol::nil);

            std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());

            auto result = m_state->lua.load(content, "@" + filepath.string());
            if (!result.valid()) {
                sol::error err = result;
                PipeServer::get().log("LuaEngine: module load error: " + std::string(err.what()));
                return sol::make_object(m_state->lua, sol::nil);
            }

            return sol::object(result);
        };

        // Install a clean searcher using the C++ loader
        lua.safe_script(R"(
            -- Clear existing custom searchers, keep only preload and C++ loader
            package.searchers = {
                package.searchers[1], -- preload searcher
                function(modname)
                    local loader = _mcp_load_module(modname)
                    if loader then
                        return loader
                    end
                    return "\n\tno module '" .. modname .. "' in scripts directory"
                end
            }
        )", sol::script_pass_on_error);
    }

    // Disable io again after setting up the loader
    lua["io"] = sol::nil;
}

// ── Coroutine scheduler setup ──────────────────────────────────────

void LuaEngine::setup_coroutine_scheduler() {
    auto& lua = m_state->lua;
    sol::table mcp = lua["mcp"];

    // Storage for active coroutines
    lua["_mcp_coroutines"] = lua.create_table();

    // mcp.async(fn) — wrap fn in a managed coroutine that can use mcp.wait()
    mcp["async"] = [this](sol::protected_function fn) -> int {
        auto& lua = m_state->lua;
        int id = m_next_coroutine_id++;

        // Create a new thread (Lua coroutine)
        sol::thread thread = sol::thread::create(lua.lua_state());
        sol::state_view thread_state = thread.state();

        // Store the function in the thread's environment
        thread_state["_mcp_co_fn"] = fn;
        // Store current coroutine id for mcp.wait() to find
        thread_state["_mcp_co_id"] = id;

        sol::table coroutines = lua["_mcp_coroutines"];
        sol::table entry = lua.create_table();
        entry["id"] = id;
        entry["thread"] = thread;
        entry["wait_type"] = "none"; // Will be resumed on next tick

        coroutines[id] = entry;
        return id;
    };

    // mcp.wait(seconds) — yield the current coroutine, resume after delay
    mcp["wait"] = [this](lua_State* L, float seconds) -> int {
        auto& lua = m_state->lua;
        sol::state_view sv(L);

        // Find which coroutine we're in
        sol::optional<int> co_id = sv["_mcp_co_id"];
        if (!co_id.has_value()) {
            luaL_error(L, "mcp.wait() can only be called inside mcp.async()");
            return 0;
        }

        // Update the coroutine entry's wait info
        sol::table coroutines = lua["_mcp_coroutines"];
        sol::optional<sol::table> entry = coroutines[co_id.value()];
        if (entry.has_value()) {
            entry.value()["wait_type"] = "seconds";
            entry.value()["remaining"] = seconds;
        }

        return lua_yield(L, 0);
    };

    // mcp.wait_until(predicate_fn) — yield, resume when predicate returns true
    mcp["wait_until"] = [this](lua_State* L, sol::protected_function predicate) -> int {
        auto& lua = m_state->lua;
        sol::state_view sv(L);

        sol::optional<int> co_id = sv["_mcp_co_id"];
        if (!co_id.has_value()) {
            luaL_error(L, "mcp.wait_until() can only be called inside mcp.async()");
            return 0;
        }

        sol::table coroutines = lua["_mcp_coroutines"];
        sol::optional<sol::table> entry = coroutines[co_id.value()];
        if (entry.has_value()) {
            entry.value()["wait_type"] = "predicate";
            entry.value()["predicate"] = predicate;
        }

        return lua_yield(L, 0);
    };

    // mcp.cancel_async(id) — cancel a running coroutine
    mcp["cancel_async"] = [this](int id) {
        sol::table coroutines = m_state->lua["_mcp_coroutines"];
        coroutines[id] = sol::nil;
    };

    // mcp.clear_async() — cancel all coroutines
    mcp["clear_async"] = [this]() {
        m_state->lua["_mcp_coroutines"] = m_state->lua.create_table();
        m_next_coroutine_id = 1;
    };
}

// ── execute_callback — run script with context variables ───────────

json LuaEngine::execute_callback(const std::string& code, const json& context) {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    if (!m_initialized || !m_state) {
        return json{{"success", false}, {"error", "Lua engine not initialized"}};
    }

    m_output_buffer.clear();
    auto& lua = m_state->lua;

    // Inject context variables into a temporary table
    sol::table ctx = lua.create_table();
    for (auto it = context.begin(); it != context.end(); ++it) {
        const auto& key = it.key();
        const auto& val = it.value();

        if (val.is_string()) ctx[key] = val.get<std::string>();
        else if (val.is_number_integer()) ctx[key] = val.get<int64_t>();
        else if (val.is_number_float()) ctx[key] = val.get<double>();
        else if (val.is_boolean()) ctx[key] = val.get<bool>();
        else ctx[key] = val.dump(); // fallback to string
    }

    lua["_callback_ctx"] = ctx;

    // Wrap the code to have access to context vars as locals
    std::string wrapped = "local ctx = _callback_ctx\n" + code;

    lua_sethook(lua.lua_state(), instruction_count_hook, LUA_MASKCOUNT, LUA_INSTRUCTION_LIMIT);
    auto result = lua.safe_script(wrapped, sol::script_pass_on_error);
    lua_sethook(lua.lua_state(), nullptr, 0, 0);

    // Clean up
    lua["_callback_ctx"] = sol::nil;

    json response;
    response["output"] = m_output_buffer;

    if (result.valid()) {
        sol::object val = result;
        response["success"] = true;
        response["result"] = sol_to_json(val);
    } else {
        sol::error err = result;
        response["success"] = false;
        response["error"] = err.what();
    }

    return response;
}

// ── reload_script — execute a file in the existing state ───────────

json LuaEngine::reload_script(const std::string& filepath) {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    if (!m_initialized || !m_state) {
        return json{{"success", false}, {"error", "Lua engine not initialized"}};
    }

    std::ifstream in(filepath, std::ios::binary);
    if (!in) {
        return json{{"success", false}, {"error", "File not found: " + filepath}};
    }

    std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    in.close();

    m_output_buffer.clear();
    auto& lua = m_state->lua;

    lua_sethook(lua.lua_state(), instruction_count_hook, LUA_MASKCOUNT, LUA_INSTRUCTION_LIMIT);
    auto result = lua.safe_script(content, sol::script_pass_on_error);
    lua_sethook(lua.lua_state(), nullptr, 0, 0);

    json response;
    response["output"] = m_output_buffer;
    response["file"] = filepath;

    if (result.valid()) {
        sol::object val = result;
        response["success"] = true;
        response["result"] = sol_to_json(val);
    } else {
        sol::error err = result;
        response["success"] = false;
        response["error"] = err.what();
    }

    return response;
}

// ── get_globals — inspect top-level Lua global variables ───────────

json LuaEngine::get_globals() {
    std::lock_guard<std::recursive_mutex> lock(m_mutex);

    if (!m_initialized || !m_state) {
        return json{{"error", "Lua engine not initialized"}};
    }

    auto& lua = m_state->lua;

    // Internal tables to skip
    static const std::unordered_set<std::string> skip = {
        "_G", "_VERSION", "_mcp_frame_callbacks", "_mcp_timers", "_mcp_coroutines",
        "_mcp_scripts_dir", "_mcp_load_module", "_callback_ctx",
        "arg", "utf8", "bit32", "coroutine", "debug", "math", "os",
        "package", "string", "table", "collectgarbage", "require",
        "assert", "error", "getmetatable", "setmetatable", "ipairs", "pairs",
        "next", "pcall", "xpcall", "rawequal", "rawget", "rawlen", "rawset",
        "select", "tonumber", "tostring", "type", "warn",
        "load", "print" // our override
    };

    json globals = json::object();

    sol::table global_table = lua.globals();
    global_table.for_each([&](const sol::object& key, const sol::object& val) {
        if (key.get_type() != sol::type::string) return;
        std::string name = key.as<std::string>();

        if (skip.count(name)) return;
        if (name.size() > 0 && name[0] == '_') return; // skip internal _prefixed

        json entry;
        entry["type"] = sol::type_name(lua.lua_state(), val.get_type());

        switch (val.get_type()) {
            case sol::type::boolean:
                entry["value"] = val.as<bool>();
                break;
            case sol::type::number:
                entry["value"] = val.as<double>();
                break;
            case sol::type::string:
                entry["value"] = val.as<std::string>();
                break;
            case sol::type::table: {
                sol::table t = val.as<sol::table>();
                int count = 0;
                t.for_each([&](const sol::object&, const sol::object&) { count++; });
                entry["entries"] = count;
                break;
            }
            case sol::type::userdata:
            case sol::type::lightuserdata: {
                void* ptr = val.as<void*>();
                entry["value"] = JsonHelpers::address_to_string(ptr);
                break;
            }
            case sol::type::function:
                entry["value"] = "<function>";
                break;
            default:
                entry["value"] = "<" + std::string(sol::type_name(lua.lua_state(), val.get_type())) + ">";
                break;
        }

        globals[name] = entry;
    });

    return json{{"globals", globals}, {"count", globals.size()}};
}
