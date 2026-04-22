#include "explorer_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../reflection/object_explorer.h"
#include "../reflection/property_reader.h"
#include "../reflection/property_writer.h"
#include "../reflection/function_caller.h"
#include "../reflection/property_probes.h"
#include "../reflection/kismet_disasm.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;

namespace ExplorerRoutes {

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
    // GET /api/explorer/is_valid — Check if a UObject pointer is still alive
    server.Get("/api/explorer/is_valid", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        if (addr_str.empty()) {
            send_json(res, json{{"error", "Missing 'address' parameter"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"valid", false}, {"address", addr_str}, {"reason", "Invalid address format"}});
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([address, addr_str]() -> json {
            auto* obj = reinterpret_cast<uevr::API::UObject*>(address);
            bool valid = uevr::API::UObjectHook::exists(obj);

            json response = {{"valid", valid}, {"address", addr_str}};

            if (valid) {
                try {
                    auto* cls = obj->get_class();
                    if (cls) {
                        auto fname = cls->get_fname();
                        response["class"] = JsonHelpers::fname_to_string(&fname);
                    }
                    auto obj_fname = obj->get_fname();
                    response["name"] = JsonHelpers::fname_to_string(&obj_fname);
                } catch (...) {
                    response["name"] = "unknown";
                }
            }

            return response;
        });
        send_json(res, result);
    });

    // GET /api/explorer/search — Search GUObjectArray by name
    server.Get("/api/explorer/search", [](const httplib::Request& req, httplib::Response& res) {
        auto query = req.get_param_value("query");
        int limit = 50;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        if (query.empty()) {
            send_json(res, json{{"error", "Missing 'query' parameter"}}, 400);
            return;
        }

        PipeServer::get().log("Explorer: search '" + query + "'");
        auto result = GameThreadQueue::get().submit_and_wait([query, limit]() {
            return ObjectExplorer::search_objects(query, limit);
        });
        send_json(res, result);
    });

