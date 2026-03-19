#include "vr_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../reflection/object_explorer.h"
#include "../reflection/property_reader.h"
#include "../reflection/property_writer.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

#include <sstream>
#include <string>

using json = nlohmann::json;
using VR = uevr::API::VR;

namespace VrRoutes {

static json pose_to_json(const VR::Pose& pose) {
    return json{
        {"position", {{"x", pose.position.x}, {"y", pose.position.y}, {"z", pose.position.z}}},
        {"rotation", {{"x", pose.rotation.x}, {"y", pose.rotation.y}, {"z", pose.rotation.z}, {"w", pose.rotation.w}}}
    };
}

void register_routes(httplib::Server& server) {
    // GET /api/vr/status — VR runtime state
    server.Get("/api/vr/status", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        json result;
        result["runtimeReady"] = VR::is_runtime_ready();
        result["isOpenVR"] = VR::is_openvr();
        result["isOpenXR"] = VR::is_openxr();
        result["hmdActive"] = VR::is_hmd_active();
        result["usingControllers"] = VR::is_using_contriollers(); // Note: typo in UEVR API

        auto w = VR::get_hmd_width();
        auto h = VR::get_hmd_height();
        result["resolution"] = {{"width", w}, {"height", h}};

        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/vr/poses — Device poses
    server.Get("/api/vr/poses", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        json result;

        // HMD
        auto hmd_idx = VR::get_hmd_index();
        result["hmd"] = pose_to_json(VR::get_pose(hmd_idx));

        // Left controller
        auto left_idx = VR::get_left_controller_index();
        result["leftController"]["grip"] = pose_to_json(VR::get_grip_pose(left_idx));
        result["leftController"]["aim"] = pose_to_json(VR::get_aim_pose(left_idx));

        // Right controller
        auto right_idx = VR::get_right_controller_index();
        result["rightController"]["grip"] = pose_to_json(VR::get_grip_pose(right_idx));
        result["rightController"]["aim"] = pose_to_json(VR::get_aim_pose(right_idx));

        // Standing origin
        auto origin = VR::get_standing_origin();
        result["standingOrigin"] = {{"x", origin.x}, {"y", origin.y}, {"z", origin.z}};

        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/vr/settings — Current VR settings
    server.Get("/api/vr/settings", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        json result;
        result["snapTurnEnabled"] = VR::is_snap_turn_enabled();
        result["decoupledPitchEnabled"] = VR::is_decoupled_pitch_enabled();
        result["aimMethod"] = (int)VR::get_aim_method();
        result["aimAllowed"] = VR::is_aim_allowed();

        res.set_content(result.dump(2), "application/json");
    });

    // POST /api/vr/settings — Set VR setting
    server.Post("/api/vr/settings", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            res.status = 400;
            res.set_content(json{{"error", "Invalid JSON"}}.dump(), "application/json");
            return;
        }

        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        json result;

        if (body.contains("snapTurnEnabled")) {
            VR::set_snap_turn_enabled(body["snapTurnEnabled"].get<bool>());
            result["snapTurnEnabled"] = VR::is_snap_turn_enabled();
        }
        if (body.contains("decoupledPitchEnabled")) {
            VR::set_decoupled_pitch_enabled(body["decoupledPitchEnabled"].get<bool>());
            result["decoupledPitchEnabled"] = VR::is_decoupled_pitch_enabled();
        }
        if (body.contains("aimMethod")) {
            VR::set_aim_method((VR::AimMethod)body["aimMethod"].get<int>());
            result["aimMethod"] = (int)VR::get_aim_method();
        }
        if (body.contains("aimAllowed")) {
            VR::set_aim_allowed(body["aimAllowed"].get<bool>());
            result["aimAllowed"] = VR::is_aim_allowed();
        }

        // Generic set_mod_value for arbitrary keys (bypass C++ template, use C API directly)
        if (body.contains("key") && body.contains("value")) {
            auto key = body["key"].get<std::string>();
            auto val = body["value"].get<std::string>();
            auto fn = api->param()->vr->set_mod_value;
            fn(key.c_str(), val.c_str());
            result["modValue"] = {{"key", key}, {"set", val}};
        }

        PipeServer::get().log("VR: settings changed");
        result["success"] = true;
        res.set_content(result.dump(2), "application/json");
    });

