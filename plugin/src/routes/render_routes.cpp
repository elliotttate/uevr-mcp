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
#include <chrono>
#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <mutex>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
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
using EyeRegionSampleFn  = const char* (*)(int, int, int, int, int);
using EyeDumpFn          = const char* (*)(int, const char*, int);
using ShaderBytecodeFn   = const char* (*)(const char*, const char*, int, int);
using IntJsonFn          = const char* (*)(int);
using HunterHighlightFn  = const char* (*)(const char*, int);
using HunterSkipEyeFn    = const char* (*)(const char*, int, int);

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
    SimpleStrFn      stereo_forensics{nullptr};
    SimpleStrFn      stereo_forensics_arm{nullptr};

    SimpleStrFn      renderdoc_status{nullptr};
    TriggerCaptureFn renderdoc_trigger{nullptr};
    SimpleStrFn      renderdoc_launch_ui{nullptr};
    SetTemplateFn    renderdoc_set_template{nullptr};

    SimpleStrFn      vr_state{nullptr};
    CvarsFn          cvars{nullptr};
    SimpleStrFn      frame_timing{nullptr};
    EyeSampleFn      eye_sample{nullptr};
    EyeRegionSampleFn eye_region_sample{nullptr};
    EyeDumpFn        eye_dump{nullptr};
    ShaderBytecodeFn shader_bytecode{nullptr};
    IntJsonFn        hunter_capture_active_override_stub{nullptr};
    HunterHighlightFn hunter_highlight_hash{nullptr};
    HunterSkipEyeFn    hunter_skip_eye_hash{nullptr};
    IntJsonFn        set_runtime_overrides_enabled{nullptr};
    SimpleStrFn      sn2_state{nullptr};

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
    g_api.stereo_forensics            = resolve<SimpleStrFn>(mod, "uevr_render_diag_stereo_forensics_json");
    g_api.stereo_forensics_arm        = resolve<SimpleStrFn>(mod, "uevr_render_diag_stereo_forensics_arm_json");

    g_api.renderdoc_status            = resolve<SimpleStrFn>(mod, "uevr_render_diag_renderdoc_status_json");
    g_api.renderdoc_trigger           = resolve<TriggerCaptureFn>(mod, "uevr_render_diag_renderdoc_trigger_capture");
    g_api.renderdoc_launch_ui         = resolve<SimpleStrFn>(mod, "uevr_render_diag_renderdoc_launch_ui");
    g_api.renderdoc_set_template      = resolve<SetTemplateFn>(mod, "uevr_render_diag_renderdoc_set_capture_template");

    g_api.vr_state                    = resolve<SimpleStrFn>(mod, "uevr_render_diag_vr_state_json");
    g_api.cvars                       = resolve<CvarsFn>(mod, "uevr_render_diag_cvars_json");
    g_api.frame_timing                = resolve<SimpleStrFn>(mod, "uevr_render_diag_frame_timing_json");
    g_api.eye_sample                  = resolve<EyeSampleFn>(mod, "uevr_render_diag_eye_pixel_sample_json");
    g_api.eye_region_sample           = resolve<EyeRegionSampleFn>(mod, "uevr_render_diag_eye_region_sample_json");
    g_api.eye_dump                    = resolve<EyeDumpFn>(mod, "uevr_render_diag_eye_dump_json");
    g_api.shader_bytecode             = resolve<ShaderBytecodeFn>(mod, "uevr_render_diag_shader_bytecode_json");
    g_api.hunter_capture_active_override_stub =
        resolve<IntJsonFn>(mod, "uevr_render_diag_hunter_capture_active_override_stub");
    g_api.hunter_highlight_hash =
        resolve<HunterHighlightFn>(mod, "uevr_render_diag_hunter_highlight_hash_json");
    g_api.hunter_skip_eye_hash =
        resolve<HunterSkipEyeFn>(mod, "uevr_render_diag_hunter_skip_eye_hash_json");
    g_api.set_runtime_overrides_enabled =
        resolve<IntJsonFn>(mod, "uevr_render_diag_set_runtime_overrides_enabled");
    g_api.sn2_state                  = resolve<SimpleStrFn>(mod, "uevr_render_diag_sn2_state_json");

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

int clamp_int(int value, int min_value, int max_value) {
    return std::max(min_value, std::min(value, max_value));
}

double json_array_number(const json& value, size_t index, double fallback = 0.0) {
    if (!value.is_array() || index >= value.size() || !value[index].is_number()) {
        return fallback;
    }
    return value[index].get<double>();
}

json eye_mean_json(const json& sample) {
    if (sample.contains("rgba_mean") && sample["rgba_mean"].is_array()) {
        return sample["rgba_mean"];
    }
    return json::array();
}

json eye_diff_metrics(const json& left, const json& right) {
    const auto left_mean = eye_mean_json(left);
    const auto right_mean = eye_mean_json(right);

    json delta = json::array();
    json abs_delta = json::array();
    double abs_rgb_sum = 0.0;
    double signed_rgb_sum = 0.0;

    for (size_t i = 0; i < 4; ++i) {
        const double d = json_array_number(right_mean, i) - json_array_number(left_mean, i);
        delta.push_back(d);
        abs_delta.push_back(d < 0.0 ? -d : d);
        if (i < 3) {
            abs_rgb_sum += d < 0.0 ? -d : d;
            signed_rgb_sum += d;
        }
    }

    return {
        {"left_rgba_mean", left_mean},
        {"right_rgba_mean", right_mean},
        {"delta_rgba_mean", std::move(delta)},
        {"abs_delta_rgba_mean", std::move(abs_delta)},
        {"mean_abs_delta_rgb", abs_rgb_sum / 3.0},
        {"mean_signed_delta_rgb", signed_rgb_sum / 3.0},
        {"left_black", left.value("is_black", false)},
        {"right_black", right.value("is_black", false)},
    };
}

json sample_eye_region(const RenderApi& a, int side, int x, int y, int sample_w, int sample_h) {
    if (a.eye_region_sample != nullptr) {
        return parse_or_passthrough(a.eye_region_sample(side, x, y, sample_w, sample_h));
    }
    if (a.eye_sample != nullptr) {
        return parse_or_passthrough(a.eye_sample(side, sample_w, sample_h));
    }
    return unavailable_payload();
}

std::string json_string_value(const json& value, const char* key, std::string fallback = {}) {
    if (!value.is_object() || !value.contains(key) || !value[key].is_string()) {
        return fallback;
    }
    return value[key].get<std::string>();
}

