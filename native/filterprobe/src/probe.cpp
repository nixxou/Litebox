// filterprobe.dll — a TIMING PROBE for the "single managed DLL" experiment (branch
// test-single-asi-no-dll). NOT the real filter — it does not filter anything, it only OBSERVES.
//
// The question: if a managed LaunchBox plugin installs the CreateFileW hook as early as it can
// (its [ModuleInitializer]), is that early enough to intercept the platform-XML reads LaunchBox
// does at startup? If yes, the whole separate ASI + winhttp early-loader can be dropped and the
// filter can live in the same managed plugin as the write-guard — which makes the two impossible
// to diverge (the source of every data-loss bug so far).
//
// Probe_Install (called by the managed plugin at ModuleInitializer) hooks CreateFileW and logs
// every LB\Data\Platforms\*.xml open to Core\filterprobe.log with a timestamp. The detour is a pure
// passthrough (logs, then calls the original) so LaunchBox behaves normally. Probe_Count returns how
// many platform-xml opens have been seen since the hook armed, for the plugin's menu caption.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlwapi.h>
#include <MinHook.h>

#include <cstdio>
#include <mutex>
#include <string>

#pragma comment(lib, "shlwapi.lib")

static std::wstring g_logPath;
static std::mutex   g_logMutex;

static void InitLogPath()
{
    if (!g_logPath.empty()) return;
    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);   // the host exe (LaunchBox.exe / BigBox.exe), in Core
    std::wstring dir(exePath);
    auto s = dir.find_last_of(L"\\/");
    if (s != std::wstring::npos) dir.resize(s + 1);
    g_logPath = dir + L"filterprobe.log";
}

