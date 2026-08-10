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
        if (ContainsCI(p, L"\\Data\\Platforms\\") && EndsWithCI(p, L".xml"))
        {
            bool w = (acc & GENERIC_WRITE) != 0;
            InterlockedIncrement(&g_platformReads);
            Log(std::string("[Open] Platforms xml ") + (w ? "WRITE " : "READ  ") + Narrow(p));
        }
    }
    return g_orig(name, acc, share, sa, disp, fl, tmpl);
}

// Called by the managed plugin from its [ModuleInitializer]. Returns 0 on success.
extern "C" __declspec(dllexport) int __stdcall Probe_Install()
{
    InitLogPath();
    Log("[ProbeInstall] called from managed ModuleInitializer");
    if (MH_Initialize() != MH_OK)                                                  { Log("[ProbeInstall] MH_Initialize FAILED"); return 1; }
    if (MH_CreateHook((LPVOID)&CreateFileW, (LPVOID)&Hook_CreateFileW, (LPVOID*)&g_orig) != MH_OK) { Log("[ProbeInstall] MH_CreateHook FAILED"); return 2; }
    if (MH_EnableHook((LPVOID)&CreateFileW) != MH_OK)                              { Log("[ProbeInstall] MH_EnableHook FAILED"); return 3; }
    Log("[ProbeInstall] hook ARMED on CreateFileW — any Platforms\\*.xml opened from now on is logged");
    return 0;
}

extern "C" __declspec(dllexport) int __stdcall Probe_Count() { return (int)g_platformReads; }

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