int json_int_value(const json& value, const char* key, int fallback = 0) {
    if (!value.is_object() || !value.contains(key) || !value[key].is_number_integer()) {
        return fallback;
    }
    return value[key].get<int>();
}

double json_number_value(const json& value, const char* key, double fallback = 0.0) {
    if (!value.is_object() || !value.contains(key) || !value[key].is_number()) {
        return fallback;
    }
    return value[key].get<double>();
}

bool json_bool_value(const json& value, const char* key, bool fallback = false) {
    if (!value.is_object() || !value.contains(key) || !value[key].is_boolean()) {
        return fallback;
    }
    return value[key].get<bool>();
}

std::string lowercase_ascii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

bool hex_resource_equal(std::string lhs, std::string rhs) {
    if (lhs.empty() || rhs.empty()) {
        return false;
    }
    return lowercase_ascii(std::move(lhs)) == lowercase_ascii(std::move(rhs));
}

bool object_has_resource(const json& value, const std::string& target_resource) {
    if (!value.is_object() || target_resource.empty()) {
        return false;
    }

    for (const char* key : {
        "resource",
        "name",
        "rtv0_resource",
        "uav_resource",
        "dst_resource",
        "destination_resource",
        "target_resource",
        "source_resource",
    }) {
        const auto candidate = json_string_value(value, key);
        if (hex_resource_equal(candidate, target_resource)) {
            return true;
        }
    }
    return false;
}

bool draw_writes_resource(const json& draw, const std::string& target_resource) {
    if (target_resource.empty()) {
        return true;
    }
    if (object_has_resource(draw, target_resource)) {
        return true;
    }

    for (const char* key : {
        "render_target_writes",
        "uav_writes",
        "writes",
        "resource_writes",
    }) {
        if (!draw.contains(key) || !draw[key].is_array()) {
            continue;
        }
        for (const auto& item : draw[key]) {
            if (object_has_resource(item, target_resource)) {
                return true;
            }
        }
    }
    return false;
}

json compact_slot_array(const json& value, size_t max_items = 12) {
    if (!value.is_array()) {
        return json::array();
    }

    json out = json::array();
    for (const auto& item : value) {
        if (out.size() >= max_items) {
            break;
        }
        out.push_back(item);
    }
    return out;
}

json build_eye_diff_grid(
    const RenderApi& a,
    int cols,
    int rows,
    int sample_w,
    int sample_h,
    int eye_w = 0,
    int eye_h = 0
) {
    cols = clamp_int(cols, 1, 8);
    rows = clamp_int(rows, 1, 8);
    sample_w = clamp_int(sample_w, 1, 512);
    sample_h = clamp_int(sample_h, 1, 512);

    if ((eye_w <= 0 || eye_h <= 0) && a.vr_state != nullptr) {
        auto vr = parse_or_passthrough(a.vr_state());
        try {
            eye_w = vr["d3d12"]["left_eye"]["region"]["w"].get<int>();
            eye_h = vr["d3d12"]["left_eye"]["region"]["h"].get<int>();
        } catch (...) {
            eye_w = eye_w <= 0 ? sample_w : eye_w;
            eye_h = eye_h <= 0 ? sample_h : eye_h;
        }
    }
    if (eye_w <= 0) eye_w = sample_w;
    if (eye_h <= 0) eye_h = sample_h;

    const int max_x = std::max(0, eye_w - sample_w);
    const int max_y = std::max(0, eye_h - sample_h);
    json cells = json::array();
    json max_cell = nullptr;
    double max_delta = -1.0;

    for (int row = 0; row < rows; ++row) {
        const int y = rows <= 1 ? max_y / 2 : static_cast<int>((static_cast<int64_t>(row) * max_y) / (rows - 1));
        for (int col = 0; col < cols; ++col) {
            const int x = cols <= 1 ? max_x / 2 : static_cast<int>((static_cast<int64_t>(col) * max_x) / (cols - 1));
            auto left = sample_eye_region(a, 0, x, y, sample_w, sample_h);
            auto right = sample_eye_region(a, 1, x, y, sample_w, sample_h);
            auto metrics = eye_diff_metrics(left, right);
            const double delta = metrics.value("mean_abs_delta_rgb", 0.0);
            json cell{
                {"row", row},
                {"col", col},
                {"x", x},
                {"y", y},
                {"metrics", std::move(metrics)},
            };
            if (delta > max_delta) {
                max_delta = delta;
                max_cell = cell;
            }
            cells.push_back(std::move(cell));
        }
    }

    return {
        {"ok", true},
        {"cols", cols},
        {"rows", rows},
        {"eye_w", eye_w},
        {"eye_h", eye_h},
        {"sample_w", sample_w},
        {"sample_h", sample_h},
        {"max_cell", std::move(max_cell)},
        {"cells", std::move(cells)},
    };
}

json summarize_draw_event(const json& draw) {
    json roots = json::array();
    std::set<int> root_slots{};
    int descriptor_reads_with_producer = 0;
    if (draw.contains("descriptor_reads") && draw["descriptor_reads"].is_array()) {
        for (const auto& read : draw["descriptor_reads"]) {
            if (read.contains("root_parameter") && read["root_parameter"].is_number_integer()) {
                root_slots.insert(read["root_parameter"].get<int>());
            }
            if (json_int_value(read, "producer_draw", 0) != 0) {
                ++descriptor_reads_with_producer;
            }
        }
    }
    for (int slot : root_slots) {
        roots.push_back(slot);
    }

    return {
        {"frame", json_int_value(draw, "frame", 0)},
        {"draw_index", json_int_value(draw, "draw_index", 0)},
        {"eye_bucket", json_int_value(draw, "eye_bucket", -1)},
        {"kind", json_string_value(draw, "kind")},
        {"pipeline_state", json_string_value(draw, "pipeline_state")},
        {"root_signature", json_string_value(draw, "root_signature")},
        {"rtv0_resource", json_string_value(draw, "rtv0_resource")},
        {"descriptor_reads_count", draw.contains("descriptor_reads") && draw["descriptor_reads"].is_array()
            ? static_cast<int>(draw["descriptor_reads"].size())
            : 0},
        {"descriptor_reads_with_producer", descriptor_reads_with_producer},
        {"descriptor_read_root_parameters", std::move(roots)},
        {"graphics_root_cbvs", compact_slot_array(draw.value("graphics_root_cbvs", json::array()))},
        {"graphics_root_descriptor_table_resource_hash", compact_slot_array(draw.value("graphics_root_descriptor_table_resource_hash", json::array()))},
        {"compute_root_cbvs", compact_slot_array(draw.value("compute_root_cbvs", json::array()))},
        {"compute_root_descriptor_table_resource_hash", compact_slot_array(draw.value("compute_root_descriptor_table_resource_hash", json::array()))},
    };
}

