#include <winsock2.h>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <Xinput.h>

#include "input_routes.h"
#include "../pipe_server.h"

#include <httplib.h>
#include <nlohmann/json.hpp>

#include <algorithm>
#include <string>
#include <unordered_map>
#include <thread>
#include <chrono>

using json = nlohmann::json;

namespace InputRoutes {

GamepadOverride& get_gamepad_override() {
    static GamepadOverride s;
    return s;
}

// Find the main game window belonging to this process
static HWND find_game_window() {
    struct Finder {
        DWORD pid;
        HWND result;
    };
    Finder f{GetCurrentProcessId(), nullptr};
    EnumWindows([](HWND hwnd, LPARAM lp) -> BOOL {
        auto* pf = reinterpret_cast<Finder*>(lp);
        DWORD wndPid;
        GetWindowThreadProcessId(hwnd, &wndPid);
        if (wndPid == pf->pid && IsWindowVisible(hwnd)) {
            pf->result = hwnd;
            return FALSE;
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&f));
    return f.result;
}

// Map common key names to Windows virtual key codes
static UINT key_name_to_vk(const std::string& name) {
    static const std::unordered_map<std::string, UINT> map = {
        // Special keys
        {"space", VK_SPACE}, {"enter", VK_RETURN}, {"return", VK_RETURN},
        {"escape", VK_ESCAPE}, {"esc", VK_ESCAPE},
        {"tab", VK_TAB}, {"backspace", VK_BACK},
        {"delete", VK_DELETE}, {"del", VK_DELETE}, {"insert", VK_INSERT},
        {"home", VK_HOME}, {"end", VK_END},
        {"pageup", VK_PRIOR}, {"pagedown", VK_NEXT},
        // Arrow keys
        {"up", VK_UP}, {"down", VK_DOWN}, {"left", VK_LEFT}, {"right", VK_RIGHT},
        // Modifier keys
        {"shift", VK_SHIFT}, {"lshift", VK_LSHIFT}, {"rshift", VK_RSHIFT},
        {"ctrl", VK_CONTROL}, {"control", VK_CONTROL},
        {"lctrl", VK_LCONTROL}, {"rctrl", VK_RCONTROL},
        {"alt", VK_MENU}, {"lalt", VK_LMENU}, {"ralt", VK_RMENU},
        // Function keys
        {"f1", VK_F1}, {"f2", VK_F2}, {"f3", VK_F3}, {"f4", VK_F4},
        {"f5", VK_F5}, {"f6", VK_F6}, {"f7", VK_F7}, {"f8", VK_F8},
        {"f9", VK_F9}, {"f10", VK_F10}, {"f11", VK_F11}, {"f12", VK_F12},
        // Letter keys
        {"a", 'A'}, {"b", 'B'}, {"c", 'C'}, {"d", 'D'}, {"e", 'E'},
        {"f", 'F'}, {"g", 'G'}, {"h", 'H'}, {"i", 'I'}, {"j", 'J'},
        {"k", 'K'}, {"l", 'L'}, {"m", 'M'}, {"n", 'N'}, {"o", 'O'},
        {"p", 'P'}, {"q", 'Q'}, {"r", 'R'}, {"s", 'S'}, {"t", 'T'},
        {"u", 'U'}, {"v", 'V'}, {"w", 'W'}, {"x", 'X'}, {"y", 'Y'}, {"z", 'Z'},
        // Number keys
        {"0", '0'}, {"1", '1'}, {"2", '2'}, {"3", '3'}, {"4", '4'},
        {"5", '5'}, {"6", '6'}, {"7", '7'}, {"8", '8'}, {"9", '9'},
        // Numpad
        {"numpad0", VK_NUMPAD0}, {"numpad1", VK_NUMPAD1}, {"numpad2", VK_NUMPAD2},
        {"numpad3", VK_NUMPAD3}, {"numpad4", VK_NUMPAD4}, {"numpad5", VK_NUMPAD5},
        {"numpad6", VK_NUMPAD6}, {"numpad7", VK_NUMPAD7}, {"numpad8", VK_NUMPAD8},
        {"numpad9", VK_NUMPAD9},
        // Mouse buttons (for reference, not used with WM_KEY*)
        {"lmb", VK_LBUTTON}, {"rmb", VK_RBUTTON}, {"mmb", VK_MBUTTON},
        // Misc
        {"capslock", VK_CAPITAL}, {"numlock", VK_NUMLOCK}, {"scrolllock", VK_SCROLL},
        {"printscreen", VK_SNAPSHOT}, {"pause", VK_PAUSE},
    };

    std::string lower = name;
    std::transform(lower.begin(), lower.end(), lower.begin(), ::tolower);
    auto it = map.find(lower);
    return it != map.end() ? it->second : 0;
}

// Resolve a key from JSON — accepts string name or integer VK code
static UINT resolve_vk(const json& key_val) {
    if (key_val.is_number_integer()) {
        return static_cast<UINT>(key_val.get<int>());
    }
    if (key_val.is_string()) {
        auto name = key_val.get<std::string>();
        auto vk = key_name_to_vk(name);
        if (vk != 0) return vk;

        // Single character — use its uppercase ASCII
        if (name.size() == 1) {
            char c = static_cast<char>(toupper(static_cast<unsigned char>(name[0])));
            return static_cast<UINT>(c);
        }
    }
    return 0;
}

// Build lParam for WM_KEYDOWN/WM_KEYUP
static LPARAM make_key_lparam(UINT vk, bool keyup) {
    UINT scancode = MapVirtualKeyA(vk, MAPVK_VK_TO_VSC);
    LPARAM lp = 1; // repeat count
    lp |= (static_cast<LPARAM>(scancode) << 16);
    if (keyup) {
        lp |= (1LL << 30); // previous key state
        lp |= (1LL << 31); // transition state
    }
    return lp;
}

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

    // POST /api/input/key — Simulate a keyboard key press/release
    server.Post("/api/input/key", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        if (!body.contains("key")) {
            send_json(res, json{{"error", "Missing 'key' parameter"}}, 400);
            return;
        }

        auto vk = resolve_vk(body["key"]);
        if (vk == 0) {
            send_json(res, json{{"error", "Unknown key: " + body["key"].dump()}}, 400);
            return;
        }

        std::string event = body.value("event", "tap");

        HWND hwnd = find_game_window();
        if (!hwnd) {
            send_json(res, json{{"error", "Game window not found"}}, 500);
            return;
        }

        PipeServer::get().log("Input: key " + event + " vk=" + std::to_string(vk));

        if (event == "press") {
            PostMessageA(hwnd, WM_KEYDOWN, static_cast<WPARAM>(vk), make_key_lparam(vk, false));
        } else if (event == "release") {
            PostMessageA(hwnd, WM_KEYUP, static_cast<WPARAM>(vk), make_key_lparam(vk, true));
        } else { // "tap" — press then release
            PostMessageA(hwnd, WM_KEYDOWN, static_cast<WPARAM>(vk), make_key_lparam(vk, false));
            // Small delay between press and release
            std::this_thread::sleep_for(std::chrono::milliseconds(body.value("duration", 50)));
            PostMessageA(hwnd, WM_KEYUP, static_cast<WPARAM>(vk), make_key_lparam(vk, true));
        }

        send_json(res, json{{"success", true}, {"key", body["key"]}, {"vkCode", vk}, {"event", event}});
    });

