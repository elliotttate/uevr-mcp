#include "lua_routes.h"
#include "../lua/lua_engine.h"
#include "../game_thread_queue.h"
#include "../pipe_server.h"
#include "../event_bus.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>
#include <filesystem>

using json = nlohmann::json;

namespace LuaRoutes {

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
    // POST /api/lua/exec — Execute Lua code
    server.Post("/api/lua/exec", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto code = body.value("code", "");
        if (code.empty()) {
            send_json(res, json{{"error", "Missing 'code' parameter"}}, 400);
            return;
        }

        PipeServer::get().log("Lua: exec (" + std::to_string(code.size()) + " chars)");

        // Execute on game thread for safe UEVR API access
        auto result = GameThreadQueue::get().submit_and_wait([code]() {
            return LuaEngine::get().execute(code);
        }, body.value("timeout", 10000));

        // Lua execution errors (syntax errors, runtime errors) are application-level —
        // always return 200 unless GameThreadQueue itself timed out.
        if (result.contains("error") && !result.contains("success")) {
            // This is a GameThreadQueue timeout/error, not a Lua error
            send_json(res, result);
        } else {
            res.status = 200;
            res.set_content(result.dump(2), "application/json");
        }
    });

    // POST /api/lua/reset — Reset Lua state
    server.Post("/api/lua/reset", [](const httplib::Request&, httplib::Response& res) {
        PipeServer::get().log("Lua: reset state");
        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return LuaEngine::get().reset();
        });
        send_json(res, result);
    });

    // GET /api/lua/state — State info
    server.Get("/api/lua/state", [](const httplib::Request&, httplib::Response& res) {
        auto result = LuaEngine::get().get_state_info();
        send_json(res, result);
    });

    // POST /api/lua/scripts/write — Write script file
    server.Post("/api/lua/scripts/write", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto filename = body.value("filename", "");
        auto content = body.value("content", "");
        bool autorun = body.value("autorun", false);

        if (filename.empty()) {
            send_json(res, json{{"error", "Missing 'filename'"}}, 400);
            return;
        }

        // Sanitize filename — no path traversal
        if (filename.find("..") != std::string::npos || filename.find('/') != std::string::npos || filename.find('\\') != std::string::npos) {
            send_json(res, json{{"error", "Invalid filename — no path separators allowed"}}, 400);
            return;
        }

        // Get UEVR persistent dir for script storage
        auto& api = uevr::API::get();
        if (!api) {
            send_json(res, json{{"error", "API not available"}}, 500);
            return;
        }

        auto base_dir = api->get_persistent_dir();
        auto scripts_dir = autorun ? (base_dir / "scripts" / "autorun") : (base_dir / "scripts");

        std::error_code ec;
        std::filesystem::create_directories(scripts_dir, ec);

        auto filepath = scripts_dir / filename;
        std::ofstream out(filepath, std::ios::binary);
        if (!out) {
            send_json(res, json{{"error", "Failed to open file for writing"}}, 500);
            return;
        }
        out.write(content.data(), content.size());
        out.close();

        PipeServer::get().log("Lua: wrote script " + filepath.string());
        send_json(res, json{{"success", true}, {"path", filepath.string()}});
    });

    // GET /api/lua/scripts/list — List script files
    server.Get("/api/lua/scripts/list", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            send_json(res, json{{"error", "API not available"}}, 500);
            return;
        }

        auto base_dir = api->get_persistent_dir();
        json scripts = json::array();

        auto scan_dir = [&](const std::filesystem::path& dir, bool is_autorun) {
            std::error_code ec;
            if (!std::filesystem::exists(dir, ec)) return;
            for (const auto& entry : std::filesystem::directory_iterator(dir, ec)) {
                if (!entry.is_regular_file()) continue;
                auto ext = entry.path().extension().string();
                if (ext != ".lua" && ext != ".txt") continue;
                scripts.push_back(json{
                    {"filename", entry.path().filename().string()},
                    {"autorun", is_autorun},
                    {"size", entry.file_size()}
                });
            }
        };

        scan_dir(base_dir / "scripts", false);
        scan_dir(base_dir / "scripts" / "autorun", true);

        send_json(res, json{{"scripts", scripts}});
    });

    // GET /api/lua/scripts/read — Read a script file
    server.Get("/api/lua/scripts/read", [](const httplib::Request& req, httplib::Response& res) {
        auto filename = req.get_param_value("filename");
        bool autorun = req.get_param_value("autorun") == "true";

        if (filename.empty()) {
            send_json(res, json{{"error", "Missing 'filename' parameter"}}, 400);
            return;
        }

        if (filename.find("..") != std::string::npos || filename.find('/') != std::string::npos || filename.find('\\') != std::string::npos) {
            send_json(res, json{{"error", "Invalid filename"}}, 400);
            return;
        }

        auto& api = uevr::API::get();
        if (!api) {
            send_json(res, json{{"error", "API not available"}}, 500);
            return;
        }

        auto base_dir = api->get_persistent_dir();
        auto scripts_dir = autorun ? (base_dir / "scripts" / "autorun") : (base_dir / "scripts");
        auto filepath = scripts_dir / filename;

        std::ifstream in(filepath, std::ios::binary);
        if (!in) {
            send_json(res, json{{"error", "File not found: " + filename}}, 404);
            return;
        }

        std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        send_json(res, json{{"filename", filename}, {"autorun", autorun}, {"content", content}});
    });

    // POST /api/lua/reload — Hot-reload a script file (preserves existing state)
    server.Post("/api/lua/reload", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto filename = body.value("filename", "");
        bool autorun = body.value("autorun", false);

        if (filename.empty()) {
            send_json(res, json{{"error", "Missing 'filename'"}}, 400);
            return;
        }

        if (filename.find("..") != std::string::npos || filename.find('/') != std::string::npos || filename.find('\\') != std::string::npos) {
            send_json(res, json{{"error", "Invalid filename"}}, 400);
            return;
        }

        auto& api = uevr::API::get();
        if (!api) {
            send_json(res, json{{"error", "API not available"}}, 500);
            return;
        }

        auto base_dir = api->get_persistent_dir();
        auto scripts_dir = autorun ? (base_dir / "scripts" / "autorun") : (base_dir / "scripts");
        auto filepath = scripts_dir / filename;

        PipeServer::get().log("Lua: reload script " + filepath.string());

        auto result = GameThreadQueue::get().submit_and_wait([fp = filepath.string()]() {
            return LuaEngine::get().reload_script(fp);
        });

        if (result.contains("error") && !result.contains("success")) {
            send_json(res, result);
        } else {
            res.status = 200;
            res.set_content(result.dump(2), "application/json");
        }
    });

    // GET /api/lua/globals — Inspect top-level Lua globals
    server.Get("/api/lua/globals", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return LuaEngine::get().get_globals();
        });
        send_json(res, result);
    });

    // GET /api/events — Server-Sent Events stream for real-time events
    server.Get("/api/events", [](const httplib::Request& req, httplib::Response& res) {
        // Parse optional since_seq parameter
        uint64_t since_seq = 0;
        if (req.has_param("since")) {
            try { since_seq = std::stoull(req.get_param_value("since")); } catch (...) {}
        }
        int timeout_ms = 30000;
        if (req.has_param("timeout")) {
            try { timeout_ms = std::stoi(req.get_param_value("timeout")); } catch (...) {}
        }
        if (timeout_ms > 60000) timeout_ms = 60000;
        if (timeout_ms < 100) timeout_ms = 100;

        // Long-poll: wait for events then return them as JSON
        // (SSE requires chunked streaming which httplib supports but is complex;
        //  long-polling is simpler and works well with MCP's request/response model)
        auto& bus = EventBus::get();

        // First check if there are already events available
        auto [events, new_seq] = bus.poll(since_seq, 100);
        if (!events.empty()) {
            json result;
            result["events"] = json::array();
            for (const auto& evt : events) {
                result["events"].push_back(json{
                    {"seq", evt.seq},
                    {"type", evt.type},
                    {"data", evt.data}
                });
            }
            result["seq"] = new_seq;
            result["count"] = events.size();
            res.status = 200;
            res.set_content(result.dump(2), "application/json");
            return;
        }

        // Wait for new events
        bus.wait_for_events(since_seq, timeout_ms);

        // Poll again after waiting
        auto [events2, new_seq2] = bus.poll(since_seq, 100);
        json result;
        result["events"] = json::array();
        for (const auto& evt : events2) {
            result["events"].push_back(json{
                {"seq", evt.seq},
                {"type", evt.type},
                {"data", evt.data}
            });
        }
        result["seq"] = new_seq2;
        result["count"] = events2.size();
        res.status = 200;
        res.set_content(result.dump(2), "application/json");
    });

    // DELETE /api/lua/scripts/delete — Delete a script file
    server.Delete("/api/lua/scripts/delete", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto filename = body.value("filename", "");
        bool autorun = body.value("autorun", false);

        if (filename.empty()) {
            send_json(res, json{{"error", "Missing 'filename'"}}, 400);
            return;
        }

        if (filename.find("..") != std::string::npos || filename.find('/') != std::string::npos || filename.find('\\') != std::string::npos) {
            send_json(res, json{{"error", "Invalid filename"}}, 400);
            return;
        }

        auto& api = uevr::API::get();
        if (!api) {
            send_json(res, json{{"error", "API not available"}}, 500);
            return;
        }

        auto base_dir = api->get_persistent_dir();
        auto scripts_dir = autorun ? (base_dir / "scripts" / "autorun") : (base_dir / "scripts");
        auto filepath = scripts_dir / filename;

        std::error_code ec;
        if (std::filesystem::remove(filepath, ec)) {
            send_json(res, json{{"success", true}, {"deleted", filename}});
        } else {
            send_json(res, json{{"error", "File not found or could not be deleted"}}, 404);
        }
    });
}

} // namespace LuaRoutes