std::string shader_stage_hash(const json& shader, const char* top_level_key, const char* nested_stage_key) {
    auto value = json_string_value(shader, top_level_key);
    if (!value.empty()) {
        return value;
    }
    if (shader.contains(nested_stage_key) && shader[nested_stage_key].is_object()) {
        value = json_string_value(shader[nested_stage_key], "hash");
        if (!value.empty()) {
            return value;
        }
    }
    return {};
}

std::string shader_stage_crc(const json& shader, const char* top_level_key, const char* nested_stage_key) {
    auto value = json_string_value(shader, top_level_key);
    if (!value.empty()) {
        return value;
    }
    if (shader.contains(nested_stage_key) && shader[nested_stage_key].is_object()) {
        value = json_string_value(shader[nested_stage_key], "crc32");
        if (!value.empty()) {
            return value;
        }
    }
    return {};
}

json rank_capture_candidates(const json& d3d12, const json& shaders, const json& eye_diff_grid, int limit) {
    limit = clamp_int(limit, 1, 64);

    std::unordered_map<std::string, json> shader_by_pso{};
    auto index_shader = [&](const json& item) {
        for (const char* key : {"original_pso", "last_bound_pso", "original_pipeline_state", "bound_pipeline_state"}) {
            const auto pso = json_string_value(item, key);
            if (!pso.empty()) {
                shader_by_pso[pso] = item;
            }
        }
    };
    if (shaders.contains("d3d12_pso_aggregates") && shaders["d3d12_pso_aggregates"].is_array()) {
        for (const auto& item : shaders["d3d12_pso_aggregates"]) {
            index_shader(item);
        }
    }
    if (shaders.contains("distinct_d3d12_pairs") && shaders["distinct_d3d12_pairs"].is_array()) {
        for (const auto& item : shaders["distinct_d3d12_pairs"]) {
            index_shader(item);
        }
    }

    std::unordered_map<std::string, std::vector<json>> draws_by_pso{};
    std::unordered_map<std::string, json> draw_by_pso_index{};
    if (d3d12.contains("recent_draw_events") && d3d12["recent_draw_events"].is_array()) {
        for (const auto& draw : d3d12["recent_draw_events"]) {
            const auto pso = json_string_value(draw, "pipeline_state");
            if (pso.empty()) {
                continue;
            }
            draws_by_pso[pso].push_back(draw);
            const auto index = json_int_value(draw, "draw_index", 0);
            draw_by_pso_index[pso + "#" + std::to_string(index)] = draw;
        }
    }

    json ranked = json::array();
    const auto asym = d3d12.contains("symmetry_oracle") ? d3d12["symmetry_oracle"].value("asymmetric_psos", json::array()) : json::array();
    if (asym.is_array()) {
        for (const auto& item : asym) {
            const auto pso = json_string_value(item, "pipeline_state");
            if (pso.empty()) {
                continue;
            }

            double score = 0.0;
            json reasons = json::array();
            if (json_bool_value(item, "binding_mismatch")) {
                score += 50.0;
                reasons.push_back("left/right binding fingerprint mismatch");
            }
            if (json_bool_value(item, "count_mismatch")) {
                score += 25.0;
                reasons.push_back("left/right draw count mismatch");
            }

            const int left_count = json_int_value(item, "left_count", 0);
            const int right_count = json_int_value(item, "right_count", 0);
            const int unknown_count = json_int_value(item, "unknown_count", 0);
            score += std::min(left_count + right_count, 40) * 0.25;
            score -= std::min(unknown_count, 30) * 0.25;

            json representative = json::array();
            for (const auto index : {json_int_value(item, "left_draw_index", 0), json_int_value(item, "right_draw_index", 0)}) {
                if (index <= 0) {
                    continue;
                }
                const auto it = draw_by_pso_index.find(pso + "#" + std::to_string(index));
                if (it != draw_by_pso_index.end()) {
                    representative.push_back(summarize_draw_event(it->second));
                }
            }
            if (representative.empty()) {
                const auto it = draws_by_pso.find(pso);
                if (it != draws_by_pso.end()) {
                    for (const auto& draw : it->second) {
                        if (representative.size() >= 2) {
                            break;
                        }
                        representative.push_back(summarize_draw_event(draw));
                    }
                }
            }

            int descriptor_reads = 0;
            int reads_with_producer = 0;
            bool has_root_signature = false;
            for (const auto& draw : representative) {
                descriptor_reads += json_int_value(draw, "descriptor_reads_count", 0);
                reads_with_producer += json_int_value(draw, "descriptor_reads_with_producer", 0);
                const auto root_signature = json_string_value(draw, "root_signature");
                if (!root_signature.empty() && root_signature != "0x0") {
                    has_root_signature = true;
                }
            }
            if (has_root_signature) {
                score += 8.0;
            }
            if (descriptor_reads > 0) {
                score += std::min(descriptor_reads, 30) * 0.4;
            }
            if (reads_with_producer > 0) {
                score += std::min(reads_with_producer, 10) * 2.0;
                reasons.push_back("descriptor reads have producer lineage");
            }

            json shader = nullptr;
            std::string ps_hash{};
            std::string vs_hash{};
            std::string gs_hash{};
            if (const auto it = shader_by_pso.find(pso); it != shader_by_pso.end()) {
                shader = it->second;
                ps_hash = shader_stage_hash(shader, "ps_hash", "pixel_shader");
                vs_hash = shader_stage_hash(shader, "vs_hash", "vertex_shader");
                gs_hash = shader_stage_hash(shader, "gs_hash", "geometry_shader");
                const auto note = json_string_value(shader, "tracking_note");
                if (!ps_hash.empty()) {
                    score += 28.0;
                    reasons.push_back("pixel shader hash available for override/disassembly");
                }
                if (!vs_hash.empty()) {
                    score += 6.0;
                }
                if (!gs_hash.empty()) {
                    score += 10.0;
                    reasons.push_back("geometry shader hash available; useful for slice/view routing fixes");
                }
                if (note.find("compute") != std::string::npos) {
                    score -= 8.0;
                    reasons.push_back("compute/stream PSO may not map directly to final color");
                }
                if (note.find("mesh") != std::string::npos) {
                    score -= 5.0;
                    reasons.push_back("mesh/stream PSO has limited shader metadata");
                }
            }
            if (ps_hash.empty() && !vs_hash.empty()) {
                score -= 22.0;
                reasons.push_back("no pixel shader bytecode observed; lower priority for color/atmosphere fixes");
            } else if (ps_hash.empty()) {
                score -= 12.0;
                reasons.push_back("no shader bytecode hash observed in this capture window");
            }

            json root_signature = nullptr;
            if (!representative.empty()) {
                const auto root = json_string_value(representative.front(), "root_signature");
                if (!root.empty()) {
                    root_signature = root;
                }
            }

            ranked.push_back({
                {"score", score},
                {"pipeline_state", pso},
                {"root_signature", std::move(root_signature)},
                {"candidate_kind", !ps_hash.empty() ? "pixel_shader" : (!vs_hash.empty() ? "vertex_only" : "unknown")},
                {"ps_hash", ps_hash},
                {"vs_hash", vs_hash},
                {"gs_hash", gs_hash},
                {"symmetry", item},
                {"shader", std::move(shader)},
                {"representative_draws", std::move(representative)},
                {"reasons", std::move(reasons)},
            });
        }
    }

    std::sort(ranked.begin(), ranked.end(), [](const json& lhs, const json& rhs) {
        return json_number_value(lhs, "score", 0.0) > json_number_value(rhs, "score", 0.0);
    });

    while (ranked.size() > static_cast<size_t>(limit)) {
        ranked.erase(ranked.end() - 1);
    }

    json roi_summary = nullptr;
    if (eye_diff_grid.is_object()) {
        roi_summary = {
            {"max_cell", eye_diff_grid.value("max_cell", json(nullptr))},
            {"cols", eye_diff_grid.value("cols", 0)},
            {"rows", eye_diff_grid.value("rows", 0)},
            {"sample_w", eye_diff_grid.value("sample_w", 0)},
            {"sample_h", eye_diff_grid.value("sample_h", 0)},
        };
    }

    return {
        {"ranked", std::move(ranked)},
        {"roi", std::move(roi_summary)},
        {"asymmetric_pso_count", d3d12.contains("symmetry_oracle") ? d3d12["symmetry_oracle"].value("asymmetric_pso_count", 0) : 0},
        {"recent_draws_analyzed", d3d12.contains("symmetry_oracle") ? d3d12["symmetry_oracle"].value("recent_draws_analyzed", 0) : 0},
        {"tracked_pso_count", d3d12.contains("symmetry_oracle") ? d3d12["symmetry_oracle"].value("tracked_pso_count", 0) : 0},
    };
}

