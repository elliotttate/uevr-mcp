#include "render_routes.h"
#include "../pipe_server.h"

#include <Windows.h>
#include <d3d11.h>
#include <d3d11sdklayers.h>
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <dxgi.h>
#include <dxgidebug.h>
#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

using json = nlohmann::json;
template <typename T> using ComPtr = Microsoft::WRL::ComPtr<T>;

// FFI surface exposed by UEVRBackend.dll (see UEVRJ src/render/RenderDiagnosticsCAPI.hpp).
// We resolve these lazily via GetProcAddress so the plugin still loads even if
// it's running against a UEVR build that predates the FFI.
namespace {

using SnapshotFn         = const char* (*)(int, int, int, int);
using ResourcesFn        = const char* (*)(int);
using D3D12Fn            = const char* (*)(int, int);
using ShadersFn          = const char* (*)(int, int);
using SimpleStrFn        = const char* (*)();
using SetU64Fn           = void (*)(uint64_t);
using SetIntFn           = void (*)(int);
using VoidFn             = void (*)();
using ExportPairsFn      = const char* (*)(int);
using ExportBundleFn     = const char* (*)(const char*, const char*);
using SelectEyeFn        = const char* (*)(int);
using TriggerCaptureFn   = const char* (*)(int);
using SetTemplateFn      = const char* (*)(const char*);
using CvarsFn            = const char* (*)(const char*);
using EyeSampleFn        = const char* (*)(int, int, int);
using EyeDumpFn          = const char* (*)(int, const char*, int);
using ShaderBytecodeFn   = const char* (*)(const char*, const char*, int, int);
using IntJsonFn          = const char* (*)(int);

struct RenderApi {
    bool resolved{false};
    bool available{false};
    std::string backend_module{};

    SnapshotFn       snapshot{nullptr};
    ResourcesFn      resources{nullptr};
    D3D12Fn          d3d12{nullptr};
    ShadersFn        shaders{nullptr};
    SimpleStrFn      preview{nullptr};
    SimpleStrFn      context{nullptr};
    SetU64Fn         set_selected{nullptr};
    SetIntFn         set_force_resources{nullptr};
    SetIntFn         set_force_shaders{nullptr};
    SetIntFn         set_force_d3d12{nullptr};
    VoidFn           request_shader_reload{nullptr};
    VoidFn           capture_next_d3d12_change{nullptr};
    VoidFn           clear_captured_d3d12_change{nullptr};
    VoidFn           reset_d3d12{nullptr};
    ExportPairsFn    export_pairs{nullptr};
    ExportBundleFn   export_bundle{nullptr};
    IntJsonFn         export_frame_pair_diff{nullptr};

    SimpleStrFn      stereo_summary{nullptr};
    SelectEyeFn      select_eye{nullptr};

    SimpleStrFn      renderdoc_status{nullptr};
    TriggerCaptureFn renderdoc_trigger{nullptr};
    SimpleStrFn      renderdoc_launch_ui{nullptr};
    SetTemplateFn    renderdoc_set_template{nullptr};

    SimpleStrFn      vr_state{nullptr};
    CvarsFn          cvars{nullptr};
    SimpleStrFn      frame_timing{nullptr};
    EyeSampleFn      eye_sample{nullptr};
    EyeDumpFn        eye_dump{nullptr};
    ShaderBytecodeFn shader_bytecode{nullptr};
    IntJsonFn        hunter_capture_active_override_stub{nullptr};
    IntJsonFn        set_runtime_overrides_enabled{nullptr};

