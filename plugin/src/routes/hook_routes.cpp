#include "hook_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../hook/hook_registry.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;

namespace HookRoutes {

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (status == 200 && data.contains("error")) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

static HookAction parse_action(const std::string& s) {
    if (s == "block")         return HookAction::Block;
    if (s == "log_and_block") return HookAction::LogAndBlock;
    return HookAction::Log;
}

void register_routes(httplib::Server& server) {

    // POST /api/hook/add -- Add a function hook
    server.Post("/api/hook/add", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto class_name = body.value("className", "");
        auto function_name = body.value("functionName", "");
        auto action_str = body.value("action", "log");

        if (class_name.empty() || function_name.empty()) {
            send_json(res, json{{"error", "Missing 'className' or 'functionName'"}}, 400);
            return;
        }

        auto action = parse_action(action_str);

        PipeServer::get().log("Hook: adding hook on " + class_name + "::" + function_name +
                              " action=" + action_str);

        // add_hook accesses UE objects (find_uobject, find_function), so it must run on game thread
        auto result = GameThreadQueue::get().submit_and_wait([class_name, function_name, action]() {
            return HookRegistry::get().add_hook(class_name, function_name, action);
        });

        send_json(res, result);
    });

    // DELETE /api/hook/remove -- Remove a hook by ID
    server.Delete("/api/hook/remove", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        int hook_id = body.value("hookId", -1);
        if (hook_id < 0) {
            send_json(res, json{{"error", "Missing or invalid 'hookId'"}}, 400);
            return;
        }

        // remove_hook only touches our internal data structures (no UE calls needed),
        // but we route through game thread for consistency and to avoid races with on_pre_hook
        auto result = GameThreadQueue::get().submit_and_wait([hook_id]() {
            return HookRegistry::get().remove_hook(hook_id);
        });

        send_json(res, result);
    });

    // GET /api/hook/list -- List all hooks
    server.Get("/api/hook/list", [](const httplib::Request&, httplib::Response& res) {
        // list_hooks only reads internal state, safe without game thread but we keep
        // consistency by going through it
        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return HookRegistry::get().list_hooks();
        });

        send_json(res, result);
    });

    // GET /api/hook/log -- Get call log for a specific hook
    server.Get("/api/hook/log", [](const httplib::Request& req, httplib::Response& res) {
        int hook_id = -1;
        int max_entries = 50;

        if (req.has_param("hookId")) {
            try { hook_id = std::stoi(req.get_param_value("hookId")); } catch (...) {}
        }
        if (req.has_param("max")) {
            try { max_entries = std::stoi(req.get_param_value("max")); } catch (...) {}
        }

        if (hook_id < 0) {
            send_json(res, json{{"error", "Missing or invalid 'hookId' query parameter"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([hook_id, max_entries]() {
            return HookRegistry::get().get_hook_log(hook_id, max_entries);
        });

        send_json(res, result);
    });

    // POST /api/hook/clear -- Clear all hooks
    server.Post("/api/hook/clear", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return HookRegistry::get().clear_hooks();
        });

        send_json(res, result);
    });
}

} // namespace HookRoutes
