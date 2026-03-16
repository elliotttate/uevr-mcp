// Include Plugin.hpp which provides DllMain, uevr_plugin_initialize, etc.
#include <uevr/Plugin.hpp>

#include "http_server.h"
#include "pipe_server.h"
#include "game_thread_queue.h"
#include "json_helpers.h"
#include "routes/status_routes.h"
#include "routes/input_routes.h"
#include "lua/lua_engine.h"
#include "screenshot/screenshot_capture.h"
#include "watch/property_watch.h"

#include <memory>
#include <string>

using namespace uevr;

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

        // Start HTTP server
        auto& http = HttpServer::get();
        if (http.start(8899)) {
            pipe.log("HTTP server started on port 8899");
        } else {
            API::get()->log_error("UEVR-MCP: Failed to start HTTP server");
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
                renderer->renderer_type
            );
            pipe.log("Screenshot capture initialized (renderer type: " + std::to_string(renderer->renderer_type) + ")");
        }

        API::get()->log_info("UEVR-MCP: Initialized successfully");
        pipe.log("Initialization complete");
    }

    void on_pre_engine_tick(API::UGameEngine* engine, float delta) override {
        // Process queued game-thread requests
        GameThreadQueue::get().process_pending(16);

        // Run Lua frame callbacks and timers
        LuaEngine::get().on_frame(delta);

        // Process property watches
        PropertyWatch::get().tick(StatusRoutes::get_tick_count());

        // Track tick count for status
        StatusRoutes::increment_tick_count();
    }

    void on_present() override {
        // Screenshot capture from D3D backbuffer
        ScreenshotCapture::get().on_present();
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
