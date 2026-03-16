#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <Xinput.h>
#include <atomic>
#include <mutex>

namespace httplib { class Server; }

namespace InputRoutes {
    void register_routes(httplib::Server& server);

    struct GamepadOverride {
        std::atomic<bool> active{false};
        XINPUT_GAMEPAD pad{};
        std::mutex mutex;
    };

    GamepadOverride& get_gamepad_override();
}
