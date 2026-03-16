#include "blueprint_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../reflection/object_registry.h"
#include "../reflection/property_reader.h"
#include "../reflection/property_writer.h"
#include "../reflection/function_caller.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;
using namespace uevr;

namespace BlueprintRoutes {

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (data.contains("error") && status == 200) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

// Check if a UClass is derived from AActor by walking the super chain
static bool is_actor_class(API::UClass* cls) {
    static API::UClass* actor_class = nullptr;
    if (!actor_class) {
        actor_class = API::get()->find_uobject<API::UClass>(L"Class /Script/Engine.Actor");
    }
    if (!actor_class || !cls) return false;

    auto* s = reinterpret_cast<API::UStruct*>(cls);
    while (s) {
        if (s == reinterpret_cast<API::UStruct*>(actor_class)) return true;
        s = s->get_super();
    }
    return false;
}

void register_routes(httplib::Server& server) {
    // POST /api/blueprint/spawn — Spawn a UObject/Actor
    server.Post("/api/blueprint/spawn", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto class_name = body.value("className", "");
        auto outer_str = body.value("outerAddress", "");

        if (class_name.empty()) {
            send_json(res, json{{"error", "Missing 'className'"}}, 400);
            return;
        }

        PipeServer::get().log("Blueprint: spawn " + class_name);

        auto result = GameThreadQueue::get().submit_and_wait([class_name, outer_str]() -> json {
            auto& api = API::get();

            // Find the class
            auto wname = JsonHelpers::utf8_to_wide(class_name);
            auto* cls = api->find_uobject<API::UClass>(wname);
            if (!cls) {
                // Try with "Class " prefix
                cls = api->find_uobject<API::UClass>(L"Class " + wname);
            }
            if (!cls) {
                return json{{"error", "Class not found: " + class_name}};
            }

            // Get outer — default to transient package
            API::UObject* outer = nullptr;
            if (!outer_str.empty()) {
                auto addr = JsonHelpers::string_to_address(outer_str);
                outer = reinterpret_cast<API::UObject*>(addr);
                if (!API::UObjectHook::exists(outer)) {
                    return json{{"error", "Outer object no longer valid"}};
                }
            } else {
                // Use /Engine/Transient as outer
                outer = api->find_uobject(L"/Engine/Transient");
                if (!outer) {
                    return json{{"error", "Could not find default outer (/Engine/Transient)"}};
                }
            }

            auto* obj = api->spawn_object(cls, outer);
            if (!obj) {
                return json{{"error", "spawn_object returned null"}};
            }

            // Register in our tracker
            ObjectRegistry::get().register_spawned(obj);

            json result;
            result["success"] = true;
            result["address"] = JsonHelpers::address_to_string(obj);
            result["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());
            result["isActor"] = is_actor_class(cls);

            auto obj_cls = obj->get_class();
            if (obj_cls) {
                auto name = obj_cls->get_fname();
                if (name) result["class"] = JsonHelpers::fname_to_string(name);
            }

            return result;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/blueprint/add_component — Add component to actor
    server.Post("/api/blueprint/add_component", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto actor_str = body.value("actorAddress", "");
        auto comp_class = body.value("componentClass", "");
        bool deferred = body.value("deferred", false);

        if (actor_str.empty() || comp_class.empty()) {
            send_json(res, json{{"error", "Missing 'actorAddress' or 'componentClass'"}}, 400);
            return;
        }

        PipeServer::get().log("Blueprint: add_component " + comp_class + " to " + actor_str);

        auto result = GameThreadQueue::get().submit_and_wait([actor_str, comp_class, deferred]() -> json {
            auto& api = API::get();

            auto actor_addr = JsonHelpers::string_to_address(actor_str);
            auto* actor = reinterpret_cast<API::UObject*>(actor_addr);
            if (!API::UObjectHook::exists(actor)) {
                return json{{"error", "Actor no longer valid"}};
            }

            auto wname = JsonHelpers::utf8_to_wide(comp_class);
            auto* cls = api->find_uobject<API::UClass>(wname);
            if (!cls) {
                cls = api->find_uobject<API::UClass>(L"Class " + wname);
            }
            if (!cls) {
                return json{{"error", "Component class not found: " + comp_class}};
            }

            auto* comp = api->add_component_by_class(actor, cls, deferred);
            if (!comp) {
                return json{{"error", "add_component_by_class returned null"}};
            }

            json result;
            result["success"] = true;
            result["address"] = JsonHelpers::address_to_string(comp);
            result["fullName"] = JsonHelpers::wide_to_utf8(comp->get_full_name());

            auto comp_cls = comp->get_class();
            if (comp_cls) {
                auto name = comp_cls->get_fname();
                if (name) result["class"] = JsonHelpers::fname_to_string(name);
            }

            return result;
        }, 10000);

        send_json(res, result);
    });

    // GET /api/blueprint/cdo — Get Class Default Object
    server.Get("/api/blueprint/cdo", [](const httplib::Request& req, httplib::Response& res) {
        auto class_name = req.get_param_value("className");
        if (class_name.empty()) {
            send_json(res, json{{"error", "Missing 'className' parameter"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([class_name]() -> json {
            auto& api = API::get();

            auto wname = JsonHelpers::utf8_to_wide(class_name);
            auto* cls = api->find_uobject<API::UClass>(wname);
            if (!cls) {
                cls = api->find_uobject<API::UClass>(L"Class " + wname);
            }
            if (!cls) {
                return json{{"error", "Class not found: " + class_name}};
            }

            auto* cdo = cls->get_class_default_object();
            if (!cdo) {
                return json{{"error", "No CDO for class: " + class_name}};
            }

            json result;
            result["className"] = class_name;
            result["cdoAddress"] = JsonHelpers::address_to_string(cdo);
            result["cdoFullName"] = JsonHelpers::wide_to_utf8(cdo->get_full_name());

            // Read a summary of fields
            json fields = json::array();
            for (auto* prop = cls->get_child_properties(); prop; prop = prop->get_next()) {
                auto* fprop = reinterpret_cast<API::FProperty*>(prop);
                auto* fclass = fprop->get_class();
                if (!fclass) continue;

                auto type_name = JsonHelpers::wide_to_utf8(fclass->get_fname()->to_string());
                if (type_name.find("Property") == std::string::npos) continue;

                auto field_name = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());
                json field;
                field["name"] = field_name;
                field["type"] = type_name;
                field["offset"] = fprop->get_offset();

                // Read value from CDO
                try {
                    if (type_name == "BoolProperty") {
                        field["value"] = reinterpret_cast<API::FBoolProperty*>(fprop)->get_value_from_object(cdo);
                    } else {
                        field["value"] = PropertyReader::read_property(cdo, fprop, 1);
                    }
                } catch (...) {
                    field["value"] = "<read error>";
                }

                fields.push_back(field);
            }
            result["fields"] = fields;

            return result;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/blueprint/cdo — Write CDO field
    server.Post("/api/blueprint/cdo", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto class_name = body.value("className", "");
        auto field_name = body.value("fieldName", "");

        if (class_name.empty() || field_name.empty() || !body.contains("value")) {
            send_json(res, json{{"error", "Missing className, fieldName, or value"}}, 400);
            return;
        }

        auto value = body["value"];
        PipeServer::get().log("Blueprint: write CDO " + class_name + "." + field_name);

        auto result = GameThreadQueue::get().submit_and_wait([class_name, field_name, value]() -> json {
            auto& api = API::get();

            auto wname = JsonHelpers::utf8_to_wide(class_name);
            auto* cls = api->find_uobject<API::UClass>(wname);
            if (!cls) {
                cls = api->find_uobject<API::UClass>(L"Class " + wname);
            }
            if (!cls) {
                return json{{"error", "Class not found: " + class_name}};
            }

            auto* cdo = cls->get_class_default_object();
            if (!cdo) {
                return json{{"error", "No CDO for class: " + class_name}};
            }

            auto wfield = JsonHelpers::utf8_to_wide(field_name);
            auto* prop = cls->find_property(wfield.c_str());
            if (!prop) {
                return json{{"error", "Field not found: " + field_name}};
            }

            auto* fprop = reinterpret_cast<API::FProperty*>(prop);
            std::string err;
            if (PropertyWriter::write_property(cdo, fprop, value, err)) {
                auto new_val = PropertyReader::read_property(cdo, fprop, 2);
                return json{{"success", true}, {"field", field_name}, {"newValue", new_val}};
            }
            return json{{"error", err}};
        });

        send_json(res, result);
    });

    // POST /api/blueprint/destroy — Destroy an actor
    server.Post("/api/blueprint/destroy", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty()) {
            send_json(res, json{{"error", "Missing 'address'"}}, 400);
            return;
        }

        PipeServer::get().log("Blueprint: destroy " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([addr_str]() -> json {
            auto addr = JsonHelpers::string_to_address(addr_str);
            auto* obj = reinterpret_cast<API::UObject*>(addr);

            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            // Try K2_DestroyActor
            auto* cls = obj->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            auto* func = cls->find_function(L"K2_DestroyActor");
            if (func) {
                auto ps = func->get_properties_size();
                std::vector<uint8_t> params(ps, 0);
                obj->process_event(func, params.data());
            }

            ObjectRegistry::get().unregister(obj);

            return json{{"success", true}, {"destroyed", addr_str}};
        });

        send_json(res, result);
    });

    // POST /api/blueprint/set_transform — Set actor transform
    server.Post("/api/blueprint/set_transform", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty()) {
            send_json(res, json{{"error", "Missing 'address'"}}, 400);
            return;
        }

        PipeServer::get().log("Blueprint: set_transform " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([addr_str, body]() -> json {
            auto addr = JsonHelpers::string_to_address(addr_str);
            auto* obj = reinterpret_cast<API::UObject*>(addr);

            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            auto* cls = obj->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            json results;

            // Set location
            if (body.contains("location")) {
                auto loc = body["location"];
                json args;
                args["NewLocation"] = loc;
                args["bSweep"] = false;
                args["bTeleport"] = true;
                auto r = FunctionCaller::invoke_function(addr, "K2_SetActorLocation", args);
                results["location"] = r.contains("error") ? r : json{{"set", true}};
            }

            // Set rotation
            if (body.contains("rotation")) {
                auto rot = body["rotation"];
                json args;
                args["NewRotation"] = rot;
                args["bTeleportPhysics"] = true;
                auto r = FunctionCaller::invoke_function(addr, "K2_SetActorRotation", args);
                results["rotation"] = r.contains("error") ? r : json{{"set", true}};
            }

            // Set scale
            if (body.contains("scale")) {
                auto scale = body["scale"];
                json args;
                args["NewScale3D"] = scale;
                auto r = FunctionCaller::invoke_function(addr, "SetActorScale3D", args);
                results["scale"] = r.contains("error") ? r : json{{"set", true}};
            }

            results["success"] = true;
            return results;
        });

        send_json(res, result);
    });

    // GET /api/blueprint/spawned — List MCP-spawned objects
    server.Get("/api/blueprint/spawned", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() {
            return ObjectRegistry::get().get_spawned_objects();
        });
        send_json(res, result);
    });
}

} // namespace BlueprintRoutes
