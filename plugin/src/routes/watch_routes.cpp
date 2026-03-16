#include "watch_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../watch/property_watch.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;

namespace WatchRoutes {

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (data.contains("error") && status == 200) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

void register_routes(httplib::Server& server) {
    // POST /api/watch/add — Add a property watch
    server.Post("/api/watch/add", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        auto field_name = body.value("fieldName", "");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0 || field_name.empty()) {
            send_json(res, json{{"error", "Missing 'address' or 'fieldName'"}}, 400);
            return;
        }

        int interval = body.value("interval", 1);

        PipeServer::get().log("Watch: add watch on " + addr_str + "." + field_name);

        auto result = GameThreadQueue::get().submit_and_wait([address, field_name, interval]() {
            return PropertyWatch::get().add_watch(address, field_name, interval);
        });
        send_json(res, result);
    });

    // DELETE /api/watch/remove — Remove a property watch
    server.Delete("/api/watch/remove", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        int watch_id = body.value("watchId", 0);
        if (watch_id <= 0) {
            send_json(res, json{{"error", "Missing or invalid 'watchId'"}}, 400);
            return;
        }

        PipeServer::get().log("Watch: remove watch #" + std::to_string(watch_id));

        auto result = GameThreadQueue::get().submit_and_wait([watch_id]() {
            return PropertyWatch::get().remove_watch(watch_id);
        });
        send_json(res, result);
    });

    // GET /api/watch/list — List all active watches
    server.Get("/api/watch/list", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return PropertyWatch::get().list_watches();
        });
        send_json(res, result);
    });

    // GET /api/watch/changes — Get recent change events
    server.Get("/api/watch/changes", [](const httplib::Request& req, httplib::Response& res) {
        int max_count = 100;
        if (req.has_param("max")) {
            try { max_count = std::stoi(req.get_param_value("max")); } catch (...) {}
        }

        auto result = GameThreadQueue::get().submit_and_wait([max_count]() {
            return PropertyWatch::get().get_changes(max_count);
        });
        send_json(res, result);
    });

    // POST /api/watch/clear — Clear all watches and change history
    server.Post("/api/watch/clear", [](const httplib::Request&, httplib::Response& res) {
        PipeServer::get().log("Watch: clearing all watches");

        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return PropertyWatch::get().clear_watches();
        });
        send_json(res, result);
    });

    // POST /api/watch/snapshot — Take a snapshot of all object properties
    server.Post("/api/watch/snapshot", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0) {
            send_json(res, json{{"error", "Missing or invalid 'address'"}}, 400);
            return;
        }

        PipeServer::get().log("Watch: take snapshot of " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address]() {
            return PropertyWatch::get().take_snapshot(address);
        });
        send_json(res, result);
    });

    // GET /api/watch/snapshots — List all snapshots (metadata only)
    server.Get("/api/watch/snapshots", [](const httplib::Request&, httplib::Response& res) {
        auto result = PropertyWatch::get().list_snapshots();
        send_json(res, result);
    });

    // POST /api/watch/diff — Diff a snapshot against current object state
    server.Post("/api/watch/diff", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        int snapshot_id = body.value("snapshotId", 0);
        if (snapshot_id <= 0) {
            send_json(res, json{{"error", "Missing or invalid 'snapshotId'"}}, 400);
            return;
        }

        uintptr_t address = 0;
        if (body.contains("address")) {
            address = JsonHelpers::string_to_address(body.value("address", ""));
        }

        PipeServer::get().log("Watch: diff snapshot #" + std::to_string(snapshot_id));

        auto result = GameThreadQueue::get().submit_and_wait([snapshot_id, address]() {
            return PropertyWatch::get().diff_snapshot(snapshot_id, address);
        });
        send_json(res, result);
    });

    // DELETE /api/watch/snapshot — Delete a snapshot
    server.Delete("/api/watch/snapshot", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        int snapshot_id = body.value("snapshotId", 0);
        if (snapshot_id <= 0) {
            send_json(res, json{{"error", "Missing or invalid 'snapshotId'"}}, 400);
            return;
        }

        PipeServer::get().log("Watch: delete snapshot #" + std::to_string(snapshot_id));

        auto result = GameThreadQueue::get().submit_and_wait([snapshot_id]() {
            return PropertyWatch::get().delete_snapshot(snapshot_id);
        });
        send_json(res, result);
    });
}

} // namespace WatchRoutes