    // const char* (int) — used for both set_stereo_trace_enabled and stereo_trace_json
    using IntInJsonOutFn = const char* (*)(int);
    IntInJsonOutFn   set_stereo_trace_enabled{nullptr};
    IntInJsonOutFn   stereo_trace_json{nullptr};
};

std::mutex g_api_mutex{};
RenderApi g_api{};

HMODULE find_uevr_module() {
    // UEVR builds the runtime as UEVRBackend.dll. Some forks rename it; check
    // a small set of known names.
    static const char* kNames[] = {
        "UEVRBackend.dll",
        "UEVRBackend",
        "UEVR.dll",
        "UEVR",
    };
    for (const char* name : kNames) {
        if (HMODULE h = GetModuleHandleA(name); h != nullptr) {
            return h;
        }
    }
    return nullptr;
}

template <typename Fn>
Fn resolve(HMODULE mod, const char* name) {
    return reinterpret_cast<Fn>(GetProcAddress(mod, name));
}

const RenderApi& api() {
    std::lock_guard lock{g_api_mutex};
    if (g_api.resolved) {
        return g_api;
    }
    g_api.resolved = true;

    HMODULE mod = find_uevr_module();
    if (mod == nullptr) {
        return g_api;
    }

    char path[MAX_PATH]{};
    GetModuleFileNameA(mod, path, MAX_PATH);
    g_api.backend_module = path;

    g_api.snapshot                    = resolve<SnapshotFn>(mod, "uevr_render_diag_snapshot_json");
    g_api.resources                   = resolve<ResourcesFn>(mod, "uevr_render_diag_resources_json");
    g_api.d3d12                       = resolve<D3D12Fn>(mod, "uevr_render_diag_d3d12_json");
    g_api.shaders                     = resolve<ShadersFn>(mod, "uevr_render_diag_shaders_json");
    g_api.preview                     = resolve<SimpleStrFn>(mod, "uevr_render_diag_preview_info_json");
    g_api.context                     = resolve<SimpleStrFn>(mod, "uevr_render_diag_context_json");
    g_api.set_selected                = resolve<SetU64Fn>(mod, "uevr_render_diag_set_selected_resource");
    g_api.set_force_resources         = resolve<SetIntFn>(mod, "uevr_render_diag_set_force_resources_sampling");
    g_api.set_force_shaders           = resolve<SetIntFn>(mod, "uevr_render_diag_set_force_shader_tracking");
    g_api.set_force_d3d12             = resolve<SetIntFn>(mod, "uevr_render_diag_set_force_d3d12_diagnostics");
    g_api.request_shader_reload       = resolve<VoidFn>(mod, "uevr_render_diag_request_shader_reload");
    g_api.capture_next_d3d12_change   = resolve<VoidFn>(mod, "uevr_render_diag_capture_next_d3d12_change");
    g_api.clear_captured_d3d12_change = resolve<VoidFn>(mod, "uevr_render_diag_clear_captured_d3d12_change");
    g_api.reset_d3d12                 = resolve<VoidFn>(mod, "uevr_render_diag_reset_d3d12");
    g_api.export_pairs                = resolve<ExportPairsFn>(mod, "uevr_render_diag_export_d3d12_pairs");
    g_api.export_bundle               = resolve<ExportBundleFn>(mod, "uevr_render_diag_export_bundle");
    g_api.export_frame_pair_diff      = resolve<IntJsonFn>(mod, "uevr_render_diag_export_frame_pair_diff_json");

    g_api.stereo_summary              = resolve<SimpleStrFn>(mod, "uevr_render_diag_stereo_summary_json");
    g_api.select_eye                  = resolve<SelectEyeFn>(mod, "uevr_render_diag_select_eye");

    g_api.renderdoc_status            = resolve<SimpleStrFn>(mod, "uevr_render_diag_renderdoc_status_json");
    g_api.renderdoc_trigger           = resolve<TriggerCaptureFn>(mod, "uevr_render_diag_renderdoc_trigger_capture");
    g_api.renderdoc_launch_ui         = resolve<SimpleStrFn>(mod, "uevr_render_diag_renderdoc_launch_ui");
    g_api.renderdoc_set_template      = resolve<SetTemplateFn>(mod, "uevr_render_diag_renderdoc_set_capture_template");

    g_api.vr_state                    = resolve<SimpleStrFn>(mod, "uevr_render_diag_vr_state_json");
    g_api.cvars                       = resolve<CvarsFn>(mod, "uevr_render_diag_cvars_json");
    g_api.frame_timing                = resolve<SimpleStrFn>(mod, "uevr_render_diag_frame_timing_json");
    g_api.eye_sample                  = resolve<EyeSampleFn>(mod, "uevr_render_diag_eye_pixel_sample_json");
    g_api.eye_dump                    = resolve<EyeDumpFn>(mod, "uevr_render_diag_eye_dump_json");
    g_api.shader_bytecode             = resolve<ShaderBytecodeFn>(mod, "uevr_render_diag_shader_bytecode_json");
    g_api.hunter_capture_active_override_stub =
        resolve<IntJsonFn>(mod, "uevr_render_diag_hunter_capture_active_override_stub");
    g_api.set_runtime_overrides_enabled =
        resolve<IntJsonFn>(mod, "uevr_render_diag_set_runtime_overrides_enabled");

    g_api.set_stereo_trace_enabled    = resolve<RenderApi::IntInJsonOutFn>(mod, "uevr_render_diag_set_stereo_trace_enabled");
    g_api.stereo_trace_json           = resolve<RenderApi::IntInJsonOutFn>(mod, "uevr_render_diag_stereo_trace_json");

    g_api.available =
        g_api.snapshot != nullptr &&
        g_api.resources != nullptr &&
        g_api.d3d12 != nullptr &&
        g_api.shaders != nullptr;

    return g_api;
}

void send_json(httplib::Response& res, const json& data, int status = 200) {
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

json unavailable_payload() {
    json payload{
        {"error", "UEVRJ render diagnostics FFI not available"},
        {"hint", "Requires a UEVR build that exports uevr_render_diag_* symbols (UEVRBackend.dll built from the UEVRJ render-diagnostics CAPI changeset)."},
    };
    payload["backend_module"] = api().backend_module;
    return payload;
}

json parse_or_passthrough(const char* raw) {
    if (raw == nullptr) {
        return json{{"error", "FFI returned null"}};
    }
    try {
        return json::parse(raw);
    } catch (const std::exception& e) {
        return json{{"error", "FFI returned invalid JSON"}, {"detail", e.what()}, {"raw", raw}};
    }
}

int get_int_param(const httplib::Request& req, const char* key, int fallback) {
    if (!req.has_param(key)) {
        return fallback;
    }
    try {
        return std::stoi(req.get_param_value(key));
    } catch (...) {
        return fallback;
    }
}

uint64_t get_u64_param(const httplib::Request& req, const char* key, uint64_t fallback) {
    if (!req.has_param(key)) {
        return fallback;
    }
    try {
        return std::stoull(req.get_param_value(key));
    } catch (...) {
        return fallback;
    }
}

int truthy_param(const httplib::Request& req, const char* key, int fallback) {
    if (!req.has_param(key)) {
        return fallback;
    }
    auto v = req.get_param_value(key);
    if (v == "1" || v == "true" || v == "yes" || v == "on") return 1;
    if (v == "0" || v == "false" || v == "no" || v == "off") return 0;
    try {
        return std::stoi(v) != 0 ? 1 : 0;
    } catch (...) {
        return fallback;
    }
}

int truthy_body_or_param(const httplib::Request& req, const char* key, int fallback) {
    const int from_query = truthy_param(req, key, -1);
    if (from_query >= 0) {
        return from_query;
    }

    try {
        auto body = json::parse(req.body);
        if (body.contains(key)) {
            if (body[key].is_boolean()) return body[key].get<bool>() ? 1 : 0;
            if (body[key].is_number()) return body[key].get<int>() != 0 ? 1 : 0;
            if (body[key].is_string()) {
                auto v = body[key].get<std::string>();
                std::transform(v.begin(), v.end(), v.begin(), [](unsigned char c){ return (char)std::tolower(c); });
                if (v == "1" || v == "true" || v == "yes" || v == "on") return 1;
                if (v == "0" || v == "false" || v == "no" || v == "off") return 0;
            }
        }
    } catch (...) {
        // fall through
    }

    return fallback;
}

std::string body_or_param_string(const httplib::Request& req, const char* key) {
    if (req.has_param(key)) {
        return req.get_param_value(key);
    }
    if (!req.body.empty()) {
        try {
            auto body = json::parse(req.body);
            if (body.contains(key) && body[key].is_string()) {
                return body[key].get<std::string>();
            }
        } catch (...) {
            // fall through
        }
    }
    return {};
}

int body_or_param_int(const httplib::Request& req, const char* key, int fallback) {
    if (req.has_param(key)) {
        try {
            return std::stoi(req.get_param_value(key));
        } catch (...) {
            return fallback;
        }
    }

    if (!req.body.empty()) {
        try {
            auto body = json::parse(req.body);
            if (body.contains(key)) {
                if (body[key].is_number_integer()) {
                    return body[key].get<int>();
                }
                if (body[key].is_boolean()) {
                    return body[key].get<bool>() ? 1 : 0;
                }
                if (body[key].is_string()) {
                    return std::stoi(body[key].get<std::string>());
                }
            }
        } catch (...) {
            // fall through
        }
    }

    return fallback;
}

int stage_selector_to_hunter_code(const std::string& raw, int fallback = 0) {
    auto s = raw;
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c){ return (char)std::tolower(c); });
    if (s == "ps" || s == "pixel" || s == "pixel_shader" || s == "0") return 0;
    if (s == "vs" || s == "vertex" || s == "vertex_shader" || s == "1") return 1;
    if (s == "cs" || s == "compute" || s == "compute_shader" || s == "2") return 2;
    return fallback;
}

} // namespace

