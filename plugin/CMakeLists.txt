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
    src/diagnostics.cpp
    src/http_server.cpp
    src/pipe_server.cpp
    src/game_thread_queue.cpp
    src/json_helpers.cpp
    src/reflection/property_reader.cpp
    src/reflection/property_writer.cpp
    src/reflection/object_explorer.cpp
    src/reflection/function_caller.cpp
    src/reflection/object_registry.cpp
    src/reflection/property_probes.cpp
    src/lua/lua_engine.cpp
    src/routes/status_routes.cpp
    src/routes/log_routes.cpp
    src/routes/diagnostics_routes.cpp
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
    src/event_bus.cpp
    src/routes/discovery_routes.cpp
    src/process_event_listener.cpp
    src/routes/process_event_routes.cpp
    src/routes/motion_controller_routes.cpp
    src/routes/timer_routes.cpp
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

target_link_libraries(uevr_mcp PRIVATE ws2_32 lua54 d3d11 d3d12 dxgi windowscodecs ole32 psapi)

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

# ── Dumper-7 (vendored, standalone injectable DLL) ──────────────────
# Upstream: https://github.com/Encryqed/Dumper-7 (MIT).
# Vendored under external/Dumper-7/ — built as its own DLL that the MCP tools
# inject into a running UE process. Entry point is external/Dumper-7/main.cpp,
# which restores the stock "auto-dump on DllMain" behaviour (the fork we
# referenced had replaced it with a UEVR plugin wrapper — we don't want that).

file(GLOB DUMPER7_CPP CONFIGURE_DEPENDS external/Dumper-7/*.cpp)
file(GLOB DUMPER7_H   CONFIGURE_DEPENDS external/Dumper-7/*.h external/Dumper-7/*.hpp)

add_library(dumper7 SHARED
    ${DUMPER7_CPP}
    ${DUMPER7_H}
    external/Dumper-7/ExternalDependencies/Compression/zstd.c
    external/Dumper-7/ExternalDependencies/Compression/zstd.h
    external/Dumper-7/ExternalDependencies/Dumpspace/DSGen.cpp
    external/Dumper-7/ExternalDependencies/Dumpspace/DSGen.h
)

target_include_directories(dumper7 PRIVATE
    external/Dumper-7
    external/Dumper-7/ExternalDependencies
    external/Dumper-7/ExternalDependencies/Compression
    external/Dumper-7/ExternalDependencies/Dumpspace
    external/Dumper-7/ExternalDependencies/Json
)

# Dumper-7 uses C++20/23 features; match upstream latest.
set_target_properties(dumper7 PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    OUTPUT_NAME "dumper7"
    SUFFIX ".dll"
)

# zstd.c must be compiled as C, not C++.
set_source_files_properties(
    external/Dumper-7/ExternalDependencies/Compression/zstd.c
    PROPERTIES LANGUAGE C
)

if(MSVC)
    target_compile_options(dumper7 PRIVATE /EHsc /bigobj /utf-8 /wd4267 /wd4244 /wd4305 /wd4018 /wd4146)
    set_target_properties(dumper7 PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
    # Note: NOT defining NOMINMAX — Dumper-7's Utils.h/OffsetFinder.h use bare
    # min()/max() and rely on the Windows.h macros.
    target_compile_definitions(dumper7 PRIVATE
        _CRT_SECURE_NO_WARNINGS
    )
endif()
