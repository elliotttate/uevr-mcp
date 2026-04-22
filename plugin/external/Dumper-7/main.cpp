// Standalone DllMain entry point for the vendored Dumper-7 build.
//
// Vendored from https://github.com/Encryqed/Dumper-7 (MIT, by Encryqed) with UEVR-
// oriented tweaks taken from the Dumper7UEVR fork by praydog. The upstream repo
// ships its own main.cpp with a DllMain; the fork replaced it with a UEVR plugin
// wrapper. We want neither — we inject this DLL ourselves from the uevr-mcp MCP
// server (see mcp-server/Dumper7Tools.cs) and expect it to kick off a dump on
// DLL_PROCESS_ATTACH, exactly like upstream stock Dumper-7. This file restores
// that behaviour while adding an explicit exported entry point so an in-process
// caller can also invoke the dump manually with a custom output path.
//
// MainThread is copied essentially verbatim from Dumper7UEVR/Dumper/main.cpp.

#define _SILENCE_ALL_CXX17_DEPRECATION_WARNINGS

#include <Windows.h>
#include <iostream>
#include <chrono>
#include <string>

#include "CppGenerator.h"
#include "MappingGenerator.h"
#include "IDAMappingGenerator.h"
#include "DumpspaceGenerator.h"

#include "StructManager.h"
#include "EnumManager.h"

#include "Generator.h"

#include "UnicodeNames.h"

namespace {

DWORD WINAPI MainThread(LPVOID param)
{
    HMODULE Module = static_cast<HMODULE>(param);

    AllocConsole();
    FILE* Dummy = nullptr;
    freopen_s(&Dummy, "CONOUT$", "w", stdout);
    freopen_s(&Dummy, "CONIN$",  "r", stdin);
    SetConsoleTitleA("Dumper-7 (uevr-mcp)");

    auto t_1 = std::chrono::high_resolution_clock::now();

    std::cout << "Started Generation [Dumper-7 / uevr-mcp vendored]!\n";
    std::cout << "Output folder: " << Generator::SDKFolder << "\n";

    try
    {
        Generator::InitEngineCore();
        Generator::InitInternal();

        if (Settings::Generator::GameName.empty() && Settings::Generator::GameVersion.empty())
        {
            FString Name;
            FString Version;
            UEClass Kismet = ObjectArray::FindClassFast("KismetSystemLibrary");
            UEFunction GetGameName = Kismet.GetFunction("KismetSystemLibrary", "GetGameName");
            UEFunction GetEngineVersion = Kismet.GetFunction("KismetSystemLibrary", "GetEngineVersion");

            Kismet.ProcessEvent(GetGameName, &Name);
            Kismet.ProcessEvent(GetEngineVersion, &Version);

            Settings::Generator::GameName = Name.ToString();
            Settings::Generator::GameVersion = Version.ToString();
        }

        std::cout << "GameName: "    << Settings::Generator::GameName    << "\n";
        std::cout << "GameVersion: " << Settings::Generator::GameVersion << "\n\n";

        // Restore upstream Dumper-7 behaviour: write each game's SDK into its own
        // <root>/<GameName-Version>/ subfolder so multiple dumps don't clobber
        // each other. The fork we vendored dropped this; without it, dumping
        // game A then game B would overwrite A's output.
        if (!Settings::Generator::GameName.empty())
        {
            std::string sub = Settings::Generator::GameName;
            if (!Settings::Generator::GameVersion.empty())
                sub += "-" + Settings::Generator::GameVersion;
            Generator::SDKFolder = Generator::SDKFolder + "/" + sub;
            std::cout << "Per-game output folder: " << Generator::SDKFolder << "\n\n";
        }

        Generator::Generate<CppGenerator>();
        Generator::Generate<MappingGenerator>();
        Generator::Generate<IDAMappingGenerator>();
        Generator::Generate<DumpspaceGenerator>();

        auto t_C = std::chrono::high_resolution_clock::now();
        std::chrono::duration<double, std::milli> ms = t_C - t_1;
        std::cout << "\n\nGenerating SDK took (" << ms.count() << "ms)\n\n\n";
    }
    catch (const std::exception& e)
    {
        std::cout << "Dumper-7 exception: " << e.what() << "\n";
    }
    catch (...)
    {
        std::cout << "Dumper-7 unknown exception\n";
    }

    // Drop a one-line marker file so external pollers can reliably detect "done".
    // The MCP tool watches the output root for file changes plus this marker.
    try
    {
        std::string donePath = Generator::SDKFolder + "/dumper7_done.txt";
        FILE* doneFile = nullptr;
        fopen_s(&doneFile, donePath.c_str(), "w");
        if (doneFile) { fputs("ok\n", doneFile); fclose(doneFile); }
    }
    catch (...) {}

    std::fflush(stdout);
    if (Dummy) std::fclose(Dummy);
    FreeConsole();

    (void)Module;
    ExitThread(0);
}

void ApplyEnvOverrides()
{
    // DUMPER7_OUTPUT_ROOT — override Generator::SDKFolder before the dump runs.
    char buf[1024] = {};
    DWORD len = GetEnvironmentVariableA("DUMPER7_OUTPUT_ROOT", buf, sizeof(buf));
    if (len > 0 && len < sizeof(buf))
        Generator::SDKFolder = buf;
}

} // namespace

// Auto-dump on inject. Matches stock Dumper-7 behaviour.
BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID /*reserved*/)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        ApplyEnvOverrides();
        HANDLE t = CreateThread(nullptr, 0, MainThread, hModule, 0, nullptr);
        if (t) CloseHandle(t);
    }
    return TRUE;
}

// Explicit entry point for callers that load the DLL once and want to trigger
// additional dumps with custom output paths. If outputPath is null or empty,
// the current Generator::SDKFolder (possibly already set via DllMain + env var)
// is kept unchanged.
extern "C" __declspec(dllexport) BOOL Dumper7_Run(const char* outputPath)
{
    if (outputPath && *outputPath)
        Generator::SDKFolder = outputPath;

    HMODULE self = GetModuleHandleA("dumper7.dll");
    HANDLE t = CreateThread(nullptr, 0, MainThread, self, 0, nullptr);
    if (!t) return FALSE;
    CloseHandle(t);
    return TRUE;
}