json roi_writer_candidates(
    const json& d3d12,
    const json& shaders,
    int side_code,
    int roi_x,
    int roi_y,
    int roi_w,
    int roi_h,
    int eye_w,
    int eye_h,
    int limit,
    bool include_dispatch,
    const std::string& target_resource
) {
    limit = clamp_int(limit, 1, 256);
    roi_w = clamp_int(roi_w, 1, 8192);
    roi_h = clamp_int(roi_h, 1, 8192);
    eye_w = std::max(eye_w, roi_x + roi_w);
    eye_h = std::max(eye_h, roi_y + roi_h);

    std::unordered_map<std::string, json> shader_by_pso{};
    auto index_shader = [&](const json& item) {
        for (const char* key : {"original_pso", "last_bound_pso", "original_pipeline_state", "bound_pipeline_state"}) {
            const auto pso = json_string_value(item, key);
            if (!pso.empty()) {
                shader_by_pso[pso] = item;
            }
        }
    };
    if (shaders.contains("d3d12_pso_aggregates") && shaders["d3d12_pso_aggregates"].is_array()) {
        for (const auto& item : shaders["d3d12_pso_aggregates"]) {
            index_shader(item);
        }
    }
    if (shaders.contains("distinct_d3d12_pairs") && shaders["distinct_d3d12_pairs"].is_array()) {
        for (const auto& item : shaders["distinct_d3d12_pairs"]) {
            index_shader(item);
        }
    }

    int expected_bucket = -1;
    if (side_code == 0) expected_bucket = 1;
    if (side_code == 1) expected_bucket = 2;

    json out = json::array();
    if (!d3d12.contains("recent_draw_events") || !d3d12["recent_draw_events"].is_array()) {
        return out;
    }

    for (const auto& draw : d3d12["recent_draw_events"]) {
        const auto kind = json_string_value(draw, "kind");
        if (!include_dispatch && kind.find("draw") == std::string::npos) {
            continue;
        }
        if (!draw_writes_resource(draw, target_resource)) {
            continue;
        }
        const int bucket = json_int_value(draw, "eye_bucket", -1);
        if (expected_bucket >= 0 && bucket > 0 && bucket != expected_bucket) {
            continue;
        }
        const auto pso = json_string_value(draw, "pipeline_state");
        json shader = nullptr;
        std::string ps_hash{};
        std::string ps_crc{};
        std::string vs_hash{};
        std::string cs_hash{};
        std::string cs_crc{};
        if (const auto it = shader_by_pso.find(pso); it != shader_by_pso.end()) {
            shader = it->second;
            ps_hash = shader_stage_hash(shader, "ps_hash", "pixel_shader");
            ps_crc = shader_stage_crc(shader, "pixel_crc32", "pixel_shader");
            vs_hash = shader_stage_hash(shader, "vs_hash", "vertex_shader");
            cs_hash = shader_stage_hash(shader, "cs_hash", "compute_shader");
            cs_crc = shader_stage_crc(shader, "cs_crc32", "compute_shader");
        }

        int reads_with_producer = 0;
        if (draw.contains("descriptor_reads") && draw["descriptor_reads"].is_array()) {
            for (const auto& read : draw["descriptor_reads"]) {
                if (json_int_value(read, "producer_draw", 0) != 0) {
                    ++reads_with_producer;
                }
            }
        }

        if (!json_bool_value(draw, "has_viewport")) {
            if (include_dispatch && !target_resource.empty()) {
                out.push_back({
                    {"draw_index", json_int_value(draw, "draw_index", 0)},
                    {"frame", json_int_value(draw, "frame", 0)},
                    {"kind", kind},
                    {"eye_bucket", bucket},
                    {"pipeline_state", pso},
                    {"root_signature", json_string_value(draw, "root_signature")},
                    {"ps_hash", ps_hash},
                    {"ps_crc32", ps_crc},
                    {"vs_hash", vs_hash},
                    {"cs_hash", cs_hash},
                    {"cs_crc32", cs_crc},
                    {"coverage", 1.0},
                    {"mapped_roi", nullptr},
                    {"mapping", "viewportless_target_resource"},
                    {"viewport0", draw.value("viewport0", json::object())},
                    {"scissor0", draw.value("scissor0", json::object())},
                    {"rtv0_resource", json_string_value(draw, "rtv0_resource")},
                    {"prior_rtv0_producer_draw", json_int_value(draw, "prior_rtv0_producer_draw", 0)},
                    {"prior_rtv0_producer_pso", json_string_value(draw, "prior_rtv0_producer_pso")},
                    {"descriptor_reads_count", draw.contains("descriptor_reads") && draw["descriptor_reads"].is_array()
                        ? static_cast<int>(draw["descriptor_reads"].size())
                        : 0},
                    {"descriptor_reads_with_producer", reads_with_producer},
                    {"descriptor_reads", compact_slot_array(draw.value("descriptor_reads", json::array()), 16)},
                    {"render_target_writes", compact_slot_array(draw.value("render_target_writes", json::array()), 8)},
                    {"uav_writes", compact_slot_array(draw.value("uav_writes", json::array()), 8)},
                    {"compute_root_cbvs", compact_slot_array(draw.value("compute_root_cbvs", json::array()), 12)},
                    {"compute_root_descriptor_table_resource_hash", compact_slot_array(draw.value("compute_root_descriptor_table_resource_hash", json::array()), 12)},
                    {"shader", std::move(shader)},
                });
            }
            continue;
        }

        auto vp = draw.value("viewport0", json::object());
        double dx0 = json_number_value(vp, "x", 0.0);
        double dy0 = json_number_value(vp, "y", 0.0);
        const double viewport_w = json_number_value(vp, "w", 0.0);
        const double viewport_h = json_number_value(vp, "h", 0.0);
        double dx1 = dx0 + viewport_w;
        double dy1 = dy0 + viewport_h;

        if (json_bool_value(draw, "has_scissor")) {
            const auto sc = draw.value("scissor0", json::object());
            dx0 = std::max(dx0, json_number_value(sc, "left", dx0));
            dy0 = std::max(dy0, json_number_value(sc, "top", dy0));
            dx1 = std::min(dx1, json_number_value(sc, "right", dx1));
            dy1 = std::min(dy1, json_number_value(sc, "bottom", dy1));
        }

        if (viewport_w <= 0.0 || viewport_h <= 0.0) {
            continue;
        }

        const double eye_aspect = eye_h > 0 ? static_cast<double>(eye_w) / static_cast<double>(eye_h) : 1.0;
        const double draw_aspect = viewport_h > 0.0 ? viewport_w / viewport_h : 0.0;
        const bool unknown_bucket_sbs = expected_bucket >= 0
            && bucket <= 0
            && draw_aspect > eye_aspect * 1.35;
        const double local_viewport_w = unknown_bucket_sbs ? viewport_w * 0.5 : viewport_w;
        const double local_viewport_x = json_number_value(vp, "x", 0.0)
            + (unknown_bucket_sbs && side_code == 1 ? local_viewport_w : 0.0);
        const double sx = local_viewport_w / static_cast<double>(eye_w);
        const double sy = viewport_h / static_cast<double>(eye_h);
        const double mapped_x0 = local_viewport_x + static_cast<double>(roi_x) * sx;
        const double mapped_y0 = json_number_value(vp, "y", 0.0) + static_cast<double>(roi_y) * sy;
        const double mapped_x1 = mapped_x0 + static_cast<double>(roi_w) * sx;
        const double mapped_y1 = mapped_y0 + static_cast<double>(roi_h) * sy;

        const double ix0 = std::max(mapped_x0, dx0);
        const double iy0 = std::max(mapped_y0, dy0);
        const double ix1 = std::min(mapped_x1, dx1);
        const double iy1 = std::min(mapped_y1, dy1);
        const double iw = ix1 - ix0;
        const double ih = iy1 - iy0;
        if (iw <= 0.0 || ih <= 0.0) {
            continue;
        }

        const double roi_area = static_cast<double>(roi_w) * static_cast<double>(roi_h);
        const double mapped_area = (mapped_x1 - mapped_x0) * (mapped_y1 - mapped_y0);
        const double coverage = mapped_area > 0.0 ? (iw * ih) / mapped_area : (roi_area > 0.0 ? (iw * ih) / roi_area : 0.0);
        out.push_back({
            {"draw_index", json_int_value(draw, "draw_index", 0)},
            {"frame", json_int_value(draw, "frame", 0)},
            {"kind", kind},
            {"eye_bucket", bucket},
            {"pipeline_state", pso},
            {"root_signature", json_string_value(draw, "root_signature")},
            {"ps_hash", ps_hash},
            {"ps_crc32", ps_crc},
            {"vs_hash", vs_hash},
            {"cs_hash", cs_hash},
            {"cs_crc32", cs_crc},
            {"coverage", coverage},
            {"mapped_roi", {{"x0", mapped_x0}, {"y0", mapped_y0}, {"x1", mapped_x1}, {"y1", mapped_y1}}},
            {"mapping", unknown_bucket_sbs ? "split_sbs_unknown_bucket" : "per_viewport_eye"},
            {"viewport0", draw.value("viewport0", json::object())},
            {"scissor0", draw.value("scissor0", json::object())},
            {"rtv0_resource", json_string_value(draw, "rtv0_resource")},
            {"prior_rtv0_producer_draw", json_int_value(draw, "prior_rtv0_producer_draw", 0)},
            {"prior_rtv0_producer_pso", json_string_value(draw, "prior_rtv0_producer_pso")},
            {"descriptor_reads_count", draw.contains("descriptor_reads") && draw["descriptor_reads"].is_array()
                ? static_cast<int>(draw["descriptor_reads"].size())
                : 0},
            {"descriptor_reads_with_producer", reads_with_producer},
            {"descriptor_reads", compact_slot_array(draw.value("descriptor_reads", json::array()), 16)},
            {"render_target_writes", compact_slot_array(draw.value("render_target_writes", json::array()), 8)},
            {"uav_writes", compact_slot_array(draw.value("uav_writes", json::array()), 8)},
            {"graphics_root_cbvs", compact_slot_array(draw.value("graphics_root_cbvs", json::array()), 12)},
            {"graphics_root_descriptor_table_resource_hash", compact_slot_array(draw.value("graphics_root_descriptor_table_resource_hash", json::array()), 12)},
            {"shader", std::move(shader)},
        });
    }

    std::sort(out.begin(), out.end(), [](const json& lhs, const json& rhs) {
        const auto lf = json_int_value(lhs, "frame", 0);
        const auto rf = json_int_value(rhs, "frame", 0);
        if (lf != rf) {
            return lf > rf;
        }
        const auto li = json_int_value(lhs, "draw_index", 0);
        const auto ri = json_int_value(rhs, "draw_index", 0);
        if (li != ri) {
            return li > ri;
        }
        return json_number_value(lhs, "coverage", 0.0) > json_number_value(rhs, "coverage", 0.0);
    });

    while (out.size() > static_cast<size_t>(limit)) {
        out.erase(out.end() - 1);
    }
    return out;
}