    // GET /api/explorer/classes — Search UClass objects
    server.Get("/api/explorer/classes", [](const httplib::Request& req, httplib::Response& res) {
        auto query = req.get_param_value("query");
        int limit = 50;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        if (query.empty()) {
            send_json(res, json{{"error", "Missing 'query' parameter"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([query, limit]() {
            return ObjectExplorer::search_classes(query, limit);
        });
        send_json(res, result);
    });

    // GET /api/explorer/type — Type schema
    server.Get("/api/explorer/type", [](const httplib::Request& req, httplib::Response& res) {
        auto type_name = req.get_param_value("typeName");
        if (type_name.empty()) {
            send_json(res, json{{"error", "Missing 'typeName' parameter"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([type_name]() {
            return ObjectExplorer::get_type_info(type_name);
        });
        send_json(res, result);
    });

    // GET /api/explorer/object — Inspect live object
    server.Get("/api/explorer/object", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        if (addr_str.empty()) {
            send_json(res, json{{"error", "Missing 'address' parameter"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        int depth = 2;
        if (req.has_param("depth")) {
            try { depth = std::stoi(req.get_param_value("depth")); } catch (...) {}
        }

        PipeServer::get().log("Explorer: inspect " + addr_str);
        auto result = GameThreadQueue::get().submit_and_wait([address, depth]() {
            return ObjectExplorer::inspect_object(address, depth);
        });
        send_json(res, result);
    });

    // GET /api/explorer/summary — Lightweight summary
    server.Get("/api/explorer/summary", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid or missing address"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([address]() {
            return ObjectExplorer::summarize_object(address);
        });
        send_json(res, result);
    });

    // GET /api/explorer/field — Read single field
    server.Get("/api/explorer/field", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        auto field_name = req.get_param_value("fieldName");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0 || field_name.empty()) {
            send_json(res, json{{"error", "Missing 'address' or 'fieldName'"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([address, field_name]() {
            return ObjectExplorer::read_field(address, field_name);
        });
        send_json(res, result);
    });

    // GET /api/explorer/method — Call 0-param getter
    server.Get("/api/explorer/method", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        auto method_name = req.get_param_value("methodName");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0 || method_name.empty()) {
            send_json(res, json{{"error", "Missing 'address' or 'methodName'"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([address, method_name]() {
            return FunctionCaller::call_getter(address, method_name);
        });
        send_json(res, result);
    });

    // GET /api/explorer/objects_by_class — Find instances
    server.Get("/api/explorer/objects_by_class", [](const httplib::Request& req, httplib::Response& res) {
        auto class_name = req.get_param_value("className");
        int limit = 50;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        if (class_name.empty()) {
            send_json(res, json{{"error", "Missing 'className' parameter"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([class_name, limit]() {
            return ObjectExplorer::find_objects_by_class(class_name, limit);
        });
        send_json(res, result);
    });

    // POST /api/explorer/field — Write field value
    server.Post("/api/explorer/field", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        auto field_name = body.value("fieldName", "");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0 || field_name.empty() || !body.contains("value")) {
            send_json(res, json{{"error", "Missing address, fieldName, or value"}}, 400);
            return;
        }

        auto value = body["value"];
        PipeServer::get().log("Explorer: write " + field_name + " on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, field_name, value]() -> json {
            auto obj = reinterpret_cast<uevr::API::UObject*>(address);
            if (!uevr::API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            auto cls = obj->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            auto wname = JsonHelpers::utf8_to_wide(field_name);
            auto prop = cls->find_property(wname.c_str());
            if (!prop) return json{{"error", "Field not found: " + field_name}};

            std::string error;
            if (PropertyWriter::write_property(obj, prop, value, error)) {
                // Read back the value to confirm
                auto new_val = PropertyReader::read_property(obj, prop, 2);
                return json{{"success", true}, {"field", field_name}, {"newValue", new_val}};
            }
            return json{{"error", error}};
        });
        send_json(res, result);
    });

    // POST /api/explorer/method — Call UFunction with args
    server.Post("/api/explorer/method", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        auto method_name = body.value("methodName", "");
        auto address = JsonHelpers::string_to_address(addr_str);
        auto args = body.value("args", json::object());

        if (address == 0 || method_name.empty()) {
            send_json(res, json{{"error", "Missing address or methodName"}}, 400);
            return;
        }

        PipeServer::get().log("Explorer: invoke " + method_name + " on " + addr_str);
        auto result = GameThreadQueue::get().submit_and_wait([address, method_name, args]() {
            return FunctionCaller::invoke_function(address, method_name, args);
        });
        send_json(res, result);
    });

    // POST /api/explorer/chain — Multi-step object graph traversal
    server.Post("/api/explorer/chain", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0 || !body.contains("steps")) {
            send_json(res, json{{"error", "Missing 'address' or 'steps'"}}, 400);
            return;
        }

        auto steps = body["steps"];
        PipeServer::get().log("Explorer: chain " + std::to_string(steps.size()) + " steps from " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, steps]() {
            return ObjectExplorer::chain_query(address, steps);
        }, 10000);
        send_json(res, result);
    });

    // GET /api/explorer/singletons — List common singleton objects
    server.Get("/api/explorer/singletons", [](const httplib::Request&, httplib::Response& res) {
        PipeServer::get().log("Explorer: get singletons");
        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return ObjectExplorer::get_singletons();
        });
        send_json(res, result);
    });

    // GET /api/explorer/singleton — Find singleton by type name
    server.Get("/api/explorer/singleton", [](const httplib::Request& req, httplib::Response& res) {
        auto type_name = req.get_param_value("typeName");
        if (type_name.empty()) {
            send_json(res, json{{"error", "Missing 'typeName' parameter"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([type_name]() {
            return ObjectExplorer::get_singleton(type_name);
        });
        send_json(res, result);
    });

    // GET /api/explorer/array — Read array property with pagination
    server.Get("/api/explorer/array", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        auto field_name = req.get_param_value("fieldName");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0 || field_name.empty()) {
            send_json(res, json{{"error", "Missing 'address' or 'fieldName'"}}, 400);
            return;
        }

        int offset = 0, limit = 50;
        if (req.has_param("offset")) {
            try { offset = std::stoi(req.get_param_value("offset")); } catch (...) {}
        }
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        auto result = GameThreadQueue::get().submit_and_wait([address, field_name, offset, limit]() {
            return ObjectExplorer::read_array(address, field_name, offset, limit);
        });
        send_json(res, result);
    });

    // GET /api/explorer/memory — Raw hex dump of memory
    server.Get("/api/explorer/memory", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        auto address = JsonHelpers::string_to_address(addr_str);

        if (address == 0) {
            send_json(res, json{{"error", "Missing or invalid 'address'"}}, 400);
            return;
        }

        int size = 256;
        if (req.has_param("size")) {
            try { size = std::stoi(req.get_param_value("size")); } catch (...) {}
        }

        auto result = GameThreadQueue::get().submit_and_wait([address, size]() {
            return ObjectExplorer::read_memory(address, size);
        });
        send_json(res, result);
    });

    // GET /api/explorer/typed — Read typed values from memory
    server.Get("/api/explorer/typed", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        auto address = JsonHelpers::string_to_address(addr_str);
        auto type = req.get_param_value("type");

        if (address == 0 || type.empty()) {
            send_json(res, json{{"error", "Missing 'address' or 'type'"}}, 400);
            return;
        }

        int count = 1, stride = 0;
        if (req.has_param("count")) {
            try { count = std::stoi(req.get_param_value("count")); } catch (...) {}
        }
        if (req.has_param("stride")) {
            try { stride = std::stoi(req.get_param_value("stride")); } catch (...) {}
        }

        auto result = GameThreadQueue::get().submit_and_wait([address, type, count, stride]() {
            return ObjectExplorer::read_typed(address, type, count, stride);
        });
        send_json(res, result);
    });

    // POST /api/explorer/batch — Multiple operations
    server.Post("/api/explorer/batch", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        if (!body.contains("operations") || !body["operations"].is_array()) {
            send_json(res, json{{"error", "Missing 'operations' array"}}, 400);
            return;
        }

        auto operations = body["operations"];
        PipeServer::get().log("Explorer: batch " + std::to_string(operations.size()) + " ops");

        auto result = GameThreadQueue::get().submit_and_wait([operations]() -> json {
            json results = json::array();

            for (const auto& op : operations) {
                auto type = op.value("type", "");
                try {
                    if (type == "read_field") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        results.push_back(ObjectExplorer::read_field(addr, op.value("fieldName", "")));
                    } else if (type == "write_field") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        auto obj = reinterpret_cast<uevr::API::UObject*>(addr);
                        auto field = op.value("fieldName", "");

                        if (!uevr::API::UObjectHook::exists(obj)) {
                            results.push_back(json{{"error", "Object no longer valid"}});
                            continue;
                        }
                        auto cls = obj->get_class();
                        if (!cls) { results.push_back(json{{"error", "No class"}}); continue; }

                        auto wname = JsonHelpers::utf8_to_wide(field);
                        auto prop = cls->find_property(wname.c_str());
                        if (!prop) { results.push_back(json{{"error", "Field not found"}}); continue; }

                        std::string err;
                        if (PropertyWriter::write_property(obj, prop, op["value"], err)) {
                            results.push_back(json{{"success", true}});
                        } else {
                            results.push_back(json{{"error", err}});
                        }
                    } else if (type == "inspect") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        results.push_back(ObjectExplorer::inspect_object(addr, op.value("depth", 1)));
                    } else if (type == "call_method") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        results.push_back(FunctionCaller::invoke_function(addr, op.value("methodName", ""), op.value("args", json::object())));
                    } else if (type == "search") {
                        results.push_back(ObjectExplorer::search_objects(op.value("query", ""), op.value("limit", 10)));
                    } else if (type == "summary") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        results.push_back(ObjectExplorer::summarize_object(addr));
                    } else if (type == "get_type") {
                        results.push_back(ObjectExplorer::get_type_info(op.value("typeName", "")));
                    } else if (type == "singleton") {
                        results.push_back(ObjectExplorer::get_singleton(op.value("typeName", "")));
                    } else if (type == "read_array") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        results.push_back(ObjectExplorer::read_array(addr, op.value("fieldName", ""), op.value("offset", 0), op.value("limit", 50)));
                    } else if (type == "read_memory") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        results.push_back(ObjectExplorer::read_memory(addr, op.value("size", 256)));
                    } else if (type == "read_typed") {
                        auto addr = JsonHelpers::string_to_address(op.value("address", ""));
                        results.push_back(ObjectExplorer::read_typed(addr, op.value("type", "u8"), op.value("count", 1), op.value("stride", 0)));
                    } else {
                        results.push_back(json{{"error", "Unknown operation type: " + type}});
                    }
                } catch (const std::exception& e) {
                    results.push_back(json{{"error", std::string("Exception: ") + e.what()}});
                }
            }

            return json{{"results", results}};
        }, 10000); // Longer timeout for batch

        send_json(res, result);
    });

