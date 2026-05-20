// Include Plugin.hpp which provides DllMain, uevr_plugin_initialize, etc.
#include <uevr/Plugin.hpp>

#include "http_server.h"
#include "diagnostics.h"
#include "pipe_server.h"
#include "game_thread_queue.h"
#include "json_helpers.h"
#include "game_patches.h"
#include "routes/status_routes.h"
#include "routes/input_routes.h"
#include "lua/lua_engine.h"
#include "screenshot/screenshot_capture.h"
#include "watch/property_watch.h"

#include <memory>
#include <string>

using namespace uevr;

namespace {

std::string current_renderer_name() {
    auto& api = API::get();
    if (!api || !api->param() || !api->param()->renderer) {
        return {};
    }

    switch (api->param()->renderer->renderer_type) {
    case UEVR_RENDERER_D3D11:
        return "D3D11";
    case UEVR_RENDERER_D3D12:
        return "D3D12";
    default:
        return "Unknown";
    }
}

void log_current_exception(const char* context, const char* callback = nullptr) {
    const auto error = Diagnostics::describe_current_exception();
    PipeServer::get().log(std::string(context) + ": " + error, "Diagnostics", callback ? callback : "", current_renderer_name());
}

} // namespace

class UevrMcpPlugin : public uevr::Plugin {
public:
    UevrMcpPlugin() = default;

    void on_initialize() override {
        API::get()->log_info("UEVR-MCP: Initializing...");

        // Get game process name for status
        char exe_path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, exe_path, MAX_PATH);
        std::string exe(exe_path);
        auto slash = exe.find_last_of("\\/");
        std::string game_name = (slash != std::string::npos) ? exe.substr(slash + 1) : exe;

        // Start pipe server
        auto& pipe = PipeServer::get();
        pipe.set_game_name(game_name);
        pipe.start();
        pipe.log("Plugin initializing for " + game_name);
        GamePatches::apply_for_game(game_name);
        Diagnostics::get().initialize();

        // Start HTTP server (tries port 8899, falls back to next available)
        auto& http = HttpServer::get();
        if (http.start(8899)) {
            pipe.set_http_port(http.port());
            pipe.log("HTTP server started on port " + std::to_string(http.port()));
        } else {
            API::get()->log_error("UEVR-MCP: Failed to start HTTP server on any port");
            pipe.log("ERROR: HTTP server failed to start");
        }

        // Activate UObjectHook for object lifetime tracking
        API::UObjectHook::activate();

        // Initialize Lua engine
        LuaEngine::get().initialize();

        // Initialize screenshot capture with renderer data
        auto renderer = API::get()->param()->renderer;
        if (renderer) {
            ScreenshotCapture::get().initialize(
                renderer->device,
                renderer->swapchain,
                renderer->command_queue,
                renderer->renderer_type
            );
            pipe.log("Screenshot capture initialized (renderer type: " + std::to_string(renderer->renderer_type) + ")");
        }

        API::get()->log_info("UEVR-MCP: Initialized successfully");
        pipe.log("Initialization complete");
    }

    void on_pre_engine_tick(API::UGameEngine* engine, float delta) override {
        try {
            // Process queued game-thread requests
            GameThreadQueue::get().process_pending(16);
        } catch (...) {
            log_current_exception("UEVR-MCP: on_pre_engine_tick game-thread queue exception", "on_pre_engine_tick_queue");
        }

        try {
            // Run Lua frame callbacks and timers
            LuaEngine::get().on_frame(delta);
        } catch (...) {
            log_current_exception("UEVR-MCP: on_pre_engine_tick Lua exception", "lua_on_frame");
        }

        try {
            // Process property watches
            PropertyWatch::get().tick(StatusRoutes::get_tick_count());
        } catch (...) {
            log_current_exception("UEVR-MCP: on_pre_engine_tick property watch exception", "property_watch_tick");
        }

        // Track tick count for status
        StatusRoutes::increment_tick_count();
    }

    void on_present() override {
        Diagnostics::ScopedCallback diag("on_present", current_renderer_name());
        try {
            // Fallback screenshot capture path when no post-render target is available.
            ScreenshotCapture::get().on_present();
        } catch (...) {
            const auto error = Diagnostics::describe_current_exception();
            PipeServer::get().log("UEVR-MCP: on_present exception: " + error, "Diagnostics", "on_present", current_renderer_name());
            diag.fail(error);
        }
    }

    void on_post_render_vr_framework_dx11(ID3D11DeviceContext* context, ID3D11Texture2D* texture, ID3D11RenderTargetView*) override {
        Diagnostics::ScopedCallback diag("on_post_render_vr_framework_dx11", "D3D11");
        try {
            ScreenshotCapture::get().on_post_render_dx11(context, texture);
        } catch (...) {
            const auto error = Diagnostics::describe_current_exception();
            PipeServer::get().log("UEVR-MCP: on_post_render_vr_framework_dx11 exception: " + error, "Diagnostics", "on_post_render_vr_framework_dx11", "D3D11");
            diag.fail(error);
        }
    }

    void on_xinput_get_state(uint32_t* retval, uint32_t user_index, XINPUT_STATE* state) override {
        // Apply gamepad override if active
        auto& override_state = InputRoutes::get_gamepad_override();
        if (override_state.active.load()) {
            std::lock_guard<std::mutex> lock(override_state.mutex);
            state->Gamepad = override_state.pad;
        }
    }

    // Cleanup on unload
    ~UevrMcpPlugin() {
        PipeServer::get().log("Plugin shutting down");
        HttpServer::get().stop();
        PipeServer::get().stop();
    }
};

// Global plugin instance — Plugin constructor registers with uevr::detail::g_plugin
std::unique_ptr<UevrMcpPlugin> g_plugin{new UevrMcpPlugin()};
