// starthook.asi — the NATIVE trigger for the DOTNET_STARTUP_HOOKS experiment (branch
// test-single-asi-no-dll). Loaded EARLY by the Ultimate ASI Loader (winhttp), before LaunchBox's
// .NET runtime starts. Its ONLY job: set the DOTNET_STARTUP_HOOKS environment variable to the managed
// startuphook.dll (in Core) so the CLR runs StartupHook.Initialize() BEFORE LaunchBox's Main — i.e.
// before the platform-XML reads and before plugins. Pure native (no CLR touched here). Logs to
// Core\starthook.log so we can see the env var was set and when.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <cstdio>

static std::wstring CoreDir()
{
    wchar_t exe[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exe, MAX_PATH);   // the host exe (LaunchBox.exe), in Core
    std::wstring d(exe);
    auto s = d.find_last_of(L"\\/");
    if (s != std::wstring::npos) d.resize(s + 1);
    return d;
}

static void Log(const std::wstring& coreDir, const std::wstring& msg)
{
    std::wstring p = coreDir + L"starthook.log";
    FILE* f = nullptr;
    if (_wfopen_s(&f, p.c_str(), L"ab") != 0 || !f) return;
    SYSTEMTIME st; GetLocalTime(&st);
    fwprintf(f, L"[%02u:%02u:%02u.%03u] %ls\r\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, msg.c_str());
    fclose(f);
}

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(h);
        std::wstring core = CoreDir();                 // ...\LB\Core\
        // LB root = core minus the trailing "Core\"; point the hook at the combined plugin dll in Plugins.
        std::wstring root = core;
        if (root.size() >= 5) root.resize(root.size() - 5);   // strip "Core\"
        std::wstring hookDll = root + L"Plugins\\parentalprobe\\parentalprobe.dll";
        BOOL ok = SetEnvironmentVariableW(L"DOTNET_STARTUP_HOOKS", hookDll.c_str());
        Log(core, (ok ? L"DllMain: set DOTNET_STARTUP_HOOKS=" : L"DllMain: FAILED to set DOTNET_STARTUP_HOOKS=") + hookDll);
    }
    return TRUE;
}