size_t json_array_size(const json& value, const char* key) {
    if (!value.is_object() || !value.contains(key) || !value[key].is_array()) {
        return 0;
    }
    return value[key].size();
}

json d3d12_capture_summary(const json& d3d12) {
    int draws_with_root_signature = 0;
    int descriptor_reads = 0;
    int descriptor_reads_with_producer = 0;
    if (d3d12.contains("recent_draw_events") && d3d12["recent_draw_events"].is_array()) {
        for (const auto& draw : d3d12["recent_draw_events"]) {
            const auto root_signature = json_string_value(draw, "root_signature");
            if (!root_signature.empty() && root_signature != "0x0") {
                ++draws_with_root_signature;
            }
            if (draw.contains("descriptor_reads") && draw["descriptor_reads"].is_array()) {
                descriptor_reads += static_cast<int>(draw["descriptor_reads"].size());
                for (const auto& read : draw["descriptor_reads"]) {
                    if (json_int_value(read, "producer_draw", 0) != 0) {
                        ++descriptor_reads_with_producer;
                    }
                }
            }
        }
    }

    const auto symmetry = d3d12.contains("symmetry_oracle") && d3d12["symmetry_oracle"].is_object()
        ? d3d12["symmetry_oracle"]
        : json::object();

    return {
        {"available", d3d12.value("available", false)},
        {"frame", d3d12.value("frame", 0)},
        {"root_signatures", json_array_size(d3d12, "root_signatures")},
        {"pipeline_cache_events", json_array_size(d3d12, "pipeline_cache_events")},
        {"recent_draw_events", json_array_size(d3d12, "recent_draw_events")},
        {"recent_draws_with_root_signature", draws_with_root_signature},
        {"descriptor_reads", descriptor_reads},
        {"descriptor_reads_with_producer", descriptor_reads_with_producer},
        {"asymmetric_pso_count", symmetry.value("asymmetric_pso_count", 0)},
        {"recent_draws_analyzed", symmetry.value("recent_draws_analyzed", 0)},
        {"tracked_pso_count", symmetry.value("tracked_pso_count", 0)},
    };
}

