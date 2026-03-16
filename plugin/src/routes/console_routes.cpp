#include "console_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>
#include <algorithm>
#include <cmath>
#include <unordered_map>

using json = nlohmann::json;

namespace ConsoleRoutes {

static std::string compute_category(const std::string& name) {
    if (name.empty()) return "Other";

    size_t dot = name.find('.');
    size_t under = name.find('_');

    size_t sep = dot;
    if (under != std::string::npos && (sep == std::string::npos || under < sep))
        sep = under;
    if (sep == std::string::npos) return "Other";

    std::string prefix = name.substr(0, sep);
    std::string lp = prefix;
    std::transform(lp.begin(), lp.end(), lp.begin(), ::tolower);

    static const std::unordered_map<std::string, std::string> map = {
        {"r", "Rendering"}, {"ai", "AI"}, {"fx", "Effects"}, {"audio", "Audio"},
        {"net", "Networking"}, {"ui", "UI"}, {"vr", "VR"}, {"t", "Texture"},
        {"sg", "Scalability"}, {"p", "Physics"}, {"stat", "Statistics"},
        {"show", "Debug"}, {"log", "Logging"}, {"foliage", "Foliage"},
        {"landscape", "Landscape"}, {"streaming", "Streaming"}, {"shadow", "Shadows"},
        {"light", "Lighting"}, {"post", "PostProcess"}, {"bloom", "PostProcess"},
        {"dof", "PostProcess"}, {"motion", "PostProcess"}, {"screen", "PostProcess"},
        {"temporal", "PostProcess"}, {"console", "Console"}, {"game", "Game"},
        {"world", "World"}, {"player", "Player"}, {"camera", "Camera"},
        {"input", "Input"}, {"hmd", "VR"}, {"oculus", "VR"}, {"steamvr", "VR"},
        {"openvr", "VR"}, {"openxr", "VR"}, {"material", "Materials"},
        {"particle", "Particles"}, {"lod", "LOD"}
    };

    auto it = map.find(lp);
    if (it != map.end()) return it->second;

    if (!prefix.empty() && prefix[0] >= 'a' && prefix[0] <= 'z')
        prefix[0] -= 32;
    return prefix;
}

void register_routes(httplib::Server& server) {
    // GET /api/console/cvars — List console variables with values and categories
    server.Get("/api/console/cvars", [](const httplib::Request& req, httplib::Response& res) {
        auto query = req.get_param_value("query");
        int limit = 5000;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }
        bool include_values = !req.has_param("values") || req.get_param_value("values") != "false";

        auto result = GameThreadQueue::get().submit_and_wait([query, limit, include_values]() -> json {
            // Note: 15s timeout below (CVar enumeration can be slow with thousands of entries)
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            auto console = api->get_console_manager();
            if (!console) return json{{"error", "Console manager not available"}};

            auto& objects = console->get_console_objects();
            json vars = json::array();
            int count = 0;

            for (const auto& elem : objects) {
                if (count >= limit) break;
                if (!elem.key || !elem.value) continue;

                std::wstring name(elem.key);
                auto name_utf8 = JsonHelpers::wide_to_utf8(name);

                // Filter by query if provided
                if (!query.empty()) {
                    std::string lower_name = name_utf8;
                    std::string lower_query = query;
                    std::transform(lower_name.begin(), lower_name.end(), lower_name.begin(), ::tolower);
                    std::transform(lower_query.begin(), lower_query.end(), lower_query.begin(), ::tolower);
                    if (lower_name.find(lower_query) == std::string::npos) continue;
                }

                json entry;
                entry["name"] = name_utf8;
                entry["category"] = compute_category(name_utf8);

                // Match UEVR's dump_commands() pattern exactly:
                // Check AsCommand() first — only call GetFloat() on actual variables.
                // locate_vtable_indices() only works on IConsoleVariable vtables,
                // calling GetInt/GetFloat on commands gives garbage.
                bool is_command = false;
                try {
                    is_command = (elem.value->as_command() != nullptr);
                } catch (...) {
                    continue;
                }

                entry["isCommand"] = is_command;

                if (!is_command && include_values) {
                    try {
                        auto variable = static_cast<uevr::API::IConsoleVariable*>(elem.value);
                        float fv = variable->get_float();
                        int iv = variable->get_int();
                        entry["float"] = fv;
                        entry["int"] = iv;
                        // Determine display value
                        if (static_cast<float>(iv) == fv) {
                            entry["value"] = iv;
                        } else {
                            entry["value"] = fv;
                        }
                    } catch (...) {
                        entry["value"] = 0;
                    }
                } else if (is_command) {
                    // Skip commands when fetching values
                    if (include_values) continue;
                }

                vars.push_back(entry);
                count++;
            }

            return json{{"variables", vars}, {"count", vars.size()}};
        }, 15000);

        if (result.contains("error")) {
            res.status = 500;
        }
        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/console/cvar — Read CVar value
    server.Get("/api/console/cvar", [](const httplib::Request& req, httplib::Response& res) {
        auto name = req.get_param_value("name");
        if (name.empty()) {
            res.status = 400;
            res.set_content(json{{"error", "Missing 'name' parameter"}}.dump(), "application/json");
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([name]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            auto console = api->get_console_manager();
            if (!console) return json{{"error", "Console manager not available"}};

            auto wname = JsonHelpers::utf8_to_wide(name);
            auto cvar = console->find_variable(wname.c_str());
            if (!cvar) return json{{"error", "CVar not found: " + name}};

            json result;
            result["name"] = name;
            result["int"] = cvar->get_int();
            result["float"] = cvar->get_float();
            return result;
        });

        if (result.contains("error")) {
            res.status = result["error"].get<std::string>().find("not found") != std::string::npos ? 404 : 500;
        }
        res.set_content(result.dump(2), "application/json");
    });

    // POST /api/console/cvar — Set CVar value
    server.Post("/api/console/cvar", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            res.status = 400;
            res.set_content(json{{"error", "Invalid JSON"}}.dump(), "application/json");
            return;
        }

        auto name = body.value("name", "");
        if (name.empty() || !body.contains("value")) {
            res.status = 400;
            res.set_content(json{{"error", "Missing 'name' or 'value'"}}.dump(), "application/json");
            return;
        }

        auto value_json = body["value"];
        PipeServer::get().log("Console: set " + name + " = " + value_json.dump());

        auto result = GameThreadQueue::get().submit_and_wait([name, value_json]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            auto console = api->get_console_manager();
            if (!console) return json{{"error", "Console manager not available"}};

            auto wname = JsonHelpers::utf8_to_wide(name);
            auto cvar = console->find_variable(wname.c_str());
            if (!cvar) return json{{"error", "CVar not found: " + name}};

            if (value_json.is_number_integer()) {
                cvar->set(value_json.get<int>());
            } else if (value_json.is_number_float()) {
                cvar->set(value_json.get<float>());
            } else if (value_json.is_string()) {
                auto wval = JsonHelpers::utf8_to_wide(value_json.get<std::string>());
                cvar->set(wval.c_str());
            } else {
                auto str = value_json.dump();
                auto wval = JsonHelpers::utf8_to_wide(str);
                cvar->set(wval.c_str());
            }

            json result;
            result["success"] = true;
            result["name"] = name;
            result["newInt"] = cvar->get_int();
            result["newFloat"] = cvar->get_float();
            return result;
        });

        if (result.contains("error")) res.status = 500;
        res.set_content(result.dump(2), "application/json");
    });

    // POST /api/console/command — Execute console command
    server.Post("/api/console/command", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            res.status = 400;
            res.set_content(json{{"error", "Invalid JSON"}}.dump(), "application/json");
            return;
        }

        auto command = body.value("command", "");
        if (command.empty()) {
            res.status = 400;
            res.set_content(json{{"error", "Missing 'command'"}}.dump(), "application/json");
            return;
        }

        PipeServer::get().log("Console: exec '" + command + "'");

        auto result = GameThreadQueue::get().submit_and_wait([command]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            auto wcmd = JsonHelpers::utf8_to_wide(command);
            api->execute_command(wcmd.c_str());

            return json{{"success", true}, {"command", command}};
        });

        if (result.contains("error")) res.status = 500;
        res.set_content(result.dump(2), "application/json");
    });
}

} // namespace ConsoleRoutes
