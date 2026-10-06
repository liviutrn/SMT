#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdarg>
#include <filesystem>

#include <MinHook.h>

namespace {

constexpr std::uintptr_t kTargetRva = 0x001217B0;

// Exact first 32 bytes from the user's uploaded SteamVR 2.18.2 vrcompositor.exe
// SHA-256: 5dabeae9a0ac12d7c47a3aed9576c0d5116039eafffb47756fd674605d1fe554
constexpr unsigned char kExpectedTargetBytes[] = {
    0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,
    0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x48,
    0x89,0x7C,0x24,0x20,0x41,0x54,0x41,0x56,
    0x41,0x57,0x48,0x81,0xEC,0x60,0x01,0x00
};

using SelectMotionVectorsFn =
    bool(__fastcall*)(void* manager,
                     std::uint32_t inputVsyncId,
                     std::uint32_t targetVsyncId,
                     void* output,
                     bool modeFlag);

SelectMotionVectorsFn g_original = nullptr;
std::atomic<bool> g_enabled{true};
std::atomic<bool> g_running{true};

std::atomic<std::uint64_t> g_calls{0};
std::atomic<std::uint64_t> g_gap4Calls{0};
std::atomic<std::uint64_t> g_midpointPasses{0};
std::atomic<std::uint64_t> g_outerSuppressions{0};

HMODULE g_self = nullptr;
FILE* g_log = nullptr;

std::filesystem::path ModuleDir(HMODULE module) {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(module, path, MAX_PATH);
    return std::filesystem::path(path).parent_path();
}

void Log(const char* fmt, ...) {
    if (!g_log && g_self) {
        const auto path = ModuleDir(g_self) / L"SteamVRMSMidpoint.log";
        _wfopen_s(&g_log, path.c_str(), L"a");
    }
    if (!g_log) return;

    SYSTEMTIME st{};
    GetLocalTime(&st);
    std::fprintf(g_log, "[%02u:%02u:%02u.%03u] ",
                 st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_list args;
    va_start(args, fmt);
    std::vfprintf(g_log, fmt, args);
    va_end(args);
    std::fprintf(g_log, "\n");
    std::fflush(g_log);
}

bool BytesMatch(const void* address, const unsigned char* expected, std::size_t size) {
    __try {
        return std::memcmp(address, expected, size) == 0;
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool __fastcall HookSelectMotionVectors(void* manager,
                                        std::uint32_t inputVsyncId,
                                        std::uint32_t targetVsyncId,
                                        void* output,
                                        bool modeFlag) {
    g_calls.fetch_add(1, std::memory_order_relaxed);

    const bool ok = g_original(manager, inputVsyncId, targetVsyncId, output, modeFlag);
    if (!ok || !g_enabled.load(std::memory_order_relaxed) || !output) {
        return ok;
    }

    // In this exact compositor build, on successful return:
    //   output + 0x258 = (InputVsyncId - ReferenceVsyncId) * 0.5f
    //
    // Therefore a 30-fps source on a 120-Hz compositor has a VSync gap of 4
    // and this field is exactly 2.0f. Target-Input is then 1, 2, 3 for the
    // 25%, 50%, 75% synthetic slots.
    float halfReferenceGap = 0.0f;
    __try {
        halfReferenceGap = *reinterpret_cast<float*>(
            reinterpret_cast<unsigned char*>(output) + 0x258);
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return ok;
    }

    if (std::fabs(halfReferenceGap - 2.0f) > 0.01f) {
        // Preserve SteamVR native behavior at every ratio other than 30->120.
        return ok;
    }

    g_gap4Calls.fetch_add(1, std::memory_order_relaxed);

    const std::uint32_t delta = targetVsyncId - inputVsyncId;

    if (delta == 2) {
        // Keep exactly the midpoint hallucination: 30 -> 60 scene cadence.
        g_midpointPasses.fetch_add(1, std::memory_order_relaxed);
        return true;
    }

    if (delta == 1 || delta == 3) {
        // Reject the 25% and 75% hallucinations. The compositor still runs
        // physically at 120 Hz and can do its ordinary pose reprojection on
        // these presentation slots.
        g_outerSuppressions.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    // Unexpected target relation: fail open to native SteamVR behavior.
    return ok;
}

DWORD WINAPI ControlThread(LPVOID) {
    Log("============================================================");
    Log("SteamVR Motion Smoothing midpoint-only hook loaded.");
    Log("Target RVA=0x%llX", static_cast<unsigned long long>(kTargetRva));

    HMODULE exe = GetModuleHandleW(nullptr);
    if (!exe) {
        Log("ERROR: GetModuleHandleW(nullptr) failed.");
        return 1;
    }

    const auto target = reinterpret_cast<unsigned char*>(exe) + kTargetRva;
    if (!BytesMatch(target, kExpectedTargetBytes, sizeof(kExpectedTargetBytes))) {
        Log("ERROR: vrcompositor target signature mismatch. Refusing to hook.");
        Log("This build is locked to the user's exact SteamVR 2.18.2 binary.");
        return 2;
    }

    if (MH_Initialize() != MH_OK) {
        Log("ERROR: MH_Initialize failed.");
        return 3;
    }

    if (MH_CreateHook(target,
                      reinterpret_cast<void*>(&HookSelectMotionVectors),
                      reinterpret_cast<void**>(&g_original)) != MH_OK) {
        Log("ERROR: MH_CreateHook failed.");
        MH_Uninitialize();
        return 4;
    }

    if (MH_EnableHook(target) != MH_OK) {
        Log("ERROR: MH_EnableHook failed.");
        MH_RemoveHook(target);
        MH_Uninitialize();
        return 5;
    }

    Log("HOOK ACTIVE. Native behavior preserved except 30->120 quarter/three-quarter hallucinations.");
    Log("Ctrl+Alt+I toggles filtering on/off live. Enabled=true");

    bool previousChord = false;
    std::uint64_t lastCalls = 0;
    std::uint64_t lastGap4 = 0;
    std::uint64_t lastMid = 0;
    std::uint64_t lastSuppressed = 0;
    DWORD lastStatsTick = GetTickCount();

    while (g_running.load(std::memory_order_relaxed)) {
        const bool chord =
            (GetAsyncKeyState(VK_CONTROL) & 0x8000) &&
            (GetAsyncKeyState(VK_MENU) & 0x8000) &&
            (GetAsyncKeyState('I') & 0x8000);

        if (chord && !previousChord) {
            const bool next = !g_enabled.load(std::memory_order_relaxed);
            g_enabled.store(next, std::memory_order_relaxed);
            Log("LIVE TOGGLE: enabled=%s", next ? "true" : "false");
        }
        previousChord = chord;

        const DWORD now = GetTickCount();
        if (now - lastStatsTick >= 5000) {
            const auto calls = g_calls.load(std::memory_order_relaxed);
            const auto gap4 = g_gap4Calls.load(std::memory_order_relaxed);
            const auto mid = g_midpointPasses.load(std::memory_order_relaxed);
            const auto suppressed = g_outerSuppressions.load(std::memory_order_relaxed);

            if (calls != lastCalls || gap4 != lastGap4 ||
                mid != lastMid || suppressed != lastSuppressed) {
                Log("STATS calls=%llu gap4=%llu midpoint_pass=%llu outer_suppressed=%llu enabled=%s",
                    static_cast<unsigned long long>(calls),
                    static_cast<unsigned long long>(gap4),
                    static_cast<unsigned long long>(mid),
                    static_cast<unsigned long long>(suppressed),
                    g_enabled.load(std::memory_order_relaxed) ? "true" : "false");
                lastCalls = calls;
                lastGap4 = gap4;
                lastMid = mid;
                lastSuppressed = suppressed;
            }
            lastStatsTick = now;
        }

        Sleep(25);
    }

    MH_DisableHook(target);
    MH_RemoveHook(target);
    MH_Uninitialize();
    Log("Hook disabled and MinHook shut down.");
    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = module;
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, ControlThread, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    } else if (reason == DLL_PROCESS_DETACH) {
        g_running.store(false, std::memory_order_relaxed);
        if (g_log) {
            std::fprintf(g_log, "Process detach.\n");
            std::fclose(g_log);
            g_log = nullptr;
        }
    }
    return TRUE;
}
