#include "property_probes.h"
#include "../json_helpers.h"

#include <atomic>
#include <mutex>
#include <string>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

using json = nlohmann::json;

namespace PropertyProbes {

// ── State ──────────────────────────────────────────────────────────
//
// One atomic-guarded cache per probe. Offsets are bounded small ints so
// sizeof(int32_t) + atomic works cleanly. Hit counters help us see which
// probes are actually load-bearing in dumps.

struct ProbeCache {
    std::atomic<int32_t> offset{-1}; // -1 = undiscovered, -2 = tried and failed
    std::atomic<uint64_t> hits{0};
    std::atomic<uint64_t> discoveries{0};

    int32_t load() const { return offset.load(std::memory_order_acquire); }
    void store(int32_t v)
    {
        offset.store(v, std::memory_order_release);
        if (v >= 0) discoveries.fetch_add(1, std::memory_order_relaxed);
    }
};

static ProbeCache g_obj_property_class;      // FObjectPropertyBase::PropertyClass
static ProbeCache g_class_meta_class;        // FClassProperty::MetaClass
static ProbeCache g_iface_interface_class;   // FInterfaceProperty::InterfaceClass
static ProbeCache g_delegate_sig_function;   // F*DelegateProperty::SignatureFunction
static ProbeCache g_map_key_prop;            // FMapProperty::KeyProp
static ProbeCache g_map_value_prop;          // FMapProperty::ValueProp
static ProbeCache g_set_element_prop;        // FSetProperty::ElementProp
static std::mutex g_discovery_mutex;

// ── Candidate offset ranges ────────────────────────────────────────
//
// FProperty base size across UE4.22–UE5.4 is roughly 0x70–0x80 bytes. Each
// subclass appends its own fields right after the base. Candidate ranges
// cover that window generously.
//
// - FObjectProperty::PropertyClass:      0x40–0x90 (8-byte steps)
// - FClassProperty::MetaClass:           PropertyClass + 8 (adjacent) or 0x48–0x98
// - FInterfaceProperty::InterfaceClass:  similar to ObjectProperty
// - F*DelegateProperty::SignatureFunction: 0x40–0x90
// - FMapProperty::KeyProp/ValueProp:     0x40–0xA0
// - FSetProperty::ElementProp:           0x40–0xA0

static const int32_t kShortRange[]  = { 0x40, 0x48, 0x50, 0x58, 0x60, 0x68, 0x70, 0x78, 0x80, 0x88, 0x90 };
static const int32_t kFieldRange[]  = { 0x40, 0x48, 0x50, 0x58, 0x60, 0x68, 0x70, 0x78, 0x80, 0x88, 0x90, 0x98, 0xA0 };

// ── Helpers ────────────────────────────────────────────────────────

static bool page_readable(const void* p, size_t size) noexcept
{
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(p, &mbi, sizeof(mbi))) return false;
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD bad = PAGE_NOACCESS | PAGE_GUARD;
    if (mbi.Protect & bad) return false;
    auto end = reinterpret_cast<const uint8_t*>(p) + size;
    auto region_end = reinterpret_cast<const uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
    return end <= region_end;
}

