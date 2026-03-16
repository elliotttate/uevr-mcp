#include "physics_routes.h"
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

namespace PhysicsRoutes {

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (data.contains("error") && status == 200) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

// Validate an address points to a still-living UObject
static API::UObject* validate_object(uintptr_t address) {
    auto* obj = reinterpret_cast<API::UObject*>(address);
    if (!API::UObjectHook::exists(obj)) return nullptr;
    return obj;
}

// Try to invoke a function via FunctionCaller first, then fall back to direct property write
static json try_set_bool_property(uintptr_t address, const std::string& func_name,
                                   const std::string& param_name, bool value,
                                   const std::string& prop_name) {
    // Try function call first
    json args;
    args[param_name] = value;
    auto result = FunctionCaller::invoke_function(address, func_name, args);

    if (!result.contains("error")) {
        return json{{"success", true}, {"method", "function"}, {"function", func_name}};
    }

    // Fallback: direct property write
    auto* obj = reinterpret_cast<API::UObject*>(address);
    auto* cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    auto wprop = JsonHelpers::utf8_to_wide(prop_name);
    auto* prop = cls->find_property(wprop.c_str());
    if (!prop) {
        return json{{"error", "Neither function '" + func_name + "' nor property '" + prop_name + "' found"}};
    }

    auto* fprop = reinterpret_cast<API::FProperty*>(prop);
    std::string err;
    if (PropertyWriter::write_property(obj, fprop, value, err)) {
        auto new_val = PropertyReader::read_property(obj, fprop, 0);
        return json{{"success", true}, {"method", "property"}, {"property", prop_name}, {"newValue", new_val}};
    }

    return json{{"error", "Property write failed: " + err}};
}

void register_routes(httplib::Server& server) {

    // POST /api/physics/add_impulse — Add impulse to a primitive component
    server.Post("/api/physics/add_impulse", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty() || !body.contains("impulse")) {
            send_json(res, json{{"error", "Missing 'address' or 'impulse'"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        PipeServer::get().log("Physics: add_impulse on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, body]() -> json {
            auto* obj = validate_object(address);
            if (!obj) return json{{"error", "Object no longer valid"}};

            bool vel_change = body.value("velocityChange", false);
            std::string bone_name = body.value("boneName", "None");

            // Try AddImpulse(Impulse, BoneName, bVelChange)
            json args;
            args["Impulse"] = body["impulse"];
            args["BoneName"] = bone_name;
            args["bVelChange"] = vel_change;

            auto result = FunctionCaller::invoke_function(address, "AddImpulse", args);

            if (result.contains("error")) {
                // Try AddImpulseAtLocation if a location is provided
                if (body.contains("location")) {
                    json args2;
                    args2["Impulse"] = body["impulse"];
                    args2["Location"] = body["location"];
                    args2["BoneName"] = bone_name;
                    auto result2 = FunctionCaller::invoke_function(address, "AddImpulseAtLocation", args2);
                    if (!result2.contains("error")) {
                        return json{{"success", true}, {"function", "AddImpulseAtLocation"}};
                    }
                }
                return result;
            }

            return json{{"success", true}, {"function", "AddImpulse"}};
        });

        send_json(res, result);
    });

    // POST /api/physics/add_force — Add force to a primitive component
    server.Post("/api/physics/add_force", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty() || !body.contains("force")) {
            send_json(res, json{{"error", "Missing 'address' or 'force'"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        PipeServer::get().log("Physics: add_force on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, body]() -> json {
            auto* obj = validate_object(address);
            if (!obj) return json{{"error", "Object no longer valid"}};

            std::string bone_name = body.value("boneName", "None");
            bool accel_change = body.value("accelChange", false);

            // Try AddForce(Force, BoneName, bAccelChange)
            json args;
            args["Force"] = body["force"];
            args["BoneName"] = bone_name;
            args["bAccelChange"] = accel_change;

            auto result = FunctionCaller::invoke_function(address, "AddForce", args);

            if (result.contains("error")) {
                // Try AddForceAtLocation if a location is provided
                if (body.contains("location")) {
                    json args2;
                    args2["Force"] = body["force"];
                    args2["Location"] = body["location"];
                    args2["BoneName"] = bone_name;
                    auto result2 = FunctionCaller::invoke_function(address, "AddForceAtLocation", args2);
                    if (!result2.contains("error")) {
                        return json{{"success", true}, {"function", "AddForceAtLocation"}};
                    }
                }
                return result;
            }

            return json{{"success", true}, {"function", "AddForce"}};
        });

        send_json(res, result);
    });

    // POST /api/physics/set_simulate — Enable/disable physics simulation
    server.Post("/api/physics/set_simulate", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty() || !body.contains("simulate")) {
            send_json(res, json{{"error", "Missing 'address' or 'simulate'"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        bool simulate = body["simulate"].get<bool>();
        PipeServer::get().log("Physics: set_simulate=" + std::string(simulate ? "true" : "false") + " on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, simulate]() -> json {
            auto* obj = validate_object(address);
            if (!obj) return json{{"error", "Object no longer valid"}};

            auto r = try_set_bool_property(address, "SetSimulatePhysics", "bSimulate", simulate, "bSimulatePhysics");
            if (r.contains("success")) {
                r["simulating"] = simulate;
            }
            return r;
        });

        send_json(res, result);
    });

    // POST /api/physics/set_gravity — Enable/disable gravity on a component
    server.Post("/api/physics/set_gravity", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty() || !body.contains("enabled")) {
            send_json(res, json{{"error", "Missing 'address' or 'enabled'"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        bool enabled = body["enabled"].get<bool>();
        PipeServer::get().log("Physics: set_gravity=" + std::string(enabled ? "true" : "false") + " on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, enabled]() -> json {
            auto* obj = validate_object(address);
            if (!obj) return json{{"error", "Object no longer valid"}};

            auto r = try_set_bool_property(address, "SetEnableGravity", "bGravityEnabled", enabled, "bEnableGravity");
            if (r.contains("success")) {
                r["gravityEnabled"] = enabled;
            }
            return r;
        });

        send_json(res, result);
    });

    // POST /api/physics/set_collision — Enable/disable collision on a component
    server.Post("/api/physics/set_collision", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty() || !body.contains("enabled")) {
            send_json(res, json{{"error", "Missing 'address' or 'enabled'"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        bool enabled = body["enabled"].get<bool>();
        PipeServer::get().log("Physics: set_collision=" + std::string(enabled ? "true" : "false") + " on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, enabled]() -> json {
            auto* obj = validate_object(address);
            if (!obj) return json{{"error", "Object no longer valid"}};

            // SetCollisionEnabled takes an enum: ECollisionEnabled::Type
            // 0 = NoCollision, 1 = QueryOnly, 2 = PhysicsOnly, 3 = QueryAndPhysics
            int collision_type = enabled ? 3 : 0; // QueryAndPhysics or NoCollision

            // Try function call with enum value
            json args;
            args["NewType"] = collision_type;
            auto result = FunctionCaller::invoke_function(address, "SetCollisionEnabled", args);

            if (!result.contains("error")) {
                return json{{"success", true}, {"method", "function"}, {"collisionEnabled", enabled}, {"collisionType", collision_type}};
            }

            // Fallback: try writing the collision enabled property directly
            auto* obj_ptr = reinterpret_cast<API::UObject*>(address);
            auto* cls = obj_ptr->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            // Try writing CollisionEnabled property
            auto* prop = cls->find_property(L"CollisionEnabled");
            if (prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(prop);
                std::string err;
                if (PropertyWriter::write_property(obj_ptr, fprop, collision_type, err)) {
                    return json{{"success", true}, {"method", "property"}, {"collisionEnabled", enabled}, {"collisionType", collision_type}};
                }
                return json{{"error", "Property write failed: " + err}};
            }

            // Try bGenerateOverlapEvents as a minimal fallback
            auto* overlap_prop = cls->find_property(L"bGenerateOverlapEvents");
            if (overlap_prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(overlap_prop);
                std::string err;
                PropertyWriter::write_property(obj_ptr, fprop, enabled, err);
            }

            return json{{"error", "Neither SetCollisionEnabled function nor CollisionEnabled property found"}};
        });

        send_json(res, result);
    });

    // POST /api/physics/set_mass — Set mass override on a primitive component
    server.Post("/api/physics/set_mass", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto addr_str = body.value("address", "");
        if (addr_str.empty() || !body.contains("mass")) {
            send_json(res, json{{"error", "Missing 'address' or 'mass'"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        float mass = body["mass"].get<float>();
        std::string bone_name = body.value("boneName", "None");

        PipeServer::get().log("Physics: set_mass=" + std::to_string(mass) + " on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address, mass, bone_name]() -> json {
            auto* obj = validate_object(address);
            if (!obj) return json{{"error", "Object no longer valid"}};

            // Try SetMassOverrideInKg(BoneName, MassInKg, bOverrideMass)
            json args;
            args["BoneName"] = bone_name;
            args["MassInKg"] = mass;
            args["bOverrideMass"] = true;

            auto result = FunctionCaller::invoke_function(address, "SetMassOverrideInKg", args);

            if (!result.contains("error")) {
                return json{{"success", true}, {"method", "function"}, {"function", "SetMassOverrideInKg"}, {"mass", mass}};
            }

            // Fallback: try SetMassScale
            json args2;
            args2["InMassScale"] = mass;
            auto result2 = FunctionCaller::invoke_function(address, "SetMassScale", args2);
            if (!result2.contains("error")) {
                return json{{"success", true}, {"method", "function"}, {"function", "SetMassScale"}, {"massScale", mass}};
            }

            // Fallback: direct property write on MassInKgOverride
            auto* obj_ptr = reinterpret_cast<API::UObject*>(address);
            auto* cls = obj_ptr->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            auto* prop = cls->find_property(L"MassInKgOverride");
            if (prop) {
                auto* fprop = reinterpret_cast<API::FProperty*>(prop);
                std::string err;
                if (PropertyWriter::write_property(obj_ptr, fprop, mass, err)) {
                    // Also enable the override flag
                    auto* override_prop = cls->find_property(L"bOverrideMass");
                    if (override_prop) {
                        auto* fprop2 = reinterpret_cast<API::FProperty*>(override_prop);
                        PropertyWriter::write_property(obj_ptr, fprop2, true, err);
                    }
                    return json{{"success", true}, {"method", "property"}, {"property", "MassInKgOverride"}, {"mass", mass}};
                }
                return json{{"error", "Property write failed: " + err}};
            }

            return json{{"error", "No mass override function or property found on this object"}};
        });

        send_json(res, result);
    });
}

} // namespace PhysicsRoutes