    // POST /api/explorer/memory — Raw byte write. Body: {address: "0xHEX", bytes: "DEADBEEF..."}
    // where bytes is a hex string (with or without separators). Temporarily flips page protection
    // to RWX for the write so code patches work, restores afterwards, flushes instruction cache.
    server.Post("/api/explorer/memory", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(body.value("address", ""));
        if (address == 0) {
            send_json(res, json{{"error", "Missing or invalid 'address'"}}, 400);
            return;
        }

        // Accept bytes as a hex string ("DE AD BE EF" or "DEADBEEF") or a JSON array of ints.
        std::vector<uint8_t> bytes;
        if (body.contains("bytes") && body["bytes"].is_string()) {
            auto s = body["bytes"].get<std::string>();
            std::string cleaned;
            cleaned.reserve(s.size());
            for (char c : s) {
                if (std::isxdigit((unsigned char)c)) cleaned.push_back(c);
                else if (c == ' ' || c == ',' || c == ':' || c == '-' || c == '\t' || c == '\n' || c == '\r') continue;
                else {
                    send_json(res, json{{"error", std::string("Unexpected character in hex: '") + c + "'"}}, 400);
                    return;
                }
            }
            if (cleaned.size() % 2 != 0) {
                send_json(res, json{{"error", "Hex string length must be even"}}, 400);
                return;
            }
            bytes.reserve(cleaned.size() / 2);
            for (size_t i = 0; i < cleaned.size(); i += 2) {
                bytes.push_back((uint8_t)std::stoul(cleaned.substr(i, 2), nullptr, 16));
            }
        } else if (body.contains("bytes") && body["bytes"].is_array()) {
            for (const auto& b : body["bytes"]) {
                if (!b.is_number_integer()) {
                    send_json(res, json{{"error", "bytes array must contain integers"}}, 400);
                    return;
                }
                int v = b.get<int>();
                if (v < 0 || v > 255) {
                    send_json(res, json{{"error", "byte value out of range 0..255"}}, 400);
                    return;
                }
                bytes.push_back((uint8_t)v);
            }
        } else {
            send_json(res, json{{"error", "Missing 'bytes' (hex string or int array)"}}, 400);
            return;
        }

        PipeServer::get().log("Explorer: write " + std::to_string(bytes.size()) +
                              " bytes @ " + JsonHelpers::address_to_string(address));

        auto result = GameThreadQueue::get().submit_and_wait([address, bytes]() {
            return ObjectExplorer::write_memory(address, bytes);
        });
        send_json(res, result);
    });

    // GET /api/dump/reflection — Walk GUObjectArray and emit every UClass/UScriptStruct/UEnum
    // with fields and (optionally) methods. Powers the dump_reflection / dump_sdk / dump_usmap
    // MCP tools. Query params:
    //   filter: case-insensitive substring applied to full names
    //   methods: "true"/"false" (default true)
    //   enums:   "true"/"false" (default true)
    server.Get("/api/dump/reflection", [](const httplib::Request& req, httplib::Response& res) {
        auto filter = req.has_param("filter") ? req.get_param_value("filter") : std::string();
        auto parse_bool = [&](const std::string& key, bool def) {
            if (!req.has_param(key)) return def;
            auto v = req.get_param_value(key);
            return !(v == "false" || v == "0" || v == "no");
        };
        bool include_methods = parse_bool("methods", true);
        bool include_enums   = parse_bool("enums",   true);

        PipeServer::get().log("Dump: reflection filter='" + filter + "' methods=" +
                              (include_methods ? "y" : "n") + " enums=" + (include_enums ? "y" : "n"));

        // Reflection walk on big games can run several minutes (methods+enums across
        // every UClass in GUObjectArray). 5 minutes covers even large AAA UE5 titles;
        // callers can filter with the `filter` param to shrink scope.
        // NOTE: Prefer /api/dump/reflection_batch for large games — a single long
        // game-thread slice starves UE's tick and can crash the render thread.
        auto result = GameThreadQueue::get().submit_and_wait(
            [filter, include_methods, include_enums]() {
                return ObjectExplorer::dump_reflection(filter, include_methods, include_enums);
            },
            300000);
        send_json(res, result);
    });

    // GET /api/dump/reflection_batch?offset=&limit=&filter=&methods=&enums=
    //
    // Paginated reflection walk — each call processes at most `limit` objects
    // from GUObjectArray starting at `offset`. The game thread yields between
    // batches, so multi-second dumps don't block UE's tick and crash the render
    // thread. Server loops until `done`=true.
    server.Get("/api/dump/reflection_batch", [](const httplib::Request& req, httplib::Response& res) {
        int offset = 0, limit = 500;
        if (req.has_param("offset")) try { offset = std::stoi(req.get_param_value("offset")); } catch (...) {}
        if (req.has_param("limit"))  try { limit  = std::stoi(req.get_param_value("limit"));  } catch (...) {}
        auto filter = req.has_param("filter") ? req.get_param_value("filter") : std::string();
        auto parse_bool = [&](const std::string& key, bool def) {
            if (!req.has_param(key)) return def;
            auto v = req.get_param_value(key);
            return !(v == "false" || v == "0" || v == "no");
        };
        bool include_methods = parse_bool("methods", true);
        bool include_enums   = parse_bool("enums",   true);

        // Clamp — each batch must fit comfortably in a single game-thread slice.
        // 2000 is aggressive but tolerable on fast machines; default 500 is safe.
        if (limit < 1) limit = 1;
        if (limit > 5000) limit = 5000;

        // Per-batch timeout: methods=true does significantly more work per class
        // (walks every UFunction's parameter chain up the super hierarchy). The
        // first batch can be extra slow because UE is still registering types.
        int timeout_ms = include_methods ? 20000 : 10000;
        auto result = GameThreadQueue::get().submit_and_wait(
            [offset, limit, filter, include_methods, include_enums]() {
                return ObjectExplorer::dump_reflection_batch(
                    offset, limit, filter, include_methods, include_enums);
            },
            timeout_ms);
        send_json(res, result);
    });

    // GET /api/dump/probe_status — Report which property-subclass field offsets
    // the self-discovery probes have resolved. Useful cross-game to verify
    // PropertyClass, MetaClass, InterfaceClass, MapKey/Value, SetElement and
    // delegate signature probes all resolved on the current target. Each
    // entry includes status (resolved/failed/undiscovered), offset, and hit
    // count since injection.
    server.Get("/api/dump/probe_status", [](const httplib::Request&, httplib::Response& res) {
        // Merge PropertyProbes + Kismet probe diagnostics so one endpoint
        // gives a full picture of what the self-discovery has resolved.
        json combined = PropertyProbes::diagnostics();
        if (combined.contains("probes") && combined["probes"].is_array()) {
            combined["probes"].push_back(KismetDisasm::diagnostics());
        }

        // Engine-version hint. No direct version fn in UEVR API, so we check
        // a UE5-only class marker. DoubleProperty existed in UE 4.25+ so
        // doesn't discriminate. LargeWorldCoordinates and Nanite types are
        // genuinely UE5-only.
        auto& api = uevr::API::get();
        std::string version_hint = "unknown";
        if (api) {
            // ChaosFlesh / Nanite / LargeWorldCoordinatesReal-adjacent types
            // are UE5 exclusives. Probe a handful; if any exists, call it UE5.
            const wchar_t* ue5_markers[] = {
                L"Class /Script/NaniteDisplacedMesh.NaniteDisplacedMesh",
                L"Class /Script/Engine.WorldPartition",
                L"ScriptStruct /Script/CoreUObject.LargeWorldCoordinatesReal",
                L"Class /Script/GeometryCollectionEngine.GeometryCollectionRoot",
            };
            bool is_ue5 = false;
            for (const wchar_t* m : ue5_markers) {
                if (api->find_uobject<uevr::API::UStruct>(m) != nullptr) { is_ue5 = true; break; }
            }
            version_hint = is_ue5 ? "UE5" : "UE4";
        }
        combined["engineVersionHint"] = version_hint;

        send_json(res, combined);
    });

    // POST /api/dump/probe_reset — Clear all probe caches. After a game
    // hot-reload (rare — most UE games don't support it) or when testing,
    // this forces re-discovery on the next dump.
    server.Post("/api/dump/probe_reset", [](const httplib::Request&, httplib::Response& res) {
        PropertyProbes::reset();
        KismetDisasm::reset();
        send_json(res, json{{"ok", true}, {"reset", true}});
    });
}

} // namespace ExplorerRoutes
