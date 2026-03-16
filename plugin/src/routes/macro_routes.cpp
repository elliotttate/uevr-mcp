#include "macro_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../reflection/object_explorer.h"
#include "../reflection/property_reader.h"
#include "../reflection/property_writer.h"
#include "../reflection/function_caller.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

#include <mutex>
#include <unordered_map>
#include <functional>

using json = nlohmann::json;

namespace MacroRoutes {

// ---- In-memory macro storage ----
struct MacroDefinition {
    std::string name;
    std::string description;
    json operations; // JSON array of operation objects
};

static std::mutex s_macro_mutex;
static std::unordered_map<std::string, MacroDefinition> s_macros;

// ---- Parameter substitution ----
// Walks the JSON tree and replaces any string value starting with "$" with the
// corresponding value from the params object. E.g. "$address" -> params["address"].
static json substitute_params(const json& operations, const json& params) {
    if (!params.is_object() || params.empty()) {
        return operations;
    }

    auto result = operations;

    std::function<void(json&)> walk = [&](json& node) {
        if (node.is_string()) {
            auto s = node.get<std::string>();
            if (s.size() > 1 && s[0] == '$') {
                auto key = s.substr(1);
                if (params.contains(key)) {
                    node = params[key];
                }
            }
        } else if (node.is_object()) {
            for (auto& [k, v] : node.items()) {
                walk(v);
            }
        } else if (node.is_array()) {
            for (auto& v : node) {
                walk(v);
            }
        }
    };

    walk(result);
    return result;
}

// ---- Single operation executor ----
// Mirrors the batch operation logic from explorer_routes.cpp.
// Runs on the game thread.
static json execute_operation(const json& op) {
    auto type = op.value("type", "");

    try {
        if (type == "read_field") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            auto field = op.value("fieldName", "");
            if (addr == 0 || field.empty()) {
                return json{{"error", "read_field: missing address or fieldName"}};
            }
            return ObjectExplorer::read_field(addr, field);

        } else if (type == "write_field") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            auto field = op.value("fieldName", "");
            if (addr == 0 || field.empty() || !op.contains("value")) {
                return json{{"error", "write_field: missing address, fieldName, or value"}};
            }

            auto obj = reinterpret_cast<uevr::API::UObject*>(addr);
            if (!uevr::API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            auto cls = obj->get_class();
            if (!cls) {
                return json{{"error", "Object has no class"}};
            }

            auto wname = JsonHelpers::utf8_to_wide(field);
            auto prop = cls->find_property(wname.c_str());
            if (!prop) {
                return json{{"error", "Field not found: " + field}};
            }

            std::string err;
            if (PropertyWriter::write_property(obj, prop, op["value"], err)) {
                auto new_val = PropertyReader::read_property(obj, prop, 2);
                return json{{"success", true}, {"field", field}, {"newValue", new_val}};
            }
            return json{{"error", err}};

        } else if (type == "call_method") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            auto method = op.value("methodName", "");
            if (addr == 0 || method.empty()) {
                return json{{"error", "call_method: missing address or methodName"}};
            }
            return FunctionCaller::invoke_function(addr, method, op.value("args", json::object()));

        } else if (type == "inspect") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            if (addr == 0) {
                return json{{"error", "inspect: missing or invalid address"}};
            }
            return ObjectExplorer::inspect_object(addr, op.value("depth", 1));

        } else if (type == "summary") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            if (addr == 0) {
                return json{{"error", "summary: missing or invalid address"}};
            }
            return ObjectExplorer::summarize_object(addr);

        } else if (type == "search") {
            auto query = op.value("query", "");
            if (query.empty()) {
                return json{{"error", "search: missing query"}};
            }
            return ObjectExplorer::search_objects(query, op.value("limit", 10));

        } else if (type == "get_type") {
            auto type_name = op.value("typeName", "");
            if (type_name.empty()) {
                return json{{"error", "get_type: missing typeName"}};
            }
            return ObjectExplorer::get_type_info(type_name);

        } else if (type == "singleton") {
            auto type_name = op.value("typeName", "");
            if (type_name.empty()) {
                return json{{"error", "singleton: missing typeName"}};
            }
            return ObjectExplorer::get_singleton(type_name);

        } else if (type == "read_array") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            if (addr == 0) {
                return json{{"error", "read_array: missing or invalid address"}};
            }
            return ObjectExplorer::read_array(
                addr,
                op.value("fieldName", ""),
                op.value("offset", 0),
                op.value("limit", 50)
            );

        } else if (type == "read_memory") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            if (addr == 0) {
                return json{{"error", "read_memory: missing or invalid address"}};
            }
            return ObjectExplorer::read_memory(addr, op.value("size", 256));

        } else if (type == "read_typed") {
            auto addr = JsonHelpers::string_to_address(op.value("address", ""));
            if (addr == 0) {
                return json{{"error", "read_typed: missing or invalid address"}};
            }
            return ObjectExplorer::read_typed(
                addr,
                op.value("type", "u8"),
                op.value("count", 1),
                op.value("stride", 0)
            );
        }

        return json{{"error", "Unknown operation type: " + type}};

    } catch (const std::exception& e) {
        return json{{"error", std::string("Exception in ") + type + ": " + e.what()}};
    }
}

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (status == 200 && data.contains("error")) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

