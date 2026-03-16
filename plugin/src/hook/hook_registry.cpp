#include "hook_registry.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../routes/status_routes.h"
#include "../lua/lua_engine.h"
#include "../event_bus.h"

using json = nlohmann::json;

// These MUST be static free functions (not lambdas/std::function) for UFunction::hook_ptr.
// The first parameter is the UFunction handle, which we use to dispatch to the right HookEntry.
static bool global_pre_hook(uevr::API::UFunction* fn, uevr::API::UObject* obj, void* frame, void* result) {
    return HookRegistry::get().on_pre_hook(fn, obj, frame, result);
}

static void global_post_hook(uevr::API::UFunction* fn, uevr::API::UObject* obj, void* frame, void* result) {
    HookRegistry::get().on_post_hook(fn, obj, frame, result);
}

HookRegistry& HookRegistry::get() {
    static HookRegistry instance;
    return instance;
}

static std::string action_to_string(HookAction action) {
    switch (action) {
        case HookAction::Log:        return "log";
        case HookAction::Block:      return "block";
        case HookAction::LogAndBlock: return "log_and_block";
        case HookAction::Lua:        return "lua";
        case HookAction::LuaBlock:   return "lua_block";
    }
    return "unknown";
}

static HookAction string_to_action(const std::string& s) {
    if (s == "block")         return HookAction::Block;
    if (s == "log_and_block") return HookAction::LogAndBlock;
    if (s == "lua")           return HookAction::Lua;
    if (s == "lua_block")     return HookAction::LuaBlock;
    return HookAction::Log; // default
}

json HookRegistry::add_hook(const std::string& class_name, const std::string& function_name, HookAction action, const std::string& lua_script) {
    auto& api = uevr::API::get();
    if (!api) {
        return json{{"error", "API not available"}};
    }

    // Find the UClass
    auto wide_class = JsonHelpers::utf8_to_wide(class_name);
    auto cls = api->find_uobject<uevr::API::UClass>(wide_class);
    if (!cls) {
        return json{{"error", "Class not found: " + class_name}};
    }

    // Find the UFunction on that class
    auto wide_func = JsonHelpers::utf8_to_wide(function_name);
    auto func = cls->find_function(wide_func);
    if (!func) {
        return json{{"error", "Function not found: " + function_name + " on " + class_name}};
    }

    // Check if we already have a hook for this function
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        auto it = m_func_to_hook.find(func);
        if (it != m_func_to_hook.end()) {
            auto& existing = m_hooks[it->second];
            if (existing.active) {
                return json{
                    {"error", "Function already hooked"},
                    {"existingHookId", existing.id}
                };
            }
            // Reactivate the existing hook with the new action
            existing.active = true;
            existing.action = action;
            existing.call_count = 0;
            existing.call_log.clear();

            PipeServer::get().log("Hook: reactivated hook #" + std::to_string(existing.id) +
                                  " on " + function_name);

            return json{
                {"hookId", existing.id},
                {"className", class_name},
                {"functionName", function_name},
                {"action", action_to_string(action)},
                {"reactivated", true}
            };
        }
    }

    // Validate Lua actions have a script
    if ((action == HookAction::Lua || action == HookAction::LuaBlock) && lua_script.empty()) {
        return json{{"error", "Lua hook actions require a 'script' parameter"}};
    }

    // Install the hook via UEVR's hook_ptr
    // UEVR supports multiple hooks on the same function, so this is safe even if
    // something else has already hooked it.
    bool ok = func->hook_ptr(global_pre_hook, global_post_hook);
    if (!ok) {
        return json{{"error", "hook_ptr failed for " + function_name}};
    }

    // Create the hook entry
    std::lock_guard<std::mutex> lock(m_mutex);
    int id = m_next_id++;

    HookEntry entry;
    entry.id = id;
    entry.class_name = class_name;
    entry.function_name = function_name;
    entry.action = action;
    entry.lua_script = lua_script;
    entry.function = func;
    entry.call_count = 0;
    entry.active = true;

    m_hooks[id] = std::move(entry);
    m_func_to_hook[func] = id;

    PipeServer::get().log("Hook: added hook #" + std::to_string(id) +
                          " on " + class_name + "::" + function_name +
                          " action=" + action_to_string(action));

    return json{
        {"hookId", id},
        {"className", class_name},
        {"functionName", function_name},
        {"action", action_to_string(action)}
    };
}

