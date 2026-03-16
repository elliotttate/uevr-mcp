#include "status_routes.h"
#include "../pipe_server.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../reflection/object_explorer.h"
#include "../reflection/property_reader.h"
#include "../reflection/property_writer.h"
#include "../reflection/function_caller.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>
#include <chrono>

using json = nlohmann::json;

namespace StatusRoutes {

static std::atomic<uint64_t> s_tick_count{0};

void increment_tick_count() {
    s_tick_count.fetch_add(1, std::memory_order_relaxed);
}

uint64_t get_tick_count() {
    return s_tick_count.load(std::memory_order_relaxed);
}

void register_routes(httplib::Server& server) {
    // GET /api — Plugin info and endpoint index
    server.Get("/api", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();

        json result;
        result["name"] = "UEVR-MCP Plugin";
        result["version"] = "1.0.0";

        if (api) {
            // Get process name
            char exe_path[MAX_PATH]{};
            GetModuleFileNameA(nullptr, exe_path, MAX_PATH);
            std::string exe(exe_path);
            auto slash = exe.find_last_of("\\/");
            result["process"] = (slash != std::string::npos) ? exe.substr(slash + 1) : exe;
        }

        result["endpoints"] = json::array({
            "/api", "/api/status", "/api/game_info", "/api/camera",
            "/api/explorer/search", "/api/explorer/classes", "/api/explorer/type",
            "/api/explorer/object", "/api/explorer/summary", "/api/explorer/field",
            "/api/explorer/method", "/api/explorer/objects_by_class",
            "/api/explorer/chain", "/api/explorer/singletons", "/api/explorer/singleton",
            "/api/explorer/array", "/api/explorer/memory", "/api/explorer/typed",
            "/api/explorer/batch",
            "/api/console/cvars", "/api/console/cvar", "/api/console/command",
            "/api/vr/status", "/api/vr/poses", "/api/vr/settings",
            "/api/vr/recenter", "/api/vr/haptics", "/api/vr/config/save", "/api/vr/config/reload",
            "/api/player", "/api/player/position", "/api/player/health",
            "/api/lua/exec", "/api/lua/reset", "/api/lua/state",
            "/api/lua/reload", "/api/lua/globals",
            "/api/lua/scripts/write", "/api/lua/scripts/list", "/api/lua/scripts/read", "/api/lua/scripts/delete",
            "/api/events",
            "/api/blueprint/spawn", "/api/blueprint/add_component",
            "/api/blueprint/cdo", "/api/blueprint/destroy",
            "/api/blueprint/set_transform", "/api/blueprint/spawned",
            "/api/screenshot", "/api/screenshot/info",
            "/api/watch/add", "/api/watch/remove", "/api/watch/list",
            "/api/watch/changes", "/api/watch/clear",
            "/api/watch/snapshot", "/api/watch/snapshots", "/api/watch/diff",
            "/api/world/actors", "/api/world/components",
            "/api/world/line_trace", "/api/world/sphere_overlap", "/api/world/hierarchy",
            "/api/input/key", "/api/input/mouse", "/api/input/gamepad", "/api/input/text",
            "/api/material/create_dynamic", "/api/material/set_scalar",
            "/api/material/set_vector", "/api/material/params", "/api/material/set_on_actor",
            "/api/animation/play_montage", "/api/animation/stop_montage",
            "/api/animation/state", "/api/animation/set_variable", "/api/animation/montages",
            "/api/physics/add_impulse", "/api/physics/add_force",
            "/api/physics/set_simulate", "/api/physics/set_gravity",
            "/api/physics/set_collision", "/api/physics/set_mass",
            "/api/asset/find", "/api/asset/search", "/api/asset/load",
            "/api/asset/classes", "/api/asset/load_class",
            "/api/hook/add", "/api/hook/remove", "/api/hook/list",
            "/api/hook/log", "/api/hook/clear",
            "/api/macro/save", "/api/macro/play", "/api/macro/list",
            "/api/macro/delete", "/api/macro/get",
            "/api/discovery/subclasses", "/api/discovery/names",
            "/api/discovery/delegates", "/api/discovery/vtable",
            "/api/discovery/pattern_scan", "/api/discovery/all_children"
        });

        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/status — Runtime status
    server.Get("/api/status", [](const httplib::Request&, httplib::Response& res) {
        auto& api = uevr::API::get();
        auto& pipe = PipeServer::get();

        auto elapsed = std::chrono::steady_clock::now() - pipe.start_time();
        auto seconds = std::chrono::duration_cast<std::chrono::seconds>(elapsed).count();

        json result;
        result["uptime_seconds"] = seconds;
        result["tick_count"] = get_tick_count();
        result["queue_depth"] = GameThreadQueue::get().pending_count();

        if (api) {
            auto vr = api->param()->vr;
            result["vr_runtime"] = vr->is_openvr() ? "OpenVR" : (vr->is_openxr() ? "OpenXR" : "Unknown");
            result["hmd_active"] = (bool)vr->is_hmd_active();
            result["runtime_ready"] = (bool)vr->is_runtime_ready();
        }

        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/player — Player controller and pawn info
    server.Get("/api/player", [](const httplib::Request&, httplib::Response& res) {
        auto& queue = GameThreadQueue::get();
        auto result = queue.submit_and_wait([]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            json result;

            auto controller = api->get_player_controller(0);
            if (controller) {
                result["controller"]["address"] = JsonHelpers::address_to_string(controller);
                auto cls = controller->get_class();
                if (cls) {
                    auto name = cls->get_fname();
                    if (name) result["controller"]["class"] = JsonHelpers::fname_to_string(name);
                }
            } else {
                result["controller"] = nullptr;
            }

            auto pawn = api->get_local_pawn(0);
            if (pawn) {
                result["pawn"]["address"] = JsonHelpers::address_to_string(pawn);
                auto cls = pawn->get_class();
                if (cls) {
                    auto name = cls->get_fname();
                    if (name) result["pawn"]["class"] = JsonHelpers::fname_to_string(name);
                }
            } else {
                result["pawn"] = nullptr;
            }

            return result;
        });

        if (result.contains("error")) {
            res.status = result["error"].get<std::string>().find("timeout") != std::string::npos ? 504 : 500;
        }
        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/camera — Camera position, rotation, FOV
    server.Get("/api/camera", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() -> json {
            return ObjectExplorer::get_camera();
        });

        if (result.contains("error")) {
            res.status = result["error"].get<std::string>().find("timeout") != std::string::npos ? 504 : 500;
        }
        res.set_content(result.dump(2), "application/json");
    });

    // GET /api/game_info — Game executable, directory, UEVR info
    server.Get("/api/game_info", [](const httplib::Request&, httplib::Response& res) {
        json result;

        char exe_path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, exe_path, MAX_PATH);
        std::string exe(exe_path);
        result["gamePath"] = exe;

        auto slash = exe.find_last_of("\\/");
        if (slash != std::string::npos) {
            result["gameDirectory"] = exe.substr(0, slash + 1);
            result["gameName"] = exe.substr(slash + 1);
        }

        auto& api = uevr::API::get();
        if (api) {
            auto vr = api->param()->vr;
            result["vrRuntime"] = vr->is_openvr() ? "OpenVR" : (vr->is_openxr() ? "OpenXR" : "Unknown");
            result["hmdActive"] = (bool)vr->is_hmd_active();
            result["runtimeReady"] = (bool)vr->is_runtime_ready();
        }

        auto& pipe = PipeServer::get();
        auto elapsed = std::chrono::steady_clock::now() - pipe.start_time();
        result["uptimeSeconds"] = std::chrono::duration_cast<std::chrono::seconds>(elapsed).count();
        result["httpPort"] = 8899;

        res.set_content(result.dump(2), "application/json");
    });

    // POST /api/player/position — Set player position (partial update OK)
    server.Post("/api/player/position", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            res.status = 400;
            res.set_content(json{{"error", "Invalid JSON"}}.dump(), "application/json");
            return;
        }

        PipeServer::get().log("Player: set position");

        auto result = GameThreadQueue::get().submit_and_wait([body]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            auto pawn = api->get_local_pawn(0);
            if (!pawn) return json{{"error", "No local pawn"}};

            auto cls = pawn->get_class();
            if (!cls) return json{{"error", "Pawn has no class"}};

            // Try K2_SetActorLocation (Blueprint-callable version of SetActorLocation)
            auto func = cls->find_function(L"K2_SetActorLocation");
            if (!func) func = cls->find_function(L"SetActorLocation");

            if (func) {
                // First, get current location via K2_GetActorLocation
                auto get_func = cls->find_function(L"K2_GetActorLocation");
                if (!get_func) get_func = cls->find_function(L"GetActorLocation");

                json current_pos = {{"x", 0.0}, {"y", 0.0}, {"z", 0.0}};
                if (get_func) {
                    auto ps = get_func->get_properties_size();
                    auto ma = get_func->get_min_alignment();
                    std::vector<uint8_t> params;
                    if (ma > 1) params.resize(((ps + ma - 1) / ma) * ma, 0);
                    else params.resize(ps, 0);

                    pawn->process_event(get_func, params.data());

                    for (auto p = get_func->get_child_properties(); p; p = p->get_next()) {
                        auto fp = reinterpret_cast<uevr::API::FProperty*>(p);
                        if (fp->is_return_param()) {
                            current_pos = PropertyReader::read_property(params.data(), fp, 2);
                            break;
                        }
                    }
                }

                // Apply partial update
                double x = body.contains("x") ? body["x"].get<double>() : current_pos.value("x", 0.0);
                double y = body.contains("y") ? body["y"].get<double>() : current_pos.value("y", 0.0);
                double z = body.contains("z") ? body["z"].get<double>() : current_pos.value("z", 0.0);

                // Invoke with the new location
                json args;
                args["NewLocation"] = {{"x", x}, {"y", y}, {"z", z}};
                args["bSweep"] = false;
                args["bTeleport"] = true;

                auto invoke_result = FunctionCaller::invoke_function(
                    reinterpret_cast<uintptr_t>(pawn),
                    "K2_SetActorLocation",
                    args
                );

                if (invoke_result.contains("error")) {
                    // Fallback: try TeleportTo
                    json tp_args;
                    tp_args["DestLocation"] = {{"x", x}, {"y", y}, {"z", z}};
                    invoke_result = FunctionCaller::invoke_function(
                        reinterpret_cast<uintptr_t>(pawn),
                        "K2_TeleportTo",
                        tp_args
                    );
                }

                return json{{"success", true}, {"position", {{"x", x}, {"y", y}, {"z", z}}}};
            }

            return json{{"error", "Could not find SetActorLocation or K2_SetActorLocation function"}};
        });

        if (result.contains("error")) res.status = 500;
        res.set_content(result.dump(2), "application/json");
    });

    // POST /api/player/health — Set player health
    server.Post("/api/player/health", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            res.status = 400;
            res.set_content(json{{"error", "Invalid JSON"}}.dump(), "application/json");
            return;
        }

        if (!body.contains("value")) {
            res.status = 400;
            res.set_content(json{{"error", "Missing 'value'"}}.dump(), "application/json");
            return;
        }

        auto value = body["value"];
        PipeServer::get().log("Player: set health = " + value.dump());

        auto result = GameThreadQueue::get().submit_and_wait([value]() -> json {
            auto& api = uevr::API::get();
            if (!api) return json{{"error", "API not available"}};

            auto pawn = api->get_local_pawn(0);
            if (!pawn) return json{{"error", "No local pawn"}};

            auto cls = pawn->get_class();
            if (!cls) return json{{"error", "Pawn has no class"}};

            // Try common health field names
            static const std::vector<std::wstring> health_fields = {
                L"Health", L"CurrentHealth", L"HP", L"CurrentHP",
                L"HealthComponent", L"HitPoints", L"CurrentHitPoints"
            };

            for (const auto& fname : health_fields) {
                auto prop = cls->find_property(fname.c_str());
                if (!prop) continue;

                auto fprop = reinterpret_cast<uevr::API::FProperty*>(prop);
                auto fclass = fprop->get_class();
                if (!fclass) continue;

                auto type_name = fclass->get_fname()->to_string();

                // Direct numeric field
                if (type_name == L"FloatProperty" || type_name == L"DoubleProperty" ||
                    type_name == L"IntProperty" || type_name == L"Int64Property") {
                    std::string err;
                    if (PropertyWriter::write_property(pawn, fprop, value, err)) {
                        auto new_val = PropertyReader::read_property(pawn, fprop, 0);
                        return json{{"success", true}, {"field", JsonHelpers::wide_to_utf8(fname)}, {"newValue", new_val}};
                    }
                    return json{{"error", "Failed to write: " + err}};
                }

                // ObjectProperty — might be a health component, try to find Health inside it
                if (type_name == L"ObjectProperty") {
                    auto comp = *reinterpret_cast<uevr::API::UObject**>(
                        reinterpret_cast<uintptr_t>(pawn) + fprop->get_offset());
                    if (!comp) continue;

                    auto comp_cls = comp->get_class();
                    if (!comp_cls) continue;

                    // Look for Health field on the component
                    auto inner_prop = comp_cls->find_property(L"Health");
                    if (!inner_prop) inner_prop = comp_cls->find_property(L"CurrentHealth");
                    if (!inner_prop) continue;

                    auto inner_fprop = reinterpret_cast<uevr::API::FProperty*>(inner_prop);
                    std::string err;
                    if (PropertyWriter::write_property(comp, inner_fprop, value, err)) {
                        auto new_val = PropertyReader::read_property(comp, inner_fprop, 0);
                        return json{{"success", true}, {"field", JsonHelpers::wide_to_utf8(fname) + ".Health"}, {"newValue", new_val}};
                    }
                    return json{{"error", "Failed to write component health: " + err}};
                }
            }

            return json{{"error", "No health field found. Use uevr_write_field to set a specific field manually."}};
        });

        if (result.contains("error")) res.status = 500;
        res.set_content(result.dump(2), "application/json");
    });
}

} // namespace StatusRoutes