namespace RenderRoutes {

void register_routes(httplib::Server& server) {
    // ── Status / capability probe ─────────────────────────────────────
    server.Get("/api/render/status", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        json result{
            {"available", a.available},
            {"backend_module", a.backend_module},
            {"resolved_symbols", json::object()},
        };
        result["resolved_symbols"]["snapshot"] = a.snapshot != nullptr;
        result["resolved_symbols"]["resources"] = a.resources != nullptr;
        result["resolved_symbols"]["d3d12"] = a.d3d12 != nullptr;
        result["resolved_symbols"]["shaders"] = a.shaders != nullptr;
        result["resolved_symbols"]["preview"] = a.preview != nullptr;
        result["resolved_symbols"]["context"] = a.context != nullptr;
        result["resolved_symbols"]["set_selected_resource"] = a.set_selected != nullptr;
        result["resolved_symbols"]["set_force_resources_sampling"] = a.set_force_resources != nullptr;
        result["resolved_symbols"]["set_force_shader_tracking"] = a.set_force_shaders != nullptr;
        result["resolved_symbols"]["set_force_d3d12_diagnostics"] = a.set_force_d3d12 != nullptr;
        result["resolved_symbols"]["request_shader_reload"] = a.request_shader_reload != nullptr;
        result["resolved_symbols"]["capture_next_d3d12_change"] = a.capture_next_d3d12_change != nullptr;
        result["resolved_symbols"]["clear_captured_d3d12_change"] = a.clear_captured_d3d12_change != nullptr;
        result["resolved_symbols"]["reset_d3d12"] = a.reset_d3d12 != nullptr;
        result["resolved_symbols"]["export_d3d12_pairs"] = a.export_pairs != nullptr;
        result["resolved_symbols"]["export_bundle"] = a.export_bundle != nullptr;
        result["resolved_symbols"]["export_frame_pair_diff"] = a.export_frame_pair_diff != nullptr;
        result["resolved_symbols"]["stereo_summary"] = a.stereo_summary != nullptr;
        result["resolved_symbols"]["select_eye"] = a.select_eye != nullptr;
        result["resolved_symbols"]["renderdoc_status"] = a.renderdoc_status != nullptr;
        result["resolved_symbols"]["renderdoc_trigger"] = a.renderdoc_trigger != nullptr;
        result["resolved_symbols"]["renderdoc_launch_ui"] = a.renderdoc_launch_ui != nullptr;
        result["resolved_symbols"]["renderdoc_set_template"] = a.renderdoc_set_template != nullptr;
        result["resolved_symbols"]["vr_state"] = a.vr_state != nullptr;
        result["resolved_symbols"]["cvars"] = a.cvars != nullptr;
        result["resolved_symbols"]["frame_timing"] = a.frame_timing != nullptr;
        result["resolved_symbols"]["eye_sample"] = a.eye_sample != nullptr;
        result["resolved_symbols"]["eye_dump"] = a.eye_dump != nullptr;
        result["resolved_symbols"]["shader_bytecode"] = a.shader_bytecode != nullptr;
        result["resolved_symbols"]["hunter_capture_active_override_stub"] =
            a.hunter_capture_active_override_stub != nullptr;
        result["resolved_symbols"]["set_runtime_overrides_enabled"] =
            a.set_runtime_overrides_enabled != nullptr;
        result["resolved_symbols"]["set_stereo_trace_enabled"] = a.set_stereo_trace_enabled != nullptr;
        result["resolved_symbols"]["stereo_trace_json"] = a.stereo_trace_json != nullptr;
        if (a.context != nullptr) {
            result["context"] = parse_or_passthrough(a.context());
        }
        send_json(res, result);
    });