json shader_capture_summary(const json& shaders) {
    const bool has_shader_data =
        json_array_size(shaders, "distinct_d3d12_pairs") > 0 ||
        json_array_size(shaders, "d3d12_pso_aggregates") > 0 ||
        shaders.value("total_d3d12_pair_samples", 0) > 0;
    const auto override_count = json_array_size(shaders, "overrides") > 0
        ? json_array_size(shaders, "overrides")
        : json_array_size(shaders, "shader_overrides");
    return {
        {"available", shaders.contains("error") ? false : shaders.value("available", has_shader_data)},
        {"total_d3d12_pair_samples", shaders.value("total_d3d12_pair_samples", 0)},
        {"distinct_d3d12_pairs", json_array_size(shaders, "distinct_d3d12_pairs")},
        {"d3d12_pso_aggregates", json_array_size(shaders, "d3d12_pso_aggregates")},
        {"shader_overrides", override_count},
        {"overrides", override_count},
    };
}

json make_ranked_candidate_payload(
    const RenderApi& a,
    int max_heaps,
    int max_events,
    int max_pairs,
    int max_aggs,
    int limit,
    bool include_eye_diff,
    int cols,
    int rows,
    int sample_w,
    int sample_h,
    bool include_raw
) {
    json d3d12 = a.d3d12 != nullptr ? parse_or_passthrough(a.d3d12(max_heaps, max_events)) : json{{"available", false}};
    json shaders = a.shaders != nullptr ? parse_or_passthrough(a.shaders(max_pairs, max_aggs)) : json{{"available", false}};
    json eye_grid = nullptr;
    if (include_eye_diff && a.eye_sample != nullptr) {
        eye_grid = build_eye_diff_grid(a, cols, rows, sample_w, sample_h);
    }

    json result{
        {"ok", true},
        {"d3d12_summary", d3d12_capture_summary(d3d12)},
        {"shader_summary", shader_capture_summary(shaders)},
        {"ranked_candidates", rank_capture_candidates(d3d12, shaders, eye_grid, limit)},
    };
    if (!d3d12.value("available", false)) {
        result["capture_required_hint"] = "D3D12 diagnostics are disabled; call /api/render/capture-window for a scoped sample.";
    }
    if (eye_grid.is_object()) {
        result["eye_diff_grid"] = std::move(eye_grid);
    }
    if (include_raw) {
        result["d3d12"] = std::move(d3d12);
        result["shaders"] = std::move(shaders);
    }
    return result;
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
        result["resolved_symbols"]["stereo_forensics"] = a.stereo_forensics != nullptr;
        result["resolved_symbols"]["stereo_forensics_arm"] = a.stereo_forensics_arm != nullptr;
        result["resolved_symbols"]["renderdoc_status"] = a.renderdoc_status != nullptr;
        result["resolved_symbols"]["renderdoc_trigger"] = a.renderdoc_trigger != nullptr;
        result["resolved_symbols"]["renderdoc_launch_ui"] = a.renderdoc_launch_ui != nullptr;
        result["resolved_symbols"]["renderdoc_set_template"] = a.renderdoc_set_template != nullptr;
        result["resolved_symbols"]["vr_state"] = a.vr_state != nullptr;
        result["resolved_symbols"]["cvars"] = a.cvars != nullptr;
        result["resolved_symbols"]["frame_timing"] = a.frame_timing != nullptr;
        result["resolved_symbols"]["eye_sample"] = a.eye_sample != nullptr;
        result["resolved_symbols"]["eye_region_sample"] = a.eye_region_sample != nullptr;
        result["resolved_symbols"]["eye_dump"] = a.eye_dump != nullptr;
        result["resolved_symbols"]["shader_bytecode"] = a.shader_bytecode != nullptr;
        result["resolved_symbols"]["hunter_capture_active_override_stub"] =
            a.hunter_capture_active_override_stub != nullptr;
        result["resolved_symbols"]["hunter_highlight_hash"] =
            a.hunter_highlight_hash != nullptr;
        result["resolved_symbols"]["hunter_skip_eye_hash"] =
            a.hunter_skip_eye_hash != nullptr;
        result["resolved_symbols"]["set_runtime_overrides_enabled"] =
            a.set_runtime_overrides_enabled != nullptr;
        result["resolved_symbols"]["sn2_state"] = a.sn2_state != nullptr;
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

    server.Get("/api/render/root-signatures", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.d3d12 == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_heaps  = get_int_param(req, "maxHeaps", 64);
        const int max_events = get_int_param(req, "maxEvents", 64);
        auto d3d12 = parse_or_passthrough(a.d3d12(max_heaps, max_events));
        if (d3d12.contains("root_signatures") && d3d12["root_signatures"].is_array()) {
            send_json(res, json{
                {"available", d3d12.value("available", false)},
                {"frame", d3d12.value("frame", 0)},
                {"root_signatures", std::move(d3d12["root_signatures"])},
            });
            return;
        }
        send_json(res, json{
            {"available", d3d12.value("available", false)},
            {"frame", d3d12.value("frame", 0)},
            {"root_signatures", json::array()},
            {"source_error", d3d12.value("error", "")},
        });
    });

    server.Get("/api/render/shaders", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.shaders == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_pairs  = get_int_param(req, "maxDistinctPairs", 64);
        const int max_aggs   = get_int_param(req, "maxPsoAggregates", 64);
        send_json(res, parse_or_passthrough(a.shaders(max_pairs, max_aggs)));
    });

    server.Get("/api/render/ranked-candidates", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.d3d12 == nullptr || a.shaders == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int max_heaps = clamp_int(get_int_param(req, "maxHeaps", 16), 1, 256);
        const int max_events = clamp_int(get_int_param(req, "maxEvents", 512), 1, 4096);
        const int max_pairs = clamp_int(get_int_param(req, "maxDistinctPairs", 128), 1, 2048);
        const int max_aggs = clamp_int(get_int_param(req, "maxPsoAggregates", 128), 1, 2048);
        const int limit = clamp_int(get_int_param(req, "limit", 20), 1, 64);
        const bool include_eye_diff = truthy_param(req, "eyeDiff", 0) != 0;
        const bool include_raw = truthy_param(req, "includeRaw", 0) != 0;
        const int cols = clamp_int(get_int_param(req, "cols", 3), 1, 8);
        const int rows = clamp_int(get_int_param(req, "rows", 3), 1, 8);
        const int sample_w = clamp_int(get_int_param(req, "sampleW", 96), 1, 512);
        const int sample_h = clamp_int(get_int_param(req, "sampleH", 96), 1, 512);
        send_json(res, make_ranked_candidate_payload(
            a,
            max_heaps,
            max_events,
            max_pairs,
            max_aggs,
            limit,
            include_eye_diff,
            cols,
            rows,
            sample_w,
            sample_h,
            include_raw
        ));
    });

    auto capture_window = [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.d3d12 == nullptr || a.shaders == nullptr || a.set_force_d3d12 == nullptr || a.set_force_shaders == nullptr) {
            send_json(res, unavailable_payload(), 503);
            return;
        }

        const int ms = clamp_int(body_or_param_int(req, "ms", 1000), 100, 5000);
        const int max_heaps = clamp_int(body_or_param_int(req, "maxHeaps", 16), 1, 256);
        const int max_events = clamp_int(body_or_param_int(req, "maxEvents", 512), 1, 4096);
        const int max_pairs = clamp_int(body_or_param_int(req, "maxDistinctPairs", 128), 1, 2048);
        const int max_aggs = clamp_int(body_or_param_int(req, "maxPsoAggregates", 128), 1, 2048);
        const int limit = clamp_int(body_or_param_int(req, "limit", 20), 1, 64);
        const bool include_eye_diff = truthy_body_or_param(req, "eyeDiff", 1) != 0;
        const bool include_raw = truthy_body_or_param(req, "includeRaw", 0) != 0;
        const bool keep_enabled = truthy_body_or_param(req, "keepEnabled", 0) != 0;
        const int cols = clamp_int(body_or_param_int(req, "cols", 3), 1, 8);
        const int rows = clamp_int(body_or_param_int(req, "rows", 3), 1, 8);
        const int sample_w = clamp_int(body_or_param_int(req, "sampleW", 96), 1, 512);
        const int sample_h = clamp_int(body_or_param_int(req, "sampleH", 96), 1, 512);

        json previous_context = a.context != nullptr ? parse_or_passthrough(a.context()) : json::object();
        const bool previous_shader_tracking = json_bool_value(previous_context, "force_shader_tracking", false);
        const bool previous_d3d12_diagnostics = json_bool_value(previous_context, "force_d3d12_diagnostics", false);
        json vr_before = a.vr_state != nullptr ? parse_or_passthrough(a.vr_state()) : json(nullptr);

        PipeServer::get().log("Render: capture_window begin " + std::to_string(ms) + "ms");
        a.set_force_shaders(1);
        a.set_force_d3d12(1);
        std::this_thread::sleep_for(std::chrono::milliseconds(ms));

        json payload = make_ranked_candidate_payload(
            a,
            max_heaps,
            max_events,
            max_pairs,
            max_aggs,
            limit,
            include_eye_diff,
            cols,
            rows,
            sample_w,
            sample_h,
            include_raw
        );
        payload["ms"] = ms;
        payload["previous_context"] = std::move(previous_context);
        payload["vr_before"] = std::move(vr_before);
        payload["vr_after"] = a.vr_state != nullptr ? parse_or_passthrough(a.vr_state()) : json(nullptr);
        payload["frame_timing"] = a.frame_timing != nullptr ? parse_or_passthrough(a.frame_timing()) : json(nullptr);
        payload["kept_enabled"] = keep_enabled;

        if (!keep_enabled) {
            a.set_force_d3d12(previous_d3d12_diagnostics ? 1 : 0);
            a.set_force_shaders(previous_shader_tracking ? 1 : 0);
            payload["restored"] = true;
            payload["context_after_restore"] = a.context != nullptr ? parse_or_passthrough(a.context()) : json(nullptr);
        } else {
            payload["restored"] = false;
        }

        PipeServer::get().log(std::string("Render: capture_window end restored=") + (keep_enabled ? "false" : "true"));
        send_json(res, payload);
    };
    server.Post("/api/render/capture-window", capture_window);
    server.Get("/api/render/capture-window", capture_window);

    server.Get("/api/render/roi-writers", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.d3d12 == nullptr || a.shaders == nullptr) { send_json(res, unavailable_payload(), 503); return; }

        std::string side = req.has_param("side") ? req.get_param_value("side") : "right";
        std::transform(side.begin(), side.end(), side.begin(), [](unsigned char c){ return (char)std::tolower(c); });
        const int side_code = (side == "all" || side == "any" || side == "*")
            ? -1
            : ((side == "right" || side == "r" || side == "1") ? 1 : 0);
        const int x = get_int_param(req, "x", 0);
        const int y = get_int_param(req, "y", 0);
        const int w = get_int_param(req, "w", get_int_param(req, "sampleW", 64));
        const int h = get_int_param(req, "h", get_int_param(req, "sampleH", 64));
        const int limit = get_int_param(req, "limit", 64);
        const int max_heaps = get_int_param(req, "maxHeaps", 256);
        const int max_events = get_int_param(req, "maxEvents", 4096);
        const int max_pairs = get_int_param(req, "maxPairs", 2048);
        const int max_aggs = get_int_param(req, "maxPsoAggregates", 2048);
        int eye_w = get_int_param(req, "eyeW", 0);
        int eye_h = get_int_param(req, "eyeH", 0);
        const bool include_dispatch = truthy_param(req, "includeDispatch", 0) != 0;
        const auto target_resource = req.has_param("targetResource") ? req.get_param_value("targetResource") : std::string{};

        if ((eye_w <= 0 || eye_h <= 0) && a.vr_state != nullptr) {
            auto vr = parse_or_passthrough(a.vr_state());
            try {
                eye_w = vr["d3d12"]["left_eye"]["region"]["w"].get<int>();
                eye_h = vr["d3d12"]["left_eye"]["region"]["h"].get<int>();
            } catch (...) {
                eye_w = eye_w <= 0 ? x + w : eye_w;
                eye_h = eye_h <= 0 ? y + h : eye_h;
            }
        }
        if (eye_w <= 0) eye_w = x + w;
        if (eye_h <= 0) eye_h = y + h;

        auto d3d12 = parse_or_passthrough(a.d3d12(max_heaps, max_events));
        auto shaders = parse_or_passthrough(a.shaders(max_pairs, max_aggs));
        auto writers = roi_writer_candidates(d3d12, shaders, side_code, x, y, w, h, eye_w, eye_h, limit, include_dispatch, target_resource);
        send_json(res, json{
            {"ok", true},
            {"side", side_code < 0 ? "all" : (side_code == 1 ? "right" : "left")},
            {"roi_eye_relative", {{"x", x}, {"y", y}, {"w", w}, {"h", h}}},
            {"eye_region", {{"w", eye_w}, {"h", eye_h}}},
            {"include_dispatch", include_dispatch},
            {"target_resource", target_resource},
            {"d3d12_summary", d3d12_capture_summary(d3d12)},
            {"shader_summary", shader_capture_summary(shaders)},
            {"writers", std::move(writers)},
        });
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

    server.Get("/api/render/sn2-state", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.sn2_state == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.sn2_state()));
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

    server.Get("/api/render/stereo-forensics", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.stereo_forensics == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        send_json(res, parse_or_passthrough(a.stereo_forensics()));
    });

    server.Post("/api/render/stereo-forensics/arm", [](const httplib::Request&, httplib::Response& res) {
        const auto& a = api();
        if (a.stereo_forensics_arm == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        PipeServer::get().log("Render: stereo forensics capture re-armed");
        send_json(res, parse_or_passthrough(a.stereo_forensics_arm()));
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
        if ((req.has_param("x") || req.has_param("y")) && a.eye_region_sample != nullptr) {
            const int x = get_int_param(req, "x", 0);
            const int y = get_int_param(req, "y", 0);
            send_json(res, parse_or_passthrough(a.eye_region_sample(code, x, y, sample_w, sample_h)));
        } else {
            send_json(res, parse_or_passthrough(a.eye_sample(code, sample_w, sample_h)));
        }
    });

    server.Get("/api/render/eye-diff", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.eye_sample == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const int sample_w = get_int_param(req, "sampleW", 96);
        const int sample_h = get_int_param(req, "sampleH", 96);
        const int x = get_int_param(req, "x", 0);
        const int y = get_int_param(req, "y", 0);

        auto left = sample_eye_region(a, 0, x, y, sample_w, sample_h);
        auto right = sample_eye_region(a, 1, x, y, sample_w, sample_h);
        send_json(res, json{
            {"ok", !left.contains("error") && !right.contains("error")},
            {"x", x},
            {"y", y},
            {"sample_w", sample_w},
            {"sample_h", sample_h},
            {"metrics", eye_diff_metrics(left, right)},
            {"left", std::move(left)},
            {"right", std::move(right)},
        });
    });

    server.Get("/api/render/eye-diff-grid", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.eye_sample == nullptr) { send_json(res, unavailable_payload(), 503); return; }

        const int cols = get_int_param(req, "cols", 3);
        const int rows = get_int_param(req, "rows", 3);
        const int sample_w = get_int_param(req, "sampleW", 96);
        const int sample_h = get_int_param(req, "sampleH", 96);
        const int eye_w = get_int_param(req, "eyeW", 0);
        const int eye_h = get_int_param(req, "eyeH", 0);
        send_json(res, build_eye_diff_grid(a, cols, rows, sample_w, sample_h, eye_w, eye_h));
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

    server.Post("/api/render/hunter/highlight", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.hunter_highlight_hash == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const std::string hash = body_or_param_string(req, "hash");
        const int enabled = truthy_body_or_param(req, "enabled", 1);
        PipeServer::get().log(std::string("Render: hunter_highlight hash=") + hash + (enabled ? " on" : " off"));
        send_json(res, parse_or_passthrough(a.hunter_highlight_hash(hash.c_str(), enabled)));
    });

    server.Post("/api/render/hunter/skip-eye", [](const httplib::Request& req, httplib::Response& res) {
        const auto& a = api();
        if (a.hunter_skip_eye_hash == nullptr) { send_json(res, unavailable_payload(), 503); return; }
        const std::string hash = body_or_param_string(req, "hash");
        std::string side = body_or_param_string(req, "eye");
        if (side.empty()) {
            side = body_or_param_string(req, "side");
        }
        std::transform(side.begin(), side.end(), side.begin(), [](unsigned char c){ return (char)std::tolower(c); });
        const int eye = (side == "left" || side == "l" || side == "0")
            ? 0
            : ((side == "right" || side == "r" || side == "1") ? 1 : body_or_param_int(req, "eye", 1));
        const int enabled = truthy_body_or_param(req, "enabled", 1);
        PipeServer::get().log(std::string("Render: hunter_skip_eye hash=") + hash + " eye=" + (eye == 0 ? "left" : "right") + (enabled ? " on" : " off"));
        send_json(res, parse_or_passthrough(a.hunter_skip_eye_hash(hash.c_str(), eye == 0 ? 0 : 1, enabled)));
    });
}

} // namespace RenderRoutes