    // POST /api/input/mouse — Simulate mouse input
    server.Post("/api/input/mouse", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        HWND hwnd = find_game_window();
        if (!hwnd) {
            send_json(res, json{{"error", "Game window not found"}}, 500);
            return;
        }

        // Mouse movement
        if (body.contains("move")) {
            auto& mv = body["move"];
            int x = mv.value("x", 0);
            int y = mv.value("y", 0);
            LPARAM lp = MAKELPARAM(x, y);
            PostMessageA(hwnd, WM_MOUSEMOVE, 0, lp);
            PipeServer::get().log("Input: mouse move (" + std::to_string(x) + "," + std::to_string(y) + ")");
            send_json(res, json{{"success", true}, {"action", "move"}, {"x", x}, {"y", y}});
            return;
        }

        // Mouse button
        std::string button = body.value("button", "left");
        std::string event = body.value("event", "click");

        // Position for the click (optional — uses current position if not specified)
        int x = body.value("x", -1);
        int y = body.value("y", -1);

        LPARAM lp = 0;
        if (x >= 0 && y >= 0) {
            lp = MAKELPARAM(x, y);
        } else {
            // Use the center of the client area as default
            RECT rect;
            GetClientRect(hwnd, &rect);
            lp = MAKELPARAM((rect.right - rect.left) / 2, (rect.bottom - rect.top) / 2);
        }

        UINT msg_down, msg_up;
        WPARAM wp = 0;
        if (button == "right") {
            msg_down = WM_RBUTTONDOWN;
            msg_up = WM_RBUTTONUP;
            wp = MK_RBUTTON;
        } else if (button == "middle") {
            msg_down = WM_MBUTTONDOWN;
            msg_up = WM_MBUTTONUP;
            wp = MK_MBUTTON;
        } else { // "left"
            msg_down = WM_LBUTTONDOWN;
            msg_up = WM_LBUTTONUP;
            wp = MK_LBUTTON;
        }

        PipeServer::get().log("Input: mouse " + button + " " + event);

        if (event == "press") {
            PostMessageA(hwnd, msg_down, wp, lp);
        } else if (event == "release") {
            PostMessageA(hwnd, msg_up, 0, lp);
        } else { // "click"
            PostMessageA(hwnd, msg_down, wp, lp);
            std::this_thread::sleep_for(std::chrono::milliseconds(body.value("duration", 50)));
            PostMessageA(hwnd, msg_up, 0, lp);
        }

        send_json(res, json{{"success", true}, {"button", button}, {"event", event}});
    });

    // POST /api/input/gamepad — Simulate gamepad input (stored for next XInput poll)
    server.Post("/api/input/gamepad", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto& gp = get_gamepad_override();
        std::lock_guard<std::mutex> lock(gp.mutex);

        // Map button names to XINPUT_GAMEPAD_ flags
        static const std::unordered_map<std::string, WORD> button_map = {
            {"a", XINPUT_GAMEPAD_A}, {"b", XINPUT_GAMEPAD_B},
            {"x", XINPUT_GAMEPAD_X}, {"y", XINPUT_GAMEPAD_Y},
            {"lb", XINPUT_GAMEPAD_LEFT_SHOULDER}, {"rb", XINPUT_GAMEPAD_RIGHT_SHOULDER},
            {"leftshoulder", XINPUT_GAMEPAD_LEFT_SHOULDER}, {"rightshoulder", XINPUT_GAMEPAD_RIGHT_SHOULDER},
            {"start", XINPUT_GAMEPAD_START}, {"back", XINPUT_GAMEPAD_BACK},
            {"select", XINPUT_GAMEPAD_BACK},
            {"lthumb", XINPUT_GAMEPAD_LEFT_THUMB}, {"rthumb", XINPUT_GAMEPAD_RIGHT_THUMB},
            {"leftthumb", XINPUT_GAMEPAD_LEFT_THUMB}, {"rightthumb", XINPUT_GAMEPAD_RIGHT_THUMB},
            {"dpadup", XINPUT_GAMEPAD_DPAD_UP}, {"dpaddown", XINPUT_GAMEPAD_DPAD_DOWN},
            {"dpadleft", XINPUT_GAMEPAD_DPAD_LEFT}, {"dpadright", XINPUT_GAMEPAD_DPAD_RIGHT},
            {"up", XINPUT_GAMEPAD_DPAD_UP}, {"down", XINPUT_GAMEPAD_DPAD_DOWN},
            {"left", XINPUT_GAMEPAD_DPAD_LEFT}, {"right", XINPUT_GAMEPAD_DPAD_RIGHT},
        };

        // Reset pad state
        XINPUT_GAMEPAD pad{};

        // Buttons
        if (body.contains("buttons") && body["buttons"].is_object()) {
            for (auto& [key, val] : body["buttons"].items()) {
                std::string lower_key = key;
                std::transform(lower_key.begin(), lower_key.end(), lower_key.begin(), ::tolower);
                auto it = button_map.find(lower_key);
                if (it != button_map.end() && val.is_boolean() && val.get<bool>()) {
                    pad.wButtons |= it->second;
                }
            }
        }

        // Left stick
        if (body.contains("leftStick") && body["leftStick"].is_object()) {
            float lx = body["leftStick"].value("x", 0.0f);
            float ly = body["leftStick"].value("y", 0.0f);
            lx = std::clamp(lx, -1.0f, 1.0f);
            ly = std::clamp(ly, -1.0f, 1.0f);
            pad.sThumbLX = static_cast<SHORT>(lx * 32767.0f);
            pad.sThumbLY = static_cast<SHORT>(ly * 32767.0f);
        }

        // Right stick
        if (body.contains("rightStick") && body["rightStick"].is_object()) {
            float rx = body["rightStick"].value("x", 0.0f);
            float ry = body["rightStick"].value("y", 0.0f);
            rx = std::clamp(rx, -1.0f, 1.0f);
            ry = std::clamp(ry, -1.0f, 1.0f);
            pad.sThumbRX = static_cast<SHORT>(rx * 32767.0f);
            pad.sThumbRY = static_cast<SHORT>(ry * 32767.0f);
        }

        // Triggers (0.0 to 1.0)
        if (body.contains("leftTrigger")) {
            float lt = std::clamp(body["leftTrigger"].get<float>(), 0.0f, 1.0f);
            pad.bLeftTrigger = static_cast<BYTE>(lt * 255.0f);
        }
        if (body.contains("rightTrigger")) {
            float rt = std::clamp(body["rightTrigger"].get<float>(), 0.0f, 1.0f);
            pad.bRightTrigger = static_cast<BYTE>(rt * 255.0f);
        }

        gp.pad = pad;
        gp.active.store(true, std::memory_order_release);

        // If duration is specified, schedule deactivation
        int duration_ms = body.value("duration", 0);
        if (duration_ms > 0) {
            std::thread([duration_ms]() {
                std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
                auto& gp = get_gamepad_override();
                gp.active.store(false, std::memory_order_release);
            }).detach();
        }

        PipeServer::get().log("Input: gamepad override set (buttons=0x" +
            ([](WORD w) {
                char buf[8];
                snprintf(buf, sizeof(buf), "%04X", w);
                return std::string(buf);
            })(pad.wButtons) +
            ", duration=" + std::to_string(duration_ms) + "ms)");

        send_json(res, json{
            {"success", true},
            {"buttons", pad.wButtons},
            {"leftStick", {{"x", pad.sThumbLX}, {"y", pad.sThumbLY}}},
            {"rightStick", {{"x", pad.sThumbRX}, {"y", pad.sThumbRY}}},
            {"leftTrigger", pad.bLeftTrigger},
            {"rightTrigger", pad.bRightTrigger},
            {"duration", duration_ms}
        });
    });

    // POST /api/input/text — Type a string of text
    server.Post("/api/input/text", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        if (!body.contains("text") || !body["text"].is_string()) {
            send_json(res, json{{"error", "Missing 'text' string parameter"}}, 400);
            return;
        }

        auto text = body["text"].get<std::string>();
        if (text.empty()) {
            send_json(res, json{{"error", "Empty text string"}}, 400);
            return;
        }

        HWND hwnd = find_game_window();
        if (!hwnd) {
            send_json(res, json{{"error", "Game window not found"}}, 500);
            return;
        }

        int delay_ms = body.value("delay", 10);

        PipeServer::get().log("Input: text input (" + std::to_string(text.size()) + " chars)");

        // Convert to wide string for WM_CHAR
        int wide_len = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
        std::wstring wtext(wide_len, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &wtext[0], wide_len);

        for (wchar_t ch : wtext) {
            PostMessageW(hwnd, WM_CHAR, static_cast<WPARAM>(ch), 0);
            if (delay_ms > 0) {
                std::this_thread::sleep_for(std::chrono::milliseconds(delay_ms));
            }
        }

        send_json(res, json{{"success", true}, {"length", static_cast<int>(wtext.size())}});
    });
}

} // namespace InputRoutes