    // POST /api/vr/recenter — Recenter view
    server.Post("/api/vr/recenter", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        VR::recenter_view();
        PipeServer::get().log("VR: recentered");
        res.set_content(json{{"success", true}}.dump(2), "application/json");
    });

    // POST /api/vr/haptics — Trigger controller vibration
    server.Post("/api/vr/haptics", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            res.status = 400;
            res.set_content(json{{"error", "Invalid JSON"}}.dump(), "application/json");
            return;
        }

        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        float duration = body.value("duration", 0.1f);
        float amplitude = body.value("amplitude", 0.5f);
        float frequency = body.value("frequency", 1.0f);
        std::string hand = body.value("hand", "right");

        UEVR_InputSourceHandle source;
        if (hand == "left") {
            source = VR::get_left_joystick_source();
        } else {
            source = VR::get_right_joystick_source();
        }

        VR::trigger_haptic_vibration(0.0f, amplitude, frequency, duration, source);
        PipeServer::get().log("VR: haptics " + hand + " a=" + std::to_string(amplitude));
        res.set_content(json{{"success", true}, {"hand", hand}}.dump(2), "application/json");
    });

    // POST /api/vr/config/save
    server.Post("/api/vr/config/save", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        VR::save_config();
        PipeServer::get().log("VR: config saved");
        res.set_content(json{{"success", true}}.dump(2), "application/json");
    });

    // POST /api/vr/config/reload
    server.Post("/api/vr/config/reload", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        VR::reload_config();
        PipeServer::get().log("VR: config reloaded");
        res.set_content(json{{"success", true}}.dump(2), "application/json");
    });

    // GET /api/vr/input — Get controller input state (joystick axes, action states)
    server.Get("/api/vr/input", [](const httplib::Request& req, httplib::Response& res) {
        auto& api = uevr::API::get();
        if (!api) {
            res.status = 500;
            res.set_content(json{{"error", "API not available"}}.dump(), "application/json");
            return;
        }

        json result;
        result["usingControllers"] = VR::is_using_contriollers();

        // Joystick axes
        auto left_src = VR::get_left_joystick_source();
        auto right_src = VR::get_right_joystick_source();

        auto left_axis = VR::get_joystick_axis(left_src);
        auto right_axis = VR::get_joystick_axis(right_src);

        result["leftJoystick"] = {{"x", left_axis.x}, {"y", left_axis.y}};
        result["rightJoystick"] = {{"x", right_axis.x}, {"y", right_axis.y}};

        // Query common OpenXR action states if actions are provided
        if (req.has_param("actions")) {
            auto actions_str = req.get_param_value("actions");
            json action_states = json::object();

            // Split comma-separated action paths
            std::istringstream ss(actions_str);
            std::string action_path;
            while (std::getline(ss, action_path, ',')) {
                // Trim whitespace
                while (!action_path.empty() && action_path.front() == ' ') action_path.erase(0, 1);
                while (!action_path.empty() && action_path.back() == ' ') action_path.pop_back();

                if (action_path.empty()) continue;

                auto handle = VR::get_action_handle(action_path);
                if (handle) {
                    bool left_active = VR::is_action_active(handle, left_src);
                    bool right_active = VR::is_action_active(handle, right_src);
                    action_states[action_path] = {
                        {"left", left_active},
                        {"right", right_active},
                        {"any", left_active || right_active}
                    };
                } else {
                    action_states[action_path] = {{"error", "action not found"}};
                }
            }
            result["actions"] = action_states;
        }

        // Additional info
        result["movementOrientation"] = VR::get_movement_orientation();
        result["lowestXInputIndex"] = VR::get_lowest_xinput_index();

        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/vr/world_scale — Get world-to-meters scale
    // The property is "WorldToMeters" on WorldSettings (not "WorldToMetersScale" on World).
    server.Get("/api/vr/world_scale", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            // Find WorldSettings class and get first instance
            auto* ws_cls = api->find_uobject<uevr::API::UClass>(L"Class /Script/Engine.WorldSettings");
            if (!ws_cls) return json{{"error", "Could not find WorldSettings class"}};

            auto* ws = uevr::API::UObjectHook::get_first_object_by_class(ws_cls);
            if (!ws) return json{{"error", "No WorldSettings instance found"}};

            auto* type = reinterpret_cast<uevr::API::UStruct*>(ws->get_class());
            if (!type) return json{{"error", "WorldSettings has no class"}};

            auto* prop = type->find_property(L"WorldToMeters");
            float scale = 100.0f; // UE default
            if (prop) {
                auto val = PropertyReader::read_property(ws, prop, 1);
                if (val.contains("value") && val["value"].is_number()) {
                    scale = val["value"].get<float>();
                }
            }

            return json{
                {"worldToMetersScale", scale},
                {"worldSettingsAddress", JsonHelpers::address_to_string(ws)}
            };
        });

        res.status = result.contains("error") ? 500 : 200;
        res.set_content(result.dump(2), "application/json");
    });

    // POST /api/vr/world_scale — Set world-to-meters scale
    server.Post("/api/vr/world_scale", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            res.status = 400;
            res.set_content(json{{"error", "Invalid JSON"}}.dump(), "application/json");
            return;
        }

        if (!body.contains("scale")) {
            res.status = 400;
            res.set_content(json{{"error", "Missing 'scale' parameter"}}.dump(), "application/json");
            return;
        }

        float new_scale = body["scale"].get<float>();
        if (new_scale <= 0.0f) {
            res.status = 400;
            res.set_content(json{{"error", "Scale must be positive"}}.dump(), "application/json");
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([new_scale]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            auto* ws_cls = api->find_uobject<uevr::API::UClass>(L"Class /Script/Engine.WorldSettings");
            if (!ws_cls) return json{{"error", "Could not find WorldSettings class"}};

            auto* ws = uevr::API::UObjectHook::get_first_object_by_class(ws_cls);
            if (!ws) return json{{"error", "No WorldSettings instance found"}};

            auto* type = reinterpret_cast<uevr::API::UStruct*>(ws->get_class());
            if (!type) return json{{"error", "WorldSettings has no class"}};

            auto* prop = type->find_property(L"WorldToMeters");
            if (!prop) return json{{"error", "WorldToMeters property not found on WorldSettings"}};

            std::string error;
            if (!PropertyWriter::write_property(ws, prop, json(new_scale), error)) {
                return json{{"error", error}};
            }

            return json{
                {"success", true},
                {"worldToMetersScale", new_scale},
                {"worldSettingsAddress", JsonHelpers::address_to_string(ws)}
            };
        });

        if (!result.contains("error")) {
            PipeServer::get().log("VR: world scale set to " + std::to_string(new_scale));
        }
        res.status = result.contains("error") ? 500 : 200;
        res.set_content(result.dump(2), "application/json");
    });
}

} // namespace VrRoutes
