#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <openvr.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdarg>
#include <filesystem>
#include <string>
#include <thread>

static std::atomic<bool> g_running{true};
static std::atomic<bool> g_enabled{true};
static FILE* g_log = nullptr;

static std::filesystem::path GetExeDir() {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    return std::filesystem::path(path).parent_path();
}

static void Log(const char* fmt, ...) {
    if (!g_log) {
        const auto path = GetExeDir() / L"SteamVRHalfRate.log";
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

static BOOL WINAPI ConsoleHandler(DWORD type) {
    switch (type) {
    case CTRL_C_EVENT:
    case CTRL_CLOSE_EVENT:
    case CTRL_BREAK_EVENT:
    case CTRL_LOGOFF_EVENT:
    case CTRL_SHUTDOWN_EVENT:
        g_running.store(false);
        return TRUE;
    default:
        return FALSE;
    }
}

static void ApplyState(vr::IVRCompositor* compositor, bool enabled, const char* reason) {
    if (!compositor) return;
    compositor->ForceInterleavedReprojectionOn(enabled);
    Log("ForceInterleavedReprojectionOn(%s) [%s]",
        enabled ? "true" : "false", reason);
    std::printf("SteamVR Half Rate: %s (%s)\n",
                enabled ? "ON" : "OFF", reason);
}

int main() {
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);

    std::puts("SteamVR Half Rate - library-wide experiment");
    std::puts("Ctrl+Alt+I : toggle forced interleaved reprojection");
    std::puts("Ctrl+C     : exit and restore OFF");
    std::puts("");

    Log("============================================================");
    Log("SteamVRHalfRate starting. PID=%lu", GetCurrentProcessId());

    vr::EVRInitError err = vr::VRInitError_None;
    vr::IVRSystem* system = vr::VR_Init(&err, vr::VRApplication_Background);
    if (err != vr::VRInitError_None || !system) {
        const char* desc = vr::VR_GetVRInitErrorAsEnglishDescription(err);
        std::fprintf(stderr, "OpenVR init failed: %d (%s)\n",
                     static_cast<int>(err), desc ? desc : "unknown");
        Log("ERROR: OpenVR init failed: %d (%s)",
            static_cast<int>(err), desc ? desc : "unknown");
        return 2;
    }

    vr::IVRCompositor* compositor = vr::VRCompositor();
    if (!compositor) {
        std::fprintf(stderr, "OpenVR compositor interface unavailable.\n");
        Log("ERROR: VRCompositor() returned null.");
        vr::VR_Shutdown();
        return 3;
    }

    const uint32_t selfPid = GetCurrentProcessId();
    Log("OpenVR initialized as background client. ownerPid=%u", selfPid);

    uint32_t lastFocusPid = compositor->GetCurrentSceneFocusProcess();
    Log("Initial scene focus PID=%u", lastFocusPid);

    ApplyState(compositor, true, "startup");
    g_enabled.store(true);

    bool prevChord = false;
    auto lastHeartbeat = std::chrono::steady_clock::now();

    while (g_running.load()) {
        const bool chord =
            (GetAsyncKeyState(VK_CONTROL) & 0x8000) &&
            (GetAsyncKeyState(VK_MENU) & 0x8000) &&
            (GetAsyncKeyState('I') & 0x8000);

        if (chord && !prevChord) {
            const bool next = !g_enabled.load();
            g_enabled.store(next);
            ApplyState(compositor, next, "hotkey");
        }
        prevChord = chord;

        const uint32_t focusPid = compositor->GetCurrentSceneFocusProcess();
        if (focusPid != lastFocusPid) {
            Log("Scene focus PID changed: %u -> %u", lastFocusPid, focusPid);
            lastFocusPid = focusPid;

            // Re-assert after app transitions. SteamVR owns the setting globally,
            // but this protects the experiment against a scene app changing its
            // reprojection state during launch/focus transitions.
            ApplyState(compositor, g_enabled.load(), "scene-focus-change");
        }

        const auto now = std::chrono::steady_clock::now();
        if (now - lastHeartbeat > std::chrono::seconds(10)) {
            Log("Heartbeat: enabled=%s focusPid=%u ownerPid=%u",
                g_enabled.load() ? "true" : "false", focusPid, selfPid);
            lastHeartbeat = now;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }

    // Important: explicitly release the global override before disconnecting.
    ApplyState(compositor, false, "shutdown");
    vr::VR_Shutdown();

    Log("OpenVR shut down cleanly.");
    if (g_log) {
        std::fclose(g_log);
        g_log = nullptr;
    }

    return 0;
}
