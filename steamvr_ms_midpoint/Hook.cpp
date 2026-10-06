#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <filesystem>

#include <MinHook.h>

namespace {

constexpr std::uintptr_t kTargetRva = 0x001217B0;

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

enum class Mode : int {
    Native = 1,
    SoftFar = 2,
    ClampFar = 3,
    DropFar = 4,
    MidpointOnly = 5,
};

SelectMotionVectorsFn g_original = nullptr;
std::atomic<Mode> g_mode{Mode::SoftFar};
std::atomic<Mode> g_lastNonNative{Mode::SoftFar};
std::atomic<bool> g_running{true};

std::atomic<std::uint64_t> g_calls{0}, g_gap4{0};
std::atomic<std::uint64_t> g_d1{0}, g_d2{0}, g_d3{0};
std::atomic<std::uint64_t> g_soft{0}, g_clamp{0}, g_drop75{0}, g_outerDrop{0};

HMODULE g_self = nullptr;
FILE* g_log = nullptr;

const char* ModeName(Mode mode) {
    switch (mode) {
    case Mode::Native:       return "NATIVE";
    case Mode::SoftFar:      return "SOFT_FAR_62.5";
    case Mode::ClampFar:     return "CLAMP_FAR_TO_50";
    case Mode::DropFar:      return "DROP_75_ONLY";
    case Mode::MidpointOnly: return "MIDPOINT_ONLY";
    default:                 return "UNKNOWN";
    }
}

std::filesystem::path ModuleDir(HMODULE module) {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(module, path, MAX_PATH);
    return std::filesystem::path(path).parent_path();
}

void Log(const char* fmt, ...) {
    if (!g_log && g_self) {
        const auto path = ModuleDir(g_self) / L"SteamVRMSQualityV2.log";
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

// In the exact 2.18.2 selector, target-time scale contributes linearly to
// these output floats and their mirrors. Reference/history-gap fields are
// separate and intentionally left untouched.
bool ScaleTargetTimeFields(void* output, float factor) {
    constexpr std::size_t offsets[] = {0x004,0x008,0x00C,0x2C4,0x2C8,0x2CC};
    __try {
        auto* bytes = reinterpret_cast<unsigned char*>(output);
        for (std::size_t off : offsets) {
            auto* v = reinterpret_cast<float*>(bytes + off);
            *v *= factor;
        }
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void SetMode(Mode mode, const char* why) {
    g_mode.store(mode, std::memory_order_relaxed);
    if (mode != Mode::Native) g_lastNonNative.store(mode, std::memory_order_relaxed);
    Log("MODE -> %s [%s]", ModeName(mode), why);
}

bool __fastcall HookSelectMotionVectors(void* manager,
                                        std::uint32_t inputVsyncId,
                                        std::uint32_t targetVsyncId,
                                        void* output,
                                        bool modeFlag) {
    g_calls.fetch_add(1, std::memory_order_relaxed);

    const bool ok = g_original(manager, inputVsyncId, targetVsyncId, output, modeFlag);
    if (!ok || !output) return ok;

    float halfReferenceGap = 0.0f;
    __try {
        halfReferenceGap = *reinterpret_cast<float*>(
            reinterpret_cast<unsigned char*>(output) + 0x258);
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return ok;
    }

    // 30->120 only: Input-Reference = 4, therefore output+0x258 = 2.0.
    if (std::fabs(halfReferenceGap - 2.0f) > 0.01f) return ok;

    g_gap4.fetch_add(1, std::memory_order_relaxed);
    const std::uint32_t delta = targetVsyncId - inputVsyncId;
    if (delta == 1) g_d1.fetch_add(1, std::memory_order_relaxed);
    if (delta == 2) g_d2.fetch_add(1, std::memory_order_relaxed);
    if (delta == 3) g_d3.fetch_add(1, std::memory_order_relaxed);

    const Mode mode = g_mode.load(std::memory_order_relaxed);
    if (mode == Mode::Native) return ok;

    if (mode == Mode::SoftFar) {
        // Native 75% -> 62.5%: half-way back toward the 50% midpoint.
        // 0.75 * 5/6 = 0.625. Keeps a unique far scene state but reduces
        // extrapolation beyond midpoint by one third.
        if (delta == 3 && ScaleTargetTimeFields(output, 5.0f/6.0f))
            g_soft.fetch_add(1, std::memory_order_relaxed);
        return ok;
    }

    if (mode == Mode::ClampFar) {
        // Native 75% -> 50% without leaving the Motion Smoothing path.
        // This tests whether v1's roughness was partly the false/fallback path.
        if (delta == 3 && ScaleTargetTimeFields(output, 2.0f/3.0f))
            g_clamp.fetch_add(1, std::memory_order_relaxed);
        return ok;
    }

    if (mode == Mode::DropFar) {
        // Keep the two shortest predictions (25%, 50%) and reject only 75%.
        if (delta == 3) {
            g_drop75.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        return ok;
    }

    if (mode == Mode::MidpointOnly) {
        // Exact v1 behavior.
        if (delta == 1 || delta == 3) {
            g_outerDrop.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        return ok;
    }

    return ok;
}

DWORD WINAPI ControlThread(LPVOID) {
    Log("============================================================");
    Log("SteamVR Motion Smoothing quality/smoothness v2 hook loaded.");
    Log("Target RVA=0x%llX", static_cast<unsigned long long>(kTargetRva));

    HMODULE exe = GetModuleHandleW(nullptr);
    if (!exe) { Log("ERROR: no main module."); return 1; }

    const auto target = reinterpret_cast<unsigned char*>(exe) + kTargetRva;
    if (!BytesMatch(target, kExpectedTargetBytes, sizeof(kExpectedTargetBytes))) {
        Log("ERROR: target signature mismatch; refusing hook.");
        return 2;
    }

    if (MH_Initialize() != MH_OK) { Log("ERROR: MH_Initialize."); return 3; }
    if (MH_CreateHook(target, reinterpret_cast<void*>(&HookSelectMotionVectors),
                      reinterpret_cast<void**>(&g_original)) != MH_OK) {
        Log("ERROR: MH_CreateHook.");
        MH_Uninitialize();
        return 4;
    }
    if (MH_EnableHook(target) != MH_OK) {
        Log("ERROR: MH_EnableHook.");
        MH_RemoveHook(target);
        MH_Uninitialize();
        return 5;
    }

    Log("HOOK ACTIVE. Only 30->120 MS is modified.");
    Log("DEFAULT: %s", ModeName(g_mode.load()));
    Log("HOTKEYS: Ctrl+Alt+1 Native | 2 SoftFar | 3 ClampFar | 4 Drop75 | 5 MidpointOnly");
    Log("Ctrl+Alt+I toggles Native <-> last modified mode.");

    bool p1=false,p2=false,p3=false,p4=false,p5=false,pi=false;
    DWORD lastStats = GetTickCount();

    while (g_running.load(std::memory_order_relaxed)) {
        const bool ctrl=(GetAsyncKeyState(VK_CONTROL)&0x8000)!=0;
        const bool alt =(GetAsyncKeyState(VK_MENU)&0x8000)!=0;
        const bool k1=ctrl&&alt&&(GetAsyncKeyState('1')&0x8000);
        const bool k2=ctrl&&alt&&(GetAsyncKeyState('2')&0x8000);
        const bool k3=ctrl&&alt&&(GetAsyncKeyState('3')&0x8000);
        const bool k4=ctrl&&alt&&(GetAsyncKeyState('4')&0x8000);
        const bool k5=ctrl&&alt&&(GetAsyncKeyState('5')&0x8000);
        const bool ki=ctrl&&alt&&(GetAsyncKeyState('I')&0x8000);

        if(k1&&!p1) SetMode(Mode::Native,"hotkey 1");
        if(k2&&!p2) SetMode(Mode::SoftFar,"hotkey 2");
        if(k3&&!p3) SetMode(Mode::ClampFar,"hotkey 3");
        if(k4&&!p4) SetMode(Mode::DropFar,"hotkey 4");
        if(k5&&!p5) SetMode(Mode::MidpointOnly,"hotkey 5");
        if(ki&&!pi) {
            const Mode cur=g_mode.load(std::memory_order_relaxed);
            SetMode(cur==Mode::Native ? g_lastNonNative.load(std::memory_order_relaxed)
                                      : Mode::Native,
                    "A/B toggle");
        }
        p1=k1;p2=k2;p3=k3;p4=k4;p5=k5;pi=ki;

        const DWORD now=GetTickCount();
        if(now-lastStats>=5000) {
            Log("STATS mode=%s calls=%llu gap4=%llu d1=%llu d2=%llu d3=%llu soft75=%llu clamp75=%llu drop75=%llu outer_drop=%llu",
                ModeName(g_mode.load(std::memory_order_relaxed)),
                (unsigned long long)g_calls.load(), (unsigned long long)g_gap4.load(),
                (unsigned long long)g_d1.load(), (unsigned long long)g_d2.load(),
                (unsigned long long)g_d3.load(), (unsigned long long)g_soft.load(),
                (unsigned long long)g_clamp.load(), (unsigned long long)g_drop75.load(),
                (unsigned long long)g_outerDrop.load());
            lastStats=now;
        }
        Sleep(25);
    }

    MH_DisableHook(target);
    MH_RemoveHook(target);
    MH_Uninitialize();
    Log("Hook shutdown.");
    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if(reason==DLL_PROCESS_ATTACH) {
        g_self=module;
        DisableThreadLibraryCalls(module);
        HANDLE t=CreateThread(nullptr,0,ControlThread,nullptr,0,nullptr);
        if(t) CloseHandle(t);
    } else if(reason==DLL_PROCESS_DETACH) {
        g_running.store(false,std::memory_order_relaxed);
        if(g_log){ std::fprintf(g_log,"Process detach.\n"); std::fclose(g_log); g_log=nullptr; }
    }
    return TRUE;
}
