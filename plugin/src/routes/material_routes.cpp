#include "material_routes.h"
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

namespace MaterialRoutes {

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
    // POST /api/material/create_dynamic — Create a dynamic material instance
    server.Post("/api/material/create_dynamic", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto source_str = body.value("sourceMaterial", "");
        auto outer_str = body.value("outer", "");

        if (source_str.empty()) {
            send_json(res, json{{"error", "Missing 'sourceMaterial' address"}}, 400);
            return;
        }

        PipeServer::get().log("Material: create_dynamic from " + source_str);

        auto result = GameThreadQueue::get().submit_and_wait([source_str, outer_str]() -> json {
            auto& api = API::get();

            auto source_addr = JsonHelpers::string_to_address(source_str);
            if (source_addr == 0) {
                return json{{"error", "Invalid sourceMaterial address"}};
            }

            auto* source_obj = reinterpret_cast<API::UObject*>(source_addr);
            if (!API::UObjectHook::exists(source_obj)) {
                return json{{"error", "Source material object no longer valid"}};
            }

            // Find KismetMaterialLibrary class for CreateDynamicMaterialInstance
            auto* lib_cls = api->find_uobject<API::UClass>(L"Class /Script/Engine.KismetMaterialLibrary");
            if (!lib_cls) {
                return json{{"error", "KismetMaterialLibrary not found"}};
            }

            auto* func = lib_cls->find_function(L"CreateDynamicMaterialInstance");
            if (!func) {
                return json{{"error", "CreateDynamicMaterialInstance function not found"}};
            }

            // Get a world context object (pawn or player controller)
            API::UObject* context = api->get_local_pawn(0);
            if (!context) {
                context = api->get_player_controller(0);
            }
            if (!context) {
                return json{{"error", "No world context object available (no pawn or player controller)"}};
            }

            // Build args for CreateDynamicMaterialInstance(WorldContextObject, SourceMaterial, OptionalName, CreationFlags)
            json args;
            args["WorldContextObject"] = JsonHelpers::address_to_string(context);
            args["SourceMaterial"] = source_str;

            auto* cdo = lib_cls->get_class_default_object();
            if (!cdo) {
                return json{{"error", "Could not get KismetMaterialLibrary CDO"}};
            }

            auto invoke_result = FunctionCaller::invoke_function(
                reinterpret_cast<uintptr_t>(cdo), "CreateDynamicMaterialInstance", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            // Extract the return value (the new MID address)
            json response;
            response["success"] = true;

            if (invoke_result.contains("returnValue") && invoke_result["returnValue"].contains("address")) {
                response["address"] = invoke_result["returnValue"]["address"];
                response["class"] = "MaterialInstanceDynamic";
            } else if (invoke_result.contains("ReturnValue") && invoke_result["ReturnValue"].contains("address")) {
                response["address"] = invoke_result["ReturnValue"]["address"];
                response["class"] = "MaterialInstanceDynamic";
            } else {
                // Return the full invoke result so the caller can inspect it
                response["invokeResult"] = invoke_result;
                response["class"] = "MaterialInstanceDynamic";
            }

            return response;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/material/set_scalar — Set a scalar material parameter
    server.Post("/api/material/set_scalar", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        auto param_name = body.value("paramName", "");

        if (addr_str.empty() || param_name.empty() || !body.contains("value")) {
            send_json(res, json{{"error", "Missing 'address', 'paramName', or 'value'"}}, 400);
            return;
        }

        auto value = body["value"];
        PipeServer::get().log("Material: set_scalar " + param_name + " on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([addr_str, param_name, value]() -> json {
            auto address = JsonHelpers::string_to_address(addr_str);
            if (address == 0) {
                return json{{"error", "Invalid address"}};
            }

            auto* obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            json args;
            args["ParameterName"] = param_name;
            args["Value"] = value;

            auto invoke_result = FunctionCaller::invoke_function(address, "SetScalarParameterValue", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            return json{{"success", true}, {"paramName", param_name}, {"value", value}};
        });

        send_json(res, result);
    });

    // POST /api/material/set_vector — Set a vector material parameter
    server.Post("/api/material/set_vector", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        auto param_name = body.value("paramName", "");

        if (addr_str.empty() || param_name.empty() || !body.contains("value")) {
            send_json(res, json{{"error", "Missing 'address', 'paramName', or 'value'"}}, 400);
            return;
        }

        auto color_val = body["value"];
        PipeServer::get().log("Material: set_vector " + param_name + " on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([addr_str, param_name, color_val]() -> json {
            auto address = JsonHelpers::string_to_address(addr_str);
            if (address == 0) {
                return json{{"error", "Invalid address"}};
            }

            auto* obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            // Build the FLinearColor value for the parameter
            json linear_color;
            linear_color["R"] = color_val.value("r", color_val.value("R", 0.0f));
            linear_color["G"] = color_val.value("g", color_val.value("G", 0.0f));
            linear_color["B"] = color_val.value("b", color_val.value("B", 0.0f));
            linear_color["A"] = color_val.value("a", color_val.value("A", 1.0f));

            json args;
            args["ParameterName"] = param_name;
            args["Value"] = linear_color;

            auto invoke_result = FunctionCaller::invoke_function(address, "SetVectorParameterValue", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            return json{{"success", true}, {"paramName", param_name}, {"value", linear_color}};
        });

        send_json(res, result);
    });

    // GET /api/material/params — Get material parameter names
    server.Get("/api/material/params", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        if (addr_str.empty()) {
            send_json(res, json{{"error", "Missing 'address' parameter"}}, 400);
            return;
        }

        PipeServer::get().log("Material: get params for " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([addr_str]() -> json {
            auto address = JsonHelpers::string_to_address(addr_str);
            if (address == 0) {
                return json{{"error", "Invalid address"}};
            }

            auto* obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object no longer valid"}};
            }

            auto* cls = obj->get_class();
            if (!cls) {
                return json{{"error", "Object has no class"}};
            }

            json response;
            response["address"] = addr_str;
            response["class"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());

            // Try to read ScalarParameterValues, VectorParameterValues, TextureParameterValues arrays
            // These are properties on MaterialInstance classes
            json scalar_params = json::array();
            json vector_params = json::array();
            json texture_params = json::array();

            // Read ScalarParameterValues array
            auto* scalar_prop = cls->find_property(L"ScalarParameterValues");
            if (scalar_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(scalar_prop);
                try {
                    auto val = PropertyReader::read_property(obj, fprop, 3);
                    if (val.is_array()) {
                        for (const auto& entry : val) {
                            json param;
                            if (entry.contains("ParameterInfo") && entry["ParameterInfo"].contains("Name")) {
                                param["name"] = entry["ParameterInfo"]["Name"];
                            } else if (entry.contains("ParameterName")) {
                                param["name"] = entry["ParameterName"];
                            }
                            if (entry.contains("ParameterValue")) {
                                param["value"] = entry["ParameterValue"];
                            }
                            scalar_params.push_back(param);
                        }
                    }
                } catch (...) {}
            }

            // Read VectorParameterValues array
            auto* vector_prop = cls->find_property(L"VectorParameterValues");
            if (vector_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(vector_prop);
                try {
                    auto val = PropertyReader::read_property(obj, fprop, 3);
                    if (val.is_array()) {
                        for (const auto& entry : val) {
                            json param;
                            if (entry.contains("ParameterInfo") && entry["ParameterInfo"].contains("Name")) {
                                param["name"] = entry["ParameterInfo"]["Name"];
                            } else if (entry.contains("ParameterName")) {
                                param["name"] = entry["ParameterName"];
                            }
                            if (entry.contains("ParameterValue")) {
                                param["value"] = entry["ParameterValue"];
                            }
                            vector_params.push_back(param);
                        }
                    }
                } catch (...) {}
            }

            // Read TextureParameterValues array
            auto* texture_prop = cls->find_property(L"TextureParameterValues");
            if (texture_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(texture_prop);
                try {
                    auto val = PropertyReader::read_property(obj, fprop, 3);
                    if (val.is_array()) {
                        for (const auto& entry : val) {
                            json param;
                            if (entry.contains("ParameterInfo") && entry["ParameterInfo"].contains("Name")) {
                                param["name"] = entry["ParameterInfo"]["Name"];
                            } else if (entry.contains("ParameterName")) {
                                param["name"] = entry["ParameterName"];
                            }
                            if (entry.contains("ParameterValue")) {
                                param["value"] = entry["ParameterValue"];
                            }
                            texture_params.push_back(param);
                        }
                    }
                } catch (...) {}
            }

            response["scalarParams"] = scalar_params;
            response["vectorParams"] = vector_params;
            response["textureParams"] = texture_params;

            // If no parameter arrays found, fall back to inspecting the object
            if (scalar_params.empty() && vector_params.empty() && texture_params.empty()) {
                auto inspection = ObjectExplorer::inspect_object(address, 2);
                response["note"] = "No parameter value arrays found on this material. Inspect result included for reference.";
                response["inspection"] = inspection;
            }

            return response;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/material/set_on_actor — Set material on an actor's mesh component
    server.Post("/api/material/set_on_actor", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto actor_str = body.value("actorAddress", "");
        auto material_str = body.value("materialAddress", "");
        int index = body.value("index", 0);

        if (actor_str.empty() || material_str.empty()) {
            send_json(res, json{{"error", "Missing 'actorAddress' or 'materialAddress'"}}, 400);
            return;
        }

        PipeServer::get().log("Material: set_on_actor " + material_str + " on " + actor_str + " index " + std::to_string(index));

        auto result = GameThreadQueue::get().submit_and_wait([actor_str, material_str, index]() -> json {
            auto actor_addr = JsonHelpers::string_to_address(actor_str);
            auto material_addr = JsonHelpers::string_to_address(material_str);

            if (actor_addr == 0 || material_addr == 0) {
                return json{{"error", "Invalid address"}};
            }

            auto* actor = reinterpret_cast<API::UObject*>(actor_addr);
            if (!API::UObjectHook::exists(actor)) {
                return json{{"error", "Actor no longer valid"}};
            }

            auto* material = reinterpret_cast<API::UObject*>(material_addr);
            if (!API::UObjectHook::exists(material)) {
                return json{{"error", "Material no longer valid"}};
            }

            auto* actor_cls = actor->get_class();
            if (!actor_cls) {
                return json{{"error", "Actor has no class"}};
            }

            // Try to find a mesh component on the actor
            // Check for common mesh component property names
            const wchar_t* mesh_prop_names[] = {
                L"Mesh", L"MeshComponent", L"SkeletalMeshComponent",
                L"StaticMeshComponent", L"MeshComp", nullptr
            };

            API::UObject* mesh_comp = nullptr;
            std::string mesh_comp_name;

            for (int i = 0; mesh_prop_names[i] != nullptr; ++i) {
                auto* prop = actor_cls->find_property(mesh_prop_names[i]);
                if (prop) {
                    auto* fprop = reinterpret_cast<API::FProperty*>(prop);
                    auto val = PropertyReader::read_property(actor, fprop, 0);
                    if (val.is_object() && val.contains("address")) {
                        auto comp_addr = JsonHelpers::string_to_address(val["address"].get<std::string>());
                        if (comp_addr != 0) {
                            auto* comp = reinterpret_cast<API::UObject*>(comp_addr);
                            if (API::UObjectHook::exists(comp)) {
                                mesh_comp = comp;
                                mesh_comp_name = JsonHelpers::wide_to_utf8(mesh_prop_names[i]);
                                break;
                            }
                        }
                    }
                }
            }

            if (!mesh_comp) {
                return json{{"error", "No mesh component found on actor. Try inspecting the actor to find the correct mesh component address."}};
            }

            // Call SetMaterial(ElementIndex, Material) on the mesh component
            json args;
            args["ElementIndex"] = index;
            args["Material"] = material_str;

            auto invoke_result = FunctionCaller::invoke_function(
                reinterpret_cast<uintptr_t>(mesh_comp), "SetMaterial", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            return json{
                {"success", true},
                {"meshComponent", JsonHelpers::address_to_string(mesh_comp)},
                {"meshPropertyName", mesh_comp_name},
                {"materialIndex", index}
            };
        }, 10000);

        send_json(res, result);
    });
}

} // namespace MaterialRoutes
