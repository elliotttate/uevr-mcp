#include "lua_routes.h"
#include "../lua/lua_engine.h"
#include "../game_thread_queue.h"
#include "../pipe_server.h"

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
