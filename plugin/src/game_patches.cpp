#include "game_patches.h"

#include "pipe_server.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <string>

namespace {

std::string lower_copy(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool patch_bytes(uintptr_t address, const uint8_t* expected, const uint8_t* replacement, size_t size, const char* name) {
    auto* target = reinterpret_cast<uint8_t*>(address);

    if (std::memcmp(target, replacement, size) == 0) {
        PipeServer::get().log(std::string("Patch already applied: ") + name);
        return true;
    }

    if (std::memcmp(target, expected, size) != 0) {
        PipeServer::get().log(std::string("Patch skipped, byte signature mismatch: ") + name);
        return false;
    }

    DWORD old_protect{};
    if (!VirtualProtect(target, size, PAGE_EXECUTE_READWRITE, &old_protect)) {
        PipeServer::get().log(std::string("Patch failed, VirtualProtect refused write access: ") + name);
        return false;
    }

    std::memcpy(target, replacement, size);
    FlushInstructionCache(GetCurrentProcess(), target, size);

    DWORD restored{};
    VirtualProtect(target, size, old_protect, &restored);

    PipeServer::get().log(std::string("Patch applied: ") + name);
    return true;
}

void apply_subnautica2_save_thumbnail_patch() {
    auto* module = GetModuleHandleW(nullptr);
    if (module == nullptr) {
        PipeServer::get().log("Subnautica 2 save-thumbnail patch skipped, main module unavailable");
        return;
    }

    // Subnautica2-CL-112084:
    //
    // The save-thumbnail readback task allocates from crop bounds:
    //   (CropMaxY - CropMinY) * (CropMaxX - CropMinX)
    // but copies TargetWidth * TargetHeight pixels. In VR the render target is
    // double-wide while the crop rect is one eye, so TargetWidth is 3360 and
    // CropWidth is 1680. Normalize the task's expected dimensions to the crop
    // rectangle before allocation/copy.
    constexpr uintptr_t kRva = 0x5A77F32;
    constexpr std::array<uint8_t, 27> kExpected = {
        0x8B, 0x43, 0x04,             // mov eax, [rbx+04h]       ; TargetWidth
        0x89, 0x45, 0x07,             // mov [rbp+07h], eax
        0x8B, 0x43, 0x08,             // mov eax, [rbx+08h]       ; TargetHeight
        0x89, 0x45, 0x0B,             // mov [rbp+0Bh], eax
        0x8B, 0x4B, 0x14,             // mov ecx, [rbx+14h]       ; CropMaxX
        0x2B, 0x4B, 0x0C,             // sub ecx, [rbx+0Ch]       ; CropWidth
        0x8B, 0x43, 0x18,             // mov eax, [rbx+18h]       ; CropMaxY
        0x2B, 0x43, 0x10,             // sub eax, [rbx+10h]       ; CropHeight
        0x0F, 0xAF, 0xC8,             // imul ecx, eax
    };
    constexpr std::array<uint8_t, 27> kPatch = {
        0x8B, 0x4B, 0x14,             // mov ecx, [rbx+14h]       ; CropMaxX
        0x2B, 0x4B, 0x0C,             // sub ecx, [rbx+0Ch]       ; CropWidth
        0x89, 0x4D, 0x07,             // mov [rbp+07h], ecx       ; ExpectedWidth = CropWidth
        0x8B, 0x43, 0x18,             // mov eax, [rbx+18h]       ; CropMaxY
        0x2B, 0x43, 0x10,             // sub eax, [rbx+10h]       ; CropHeight
        0x89, 0x45, 0x0B,             // mov [rbp+0Bh], eax       ; ExpectedHeight = CropHeight
        0x0F, 0xAF, 0xC8,             // imul ecx, eax
        0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
    };

    const auto address = reinterpret_cast<uintptr_t>(module) + kRva;
    patch_bytes(address, kExpected.data(), kPatch.data(), kPatch.size(), "Subnautica2 save thumbnail VR crop dimensions");
}

} // namespace

namespace GamePatches {

void apply_for_game(const std::string& game_name) {
    if (lower_copy(game_name) == "subnautica2-win64-shipping.exe") {
        apply_subnautica2_save_thumbnail_patch();
    }
}

} // namespace GamePatches