static void Log(const std::string& line)
{
    std::lock_guard<std::mutex> lk(g_logMutex);
    InitLogPath();
    FILE* f = nullptr;
    if (_wfopen_s(&f, g_logPath.c_str(), L"ab") != 0 || f == nullptr) return;
    SYSTEMTIME st; GetLocalTime(&st);
    fprintf(f, "[%02u:%02u:%02u.%03u] %s\r\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line.c_str());
    fclose(f);
}

static std::string Narrow(const std::wstring& w)
{
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}
static bool ContainsCI(const std::wstring& h, const wchar_t* n) { return StrStrIW(h.c_str(), n) != nullptr; }
static bool EndsWithCI(const std::wstring& s, const wchar_t* suf)
{
    size_t n = wcslen(suf);
    if (s.size() < n) return false;
    return _wcsicmp(s.c_str() + s.size() - n, suf) == 0;
}

using CreateFileW_t = HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
static CreateFileW_t g_orig = nullptr;
static volatile LONG g_platformReads = 0;

static HANDLE WINAPI Hook_CreateFileW(LPCWSTR name, DWORD acc, DWORD share, LPSECURITY_ATTRIBUTES sa,
                                      DWORD disp, DWORD fl, HANDLE tmpl)
{
    if (name != nullptr)
    {
        std::wstring p(name);
        bool w = (acc & GENERIC_WRITE) != 0;
        if (ContainsCI(p, L"\\Data\\Platforms\\") && EndsWithCI(p, L".xml"))
        {
            InterlockedIncrement(&g_platformReads);
            Log(std::string("[Open] Platforms xml ") + (w ? "WRITE " : "READ  ") + Narrow(p));
        }
        // WRITE-GUARD probe: does LaunchBox's save surface here as a write/create open? File.Copy -> CopyFileEx
        // may use FILE_GENERIC_WRITE (not GENERIC_WRITE), so detect ANY write access bit OR a create/truncate
        // disposition, and log the RAW access mask + disposition so we see the exact pattern. Observe ONLY.
        else if (ContainsCI(p, L"\\Data\\"))
        {
            const DWORD writeBits = GENERIC_WRITE | GENERIC_ALL | 0x0002 /*FILE_WRITE_DATA*/ | 0x0004 /*FILE_APPEND_DATA*/ | 0x0100 /*FILE_WRITE_EA*/;
            bool writeish = (acc & writeBits) != 0;
            bool createish = (disp == CREATE_ALWAYS || disp == CREATE_NEW || disp == TRUNCATE_EXISTING); // 2 / 1 / 5
            if (writeish || createish)
            {
                char buf[64]; snprintf(buf, sizeof(buf), "acc=0x%08lX disp=%lu ", (unsigned long)acc, (unsigned long)disp);
                Log(std::string("[Open] Data WRITE  ") + buf + Narrow(p));
            }
        }
    }
    return g_orig(name, acc, share, sa, disp, fl, tmpl);
}

// Called by the managed plugin from its [ModuleInitializer]. Returns 0 on success.
// -------------------- copy-API hooks (the real write path: File.Copy -> CopyFile2 / CopyFileExW) --------------
// LaunchBox saves via File.Copy(temp -> Data\), which does NOT go through the exported CreateFileW (CopyFile2
// opens internally). So the write-guard needs its OWN native hook on the copy API. Observe only (passthrough).

using CopyFileExW_t = BOOL (WINAPI*)(LPCWSTR, LPCWSTR, LPPROGRESS_ROUTINE, LPVOID, LPBOOL, DWORD);
static CopyFileExW_t g_orig_CopyFileExW = nullptr;
static BOOL WINAPI Hook_CopyFileExW(LPCWSTR src, LPCWSTR dst, LPPROGRESS_ROUTINE pr, LPVOID d, LPBOOL cancel, DWORD flags)
{
    if (dst) { std::wstring p(dst); if (ContainsCI(p, L"\\Data\\")) Log(std::string("[Copy] CopyFileExW -> ") + Narrow(p)); }
    return g_orig_CopyFileExW(src, dst, pr, d, cancel, flags);
}

using CopyFile2_t = HRESULT (WINAPI*)(PCWSTR, PCWSTR, COPYFILE2_EXTENDED_PARAMETERS*);
static CopyFile2_t g_orig_CopyFile2 = nullptr;
static HRESULT WINAPI Hook_CopyFile2(PCWSTR src, PCWSTR dst, COPYFILE2_EXTENDED_PARAMETERS* ep)
{
    if (dst) { std::wstring p(dst); if (ContainsCI(p, L"\\Data\\")) Log(std::string("[Copy] CopyFile2 -> ") + Narrow(p)); }
    return g_orig_CopyFile2(src, dst, ep);
}

extern "C" __declspec(dllexport) int __stdcall Probe_Install()
{
    InitLogPath();
    Log("[ProbeInstall] called from managed ModuleInitializer");
    if (MH_Initialize() != MH_OK)                                                  { Log("[ProbeInstall] MH_Initialize FAILED"); return 1; }
    if (MH_CreateHook((LPVOID)&CreateFileW, (LPVOID)&Hook_CreateFileW, (LPVOID*)&g_orig) != MH_OK) { Log("[ProbeInstall] MH_CreateHook FAILED"); return 2; }
    if (MH_EnableHook((LPVOID)&CreateFileW) != MH_OK)                              { Log("[ProbeInstall] MH_EnableHook FAILED"); return 3; }
    Log("[ProbeInstall] hook ARMED on CreateFileW — any Platforms\\*.xml opened from now on is logged");

    // Also hook the copy APIs (the real write path). Resolve by name from kernel32 so a missing symbol is soft.
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (k32)
    {
        FARPROC pCfe = GetProcAddress(k32, "CopyFileExW");
        if (pCfe && MH_CreateHook((LPVOID)pCfe, (LPVOID)&Hook_CopyFileExW, (LPVOID*)&g_orig_CopyFileExW) == MH_OK && MH_EnableHook((LPVOID)pCfe) == MH_OK)
            Log("[ProbeInstall] hook ARMED on CopyFileExW");
        else Log("[ProbeInstall] CopyFileExW hook not installed");
        FARPROC pCf2 = GetProcAddress(k32, "CopyFile2");
        if (pCf2 && MH_CreateHook((LPVOID)pCf2, (LPVOID)&Hook_CopyFile2, (LPVOID*)&g_orig_CopyFile2) == MH_OK && MH_EnableHook((LPVOID)pCf2) == MH_OK)
            Log("[ProbeInstall] hook ARMED on CopyFile2");
        else Log("[ProbeInstall] CopyFile2 hook not installed");
    }
    return 0;
}

extern "C" __declspec(dllexport) int __stdcall Probe_Count() { return (int)g_platformReads; }

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
