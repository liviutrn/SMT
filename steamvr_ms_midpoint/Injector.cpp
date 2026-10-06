#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <bcrypt.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>
#include <cwctype>

#pragma comment(lib, "bcrypt.lib")

namespace {

constexpr unsigned char kExpectedSha256[32] = {
    0x5d,0xab,0xea,0xe9,0xa0,0xac,0x12,0xd7,
    0xc4,0x7a,0x3a,0xed,0x95,0x76,0xc0,0xd5,
    0x11,0x60,0x39,0xea,0xff,0xfb,0x47,0x75,
    0x6f,0xd6,0x74,0x60,0x5d,0x1f,0xe5,0x54
};

std::filesystem::path ExeDir() {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    return std::filesystem::path(path).parent_path();
}

bool IEquals(const std::wstring& a, const std::wstring& b) {
    if (a.size() != b.size()) return false;
    for (std::size_t i = 0; i < a.size(); ++i) {
        if (std::towlower(a[i]) != std::towlower(b[i])) return false;
    }
    return true;
}

DWORD FindProcess(const wchar_t* exeName) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;

    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    DWORD pid = 0;

    if (Process32FirstW(snapshot, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, exeName) == 0) {
                pid = pe.th32ProcessID;
                break;
            }
        } while (Process32NextW(snapshot, &pe));
    }

    CloseHandle(snapshot);
    return pid;
}

std::uintptr_t FindRemoteModuleBase(DWORD pid, const wchar_t* moduleName) {
    HANDLE snapshot = CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;

    MODULEENTRY32W me{};
    me.dwSize = sizeof(me);
    std::uintptr_t result = 0;

    if (Module32FirstW(snapshot, &me)) {
        do {
            if (_wcsicmp(me.szModule, moduleName) == 0) {
                result = reinterpret_cast<std::uintptr_t>(me.modBaseAddr);
                break;
            }
        } while (Module32NextW(snapshot, &me));
    }

    CloseHandle(snapshot);
    return result;
}

bool IsRemoteModuleLoaded(DWORD pid, const wchar_t* moduleName) {
    return FindRemoteModuleBase(pid, moduleName) != 0;
}

bool Sha256File(const std::filesystem::path& path, std::array<unsigned char,32>& digest) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;

    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD objectLength = 0, cb = 0, hashLength = 0;
    std::vector<unsigned char> object;
    bool ok = false;

    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) goto cleanup;
    if (BCryptGetProperty(alg, BCRYPT_OBJECT_LENGTH,
                          reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength), &cb, 0) != 0) goto cleanup;
    if (BCryptGetProperty(alg, BCRYPT_HASH_LENGTH,
                          reinterpret_cast<PUCHAR>(&hashLength), sizeof(hashLength), &cb, 0) != 0) goto cleanup;
    if (hashLength != digest.size()) goto cleanup;

    object.resize(objectLength);
    if (BCryptCreateHash(alg, &hash, object.data(), objectLength, nullptr, 0, 0) != 0) goto cleanup;

    {
        std::array<unsigned char, 1 << 16> buffer{};
        for (;;) {
            DWORD read = 0;
            if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) goto cleanup;
            if (read == 0) break;
            if (BCryptHashData(hash, buffer.data(), read, 0) != 0) goto cleanup;
        }
    }

    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) != 0) goto cleanup;
    ok = true;

cleanup:
    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    CloseHandle(file);
    return ok;
}

bool VerifyExactCompositor(HANDLE process, std::filesystem::path& compositorPath) {
    wchar_t path[32768]{};
    DWORD size = static_cast<DWORD>(std::size(path));
    if (!QueryFullProcessImageNameW(process, 0, path, &size)) {
        std::printf("ERROR: QueryFullProcessImageNameW failed (%lu).\n", GetLastError());
        return false;
    }

    compositorPath.assign(path, path + size);

    std::array<unsigned char,32> digest{};
    if (!Sha256File(compositorPath, digest)) {
        std::printf("ERROR: could not SHA-256 vrcompositor.exe.\n");
        return false;
    }

    if (std::memcmp(digest.data(), kExpectedSha256, digest.size()) != 0) {
        std::printf("REFUSED: vrcompositor.exe SHA-256 does not match the uploaded SteamVR 2.18.2 build.\n");
        std::wprintf(L"Detected: %ls\n", compositorPath.c_str());
        std::printf("This guard prevents applying a version-specific hook to an updated/different compositor.\n");
        return false;
    }

    return true;
}

