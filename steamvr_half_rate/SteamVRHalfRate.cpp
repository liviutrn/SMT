#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdarg>
#include <string>

#include "openvr_capi.h"

using UInt32 = std::uint32_t;
using PluginHandle = UInt32;

struct PluginInfo {
    enum { kInfoVersion = 1 };
    UInt32 infoVersion;
    const char* name;
    UInt32 version;
};

struct SKSEInterface {
    UInt32 skseVersion;
    UInt32 runtimeVersion;
    UInt32 editorVersion;
    UInt32 isEditor;
    void* (*QueryInterface)(UInt32 id);
    PluginHandle (*GetPluginHandle)(void);
};

using VR_GetGenericInterface_t = intptr_t (__cdecl*)(const char*, EVRInitError*);

static std::atomic<bool> g_running{true};
static std::atomic<bool> g_forced{false};
static FILE* g_log = nullptr;

static void Log(const char* fmt, ...) {
    if (!g_log) {
        char dllPath[MAX_PATH]{};
        HMODULE self = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCSTR>(&Log), &self);
        GetModuleFileNameA(self, dllPath, MAX_PATH);
        std::string path(dllPath);
        auto pos = path.find_last_of("\\/");
        if (pos != std::string::npos) path.resize(pos + 1);
        path += "SteamVRHalfRate.log";
        fopen_s(&g_log, path.c_str(), "a");
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

static DWORD WINAPI WorkerThread(LPVOID) {
    Log("SteamVRHalfRate worker started. PID=%lu", GetCurrentProcessId());

    VR_GetGenericInterface_t getInterface = nullptr;

    for (int i = 0; i < 120 && g_running.load(); ++i) {
        HMODULE openvr = GetModuleHandleA("openvr_api.dll");
        if (openvr) {
            getInterface = reinterpret_cast<VR_GetGenericInterface_t>(
                GetProcAddress(openvr, "VR_GetGenericInterface"));
            if (getInterface) break;
        }
        Sleep(500);
    }

    if (!getInterface) {
        Log("ERROR: openvr_api.dll / VR_GetGenericInterface was not available.");
        return 0;
    }

    EVRInitError err = EVRInitError_VRInitError_None;
    auto* compositor = reinterpret_cast<VR_IVRCompositor_FnTable*>(
        getInterface("FnTable:IVRCompositor_029", &err));

    if (!compositor || err != EVRInitError_VRInitError_None) {
        Log("ERROR: IVRCompositor_029 function table unavailable. err=%d ptr=%p",
            static_cast<int>(err), compositor);
        return 0;
    }

    if (!compositor->GetCurrentSceneFocusProcess ||
        !compositor->ForceInterleavedReprojectionOn) {
        Log("ERROR: required compositor functions are null.");
        return 0;
    }

    const DWORD selfPid = GetCurrentProcessId();
    Log("IVRCompositor_029 acquired. Waiting until Skyrim is scene-focus process.");

    while (g_running.load()) {
        const uint32_t focusPid = compositor->GetCurrentSceneFocusProcess();
        if (focusPid == selfPid) {
            compositor->ForceInterleavedReprojectionOn(true);
            g_forced.store(true);
            Log("FORCED ON: ForceInterleavedReprojectionOn(true), focusPid=%u", focusPid);
            break;
        }
        Sleep(250);
    }

    bool prevKey = false;
    while (g_running.load()) {
        const bool chord =
            (GetAsyncKeyState(VK_CONTROL) & 0x8000) &&
            (GetAsyncKeyState(VK_MENU) & 0x8000) &&
            (GetAsyncKeyState('I') & 0x8000);

        if (chord && !prevKey) {
            const bool next = !g_forced.load();
            compositor->ForceInterleavedReprojectionOn(next);
            g_forced.store(next);
            Log("LIVE TOGGLE: ForceInterleavedReprojectionOn(%s)", next ? "true" : "false");
        }
        prevKey = chord;
        Sleep(50);
    }
    return 0;
}

extern "C" __declspec(dllexport)
bool SKSEPlugin_Query(const SKSEInterface* skse, PluginInfo* info) {
    if (!info || !skse) return false;
    info->infoVersion = PluginInfo::kInfoVersion;
    info->name = "SteamVRHalfRate";
    info->version = 1;
    return skse->isEditor == 0;
}

extern "C" __declspec(dllexport)
bool SKSEPlugin_Load(const SKSEInterface*) {
    g_running.store(true);
    HANDLE thread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
    if (!thread) {
        Log("ERROR: CreateThread failed: %lu", GetLastError());
        return false;
    }
    CloseHandle(thread);
    return true;
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH) {
        g_running.store(false);
        if (g_log) {
            std::fprintf(g_log, "Process detach.\n");
            std::fclose(g_log);
            g_log = nullptr;
        }
    }
    return TRUE;
}
