#include "world_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../reflection/object_explorer.h"
#include "../reflection/function_caller.h"
#include "../reflection/property_reader.h"
#include "../reflection/property_writer.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;
using namespace uevr;

namespace WorldRoutes {

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

    // GET /api/world/actors — List actors in the current world
    server.Get("/api/world/actors", [](const httplib::Request& req, httplib::Response& res) {
        int limit = 50;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }
        std::string filter;
        if (req.has_param("filter")) {
            filter = req.get_param_value("filter");
        }

        PipeServer::get().log("World: list actors (limit=" + std::to_string(limit) + ", filter=" + filter + ")");

        auto result = GameThreadQueue::get().submit_and_wait([limit, filter]() -> json {
            auto& api = API::get();
            if (!api) return json{{"error", "API not available"}};

            // Find the AActor base class
            auto* actor_class = api->find_uobject<API::UClass>(L"Class /Script/Engine.Actor");
            if (!actor_class) {
                return json{{"error", "Actor base class not found"}};
            }

            // If a filter is provided, try to find the specific class first
            API::UClass* filter_class = nullptr;
            if (!filter.empty()) {
                auto wfilter = JsonHelpers::utf8_to_wide(filter);
                filter_class = api->find_uobject<API::UClass>(wfilter);
                if (!filter_class) {
                    // Try with "Class " prefix
                    filter_class = api->find_uobject<API::UClass>(L"Class " + wfilter);
                }
                if (!filter_class) {
                    // Try as short name match — use the base Actor class and filter by name below
                    filter_class = nullptr;
                }
            }

            auto* search_class = filter_class ? filter_class : actor_class;
            auto instances = search_class->get_objects_matching<API::UObject>(false);

            json actors = json::array();
            int count = 0;

            for (auto* obj : instances) {
                if (count >= limit) break;
                if (!obj) continue;
                if (!API::UObjectHook::exists(obj)) continue;

                auto* cls = obj->get_class();
                if (!cls) continue;

                std::string class_name;
                auto* fname = cls->get_fname();
                if (fname) class_name = JsonHelpers::fname_to_string(fname);

                // If filter was provided but class wasn't found, do a substring match on class name
                if (!filter.empty() && !filter_class) {
                    std::string lower_class = class_name;
                    std::string lower_filter = filter;
                    std::transform(lower_class.begin(), lower_class.end(), lower_class.begin(), ::tolower);
                    std::transform(lower_filter.begin(), lower_filter.end(), lower_filter.begin(), ::tolower);
                    if (lower_class.find(lower_filter) == std::string::npos) continue;
                }

                std::string obj_name;
                auto* obj_fname = obj->get_fname();
                if (obj_fname) obj_name = JsonHelpers::fname_to_string(obj_fname);

                json actor;
                actor["address"] = JsonHelpers::address_to_string(obj);
                actor["class"] = class_name;
                actor["name"] = obj_name;
                actors.push_back(actor);
                count++;
            }

            return json{{"actors", actors}, {"count", actors.size()}};
        }, 10000);

        send_json(res, result);
    });

    // GET /api/world/components — Get components of an actor
    server.Get("/api/world/components", [](const httplib::Request& req, httplib::Response& res) {
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

        PipeServer::get().log("World: get components for " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address]() -> json {
            auto* obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            auto* cls = obj->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            // Try calling K2_GetComponentsByClass via FunctionCaller
            // Pass a null class to get all components
            json args;
            args["ComponentClass"] = nullptr;
            auto invoke_result = FunctionCaller::invoke_function(address, "K2_GetComponentsByClass", args);

            // If invoke succeeded and has array results, format them
            if (!invoke_result.contains("error") && invoke_result.contains("ReturnValue")) {
                auto& ret = invoke_result["ReturnValue"];
                if (ret.is_array()) {
                    json components = json::array();
                    for (auto& entry : ret) {
                        if (entry.is_string()) {
                            auto comp_addr = JsonHelpers::string_to_address(entry.get<std::string>());
                            if (comp_addr == 0) continue;
                            auto* comp = reinterpret_cast<API::UObject*>(comp_addr);
                            if (!API::UObjectHook::exists(comp)) continue;

                            json comp_json;
                            comp_json["address"] = JsonHelpers::address_to_string(comp);
                            auto* comp_cls = comp->get_class();
                            if (comp_cls) {
                                auto* fname = comp_cls->get_fname();
                                if (fname) comp_json["class"] = JsonHelpers::fname_to_string(fname);
                            }
                            auto* comp_fname = comp->get_fname();
                            if (comp_fname) comp_json["name"] = JsonHelpers::fname_to_string(comp_fname);
                            components.push_back(comp_json);
                        }
                    }
                    return json{{"components", components}, {"count", components.size()}};
                }
            }

            // Fallback: read BlueprintCreatedComponents or OwnedComponents array property
            json components = json::array();

            static const std::vector<std::wstring> comp_fields = {
                L"OwnedComponents", L"BlueprintCreatedComponents", L"InstanceComponents", L"ReplicatedComponents"
            };

            for (const auto& field_name : comp_fields) {
                auto* prop = cls->find_property(field_name.c_str());
                if (!prop) continue;

                auto field_utf8 = JsonHelpers::wide_to_utf8(field_name);
                auto arr_result = ObjectExplorer::read_array(address, field_utf8, 0, 100);
                if (arr_result.contains("error")) continue;

                if (arr_result.contains("elements") && arr_result["elements"].is_array()) {
                    for (auto& elem : arr_result["elements"]) {
                        if (elem.is_string()) {
                            auto comp_addr = JsonHelpers::string_to_address(elem.get<std::string>());
                            if (comp_addr == 0) continue;
                            auto* comp = reinterpret_cast<API::UObject*>(comp_addr);
                            if (!API::UObjectHook::exists(comp)) continue;

                            json comp_json;
                            comp_json["address"] = JsonHelpers::address_to_string(comp);
                            auto* comp_cls = comp->get_class();
                            if (comp_cls) {
                                auto* fname = comp_cls->get_fname();
                                if (fname) comp_json["class"] = JsonHelpers::fname_to_string(fname);
                            }
                            auto* comp_fname = comp->get_fname();
                            if (comp_fname) comp_json["name"] = JsonHelpers::fname_to_string(comp_fname);
                            components.push_back(comp_json);
                        } else if (elem.is_object() && elem.contains("address")) {
                            components.push_back(elem);
                        }
                    }
                    break; // Found a valid component array
                }
            }

            return json{{"components", components}, {"count", components.size()}};
        }, 10000);

        send_json(res, result);
    });

    // POST /api/world/line_trace — Perform a line trace (raycast)
    server.Post("/api/world/line_trace", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        if (!body.contains("start") || !body.contains("end")) {
            send_json(res, json{{"error", "Missing 'start' or 'end' vectors"}}, 400);
            return;
        }

        PipeServer::get().log("World: line trace");

        auto result = GameThreadQueue::get().submit_and_wait([body]() -> json {
            auto& api = API::get();
            if (!api) return json{{"error", "API not available"}};

            // Find KismetSystemLibrary and its CDO
            auto* ksl_class = api->find_uobject<API::UClass>(L"Class /Script/Engine.KismetSystemLibrary");
            if (!ksl_class) {
                return json{{"error", "KismetSystemLibrary class not found"}};
            }

            auto* cdo = ksl_class->get_class_default_object();
            if (!cdo) {
                return json{{"error", "KismetSystemLibrary CDO not found"}};
            }

            // Get a world context object (the local pawn)
            auto* pawn = api->get_local_pawn(0);
            if (!pawn) {
                return json{{"error", "No local pawn for world context"}};
            }

            auto cdo_addr = reinterpret_cast<uintptr_t>(cdo);

            // Build args for LineTraceSingleByChannel
            json args;
            args["WorldContextObject"] = JsonHelpers::address_to_string(pawn);
            args["Start"] = body["start"];
            args["End"] = body["end"];
            args["TraceChannel"] = body.value("channel", 0); // ECC_Visibility = 0
            args["bTraceComplex"] = body.value("complex", false);
            args["ActorsToIgnore"] = json::array();
            args["DrawDebugType"] = 0; // None
            args["bIgnoreSelf"] = true;

            auto invoke_result = FunctionCaller::invoke_function(cdo_addr, "LineTraceSingleByChannel", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            // Parse out the hit result
            json response;
            if (invoke_result.contains("ReturnValue")) {
                response["hit"] = invoke_result["ReturnValue"].get<bool>();
            } else {
                response["hit"] = false;
            }

            // Try to extract HitResult out-param data
            if (invoke_result.contains("OutHitResult") || invoke_result.contains("HitResult")) {
                auto& hit = invoke_result.contains("OutHitResult") ? invoke_result["OutHitResult"] : invoke_result["HitResult"];
                if (hit.is_object()) {
                    if (hit.contains("Location")) response["location"] = hit["Location"];
                    if (hit.contains("ImpactPoint")) response["location"] = hit["ImpactPoint"];
                    if (hit.contains("Normal")) response["normal"] = hit["Normal"];
                    if (hit.contains("ImpactNormal")) response["normal"] = hit["ImpactNormal"];
                    if (hit.contains("Distance")) response["distance"] = hit["Distance"];
                    if (hit.contains("PhysMaterial")) response["physMaterial"] = hit["PhysMaterial"];
                    if (hit.contains("Actor")) response["actor"] = hit["Actor"];
                    if (hit.contains("Component")) response["component"] = hit["Component"];
                    if (hit.contains("BoneName")) response["boneName"] = hit["BoneName"];
                }
            }

            // Include the raw invoke result for debugging if needed
            response["raw"] = invoke_result;

            return response;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/world/sphere_overlap — Sphere overlap test
    server.Post("/api/world/sphere_overlap", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        if (!body.contains("center") || !body.contains("radius")) {
            send_json(res, json{{"error", "Missing 'center' or 'radius'"}}, 400);
            return;
        }

        PipeServer::get().log("World: sphere overlap");

        auto result = GameThreadQueue::get().submit_and_wait([body]() -> json {
            auto& api = API::get();
            if (!api) return json{{"error", "API not available"}};

            auto* ksl_class = api->find_uobject<API::UClass>(L"Class /Script/Engine.KismetSystemLibrary");
            if (!ksl_class) {
                return json{{"error", "KismetSystemLibrary class not found"}};
            }

            auto* cdo = ksl_class->get_class_default_object();
            if (!cdo) {
                return json{{"error", "KismetSystemLibrary CDO not found"}};
            }

            auto* pawn = api->get_local_pawn(0);
            if (!pawn) {
                return json{{"error", "No local pawn for world context"}};
            }

            auto cdo_addr = reinterpret_cast<uintptr_t>(cdo);

            // SphereOverlapActors(WorldContextObject, SpherePos, SphereRadius, ObjectTypes, ActorClassFilter, ActorsToIgnore, OutActors)
            json args;
            args["WorldContextObject"] = JsonHelpers::address_to_string(pawn);
            args["SpherePos"] = body["center"];
            args["SphereRadius"] = body["radius"].get<float>();

            // ObjectTypes — array of object type queries. Use WorldDynamic + WorldStatic + Pawn by default
            if (body.contains("objectTypes")) {
                args["ObjectTypes"] = body["objectTypes"];
            }

            args["ActorClassFilter"] = nullptr;
            args["ActorsToIgnore"] = json::array();

            auto invoke_result = FunctionCaller::invoke_function(cdo_addr, "SphereOverlapActors", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            // Parse the OutActors array
            json actors = json::array();
            if (invoke_result.contains("OutActors") && invoke_result["OutActors"].is_array()) {
                for (auto& entry : invoke_result["OutActors"]) {
                    uintptr_t actor_addr = 0;
                    if (entry.is_string()) {
                        actor_addr = JsonHelpers::string_to_address(entry.get<std::string>());
                    }
                    if (actor_addr == 0) continue;

                    auto* actor = reinterpret_cast<API::UObject*>(actor_addr);
                    if (!API::UObjectHook::exists(actor)) continue;

                    json actor_json;
                    actor_json["address"] = JsonHelpers::address_to_string(actor);

                    auto* cls = actor->get_class();
                    if (cls) {
                        auto* fname = cls->get_fname();
                        if (fname) actor_json["class"] = JsonHelpers::fname_to_string(fname);
                    }
                    auto* obj_fname = actor->get_fname();
                    if (obj_fname) actor_json["name"] = JsonHelpers::fname_to_string(obj_fname);

                    actors.push_back(actor_json);
                }
            }

            json response;
            response["hit"] = invoke_result.value("ReturnValue", false);
            response["actors"] = actors;
            response["count"] = actors.size();
            response["raw"] = invoke_result;

            return response;
        }, 10000);

        send_json(res, result);
    });

    // GET /api/world/hierarchy — Get parent/child hierarchy of an object
    server.Get("/api/world/hierarchy", [](const httplib::Request& req, httplib::Response& res) {
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

        PipeServer::get().log("World: hierarchy for " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address]() -> json {
            auto* obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            auto* cls = obj->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            json result;
            result["address"] = JsonHelpers::address_to_string(obj);

            // Object name
            auto* obj_fname = obj->get_fname();
            if (obj_fname) result["name"] = JsonHelpers::fname_to_string(obj_fname);

            // Class name
            auto* cls_fname = cls->get_fname();
            if (cls_fname) result["class"] = JsonHelpers::fname_to_string(cls_fname);

            // Full name
            result["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());

            // Outer (UObject outer chain)
            auto* outer = obj->get_outer();
            if (outer && API::UObjectHook::exists(outer)) {
                json outer_json;
                outer_json["address"] = JsonHelpers::address_to_string(outer);
                auto* outer_fname = outer->get_fname();
                if (outer_fname) outer_json["name"] = JsonHelpers::fname_to_string(outer_fname);
                auto* outer_cls = outer->get_class();
                if (outer_cls) {
                    auto* cname = outer_cls->get_fname();
                    if (cname) outer_json["class"] = JsonHelpers::fname_to_string(cname);
                }
                result["outer"] = outer_json;
            } else {
                result["outer"] = nullptr;
            }

            // Try to read Owner (for actors)
            auto* owner_prop = cls->find_property(L"Owner");
            if (owner_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(owner_prop);
                auto owner_val = PropertyReader::read_property(obj, fprop, 1);
                result["owner"] = owner_val;
            }

            // Try to read AttachParent (for scene components)
            auto* attach_prop = cls->find_property(L"AttachParent");
            if (attach_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(attach_prop);
                auto attach_val = PropertyReader::read_property(obj, fprop, 1);
                result["attachParent"] = attach_val;
            }

            // Try to read AttachChildren (for scene components)
            auto* children_prop = cls->find_property(L"AttachChildren");
            if (children_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(children_prop);
                auto children_val = PropertyReader::read_property(obj, fprop, 1);
                result["attachChildren"] = children_val;
            }

            // Try to read Children (for actors — child actor components)
            auto* actor_children_prop = cls->find_property(L"Children");
            if (actor_children_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(actor_children_prop);
                auto children_val = PropertyReader::read_property(obj, fprop, 1);
                result["children"] = children_val;
            }

            // Class hierarchy (super chain)
            json class_chain = json::array();
            auto* s = reinterpret_cast<API::UStruct*>(cls);
            while (s) {
                auto* sfname = s->get_fname();
                if (sfname) class_chain.push_back(JsonHelpers::fname_to_string(sfname));
                s = s->get_super();
            }
            result["classHierarchy"] = class_chain;

            return result;
        }, 10000);

        send_json(res, result);
    });
}

} // namespace WorldRoutes