bool InjectDll(DWORD pid, HANDLE process, const std::filesystem::path& dllPath) {
    if (!std::filesystem::exists(dllPath)) {
        std::wprintf(L"ERROR: hook DLL not found: %ls\n", dllPath.c_str());
        return false;
    }

    const std::wstring dll = std::filesystem::absolute(dllPath).wstring();
    const SIZE_T bytes = (dll.size() + 1) * sizeof(wchar_t);

    void* remoteString = VirtualAllocEx(process, nullptr, bytes,
                                        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remoteString) {
        std::printf("ERROR: VirtualAllocEx failed (%lu).\n", GetLastError());
        return false;
    }

    bool success = false;

    if (!WriteProcessMemory(process, remoteString, dll.c_str(), bytes, nullptr)) {
        std::printf("ERROR: WriteProcessMemory failed (%lu).\n", GetLastError());
        goto cleanup;
    }

    HMODULE localKernel32 = GetModuleHandleW(L"kernel32.dll");
    FARPROC localLoadLibrary = GetProcAddress(localKernel32, "LoadLibraryW");
    const std::uintptr_t localBase = reinterpret_cast<std::uintptr_t>(localKernel32);
    const std::uintptr_t loadLibraryOffset =
        reinterpret_cast<std::uintptr_t>(localLoadLibrary) - localBase;

    const std::uintptr_t remoteKernel32 = FindRemoteModuleBase(pid, L"kernel32.dll");
    if (!remoteKernel32) {
        std::printf("ERROR: could not locate remote kernel32.dll.\n");
        goto cleanup;
    }

    auto remoteLoadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        remoteKernel32 + loadLibraryOffset);

    HANDLE thread = CreateRemoteThread(process, nullptr, 0, remoteLoadLibrary,
                                       remoteString, 0, nullptr);
    if (!thread) {
        std::printf("ERROR: CreateRemoteThread failed (%lu).\n", GetLastError());
        goto cleanup;
    }

    WaitForSingleObject(thread, 10000);
    CloseHandle(thread);

    // Verify by module enumeration rather than trusting the 32-bit thread exit code.
    for (int i = 0; i < 40; ++i) {
        if (IsRemoteModuleLoaded(pid, dllPath.filename().c_str())) {
            success = true;
            break;
        }
        Sleep(100);
    }

cleanup:
    VirtualFreeEx(process, remoteString, 0, MEM_RELEASE);
    return success;
}

} // namespace

int wmain() {
    std::puts("SteamVR Motion Smoothing - midpoint-only 30->60 experiment");
    std::puts("Version locked to the user's exact SteamVR 2.18.2 vrcompositor.exe.");
    std::puts("");

    const DWORD pid = FindProcess(L"vrcompositor.exe");
    if (!pid) {
        std::puts("ERROR: vrcompositor.exe is not running. Start SteamVR first.");
        return 1;
    }

    HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION |
                                 PROCESS_CREATE_THREAD |
                                 PROCESS_VM_OPERATION |
                                 PROCESS_VM_WRITE |
                                 PROCESS_VM_READ,
                                 FALSE, pid);
    if (!process) {
        std::printf("ERROR: OpenProcess(%lu) failed (%lu).\n", pid, GetLastError());
        return 2;
    }

    std::filesystem::path compositorPath;
    if (!VerifyExactCompositor(process, compositorPath)) {
        CloseHandle(process);
        return 3;
    }

    std::printf("Matched exact vrcompositor.exe. PID=%lu\n", pid);

    const auto dllPath = ExeDir() / L"SteamVRMSMidpointHook.dll";

    if (IsRemoteModuleLoaded(pid, dllPath.filename().c_str())) {
        std::puts("Hook DLL is already loaded in vrcompositor.");
        std::puts("Ctrl+Alt+I toggles midpoint-only filtering live.");
        CloseHandle(process);
        return 0;
    }

    if (!InjectDll(pid, process, dllPath)) {
        std::puts("ERROR: DLL injection failed.");
        CloseHandle(process);
        return 4;
    }

    CloseHandle(process);

    std::puts("");
    std::puts("Injected successfully.");
    std::puts("30->120 only: 25% and 75% hallucinations are suppressed; 50% midpoint remains.");
    std::puts("All other SteamVR reprojection ratios stay native.");
    std::puts("Ctrl+Alt+I toggles the filtering live.");
    std::puts("Restart SteamVR to fully unload the hook.");
    std::puts("See SteamVRMSMidpoint.log beside this EXE/DLL for counters.");
    return 0;
}
