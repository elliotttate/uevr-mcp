#include <httplib.h>

#include "http_server.h"
#include "game_thread_queue.h"
#include "json_helpers.h"
#include "routes/status_routes.h"
#include "routes/log_routes.h"
#include "routes/diagnostics_routes.h"
#include "routes/explorer_routes.h"
#include "routes/console_routes.h"
#include "routes/vr_routes.h"
#include "routes/lua_routes.h"
#include "routes/blueprint_routes.h"
#include "routes/screenshot_routes.h"
#include "routes/watch_routes.h"
#include "routes/world_routes.h"
#include "routes/input_routes.h"
#include "routes/material_routes.h"
#include "routes/animation_routes.h"
#include "routes/physics_routes.h"
#include "routes/asset_routes.h"
#include "routes/hook_routes.h"
#include "routes/macro_routes.h"
#include "routes/discovery_routes.h"
#include "routes/process_event_routes.h"
#include "routes/motion_controller_routes.h"
#include "routes/timer_routes.h"
#include "routes/render_routes.h"

#include <uevr/API.hpp>
#include <filesystem>

HttpServer::HttpServer() : m_server(std::make_unique<httplib::Server>()) {}

HttpServer::~HttpServer() {
    stop();
}

static std::string get_dll_directory() {
    HMODULE hModule = nullptr;
    GetModuleHandleExA(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCSTR)&get_dll_directory,
        &hModule
    );
    if (!hModule) return "";

    char path[MAX_PATH]{};
    GetModuleFileNameA(hModule, path, MAX_PATH);
    std::string dir(path);
    auto slash = dir.find_last_of("\\/");
    return (slash != std::string::npos) ? dir.substr(0, slash) : dir;
}

bool HttpServer::start(int port) {
    if (m_running.load()) return false;

    // CORS headers for all responses
    m_server->set_default_headers({
        {"Access-Control-Allow-Origin", "*"},
        {"Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS"},
        {"Access-Control-Allow-Headers", "Content-Type"},
    });

    // Handle CORS preflight
    m_server->Options(R"(.*)", [](const httplib::Request&, httplib::Response& res) {
        res.status = 204;
    });

    register_routes();

    // Serve web dashboard from web/ directory next to the DLL
    auto dll_dir = get_dll_directory();
    if (!dll_dir.empty()) {
        auto web_dir = dll_dir + "\\web";
        if (std::filesystem::exists(web_dir)) {
            m_server->set_mount_point("/", web_dir);
            auto& api = uevr::API::get();
            if (api) {
                api->log_info("UEVR-MCP: Serving web dashboard from %s", web_dir.c_str());
            }
        }
    }

    // Try to bind, scanning ports if the preferred one is taken
    auto& api = uevr::API::get();
    constexpr int MAX_PORT_ATTEMPTS = 20;
    bool bound = false;

    for (int attempt = 0; attempt < MAX_PORT_ATTEMPTS; ++attempt) {
        int try_port = port + attempt;
        if (m_server->bind_to_port("127.0.0.1", try_port)) {
            m_port = try_port;
            bound = true;
            if (api) {
                if (attempt > 0) {
                    api->log_info("UEVR-MCP: Port %d in use, bound to port %d instead", port, try_port);
                } else {
                    api->log_info("UEVR-MCP: Bound to port %d", try_port);
                }
            }
            break;
        }
        if (api) {
            api->log_info("UEVR-MCP: Port %d in use, trying next...", try_port);
        }
    }

    if (!bound) {
        if (api) {
            api->log_error("UEVR-MCP: Failed to bind to any port in range %d-%d", port, port + MAX_PORT_ATTEMPTS - 1);
        }
        return false;
    }

    m_thread = std::thread(&HttpServer::server_thread_func, this);
    return true;
}

void HttpServer::stop() {
    if (m_server && m_running.load()) {
        m_server->stop();
    }
    if (m_thread.joinable()) {
        m_thread.join();
    }
    m_running.store(false);
}

void HttpServer::register_routes() {
    StatusRoutes::register_routes(*m_server);
    LogRoutes::register_routes(*m_server);
    DiagnosticsRoutes::register_routes(*m_server);
    ExplorerRoutes::register_routes(*m_server);
    ConsoleRoutes::register_routes(*m_server);
    VrRoutes::register_routes(*m_server);
    LuaRoutes::register_routes(*m_server);
    BlueprintRoutes::register_routes(*m_server);
    ScreenshotRoutes::register_routes(*m_server);
    WatchRoutes::register_routes(*m_server);
    WorldRoutes::register_routes(*m_server);
    InputRoutes::register_routes(*m_server);
    MaterialRoutes::register_routes(*m_server);
    AnimationRoutes::register_routes(*m_server);
    PhysicsRoutes::register_routes(*m_server);
    AssetRoutes::register_routes(*m_server);
    HookRoutes::register_routes(*m_server);
    MacroRoutes::register_routes(*m_server);
    DiscoveryRoutes::register_routes(*m_server);
    ProcessEventRoutes::register_routes(*m_server);
    MotionControllerRoutes::register_routes(*m_server);
    TimerRoutes::register_routes(*m_server);
    RenderRoutes::register_routes(*m_server);
}

void HttpServer::server_thread_func() {
    m_running.store(true);
    auto& api = uevr::API::get();
    if (api) {
        api->log_info("UEVR-MCP: HTTP server listening on port %d", m_port);
    }
    // Socket is already bound in start(); just accept connections.
    m_server->listen_after_bind();
    m_running.store(false);
}