    // ── Snapshots ─────────────────────────────────────────────────────
    server.Get("/api/render/snapshot", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.snapshot == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_resources       = get_int_param(req, "maxResources", 512);
        const int max_d3d12_events    = get_int_param(req, "maxD3d12Events", 64);
        const int max_distinct_pairs  = get_int_param(req, "maxDistinctPairs", 64);
        const int max_pso_aggregates  = get_int_param(req, "maxPsoAggregates", 64);
        send_json(res, parse_or_passthrough(a.snapshot(max_resources, max_d3d12_events, max_distinct_pairs, max_pso_aggregates)));
    });

    server.Get("/api/render/resources", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.resources == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_resources = get_int_param(req, "maxResources", 1024);
        send_json(res, parse_or_passthrough(a.resources(max_resources)));
    });

    server.Get("/api/render/d3d12", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.d3d12 == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_heaps  = get_int_param(req, "maxHeaps", 64);
        const int max_events = get_int_param(req, "maxEvents", 64);
        send_json(res, parse_or_passthrough(a.d3d12(max_heaps, max_events)));
    });

    server.Get("/api/render/shaders", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.shaders == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_pairs  = get_int_param(req, "maxDistinctPairs", 64);
        const int max_aggs   = get_int_param(req, "maxPsoAggregates", 64);
        send_json(res, parse_or_passthrough(a.shaders(max_pairs, max_aggs)));
    });

    server.Get("/api/render/preview", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.preview == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.preview()));
    });

    server.Get("/api/render/context", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.context == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.context()));
    });

    // ── Mutators ──────────────────────────────────────────────────────
    server.Post("/api/render/selected-resource", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.set_selected == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const uint64_t key = get_u64_param(req, "key", [&]() -> uint64_t {
            try {
                auto body = json::parse(req.body);
                if (body.contains("key") && body["key"].is_number()) {
                    return body["key"].get<uint64_t>();
                }
            } catch (...) {}
            return 0;
        }());
        a.set_selected(key);
        PipeServer::get().log("Render: set_selected_resource(" + std::to_string(key) + ")");
        send_json(res, json{{"ok", true}, {"key", key}});
    });

    auto register_force_toggle = [&server](const char* path, SetIntFn RenderApi::*member, const char* label) {
        server.Post(path, [member, label](const httplib::Request& req, httplib::Response& res) {
            const auto& a = api();
            SetIntFn fn = a.*member;
            if (fn == nullptr) { send_json(res, unavailable_payload(), 503); return; }
            const int enabled = truthy_param(req, "enabled", [&]() {
                try {
                    auto body = json::parse(req.body);
                    if (body.contains("enabled")) {
                        if (body["enabled"].is_boolean()) return body["enabled"].get<bool>() ? 1 : 0;
                        if (body["enabled"].is_number()) return body["enabled"].get<int>() != 0 ? 1 : 0;
                    }
                } catch (...) {}
                return 1;
            }());
            fn(enabled);
            PipeServer::get().log(std::string("Render: ") + label + "=" + (enabled ? "on" : "off"));
            send_json(res, json{{"ok", true}, {"enabled", enabled != 0}});
        });
    };
    register_force_toggle("/api/render/force-resources-sampling", &RenderApi::set_force_resources, "force_resources_sampling");
    register_force_toggle("/api/render/force-shader-tracking",    &RenderApi::set_force_shaders,   "force_shader_tracking");
    register_force_toggle("/api/render/force-d3d12-diagnostics",  &RenderApi::set_force_d3d12,     "force_d3d12_diagnostics");

    server.Post("/api/render/runtime-overrides", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.set_runtime_overrides_enabled == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int enabled = truthy_body_or_param(req, "enabled", 1);
        PipeServer::get().log(std::string("Render: runtime_overrides=") + (enabled ? "on" : "off"));
        send_json(res, parse_or_passthrough(a.set_runtime_overrides_enabled(enabled)));
    });

    auto register_void_action = [&server](const char* path, VoidFn RenderApi::*member, const char* label) {
        server.Post(path, [member, label](const httplib::Request&, httplib::Response& res) {
            const auto& a = api();
            VoidFn fn = a.*member;
            if (fn == nullptr) { send_json(res, unavailable_payload(), 503); return; }
            fn();
            PipeServer::get().log(std::string("Render: ") + label);
            send_json(res, json{{"ok", true}, {"action", label}});
        });
    };
    register_void_action("/api/render/request-shader-reload",       &RenderApi::request_shader_reload,       "request_shader_reload");
    register_void_action("/api/render/capture-next-d3d12-change",   &RenderApi::capture_next_d3d12_change,   "capture_next_d3d12_change");
    register_void_action("/api/render/clear-captured-d3d12-change", &RenderApi::clear_captured_d3d12_change, "clear_captured_d3d12_change");
    register_void_action("/api/render/reset-d3d12",                 &RenderApi::reset_d3d12,                 "reset_d3d12");

    // ── Disk exports ──────────────────────────────────────────────────
    server.Post("/api/render/export-d3d12-pairs", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.export_pairs == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        std::string format = body_or_param_string(req, "format");
        const int as_csv = (format == "csv" || format == "CSV") ? 1 : 0;
        auto result = parse_or_passthrough(a.export_pairs(as_csv));
        result["format"] = as_csv ? "csv" : "json";
        send_json(res, result);
    });

    server.Post("/api/render/export-bundle", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.export_bundle == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        std::string profile = body_or_param_string(req, "profileName");
        std::string backend = body_or_param_string(req, "backend");
        const char* profile_ptr = profile.empty() ? nullptr : profile.c_str();
        const char* backend_ptr = backend.empty() ? nullptr : backend.c_str();
        send_json(res, parse_or_passthrough(a.export_bundle(profile_ptr, backend_ptr)));
    });

    server.Post("/api/render/export-frame-pair-diff", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.export_frame_pair_diff == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_events = body_or_param_int(req, "maxEvents", 512);
        send_json(res, parse_or_passthrough(a.export_frame_pair_diff(max_events)));
    });

    // ── Stereo diagnostics ────────────────────────────────────────────
    server.Get("/api/render/stereo-summary", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.stereo_summary == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.stereo_summary()));
    });

    server.Post("/api/render/select-eye", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.select_eye == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        std::string side = body_or_param_string(req, "side");
        std::string s = side;
        std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c){ return (char)std::tolower(c); });
        int code = 0;
        if (s == "right" || s == "r" || s == "1") code = 1;
        else if (s == "left" || s == "l" || s == "0") code = 0;
        else code = get_int_param(req, "side", 0);
        send_json(res, parse_or_passthrough(a.select_eye(code)));
    });

    // ── RenderDoc ─────────────────────────────────────────────────────
    server.Get("/api/render/renderdoc/status", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.renderdoc_status == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.renderdoc_status()));
    });

    server.Post("/api/render/renderdoc/trigger", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.renderdoc_trigger == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int frames = get_int_param(req, "frames", [&]() {
            try {
                auto body = json::parse(req.body);
                if (body.contains("frames") && body["frames"].is_number()) {
                    return body["frames"].get<int>();
                }
            } catch (...) {}
            return 1;
        }());
        send_json(res, parse_or_passthrough(a.renderdoc_trigger(frames)));
    });

    server.Post("/api/render/renderdoc/launch-ui", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.renderdoc_launch_ui == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.renderdoc_launch_ui()));
    });

    server.Post("/api/render/renderdoc/set-template", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.renderdoc_set_template == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        std::string tmpl = body_or_param_string(req, "template");
        const char* p = tmpl.empty() ? nullptr : tmpl.c_str();
        send_json(res, parse_or_passthrough(a.renderdoc_set_template(p)));
    });

    // ── VR state, cvars, timing, eye sampling/dumps ───────────────────
    server.Get("/api/render/vr-state", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.vr_state == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.vr_state()));
    });

    server.Get("/api/render/cvars", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.cvars == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        std::string filter = req.has_param("filter") ? req.get_param_value("filter") : std::string{};
        send_json(res, parse_or_passthrough(a.cvars(filter.empty() ? nullptr : filter.c_str())));
    });

    server.Get("/api/render/frame-timing", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.frame_timing == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.frame_timing()));
    });

    server.Get("/api/render/eye-sample", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.eye_sample == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        std::string side = req.has_param("side") ? req.get_param_value("side") : "left";
        std::string s = side;
        std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c){ return (char)std::tolower(c); });
        int code = (s == "right" || s == "r" || s == "1") ? 1 : 0;
        const int sample_w = get_int_param(req, "sampleW", 64);
        const int sample_h = get_int_param(req, "sampleH", 64);
        send_json(res, parse_or_passthrough(a.eye_sample(code, sample_w, sample_h)));
    });

    // ── DXGI debug message pump ──────────────────────────────────────
    server.Get("/api/render/dxgi-messages", [](const httplib::Request& req, httplib::Response& res) {
        const int max_messages = get_int_param(req, "max", 64);

        auto& api = uevr::API::get();
        if (!api || !api->param() || !api->param()->renderer) {
            send_json(res, json{{"error", "renderer info not available"}}, 503);
            return;
        }
        json result;
        result["max_messages"] = max_messages;
        result["messages"] = json::array();
        result["sources"] = json::array();

        auto append_messages = [&](IUnknown* info_queue_raw, const char* source) {
            ComPtr<ID3D11InfoQueue> q11{};
            ComPtr<ID3D12InfoQueue> q12{};
            // Try D3D12 first
            if (info_queue_raw->QueryInterface(IID_PPV_ARGS(&q12)) == S_OK && q12 != nullptr) {
                const UINT64 stored = q12->GetNumStoredMessages();
                result["sources"].push_back({{"source", source}, {"api", "D3D12"}, {"stored", stored}});
                const UINT64 take = stored > (UINT64)max_messages ? max_messages : (int)stored;
                const UINT64 start = stored - take;
                for (UINT64 i = start; i < stored; ++i) {
                    SIZE_T msg_size{};
                    q12->GetMessage(i, nullptr, &msg_size);
                    if (msg_size == 0) continue;
                    std::vector<uint8_t> buf(msg_size);
                    auto* m = reinterpret_cast<D3D12_MESSAGE*>(buf.data());
                    if (q12->GetMessage(i, m, &msg_size) == S_OK) {
                        result["messages"].push_back({
                            {"api", "D3D12"},
                            {"category", (int)m->Category},
                            {"severity", (int)m->Severity},
                            {"id", (int)m->ID},
                            {"description", m->pDescription ? std::string{m->pDescription, m->DescriptionByteLength ? m->DescriptionByteLength - 1 : 0} : std::string{}},
                        });
                    }
                }
                return true;
            }
            if (info_queue_raw->QueryInterface(IID_PPV_ARGS(&q11)) == S_OK && q11 != nullptr) {
                const UINT64 stored = q11->GetNumStoredMessages();
                result["sources"].push_back({{"source", source}, {"api", "D3D11"}, {"stored", stored}});
                const UINT64 take = stored > (UINT64)max_messages ? max_messages : (int)stored;
                const UINT64 start = stored - take;
                for (UINT64 i = start; i < stored; ++i) {
                    SIZE_T msg_size{};
                    q11->GetMessage(i, nullptr, &msg_size);
                    if (msg_size == 0) continue;
                    std::vector<uint8_t> buf(msg_size);
                    auto* m = reinterpret_cast<D3D11_MESSAGE*>(buf.data());
                    if (q11->GetMessage(i, m, &msg_size) == S_OK) {
                        result["messages"].push_back({
                            {"api", "D3D11"},
                            {"category", (int)m->Category},
                            {"severity", (int)m->Severity},
                            {"id", (int)m->ID},
                            {"description", m->pDescription ? std::string{m->pDescription, m->DescriptionByteLength ? m->DescriptionByteLength - 1 : 0} : std::string{}},
                        });
                    }
                }
                return true;
            }
            return false;
        };

        bool got_any = false;
        if (auto* dev = api->param()->renderer->device; dev != nullptr) {
            got_any |= append_messages(reinterpret_cast<IUnknown*>(dev), "device");
        }

        // Also probe the global DXGI info queue (DXGIGetDebugInterface1).
        ComPtr<IDXGIInfoQueue> dxgi_q{};
        HMODULE dxgi_dbg = GetModuleHandleA("dxgidebug.dll");
        if (dxgi_dbg == nullptr) dxgi_dbg = LoadLibraryA("dxgidebug.dll");
        if (dxgi_dbg != nullptr) {
            using PFN_DXGIGetDebugInterface1 = HRESULT (WINAPI*)(UINT, REFIID, void**);
            auto fn = reinterpret_cast<PFN_DXGIGetDebugInterface1>(GetProcAddress(dxgi_dbg, "DXGIGetDebugInterface1"));
            if (fn != nullptr && SUCCEEDED(fn(0, IID_PPV_ARGS(&dxgi_q))) && dxgi_q != nullptr) {
                // DXGI_DEBUG_ALL produces from every producer.
                static const GUID DXGI_DEBUG_ALL_GUID = {0xe48ae283, 0xda80, 0x490b, {0x87, 0xe6, 0x43, 0xe9, 0xa9, 0xcf, 0xda, 0x8}};
                const UINT64 stored = dxgi_q->GetNumStoredMessages(DXGI_DEBUG_ALL_GUID);
                result["sources"].push_back({{"source", "DXGIInfoQueue"}, {"api", "DXGI"}, {"stored", stored}});
                const UINT64 take = stored > (UINT64)max_messages ? max_messages : (int)stored;
                const UINT64 start = stored - take;
                for (UINT64 i = start; i < stored; ++i) {
                    SIZE_T msg_size{};
                    dxgi_q->GetMessage(DXGI_DEBUG_ALL_GUID, i, nullptr, &msg_size);
                    if (msg_size == 0) continue;
                    std::vector<uint8_t> buf(msg_size);
                    auto* m = reinterpret_cast<DXGI_INFO_QUEUE_MESSAGE*>(buf.data());
                    if (SUCCEEDED(dxgi_q->GetMessage(DXGI_DEBUG_ALL_GUID, i, m, &msg_size))) {
                        result["messages"].push_back({
                            {"api", "DXGI"},
                            {"category", (int)m->Category},
                            {"severity", (int)m->Severity},
                            {"id", (int)m->ID},
                            {"description", m->pDescription ? std::string{m->pDescription, m->DescriptionByteLength ? m->DescriptionByteLength - 1 : 0} : std::string{}},
                        });
                    }
                }
                got_any = true;
            }
        }
        if (!got_any) {
            result["hint"] = "No InfoQueue available. The D3D debug layer must be enabled at device creation "
                             "(D3D12: ID3D12Debug::EnableDebugLayer before D3D12CreateDevice; "
                             "D3D11: D3D11_CREATE_DEVICE_DEBUG). UEVR does NOT enable this by default. "
                             "Enable via the DirectX Control Panel or a separate launcher.";
        }
        send_json(res, result);
    });

    // ── D3D12 stereo trace ────────────────────────────────────────────
    server.Get("/api/render/stereo-trace", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.stereo_trace_json == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int reset = truthy_param(req, "reset", 0);
        send_json(res, parse_or_passthrough(a.stereo_trace_json(reset)));
    });

    server.Post("/api/render/stereo-trace/enable", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.set_stereo_trace_enabled == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int enabled = truthy_param(req, "enabled", [&]() {
            try {
                auto body = json::parse(req.body);
                if (body.contains("enabled")) {
                    if (body["enabled"].is_boolean()) return body["enabled"].get<bool>() ? 1 : 0;
                    if (body["enabled"].is_number()) return body["enabled"].get<int>() != 0 ? 1 : 0;
                }
            } catch (...) {}
            return 1;
        }());
        send_json(res, parse_or_passthrough(a.set_stereo_trace_enabled(enabled)));
    });

    server.Post("/api/render/eye-dump", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.eye_dump == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        std::string side = body_or_param_string(req, "side");
        std::string s = side;
        std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c){ return (char)std::tolower(c); });
        int side_code = (s == "right" || s == "r" || s == "1") ? 1 : 0;
        std::string out_path = body_or_param_string(req, "outPath");
        std::string fmt_str = body_or_param_string(req, "format");
        std::transform(fmt_str.begin(), fmt_str.end(), fmt_str.begin(), [](unsigned char c){ return (char)std::tolower(c); });
        int fmt = 1; // JPG default (smaller for LLM context)
        if (fmt_str == "png") fmt = 0;
        else if (fmt_str == "jpg" || fmt_str == "jpeg") fmt = 1;
        else if (fmt_str == "bmp") fmt = 2;
        send_json(res, parse_or_passthrough(a.eye_dump(side_code, out_path.empty() ? nullptr : out_path.c_str(), fmt)));
    });

    // ── Shader bytecode / override workflow ──────────────────────────
    server.Get("/api/render/shader-bytecode", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.shader_bytecode == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const std::string stage = req.has_param("stage") ? req.get_param_value("stage") : "any";
        const std::string hash = req.has_param("hash") ? req.get_param_value("hash") : "";
        const int disassemble = truthy_param(req, "disassemble", 0);
        const int max_chars = get_int_param(req, "maxDisassemblyChars", 128 * 1024);
        send_json(res, parse_or_passthrough(a.shader_bytecode(stage.c_str(), hash.c_str(), disassemble, max_chars)));
    });

    server.Post("/api/render/hunter/capture-active-override-stub", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.hunter_capture_active_override_stub == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const std::string stage_text = body_or_param_string(req, "stage");
        const int stage = stage_text.empty()
            ? body_or_param_int(req, "stage", 0)
            : stage_selector_to_hunter_code(stage_text, 0);
        auto result = parse_or_passthrough(a.hunter_capture_active_override_stub(stage));
        result["stage"] = stage;
        send_json(res, result);
    });
}

} // namespace RenderRoutes
