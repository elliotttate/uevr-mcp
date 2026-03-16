cmake_minimum_required(VERSION 3.21)
project(uevr-mcp-plugin VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ── Lua 5.4 static library (compiled as C) ──────────────────────────

add_library(lua54 STATIC
    dependencies/lua/lapi.c
    dependencies/lua/lauxlib.c
    dependencies/lua/lbaselib.c
    dependencies/lua/lcode.c
    dependencies/lua/lcorolib.c
    dependencies/lua/lctype.c
    dependencies/lua/ldblib.c
    dependencies/lua/ldebug.c
    dependencies/lua/ldo.c
    dependencies/lua/ldump.c
    dependencies/lua/lfunc.c
    dependencies/lua/lgc.c
    dependencies/lua/linit.c
    dependencies/lua/liolib.c
    dependencies/lua/llex.c
    dependencies/lua/lmathlib.c
    dependencies/lua/lmem.c
    dependencies/lua/loadlib.c
    dependencies/lua/lobject.c
    dependencies/lua/lopcodes.c
    dependencies/lua/loslib.c
    dependencies/lua/lparser.c
    dependencies/lua/lstate.c
    dependencies/lua/lstring.c
    dependencies/lua/lstrlib.c
    dependencies/lua/ltable.c
    dependencies/lua/ltablib.c
    dependencies/lua/ltm.c
    dependencies/lua/lundump.c
    dependencies/lua/lutf8lib.c
    dependencies/lua/lvm.c
    dependencies/lua/lzio.c
)

target_include_directories(lua54 PUBLIC dependencies/lua)

if(MSVC)
    set_target_properties(lua54 PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()

# ── Main plugin DLL ─────────────────────────────────────────────────

add_library(uevr_mcp SHARED
    src/main.cpp
    src/http_server.cpp
    src/pipe_server.cpp
    src/game_thread_queue.cpp
    src/json_helpers.cpp
    src/reflection/property_reader.cpp
    src/reflection/property_writer.cpp
    src/reflection/object_explorer.cpp
    src/reflection/function_caller.cpp
    src/reflection/object_registry.cpp
    src/lua/lua_engine.cpp
    src/routes/status_routes.cpp
    src/routes/explorer_routes.cpp
    src/routes/console_routes.cpp
    src/routes/vr_routes.cpp
    src/routes/lua_routes.cpp
    src/routes/blueprint_routes.cpp
    src/routes/screenshot_routes.cpp
    src/routes/watch_routes.cpp
    src/routes/world_routes.cpp
    src/routes/input_routes.cpp
    src/routes/material_routes.cpp
    src/routes/animation_routes.cpp
    src/routes/physics_routes.cpp
    src/routes/asset_routes.cpp
    src/routes/hook_routes.cpp
    src/routes/macro_routes.cpp
    src/screenshot/screenshot_capture.cpp
    src/watch/property_watch.cpp
    src/hook/hook_registry.cpp
)

target_include_directories(uevr_mcp PRIVATE
    include
    dependencies
    dependencies/cpp-httplib
    dependencies/nlohmann
    dependencies/sol2
)

target_compile_definitions(uevr_mcp PRIVATE
    CPPHTTPLIB_NO_EXCEPTIONS
    SOL_ALL_SAFETIES_ON=1
)

target_link_libraries(uevr_mcp PRIVATE ws2_32 lua54 d3d11 dxgi windowscodecs ole32)

# MSVC settings
if(MSVC)
    target_compile_options(uevr_mcp PRIVATE /EHa /bigobj /utf-8)
    set_target_properties(uevr_mcp PROPERTIES
        MSVC_RUNTIME_LIBRARY "MultiThreaded"
    )
endif()

set_target_properties(uevr_mcp PROPERTIES
    OUTPUT_NAME "uevr_mcp"
    SUFFIX ".dll"
)
