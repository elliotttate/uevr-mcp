#include "kismet_disasm.h"
#include "property_probes.h"

#include <atomic>
#include <cstring>
#include <mutex>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

using json = nlohmann::json;

namespace KismetDisasm {

// ── Probe state ────────────────────────────────────────────────────

struct ProbeCache {
    std::atomic<int32_t> script_offset{-1}; // offset of Script TArray in UFunction
    std::atomic<uint64_t> hits{0};
    std::atomic<uint64_t> discoveries{0};
};

static ProbeCache g_cache;
static std::mutex g_discovery_mutex;

// ── SEH primitives (no C++ objects in these frames) ────────────────

static bool page_readable(const void* p, size_t size) noexcept
{
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(p, &mbi, sizeof(mbi))) return false;
    if (mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    auto end = reinterpret_cast<const uint8_t*>(p) + size;
    auto region_end = reinterpret_cast<const uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
    return end <= region_end;
}

static bool page_executable(const void* p) noexcept
{
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(p, &mbi, sizeof(mbi))) return false;
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD exec_bits = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return (mbi.Protect & exec_bits) != 0;
}

struct TArrayLike { void* data; int32_t count; int32_t capacity; };

static bool seh_read_tarray(const void* src, TArrayLike* out) noexcept
{
    __try { std::memcpy(out, src, sizeof(TArrayLike)); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static bool seh_read_ptr(const void* src, void** dst) noexcept
{
    __try { *dst = *reinterpret_cast<void* const*>(src); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// ── Probe UFunction::Script offset ─────────────────────────────────
//
// Anchor: Script immediately follows `Func` (a native fn pointer, always
// non-null — points at e.g. ProcessInternal for Blueprint-hosted funcs or
// the native stub for C++ funcs). We scan candidate offsets until we find a
// pointer-aligned slot whose value points into executable memory, then read
// the next 16 bytes as a plausible TArray<uint8>. Offset of the TArray is
// `func_ptr_offset + 8`.
//
// Candidate range covers UE4.22–UE5.5. UE5 with editor-only bits moves
// things around by ~0x20 vs cooked UE4.
//
// UFunction base size in UE is roughly 0xB0 (UStruct + function-specific
// fields + Func). The Script TArray starts right after Func.

static const int32_t kFuncPtrRange[] = {
    0x70, 0x78, 0x80, 0x88, 0x90, 0x98, 0xA0, 0xA8,
    0xB0, 0xB8, 0xC0, 0xC8, 0xD0, 0xD8, 0xE0, 0xE8, 0xF0,
    0xF8, 0x100, 0x108, 0x110, 0x118, 0x120
};

// Validate data[0] is a plausible EX_* opcode. Real UE bytecode starts in
// 0x00–0x6B (EExprToken). Values 0x80+ definitely aren't opcodes; values
// 0x6C–0x7F are unused / reserved. Reject candidate offsets where the first
// byte doesn't parse as a valid opcode — that filters out false-positives
// where the TArray-shape check passes on non-Script data (e.g. some internal
// table whose pointer happens to look like a valid buffer).
static bool seh_read_byte(const void* src, uint8_t* out) noexcept
{
    __try { *out = *reinterpret_cast<const uint8_t*>(src); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static bool is_plausible_bytecode(const void* data, int32_t size) noexcept
{
    if (size <= 0 || !data) return true; // empty script is always valid
    uint8_t b0 = 0;
    if (!seh_read_byte(data, &b0)) return false;
    // EX_Max is 0x7F in UE5, 0x6F in UE4.26. Accept anything < 0x80 since the
    // two ranges differ and we don't know which UE version we're on.
    return b0 < 0x80;
}

static int32_t probe_script_offset(void* func_base) noexcept
{
    for (int32_t off : kFuncPtrRange) {
        auto addr = reinterpret_cast<uint8_t*>(func_base) + off;
        if (!page_readable(addr, sizeof(void*))) continue;

        void* candidate = nullptr;
        if (!seh_read_ptr(addr, &candidate)) continue;
        if (!candidate) continue;
        if (!page_executable(candidate)) continue;

        // Read next 16 bytes as plausible TArray<uint8>.
        int32_t script_off = off + static_cast<int32_t>(sizeof(void*));
        auto script_addr = reinterpret_cast<uint8_t*>(func_base) + script_off;
        if (!page_readable(script_addr, sizeof(TArrayLike))) continue;

        TArrayLike arr{};
        if (!seh_read_tarray(script_addr, &arr)) continue;

        // Plausible TArray<uint8>: either empty (native function) or small-ish
        // bytecode buffer. 0–1MB is a generous cap — real BP bytecode is rarely
        // above a few KB per function.
        if (arr.count < 0 || arr.count > 0x100000) continue;
        if (arr.count > arr.capacity) continue;
        if (arr.count > 0) {
            if (!arr.data) continue;
            if (!page_readable(arr.data, static_cast<size_t>(arr.count))) continue;
            // First-byte validity: reject if not a known EX_ opcode range.
            if (!is_plausible_bytecode(arr.data, arr.count)) continue;
        }

        return script_off;
    }
    return -2;
}

ScriptView get_function_script(uevr::API::UFunction* func)
{
    ScriptView out;
    if (!func) return out;

    int32_t cached = g_cache.script_offset.load(std::memory_order_acquire);
    if (cached == -1) {
        std::lock_guard<std::mutex> lock(g_discovery_mutex);
        cached = g_cache.script_offset.load(std::memory_order_acquire);
        if (cached == -1) {
            cached = probe_script_offset(func);
            g_cache.script_offset.store(cached, std::memory_order_release);
            if (cached >= 0) g_cache.discoveries.fetch_add(1, std::memory_order_relaxed);
        }
    }
    if (cached < 0) return out;

    auto script_addr = reinterpret_cast<uint8_t*>(func) + cached;
    if (!page_readable(script_addr, sizeof(TArrayLike))) return out;

    TArrayLike arr{};
    if (!seh_read_tarray(script_addr, &arr)) return out;

    if (arr.count <= 0 || !arr.data) return out;
    if (!page_readable(arr.data, static_cast<size_t>(arr.count))) return out;

    out.data = reinterpret_cast<const uint8_t*>(arr.data);
    out.size = arr.count;
    g_cache.hits.fetch_add(1, std::memory_order_relaxed);
    return out;
}

// ── EX_* opcode mnemonic table ─────────────────────────────────────
//
// Mirrored from Engine/Source/Runtime/CoreUObject/Public/UObject/Script.h
// (EExprToken). We only need names for preview purposes; full operand
// decoding is a deeper undertaking.

static const char* opcode_name(uint8_t op)
{
    switch (op) {
        case 0x00: return "LocalVariable";
        case 0x01: return "InstanceVariable";
        case 0x02: return "DefaultVariable";
        case 0x04: return "Return";
        case 0x06: return "Jump";
        case 0x07: return "JumpIfNot";
        case 0x09: return "Assert";
        case 0x0B: return "Nothing";
        case 0x0F: return "Let";
        case 0x12: return "ClassContext";
        case 0x13: return "MetaCast";
        case 0x14: return "LetBool";
        case 0x15: return "EndParmValue";
        case 0x16: return "EndFunctionParms";
        case 0x17: return "Self";
        case 0x18: return "Skip";
        case 0x19: return "Context";
        case 0x1A: return "Context_FailSilent";
        case 0x1B: return "VirtualFunction";
        case 0x1C: return "FinalFunction";
        case 0x1D: return "IntConst";
        case 0x1E: return "FloatConst";
        case 0x1F: return "StringConst";
        case 0x20: return "ObjectConst";
        case 0x21: return "NameConst";
        case 0x22: return "RotationConst";
        case 0x23: return "VectorConst";
        case 0x24: return "ByteConst";
        case 0x25: return "IntZero";
        case 0x26: return "IntOne";
        case 0x27: return "True";
        case 0x28: return "False";
        case 0x29: return "TextConst";
        case 0x2A: return "NoObject";
        case 0x2B: return "TransformConst";
        case 0x2C: return "IntConstByte";
        case 0x2D: return "NoInterface";
        case 0x2E: return "DynamicCast";
        case 0x2F: return "StructConst";
        case 0x30: return "EndStructConst";
        case 0x31: return "SetArray";
        case 0x32: return "EndArray";
        case 0x33: return "PropertyConst";
        case 0x34: return "UnicodeStringConst";
        case 0x35: return "Int64Const";
        case 0x36: return "UInt64Const";
        case 0x37: return "DoubleConst";
        case 0x38: return "Cast";
        case 0x39: return "SetSet";
        case 0x3A: return "EndSet";
        case 0x3B: return "SetMap";
        case 0x3C: return "EndMap";
        case 0x3D: return "SetConst";
        case 0x3E: return "EndSetConst";
        case 0x3F: return "MapConst";
        case 0x40: return "EndMapConst";
        case 0x41: return "Vector3fConst";
        case 0x42: return "StructMemberContext";
        case 0x43: return "LetMulticastDelegate";
        case 0x44: return "LetDelegate";
        case 0x45: return "LocalVirtualFunction";
        case 0x46: return "LocalFinalFunction";
        case 0x47: return "LocalOutVariable";
        case 0x49: return "DeprecatedOp4A";
        case 0x4A: return "InstanceDelegate";
        case 0x4B: return "PushExecutionFlow";
        case 0x4C: return "PopExecutionFlow";
        case 0x4D: return "ComputedJump";
        case 0x4E: return "PopExecutionFlowIfNot";
        case 0x4F: return "Breakpoint";
        case 0x50: return "InterfaceContext";
        case 0x51: return "ObjToInterfaceCast";
        case 0x52: return "EndOfScript";
        case 0x53: return "CrossInterfaceCast";
        case 0x54: return "InterfaceToObjCast";
        case 0x55: return "WireTracepoint";
        case 0x56: return "SkipOffsetConst";
        case 0x57: return "AddMulticastDelegate";
        case 0x58: return "ClearMulticastDelegate";
        case 0x59: return "Tracepoint";
        case 0x5A: return "LetObj";
        case 0x5B: return "LetWeakObjPtr";
        case 0x5C: return "BindDelegate";
        case 0x5D: return "RemoveMulticastDelegate";
        case 0x5E: return "CallMulticastDelegate";
        case 0x5F: return "LetValueOnPersistentFrame";
        case 0x60: return "ArrayConst";
        case 0x61: return "EndArrayConst";
        case 0x62: return "SoftObjectConst";
        case 0x63: return "CallMath";
        case 0x64: return "SwitchValue";
        case 0x65: return "InstrumentationEvent";
        case 0x66: return "ArrayGetByRef";
        case 0x67: return "ClassSparseDataVariable";
        case 0x68: return "FieldPathConst";
        case 0x69: return "AutoRtfmTransact";
        case 0x6A: return "AutoRtfmStopTransact";
        case 0x6B: return "AutoRtfmAbortIfNot";
        default:   return nullptr;
    }
}

// A few opcodes have predictable operand sizes we can skip. The rest we
// abort on because misaligned walk produces junk. This is the minimum set
// needed to get past trivial functions like `Ret;`.

static int32_t skip_bytes_for(uint8_t op)
{
    switch (op) {
        case 0x04: // Return — followed by an expression (variable length)
        case 0x0B: // Nothing
        case 0x16: // EndFunctionParms
        case 0x17: // Self
        case 0x25: // IntZero
        case 0x26: // IntOne
        case 0x27: // True
        case 0x28: // False
        case 0x2A: // NoObject
        case 0x2D: // NoInterface
        case 0x30: // EndStructConst
        case 0x32: // EndArray
        case 0x3A: // EndSet
        case 0x3C: // EndMap
        case 0x3E: // EndSetConst
        case 0x40: // EndMapConst
        case 0x4C: // PopExecutionFlow
        case 0x4F: // Breakpoint
        case 0x52: // EndOfScript
        case 0x55: // WireTracepoint
        case 0x59: // Tracepoint
        case 0x61: // EndArrayConst
            return 0;
        case 0x24: // ByteConst
        case 0x2C: // IntConstByte
            return 1;
        case 0x1D: // IntConst (int32)
        case 0x1E: // FloatConst (float32)
        case 0x06: // Jump (CodeSkipSizeType — 4 bytes in modern UE)
        case 0x18: // Skip
        case 0x4B: // PushExecutionFlow
            return 4;
        case 0x35: // Int64Const
        case 0x36: // UInt64Const
        case 0x37: // DoubleConst
            return 8;
        case 0x21: // NameConst (FName = 2x int32 usually)
            return 12; // conservative — FName on-disk is 2x int32 + i32 number
        case 0x20: // ObjectConst — ScriptPointer (8 bytes on 64-bit)
        case 0x2A + 0x100: // placeholder
            return 8;
        default:
            return -1; // unknown size → stop walking
    }
}

std::vector<std::string> disassemble(const ScriptView& script, size_t max_instructions)
{
    std::vector<std::string> out;
    if (!script.valid()) return out;

    size_t pc = 0;
    while (pc < static_cast<size_t>(script.size) && out.size() < max_instructions) {
        uint8_t op = script.data[pc++];
        const char* name = opcode_name(op);
        if (!name) {
            // Unknown opcode → emit hex and stop walking. We'd misalign otherwise.
            char buf[16];
            std::snprintf(buf, sizeof(buf), "0x%02X?", op);
            out.emplace_back(buf);
            break;
        }
        out.emplace_back(name);

        int32_t skip = skip_bytes_for(op);
        if (skip < 0) {
            // We know the name but not the operand shape. Stop cleanly.
            out.emplace_back("...");
            break;
        }
        pc += static_cast<size_t>(skip);
    }
    return out;
}

// ── Diagnostics ────────────────────────────────────────────────────

json diagnostics()
{
    auto off = g_cache.script_offset.load();
    return json{
        {"field", "UFunction::Script"},
        {"offset", off < 0 ? json(nullptr) : json(off)},
        {"status", off == -1 ? "undiscovered" : off == -2 ? "failed" : "resolved"},
        {"hits", g_cache.hits.load()},
        {"discoveries", g_cache.discoveries.load()}
    };
}

void reset()
{
    std::lock_guard<std::mutex> lock(g_discovery_mutex);
    g_cache.script_offset.store(-1);
    g_cache.hits.store(0);
    g_cache.discoveries.store(0);
}

} // namespace KismetDisasm
