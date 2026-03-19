#include "motion_controller_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

#include <unordered_map>
#include <mutex>
#include <string>

using json = nlohmann::json;
using namespace uevr;

namespace MotionControllerRoutes {

// Track attachments so we can list them
struct AttachmentInfo {
    uintptr_t address;
    std::string name;       // full name of attached object
    std::string className;  // class name
    uint32_t hand;          // 0=left, 1=right, 2=hmd
    bool permanent;
    float location_offset[3];
    float rotation_offset[4]; // quaternion xyzw
};

static std::mutex s_attachments_mutex;
static std::unordered_map<uintptr_t, AttachmentInfo> s_attachments;

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
    // POST /api/vr/attach — Attach object to VR motion controller
    server.Post("/api/vr/attach", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON"}}, 400);
            return;
        }

        if (!body.contains("address")) {
            send_json(res, json{{"error", "Missing 'address' parameter"}}, 400);
            return;
        }

        auto addr_str = body["address"].get<std::string>();
        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        // hand: "left" (0), "right" (1), "hmd" (2)
        std::string hand_str = body.value("hand", "right");
        uint32_t hand = 1; // default right
        if (hand_str == "left") hand = 0;
        else if (hand_str == "hmd") hand = 2;

        bool permanent = body.value("permanent", true);

        // Location offset
        float loc_x = 0.0f, loc_y = 0.0f, loc_z = 0.0f;
        if (body.contains("locationOffset")) {
            auto& lo = body["locationOffset"];
            loc_x = lo.value("x", 0.0f);
            loc_y = lo.value("y", 0.0f);
            loc_z = lo.value("z", 0.0f);
        }

        // Rotation offset (quaternion)
        float rot_x = 0.0f, rot_y = 0.0f, rot_z = 0.0f, rot_w = 1.0f;
        if (body.contains("rotationOffset")) {
            auto& ro = body["rotationOffset"];
            rot_x = ro.value("x", 0.0f);
            rot_y = ro.value("y", 0.0f);
            rot_z = ro.value("z", 0.0f);
            rot_w = ro.value("w", 1.0f);
        }

        auto result = GameThreadQueue::get().submit_and_wait([=]() -> json {
            auto* obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object not found or no longer valid at " + addr_str}};
            }

            auto* state = API::UObjectHook::get_or_add_motion_controller_state(obj);
            if (!state) {
                return json{{"error", "Failed to create motion controller state — the address must be a USceneComponent (e.g. SkeletalMeshComponent, StaticMeshComponent), not an Actor. Use uevr_world_components to find component addresses on an actor."}};
            }

            state->set_hand(hand);
            state->set_permanent(permanent);

            UEVR_Vector3f loc_offset{loc_x, loc_y, loc_z};
            state->set_location_offset(&loc_offset);

            UEVR_Quaternionf rot_offset{rot_x, rot_y, rot_z, rot_w};
            state->set_rotation_offset(&rot_offset);

            // Get object info for tracking
            std::string obj_name = "unknown";
            std::string class_name = "unknown";
            try {
                auto* cls = obj->get_class();
                if (cls) {
                    auto fname = cls->get_fname();
                    class_name = JsonHelpers::fname_to_string(&fname);
                }
                auto fname = obj->get_fname();
                obj_name = JsonHelpers::fname_to_string(&fname);
            } catch (...) {}

            // Track the attachment
            {
                std::lock_guard<std::mutex> lock(s_attachments_mutex);
                s_attachments[address] = AttachmentInfo{
                    address, obj_name, class_name, hand, permanent,
                    {loc_x, loc_y, loc_z}, {rot_x, rot_y, rot_z, rot_w}
                };
            }

            static const char* hand_names[] = {"left", "right", "hmd"};
            return json{
                {"success", true},
                {"address", addr_str},
                {"name", obj_name},
                {"class", class_name},
                {"hand", hand_names[hand < 3 ? hand : 1]},
                {"permanent", permanent},
                {"locationOffset", {{"x", loc_x}, {"y", loc_y}, {"z", loc_z}}},
                {"rotationOffset", {{"x", rot_x}, {"y", rot_y}, {"z", rot_z}, {"w", rot_w}}}
            };
        });

        PipeServer::get().log("VR: attached " + addr_str + " to controller");
        send_json(res, result);
    });

    // POST /api/vr/detach — Detach object from motion controller
    server.Post("/api/vr/detach", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON"}}, 400);
            return;
        }

        if (!body.contains("address")) {
            send_json(res, json{{"error", "Missing 'address' parameter"}}, 400);
            return;
        }

        auto addr_str = body["address"].get<std::string>();
        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        auto result = GameThreadQueue::get().submit_and_wait([=]() -> json {
            auto* obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object not found at " + addr_str}};
            }

            auto* state = API::UObjectHook::get_motion_controller_state(obj);
            if (!state) {
                return json{{"error", "No motion controller state for object " + addr_str}};
            }

            API::UObjectHook::remove_motion_controller_state(obj);

            {
                std::lock_guard<std::mutex> lock(s_attachments_mutex);
                s_attachments.erase(address);
            }

            return json{{"success", true}, {"address", addr_str}, {"detached", true}};
        });

        PipeServer::get().log("VR: detached " + addr_str);
        send_json(res, result);
    });

    // GET /api/vr/attachments — List all motion controller attachments
    server.Get("/api/vr/attachments", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() -> json {
            static const char* hand_names[] = {"left", "right", "hmd"};
            json attachments = json::array();

            std::lock_guard<std::mutex> lock(s_attachments_mutex);

            // Prune dead objects and build list
            std::vector<uintptr_t> dead;
            for (auto& [addr, info] : s_attachments) {
                auto* obj = reinterpret_cast<API::UObject*>(addr);
                if (!API::UObjectHook::exists(obj)) {
                    dead.push_back(addr);
                    continue;
                }

                // Check if state still exists
                auto* state = API::UObjectHook::get_motion_controller_state(obj);
                if (!state) {
                    dead.push_back(addr);
                    continue;
                }

                attachments.push_back({
                    {"address", JsonHelpers::address_to_string(addr)},
                    {"name", info.name},
                    {"class", info.className},
                    {"hand", hand_names[info.hand < 3 ? info.hand : 1]},
                    {"permanent", info.permanent},
                    {"locationOffset", {{"x", info.location_offset[0]}, {"y", info.location_offset[1]}, {"z", info.location_offset[2]}}},
                    {"rotationOffset", {{"x", info.rotation_offset[0]}, {"y", info.rotation_offset[1]}, {"z", info.rotation_offset[2]}, {"w", info.rotation_offset[3]}}}
                });
            }

            // Clean up dead entries
            for (auto addr : dead) {
                s_attachments.erase(addr);
            }

            return json{{"attachments", attachments}, {"count", attachments.size()}};
        });

        send_json(res, result);
    });

    // POST /api/vr/clear_attachments — Remove all motion controller states
    server.Post("/api/vr/clear_attachments", [](const httplib::Request&, httplib::Response& res) {
        auto result = GameThreadQueue::get().submit_and_wait([]() -> json {
            API::UObjectHook::remove_all_motion_controller_states();

            std::lock_guard<std::mutex> lock(s_attachments_mutex);
            auto count = s_attachments.size();
            s_attachments.clear();

            return json{{"success", true}, {"removed", count}};
        });

        PipeServer::get().log("VR: cleared all motion controller attachments");
        send_json(res, result);
    });
}

} // namespace MotionControllerRoutes