json HookRegistry::remove_hook(int hook_id) {
    std::lock_guard<std::mutex> lock(m_mutex);

    auto it = m_hooks.find(hook_id);
    if (it == m_hooks.end()) {
        return json{{"error", "Hook not found: " + std::to_string(hook_id)}};
    }

    auto& entry = it->second;
    if (!entry.active) {
        return json{{"error", "Hook already inactive: " + std::to_string(hook_id)}};
    }

    // UEVR doesn't provide an unhook_ptr, so we mark as inactive and remove from dispatch map.
    // The underlying hook callback will still fire but on_pre_hook will quickly skip it.
    entry.active = false;
    m_func_to_hook.erase(entry.function);

    PipeServer::get().log("Hook: removed hook #" + std::to_string(hook_id) +
                          " on " + entry.class_name + "::" + entry.function_name);

    return json{
        {"success", true},
        {"hookId", hook_id},
        {"className", entry.class_name},
        {"functionName", entry.function_name},
        {"totalCalls", entry.call_count}
    };
}

json HookRegistry::list_hooks() {
    std::lock_guard<std::mutex> lock(m_mutex);

    json hooks_arr = json::array();
    for (const auto& [id, entry] : m_hooks) {
        json h = {
            {"id", entry.id},
            {"className", entry.class_name},
            {"functionName", entry.function_name},
            {"action", action_to_string(entry.action)},
            {"callCount", entry.call_count},
            {"active", entry.active},
            {"logSize", (int)entry.call_log.size()}
        };
        if (!entry.lua_script.empty()) {
            h["hasScript"] = true;
        }
        hooks_arr.push_back(std::move(h));
    }

    return json{
        {"hooks", hooks_arr},
        {"count", (int)m_hooks.size()}
    };
}

json HookRegistry::get_hook_log(int hook_id, int max_entries) {
    std::lock_guard<std::mutex> lock(m_mutex);

    auto it = m_hooks.find(hook_id);
    if (it == m_hooks.end()) {
        return json{{"error", "Hook not found: " + std::to_string(hook_id)}};
    }

    const auto& entry = it->second;
    json entries = json::array();

    // Return the most recent entries (the deque is ordered oldest-first)
    int start = 0;
    if ((int)entry.call_log.size() > max_entries) {
        start = (int)entry.call_log.size() - max_entries;
    }

    for (int i = start; i < (int)entry.call_log.size(); i++) {
        const auto& log = entry.call_log[i];
        entries.push_back(json{
            {"tick", log.tick},
            {"objectAddress", JsonHelpers::address_to_string(log.object_address)},
            {"objectName", log.object_name},
            {"functionName", log.function_name}
        });
    }

    return json{
        {"hookId", hook_id},
        {"className", entry.class_name},
        {"functionName", entry.function_name},
        {"callCount", entry.call_count},
        {"active", entry.active},
        {"entries", entries},
        {"count", (int)entries.size()}
    };
}

json HookRegistry::clear_hooks() {
    std::lock_guard<std::mutex> lock(m_mutex);

    int removed = 0;
    for (auto& [id, entry] : m_hooks) {
        if (entry.active) {
            entry.active = false;
            removed++;
        }
    }
    m_func_to_hook.clear();

    PipeServer::get().log("Hook: cleared all hooks (" + std::to_string(removed) + " deactivated)");

    return json{
        {"success", true},
        {"removed", removed}
    };
}