void register_routes(httplib::Server& server) {

    // POST /api/macro/save -- Save a named macro (sequence of operations)
    server.Post("/api/macro/save", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto name = body.value("name", "");
        if (name.empty()) {
            send_json(res, json{{"error", "Missing 'name'"}}, 400);
            return;
        }

        if (!body.contains("operations") || !body["operations"].is_array()) {
            send_json(res, json{{"error", "Missing 'operations' array"}}, 400);
            return;
        }

        auto operations = body["operations"];
        if (operations.empty()) {
            send_json(res, json{{"error", "Operations array is empty"}}, 400);
            return;
        }

        auto description = body.value("description", "");

        {
            std::lock_guard<std::mutex> lock(s_macro_mutex);
            MacroDefinition macro;
            macro.name = name;
            macro.description = description;
            macro.operations = operations;
            s_macros[name] = std::move(macro);
        }

        PipeServer::get().log("Macro: saved '" + name + "' with " +
                              std::to_string(operations.size()) + " operations");

        send_json(res, json{
            {"success", true},
            {"name", name},
            {"description", description},
            {"operationCount", (int)operations.size()}
        });
    });

    // POST /api/macro/play -- Execute a saved macro with optional parameter substitution
    server.Post("/api/macro/play", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto name = body.value("name", "");
        if (name.empty()) {
            send_json(res, json{{"error", "Missing 'name'"}}, 400);
            return;
        }

        // Retrieve the macro definition
        json operations;
        {
            std::lock_guard<std::mutex> lock(s_macro_mutex);
            auto it = s_macros.find(name);
            if (it == s_macros.end()) {
                send_json(res, json{{"error", "Macro not found: " + name}}, 404);
                return;
            }
            operations = it->second.operations;
        }

        // Apply parameter substitution if params are provided
        auto params = body.value("params", json::object());
        if (params.is_object() && !params.empty()) {
            operations = substitute_params(operations, params);
        }

        PipeServer::get().log("Macro: playing '" + name + "' (" +
                              std::to_string(operations.size()) + " operations)");

        // Execute all operations on the game thread as a batch
        auto result = GameThreadQueue::get().submit_and_wait([operations, name]() -> json {
            json results = json::array();

            for (const auto& op : operations) {
                results.push_back(execute_operation(op));
            }

            return json{
                {"success", true},
                {"macro", name},
                {"results", results},
                {"operationCount", (int)operations.size()}
            };
        }, 10000); // Longer timeout for macro playback

        send_json(res, result);
    });

    // GET /api/macro/list -- List all saved macros
    server.Get("/api/macro/list", [](const httplib::Request&, httplib::Response& res) {
        std::lock_guard<std::mutex> lock(s_macro_mutex);

        json macros_arr = json::array();
        for (const auto& [name, macro] : s_macros) {
            macros_arr.push_back(json{
                {"name", macro.name},
                {"description", macro.description},
                {"operationCount", (int)macro.operations.size()}
            });
        }

        send_json(res, json{
            {"macros", macros_arr},
            {"count", (int)s_macros.size()}
        });
    });

    // DELETE /api/macro/delete -- Delete a saved macro
    server.Delete("/api/macro/delete", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto name = body.value("name", "");
        if (name.empty()) {
            send_json(res, json{{"error", "Missing 'name'"}}, 400);
            return;
        }

        {
            std::lock_guard<std::mutex> lock(s_macro_mutex);
            auto it = s_macros.find(name);
            if (it == s_macros.end()) {
                send_json(res, json{{"error", "Macro not found: " + name}}, 404);
                return;
            }
            s_macros.erase(it);
        }

        PipeServer::get().log("Macro: deleted '" + name + "'");

        send_json(res, json{
            {"success", true},
            {"name", name}
        });
    });

    // GET /api/macro/get -- Get a macro's full definition
    server.Get("/api/macro/get", [](const httplib::Request& req, httplib::Response& res) {
        auto name = req.get_param_value("name");
        if (name.empty()) {
            send_json(res, json{{"error", "Missing 'name' query parameter"}}, 400);
            return;
        }

        std::lock_guard<std::mutex> lock(s_macro_mutex);
        auto it = s_macros.find(name);
        if (it == s_macros.end()) {
            send_json(res, json{{"error", "Macro not found: " + name}}, 404);
            return;
        }

        const auto& macro = it->second;
        send_json(res, json{
            {"name", macro.name},
            {"description", macro.description},
            {"operations", macro.operations},
            {"operationCount", (int)macro.operations.size()}
        });
    });
}

} // namespace MacroRoutes
