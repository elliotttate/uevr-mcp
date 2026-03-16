#include "screenshot_routes.h"
#include "../screenshot/screenshot_capture.h"
#include "../pipe_server.h"

#include <httplib.h>
#include <nlohmann/json.hpp>

using json = nlohmann::json;

namespace ScreenshotRoutes {

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

    // GET /api/screenshot — Capture a screenshot from the backbuffer
    server.Get("/api/screenshot", [](const httplib::Request& req, httplib::Response& res) {
        int max_width = 640;
        int max_height = 0;
        int quality = 75;
        int timeout = 5000;

        if (req.has_param("maxWidth")) {
            try { max_width = std::stoi(req.get_param_value("maxWidth")); } catch (...) {}
        }
        if (req.has_param("maxHeight")) {
            try { max_height = std::stoi(req.get_param_value("maxHeight")); } catch (...) {}
        }
        if (req.has_param("quality")) {
            try { quality = std::stoi(req.get_param_value("quality")); } catch (...) {}
        }
        if (req.has_param("timeout")) {
            try { timeout = std::stoi(req.get_param_value("timeout")); } catch (...) {}
        }

        auto result = ScreenshotCapture::get().capture(max_width, max_height, quality, timeout);

        if (!result.contains("error")) {
            PipeServer::get().log("Screenshot: capture " +
                std::to_string(result["width"].get<int>()) + "x" +
                std::to_string(result["height"].get<int>()));
        }

        send_json(res, result);
    });

    // GET /api/screenshot/info — Screenshot capability info
    server.Get("/api/screenshot/info", [](const httplib::Request&, httplib::Response& res) {
        auto& cap = ScreenshotCapture::get();

        json result;
        result["initialized"] = cap.is_initialized();

        int rt = cap.renderer_type();
        // UEVR_RENDERER_D3D11 = 0, UEVR_RENDERER_D3D12 = 1
        switch (rt) {
            case 0:  result["rendererType"] = "D3D11"; break;
            case 1:  result["rendererType"] = "D3D12"; break;
            default: result["rendererType"] = "Unknown"; break;
        }

        send_json(res, result);
    });
}

} // namespace ScreenshotRoutes