bool HookRegistry::on_pre_hook(uevr::API::UFunction* fn, uevr::API::UObject* obj, void* frame, void* result) {
    // Use try_lock to avoid blocking the game thread if something else holds the mutex
    // (e.g., an HTTP request is modifying hooks). If we can't acquire, just let the
    // original function execute.
    std::unique_lock<std::mutex> lock(m_mutex, std::try_to_lock);
    if (!lock.owns_lock()) {
        return true; // Don't block
    }

    auto it = m_func_to_hook.find(fn);
    if (it == m_func_to_hook.end()) {
        return true; // No active hook for this function
    }

    auto hook_it = m_hooks.find(it->second);
    if (hook_it == m_hooks.end() || !hook_it->second.active) {
        return true;
    }

    auto& entry = hook_it->second;
    entry.call_count++;

    // Get object info (shared by all action types)
    std::string obj_name = "<null>";
    uintptr_t obj_addr = 0;
    if (obj) {
        obj_addr = reinterpret_cast<uintptr_t>(obj);
        auto fname = obj->get_fname();
        if (fname) {
            obj_name = JsonHelpers::wide_to_utf8(fname->to_string());
        } else {
            obj_name = "<unnamed>";
        }
    }

    // Log the call if action includes logging
    if (entry.action == HookAction::Log || entry.action == HookAction::LogAndBlock) {
        HookCallLog log_entry;
        log_entry.tick = StatusRoutes::get_tick_count();
        log_entry.object_address = obj_addr;
        log_entry.function_name = entry.function_name;
        log_entry.object_name = obj_name;

        entry.call_log.push_back(std::move(log_entry));
        while ((int)entry.call_log.size() > HookEntry::MAX_LOG) {
            entry.call_log.pop_front();
        }
    }

    // Execute Lua callback if action is Lua/LuaBlock
    if (entry.action == HookAction::Lua || entry.action == HookAction::LuaBlock) {
        // Log the call too
        HookCallLog log_entry;
        log_entry.tick = StatusRoutes::get_tick_count();
        log_entry.object_address = obj_addr;
        log_entry.function_name = entry.function_name;
        log_entry.object_name = obj_name;
        entry.call_log.push_back(std::move(log_entry));
        while ((int)entry.call_log.size() > HookEntry::MAX_LOG) {
            entry.call_log.pop_front();
        }

        // Publish event
        EventBus::get().publish("hook_fire", json{
            {"hookId", entry.id},
            {"className", entry.class_name},
            {"functionName", entry.function_name},
            {"objectAddress", JsonHelpers::address_to_string(obj_addr)},
            {"objectName", obj_name}
        });

        // Execute the Lua script with context
        // Must release our lock first to avoid deadlock with LuaEngine
        std::string script = entry.lua_script;
        bool is_lua_block = (entry.action == HookAction::LuaBlock);
        lock.unlock();

        json context = {
            {"object_address", JsonHelpers::address_to_string(obj_addr)},
            {"object_name", obj_name},
            {"function_name", entry.function_name},
            {"class_name", entry.class_name},
            {"hook_id", entry.id},
            {"call_count", entry.call_count}
        };

        auto lua_result = LuaEngine::get().execute_callback(script, context);

        if (is_lua_block) {
            return false; // Always block
        }

        // For "lua" action, script return value controls blocking
        // Return false (block) if script returns false; otherwise allow
        if (lua_result.contains("result") && lua_result["result"].is_boolean()) {
            return lua_result["result"].get<bool>();
        }
        return true; // Default: allow execution
    }

    // Block the original function if action includes blocking
    if (entry.action == HookAction::Block || entry.action == HookAction::LogAndBlock) {
        return false; // Skip original
    }

    return true; // Execute original
}

void HookRegistry::on_post_hook(uevr::API::UFunction* fn, uevr::API::UObject* obj, void* frame, void* result) {
    // Currently a no-op. Could be extended in the future for:
    // - Return value modification
    // - Post-call logging with return values
    // - Chaining post-hook callbacks
    (void)fn;
    (void)obj;
    (void)frame;
    (void)result;
}
