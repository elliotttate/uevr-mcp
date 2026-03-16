#include "animation_routes.h"
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

namespace AnimationRoutes {

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (data.contains("error") && status == 200) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

// Helper: extract AnimInstance address from a SkeletalMeshComponent address
static json get_anim_instance(uintptr_t mesh_addr) {
    auto* mesh_obj = reinterpret_cast<API::UObject*>(mesh_addr);
    if (!API::UObjectHook::exists(mesh_obj)) {
        return json{{"error", "Mesh object no longer valid"}};
    }

    auto* mesh_cls = mesh_obj->get_class();
    if (!mesh_cls) {
        return json{{"error", "Mesh object has no class"}};
    }

    auto* prop = mesh_cls->find_property(L"AnimScriptInstance");
    if (!prop) {
        return json{{"error", "No AnimScriptInstance property — is this a SkeletalMeshComponent?"}};
    }

    auto* fprop = reinterpret_cast<API::FProperty*>(prop);
    auto anim_val = PropertyReader::read_property(mesh_obj, fprop, 0);

    if (anim_val.is_null()) {
        return json{{"error", "AnimInstance is null"}};
    }

    if (!anim_val.is_object() || !anim_val.contains("address")) {
        return json{{"error", "AnimInstance property did not return an address"}};
    }

    auto anim_addr = JsonHelpers::string_to_address(anim_val["address"].get<std::string>());
    if (anim_addr == 0) {
        return json{{"error", "AnimInstance address is null"}};
    }

    auto* anim_obj = reinterpret_cast<API::UObject*>(anim_addr);
    if (!API::UObjectHook::exists(anim_obj)) {
        return json{{"error", "AnimInstance object no longer valid"}};
    }

    return json{{"address", JsonHelpers::address_to_string(anim_addr)}};
}

void register_routes(httplib::Server& server) {
    // POST /api/animation/play_montage — Play an animation montage
    server.Post("/api/animation/play_montage", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto mesh_str = body.value("meshAddress", "");
        auto montage_str = body.value("montageAddress", "");
        float rate = body.value("rate", 1.0f);
        auto start_section = body.value("startSection", "");

        if (mesh_str.empty() || montage_str.empty()) {
            send_json(res, json{{"error", "Missing 'meshAddress' or 'montageAddress'"}}, 400);
            return;
        }

        PipeServer::get().log("Animation: play_montage on " + mesh_str);

        auto result = GameThreadQueue::get().submit_and_wait([mesh_str, montage_str, rate, start_section]() -> json {
            auto mesh_addr = JsonHelpers::string_to_address(mesh_str);
            if (mesh_addr == 0) {
                return json{{"error", "Invalid meshAddress"}};
            }

            auto anim_result = get_anim_instance(mesh_addr);
            if (anim_result.contains("error")) {
                return anim_result;
            }

            auto anim_addr = JsonHelpers::string_to_address(anim_result["address"].get<std::string>());

            // Verify montage object
            auto montage_addr = JsonHelpers::string_to_address(montage_str);
            if (montage_addr == 0) {
                return json{{"error", "Invalid montageAddress"}};
            }
            auto* montage_obj = reinterpret_cast<API::UObject*>(montage_addr);
            if (!API::UObjectHook::exists(montage_obj)) {
                return json{{"error", "Montage object no longer valid"}};
            }

            // Call Montage_Play(MontageToPlay, InPlayRate, ReturnValueType, InTimeToStartMontageAt, bStopAllMontages)
            json args;
            args["MontageToPlay"] = montage_str;
            args["InPlayRate"] = rate;

            auto invoke_result = FunctionCaller::invoke_function(anim_addr, "Montage_Play", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            json response;
            response["success"] = true;
            response["animInstance"] = JsonHelpers::address_to_string(anim_addr);

            // Extract play length from return value if available
            if (invoke_result.contains("returnValue")) {
                response["playLength"] = invoke_result["returnValue"];
            } else if (invoke_result.contains("ReturnValue")) {
                response["playLength"] = invoke_result["ReturnValue"];
            }

            // If a start section was specified, jump to it
            if (!start_section.empty()) {
                json section_args;
                section_args["MontageToPlay"] = montage_str;
                section_args["SectionName"] = start_section;
                auto section_result = FunctionCaller::invoke_function(anim_addr, "Montage_JumpToSection", section_args);
                if (section_result.contains("error")) {
                    response["sectionWarning"] = "Could not jump to section: " + section_result["error"].get<std::string>();
                } else {
                    response["startSection"] = start_section;
                }
            }

            return response;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/animation/stop_montage — Stop a playing montage
    server.Post("/api/animation/stop_montage", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto mesh_str = body.value("meshAddress", "");
        float blend_out_time = body.value("blendOutTime", 0.25f);

        if (mesh_str.empty()) {
            send_json(res, json{{"error", "Missing 'meshAddress'"}}, 400);
            return;
        }

        PipeServer::get().log("Animation: stop_montage on " + mesh_str);

        auto result = GameThreadQueue::get().submit_and_wait([mesh_str, blend_out_time]() -> json {
            auto mesh_addr = JsonHelpers::string_to_address(mesh_str);
            if (mesh_addr == 0) {
                return json{{"error", "Invalid meshAddress"}};
            }

            auto anim_result = get_anim_instance(mesh_addr);
            if (anim_result.contains("error")) {
                return anim_result;
            }

            auto anim_addr = JsonHelpers::string_to_address(anim_result["address"].get<std::string>());

            // Call Montage_Stop with blend out time
            // Montage_Stop(InBlendOutTime, InMontage = nullptr)
            json args;
            args["InBlendOutTime"] = blend_out_time;

            auto invoke_result = FunctionCaller::invoke_function(anim_addr, "Montage_Stop", args);

            if (invoke_result.contains("error")) {
                return invoke_result;
            }

            return json{
                {"success", true},
                {"animInstance", JsonHelpers::address_to_string(anim_addr)},
                {"blendOutTime", blend_out_time}
            };
        });

        send_json(res, result);
    });

    // GET /api/animation/state — Get animation state info
    server.Get("/api/animation/state", [](const httplib::Request& req, httplib::Response& res) {
        auto mesh_str = req.get_param_value("meshAddress");
        if (mesh_str.empty()) {
            send_json(res, json{{"error", "Missing 'meshAddress' parameter"}}, 400);
            return;
        }

        PipeServer::get().log("Animation: get state for " + mesh_str);

        auto result = GameThreadQueue::get().submit_and_wait([mesh_str]() -> json {
            auto mesh_addr = JsonHelpers::string_to_address(mesh_str);
            if (mesh_addr == 0) {
                return json{{"error", "Invalid meshAddress"}};
            }

            auto anim_result = get_anim_instance(mesh_addr);
            if (anim_result.contains("error")) {
                return anim_result;
            }

            auto anim_addr_str = anim_result["address"].get<std::string>();
            auto anim_addr = JsonHelpers::string_to_address(anim_addr_str);
            auto* anim_obj = reinterpret_cast<API::UObject*>(anim_addr);
            auto* anim_cls = anim_obj->get_class();

            json response;
            response["animInstance"] = anim_addr_str;

            if (anim_cls) {
                auto* fname = anim_cls->get_fname();
                if (fname) {
                    response["animInstanceClass"] = JsonHelpers::fname_to_string(fname);
                }
            }

            // Try calling IsAnyMontagePlaying
            auto is_playing_result = FunctionCaller::call_getter(anim_addr, "IsAnyMontagePlaying");
            if (!is_playing_result.contains("error")) {
                if (is_playing_result.contains("returnValue")) {
                    response["isAnyMontagePlaying"] = is_playing_result["returnValue"];
                } else if (is_playing_result.contains("ReturnValue")) {
                    response["isAnyMontagePlaying"] = is_playing_result["ReturnValue"];
                }
            }

            // Try calling GetCurrentMontage
            auto current_montage_result = FunctionCaller::call_getter(anim_addr, "GetCurrentMontage");
            if (!current_montage_result.contains("error")) {
                if (current_montage_result.contains("returnValue")) {
                    response["currentMontage"] = current_montage_result["returnValue"];
                } else if (current_montage_result.contains("ReturnValue")) {
                    response["currentMontage"] = current_montage_result["ReturnValue"];
                }
            }

            // Read common animation variables from the AnimInstance
            // Collect float and bool properties that look like animation variables
            if (anim_cls) {
                json variables = json::object();
                int var_count = 0;
                const int max_vars = 50;

                for (auto* prop = anim_cls->get_child_properties(); prop && var_count < max_vars; prop = prop->get_next()) {
                    auto* fprop = reinterpret_cast<API::FProperty*>(prop);
                    auto* fclass = fprop->get_class();
                    if (!fclass) continue;

                    auto type_name = JsonHelpers::wide_to_utf8(fclass->get_fname()->to_string());
                    auto field_name = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());

                    // Only include simple numeric and boolean properties
                    if (type_name == "FloatProperty" || type_name == "DoubleProperty" ||
                        type_name == "BoolProperty" || type_name == "IntProperty" ||
                        type_name == "ByteProperty") {
                        try {
                            json val;
                            if (type_name == "BoolProperty") {
                                val = reinterpret_cast<API::FBoolProperty*>(fprop)->get_value_from_object(anim_obj);
                            } else {
                                val = PropertyReader::read_property(anim_obj, fprop, 0);
                            }
                            variables[field_name] = val;
                            var_count++;
                        } catch (...) {}
                    }
                }
                response["variables"] = variables;
            }

            return response;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/animation/set_variable — Set an animation variable on the AnimInstance
    server.Post("/api/animation/set_variable", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto mesh_str = body.value("meshAddress", "");
        auto var_name = body.value("varName", "");

        if (mesh_str.empty() || var_name.empty() || !body.contains("value")) {
            send_json(res, json{{"error", "Missing 'meshAddress', 'varName', or 'value'"}}, 400);
            return;
        }

        auto value = body["value"];
        PipeServer::get().log("Animation: set_variable " + var_name + " on " + mesh_str);

        auto result = GameThreadQueue::get().submit_and_wait([mesh_str, var_name, value]() -> json {
            auto mesh_addr = JsonHelpers::string_to_address(mesh_str);
            if (mesh_addr == 0) {
                return json{{"error", "Invalid meshAddress"}};
            }

            auto anim_result = get_anim_instance(mesh_addr);
            if (anim_result.contains("error")) {
                return anim_result;
            }

            auto anim_addr = JsonHelpers::string_to_address(anim_result["address"].get<std::string>());
            auto* anim_obj = reinterpret_cast<API::UObject*>(anim_addr);
            auto* anim_cls = anim_obj->get_class();

            if (!anim_cls) {
                return json{{"error", "AnimInstance has no class"}};
            }

            auto wname = JsonHelpers::utf8_to_wide(var_name);
            auto* prop = anim_cls->find_property(wname.c_str());
            if (!prop) {
                return json{{"error", "Variable not found on AnimInstance: " + var_name}};
            }

            auto* fprop = reinterpret_cast<API::FProperty*>(prop);
            std::string error;
            if (PropertyWriter::write_property(anim_obj, fprop, value, error)) {
                // Read back the new value
                json new_val;
                auto* fclass = fprop->get_class();
                auto type_name = JsonHelpers::wide_to_utf8(fclass->get_fname()->to_string());
                if (type_name == "BoolProperty") {
                    new_val = reinterpret_cast<API::FBoolProperty*>(fprop)->get_value_from_object(anim_obj);
                } else {
                    new_val = PropertyReader::read_property(anim_obj, fprop, 0);
                }

                return json{
                    {"success", true},
                    {"varName", var_name},
                    {"newValue", new_val},
                    {"animInstance", JsonHelpers::address_to_string(anim_addr)}
                };
            }

            return json{{"error", error}};
        });

        send_json(res, result);
    });

    // GET /api/animation/montages — List available AnimMontage objects
    server.Get("/api/animation/montages", [](const httplib::Request& req, httplib::Response& res) {
        auto filter = req.get_param_value("filter");
        int limit = 50;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        PipeServer::get().log("Animation: list montages" + (filter.empty() ? "" : " filter=" + filter));

        auto result = GameThreadQueue::get().submit_and_wait([filter, limit]() -> json {
            // Search for AnimMontage objects
            std::string search_query = filter.empty() ? "AnimMontage" : filter;
            auto search_result = ObjectExplorer::find_objects_by_class("AnimMontage", limit);

            if (search_result.contains("error")) {
                // Fallback: search by name
                search_result = ObjectExplorer::search_objects("AnimMontage", limit);
            }

            // If we have results and a filter, apply it
            if (!filter.empty() && search_result.contains("objects") && search_result["objects"].is_array()) {
                json filtered = json::array();
                for (const auto& obj : search_result["objects"]) {
                    std::string name = "";
                    if (obj.contains("fullName")) {
                        name = obj["fullName"].get<std::string>();
                    } else if (obj.contains("name")) {
                        name = obj["name"].get<std::string>();
                    }
                    // Case-insensitive substring match
                    std::string name_lower = name;
                    std::string filter_lower = filter;
                    std::transform(name_lower.begin(), name_lower.end(), name_lower.begin(), ::tolower);
                    std::transform(filter_lower.begin(), filter_lower.end(), filter_lower.begin(), ::tolower);
                    if (name_lower.find(filter_lower) != std::string::npos) {
                        filtered.push_back(obj);
                    }
                }

                json response;
                response["montages"] = filtered;
                response["count"] = filtered.size();
                response["filter"] = filter;
                return response;
            }

            // Rename the key from "objects" to "montages" if present
            json response;
            if (search_result.contains("objects")) {
                response["montages"] = search_result["objects"];
                response["count"] = search_result["objects"].size();
            } else {
                response["montages"] = json::array();
                response["count"] = 0;
                if (search_result.contains("error")) {
                    response["error"] = search_result["error"];
                }
            }
            if (!filter.empty()) {
                response["filter"] = filter;
            }
            return response;
        }, 10000);

        send_json(res, result);
    });
}

} // namespace AnimationRoutes
