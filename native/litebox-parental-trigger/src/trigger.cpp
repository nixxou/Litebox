// litebox-parental.asi — the generic native TRIGGER of the single-artifact parental control.
//
// Loaded EARLY by the Ultimate ASI Loader (winhttp.dll proxy) into LaunchBox.exe / BigBox.exe, before .NET
// starts. Its ONLY job: if the managed dll is present at Plugins\litebox-parental\litebox-parental.dll, set
// DOTNET_STARTUP_HOOKS to it, so the CLR runs its StartupHook.Initialize() before Main (which then loads +
// arms the native .bin). If the managed dll is ABSENT, do nothing — LaunchBox runs normally, never crashes
// on a missing startup hook. Pure native, no business logic, immutable across versions → can never diverge.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(h);

    // Core dir of the host exe (LaunchBox.exe / BigBox.exe), then the LB root (Core dir minus "Core\").
    wchar_t exe[MAX_PATH] = {};
    if (GetModuleFileNameW(nullptr, exe, MAX_PATH) == 0) return TRUE;
    std::wstring core(exe);
    auto slash = core.find_last_of(L"\\/");
    if (slash != std::wstring::npos) core.resize(slash + 1);   // the Core directory, trailing separator
    std::wstring root = core;
    if (root.size() >= 5) root.resize(root.size() - 5);        // strip the 5 chars "Core" + separator

    std::wstring managed = root + L"Plugins\\litebox-parental\\litebox-parental.dll";

    // Only arm if the managed dll actually exists — a missing DOTNET_STARTUP_HOOKS target fails FAST and would
    // crash LaunchBox. Absent dll ⇒ no parental control, LaunchBox unaffected.
    DWORD attrs = GetFileAttributesW(managed.c_str());
    if (attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY))
        SetEnvironmentVariableW(L"DOTNET_STARTUP_HOOKS", managed.c_str());

    return TRUE;
}