static bool seh_read_ptr(const void* src, void** dst) noexcept
{
    __try { *dst = *reinterpret_cast<void* const*>(src); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static void* read_ptr_at(void* base, int32_t offset) noexcept
{
    auto addr = reinterpret_cast<uint8_t*>(base) + offset;
    if (!page_readable(addr, sizeof(void*))) return nullptr;
    void* value = nullptr;
    if (!seh_read_ptr(addr, &value)) return nullptr;
    return value;
}

// Validate: pointer looks like a UClass / UScriptStruct / UEnum (anything
// reachable via UObjectHook::exists). Callers narrow further when needed.
static bool is_plausible_uobject(void* p) noexcept
{
    if (!p || !page_readable(p, 8)) return false;
    try {
        return uevr::API::UObjectHook::exists(reinterpret_cast<uevr::API::UObject*>(p));
    } catch (...) { return false; }
}

// Validate: pointer looks like an FField / FProperty. FProperty isn't a
// UObject — it lives in a separate allocator — but its class pointer (a
// FFieldClass) is at a stable low offset and carries an FName whose string
// ends with "Property". We sanity-check via SEH-guarded memory reads, then
// compare the FName text in a separate (RAII-friendly) frame.
//
// Structure: SEH fn reads the FFieldClass* then the FName comparison_index
// with no C++ unwinding; caller does the FName::to_string in normal C++.

static bool seh_try_get_field_class(void* p, uevr::API::FFieldClass** out_fc) noexcept
{
    __try {
        auto prop = reinterpret_cast<uevr::API::FProperty*>(p);
        *out_fc = prop->get_class();
        return *out_fc != nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static bool is_plausible_fproperty(void* p) noexcept
{
    if (!p || !page_readable(p, 0x20)) return false;
    uevr::API::FFieldClass* fc = nullptr;
    if (!seh_try_get_field_class(p, &fc)) return false;
    if (!fc || !page_readable(fc, 0x20)) return false;
    // Resolve the class's FName in a C++ frame; wrapped in try/catch in case
    // UEVR's FName lookup throws on a bogus pointer.
    try {
        auto fname = fc->get_fname();
        if (!fname) return false;
        auto name_w = fname->to_string();
        if (name_w.size() < 8) return false;
        auto tail = name_w.substr(name_w.size() - 8);
        return tail == L"Property";
    } catch (...) { return false; }
}

// ── Generic probe primitive ────────────────────────────────────────

template <typename Validator>
static int32_t discover_offset(void* seed, const int32_t* candidates, size_t n_candidates, Validator&& validate) noexcept
{
    for (size_t i = 0; i < n_candidates; ++i) {
        auto off = candidates[i];
        auto p = read_ptr_at(seed, off);
        if (!p) continue;
        if (validate(p)) return off;
    }
    return -2; // mark as "tried, not found" so we don't keep rediscovering
}

// Resolve-or-discover: used by each public getter.
// `cache`  — per-(subclass, field) offset cache
// `seed`   — a property instance of the right subclass; probe uses its memory
// `candidates` / `n_candidates` — offsets to try
// `validate` — validator for the pointer value at each candidate
template <typename T, typename Validator>
static T* cached_or_discover(ProbeCache& cache, void* seed, const int32_t* candidates, size_t n_candidates, Validator&& validate)
{
    if (!seed) return nullptr;
    auto cached = cache.load();
    if (cached >= 0) {
        auto p = read_ptr_at(seed, cached);
        if (p) {
            cache.hits.fetch_add(1, std::memory_order_relaxed);
            return reinterpret_cast<T*>(p);
        }
        // Cached offset returned null on this instance — still valid for other
        // instances that have the field populated. Don't invalidate.
        return nullptr;
    }
    if (cached == -2) return nullptr; // discovery failed previously

    // First-time discovery. Serialize so concurrent callers don't race.
    std::lock_guard<std::mutex> lock(g_discovery_mutex);
    cached = cache.load();
    if (cached == -1) {
        cached = discover_offset(seed, candidates, n_candidates, validate);
        cache.store(cached);
    }
    if (cached < 0) return nullptr;

    auto p = read_ptr_at(seed, cached);
    if (p) {
        cache.hits.fetch_add(1, std::memory_order_relaxed);
        return reinterpret_cast<T*>(p);
    }
    return nullptr;
}

// ── Public getters ─────────────────────────────────────────────────

uevr::API::UStruct* get_property_class(uevr::API::FProperty* prop)
{
    return cached_or_discover<uevr::API::UStruct>(
        g_obj_property_class, prop,
        kShortRange, sizeof(kShortRange) / sizeof(kShortRange[0]),
        is_plausible_uobject);
}

uevr::API::UStruct* get_meta_class(uevr::API::FProperty* prop)
{
    // MetaClass sits right after PropertyClass on FClassProperty. If we've
    // already discovered PropertyClass, try PropertyClass+8 first.
    auto pc_off = g_obj_property_class.load();
    if (pc_off >= 0) {
        int32_t biased[] = { pc_off + 0x8, pc_off + 0x10, pc_off - 0x8 };
        auto biased_p = cached_or_discover<uevr::API::UStruct>(
            g_class_meta_class, prop,
            biased, sizeof(biased) / sizeof(biased[0]),
            is_plausible_uobject);
        if (biased_p) return biased_p;
    }
    return cached_or_discover<uevr::API::UStruct>(
        g_class_meta_class, prop,
        kShortRange, sizeof(kShortRange) / sizeof(kShortRange[0]),
        is_plausible_uobject);
}

uevr::API::UStruct* get_interface_class(uevr::API::FProperty* prop)
{
    return cached_or_discover<uevr::API::UStruct>(
        g_iface_interface_class, prop,
        kShortRange, sizeof(kShortRange) / sizeof(kShortRange[0]),
        is_plausible_uobject);
}

uevr::API::UFunction* get_signature_function(uevr::API::FProperty* prop)
{
    return cached_or_discover<uevr::API::UFunction>(
        g_delegate_sig_function, prop,
        kShortRange, sizeof(kShortRange) / sizeof(kShortRange[0]),
        is_plausible_uobject);
}

uevr::API::FProperty* get_map_key_prop(uevr::API::FProperty* prop)
{
    return cached_or_discover<uevr::API::FProperty>(
        g_map_key_prop, prop,
        kFieldRange, sizeof(kFieldRange) / sizeof(kFieldRange[0]),
        is_plausible_fproperty);
}

uevr::API::FProperty* get_map_value_prop(uevr::API::FProperty* prop)
{
    // ValueProp usually lives right after KeyProp — bias toward that.
    auto key_off = g_map_key_prop.load();
    if (key_off >= 0) {
        int32_t biased[] = { key_off + 0x8, key_off + 0x10 };
        auto p = cached_or_discover<uevr::API::FProperty>(
            g_map_value_prop, prop,
            biased, sizeof(biased) / sizeof(biased[0]),
            is_plausible_fproperty);
        if (p) return p;
    }
    return cached_or_discover<uevr::API::FProperty>(
        g_map_value_prop, prop,
        kFieldRange, sizeof(kFieldRange) / sizeof(kFieldRange[0]),
        is_plausible_fproperty);
}

uevr::API::FProperty* get_set_element_prop(uevr::API::FProperty* prop)
{
    return cached_or_discover<uevr::API::FProperty>(
        g_set_element_prop, prop,
        kFieldRange, sizeof(kFieldRange) / sizeof(kFieldRange[0]),
        is_plausible_fproperty);
}

// ── Diagnostics / reset ────────────────────────────────────────────

static json cache_snapshot(const char* name, const ProbeCache& c)
{
    auto off = c.offset.load();
    return json{
        {"field", name},
        {"offset", off < 0 ? json(nullptr) : json(off)},
        {"status", off == -1 ? "undiscovered" : off == -2 ? "failed" : "resolved"},
        {"hits", c.hits.load()},
        {"discoveries", c.discoveries.load()}
    };
}

json diagnostics()
{
    json probes = json::array();
    probes.push_back(cache_snapshot("FObjectPropertyBase::PropertyClass", g_obj_property_class));
    probes.push_back(cache_snapshot("FClassProperty::MetaClass",          g_class_meta_class));
    probes.push_back(cache_snapshot("FInterfaceProperty::InterfaceClass", g_iface_interface_class));
    probes.push_back(cache_snapshot("F*DelegateProperty::SignatureFunction", g_delegate_sig_function));
    probes.push_back(cache_snapshot("FMapProperty::KeyProp",              g_map_key_prop));
    probes.push_back(cache_snapshot("FMapProperty::ValueProp",            g_map_value_prop));
    probes.push_back(cache_snapshot("FSetProperty::ElementProp",          g_set_element_prop));
    return json{{"probes", probes}};
}

void reset()
{
    std::lock_guard<std::mutex> lock(g_discovery_mutex);
    for (auto* c : { &g_obj_property_class, &g_class_meta_class, &g_iface_interface_class,
                     &g_delegate_sig_function, &g_map_key_prop, &g_map_value_prop,
                     &g_set_element_prop }) {
        c->offset.store(-1);
        c->hits.store(0);
        c->discoveries.store(0);
    }
}

} // namespace PropertyProbes
