// Pause-screen engine (mode-agnostic). Owns ALL the mechanics so the screen
// implementations stay pure presentation:
//
//   • Global hotkey (RegisterHotKey, armed only while a game runs) — LiteBox.ini
//     "PauseHotkey" (default "Pause", supports "Ctrl+F12"-style combos). Works while
//     the EMULATOR has focus, which a WinForms message filter can't do.
//   • Pause:  run the emulator's PauseAutoHotkeyScript (one-off, BEFORE freezing so it
//     can still send keys), then NtSuspendProcess (emulator field SuspendProcessOnPause,
//     default true), then show the configured IPauseScreen.
//   • Resume: close the screen, NtResumeProcess, run ResumeAutoHotkeyScript, re-focus
//     the emulator window.
//   • Save/Load state, Reset, Swap discs: close + resume + focus the emulator, then run
//     the matching one-off script (LB behaviour: the pause screen does not reopen).
//   • Exit game: close + resume, then ExitAutoHotkeyScript (graceful close — this is the
//     ONLY place LB runs it, verified by RE) with a kill fallback after a timeout.
//
// The emulator-side switches (UsePauseScreen / SuspendProcessOnPause /
// ForcefulPauseScreenActivation) are NOT on the SDK IEmulator — they're read through
// the host's ILiteBoxFields dict (HostEmulator round-trips every XML field).
//
// Suspension uses ntdll!NtSuspendProcess / NtResumeProcess — undocumented but
// universal; it's what LB itself uses for SuspendProcessOnPause.

#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Pause;

internal static class PauseManager
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private const int SW_RESTORE = 9;
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    // Process-tree walk (Toolhelp) — for the optional "freeze the whole process tree" pause option.
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll")] private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    private const uint TH32CS_SNAPPROCESS = 0x2;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID;
        public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase;
        public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    // Low-level keyboard hook (WH_KEYBOARD_LL) — the AHK-grade pause interception. RegisterHotKey is
    // bypassed by raw-input / DirectInput fullscreen games; the LL hook sees every keystroke before any
    // app and can SUPPRESS it (return 1). It captures BOTH hardware AND software (injected) keys — a game
    // played over Remote Desktop delivers the pause key as injected, so skipping injected keys made
    // interception silently fail over RDP. Our OWN AHK sends are recognised (KEY_IGNORE dwExtraInfo) and,
    // for on-demand scripts, additionally run inside a pass-through window, so they still reach the game.
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    // AHK tags every key it synthesizes with a dwExtraInfo in its KEY_IGNORE family (base 0xFFC3D449,
    // plus SendLevel 0..100) — precisely so hooks can tell "this came from AHK". We use it to let our own
    // pause-script sends through even when they LOOK physical (no INJECTED flag) or fire outside the window.
    private const ulong AHK_EXTRAINFO_MIN = 0xFFC3D449, AHK_EXTRAINFO_MAX = 0xFFC3D4B0;

    private const int HotkeyId = 0xB0B;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private static readonly object _lock = new();
    private static LiteBoxConfig? _cfg;
    private static string _lbRoot = "";
    private static IPauseScreen? _screen;

    // Armed launch state.
    private static Process? _proc;
    private static IEmulator? _emu;
    private static IGame? _game;
    private static bool _paused, _suspended;
    private static readonly System.Collections.Generic.List<int> _suspendedPids = new();   // exact set to resume

    // LL-hook pause interception state.
    private static IntPtr _hook;
    private static LowLevelKeyboardProc? _hookProc;   // keep the delegate alive while the hook is set
    private static uint _hkVk, _hkMod;                 // target key + MOD_* modifier bits
    private static bool _hkLatched;                    // fired-once-per-press guard (ignore auto-repeat)
    private static long _passThroughUntilTicks;        // interception disabled while an on-demand script runs (≤5s)

    public static void Configure(LiteBoxConfig cfg, string lbRoot)
    {
        _cfg = cfg; _lbRoot = lbRoot;
        // The hotkey window and the pause overlay need a PUMPING thread: WM_HOTKEY is
        // only delivered through a message loop. UiThread runs Application.Run on a
        // dedicated STA thread; without this (GUI mode never starts it), Invoke would
        // fall back inline onto the launch worker — which blocks in WaitForExit and
        // never pumps.
        UiThread.Start();
    }

    // ── Arm / disarm (called by HostLaunch around the emulator's lifetime) ──

    /// <summary>Registers the pause hotkey for this launch. No-op when pause resolves OFF for this game
    /// (game override → emulator field → global default).</summary>
    public static void Arm(Process proc, IEmulator? emulator, IGame game)
    {
        // emulator may be null: store / direct-exe / DOSBox games pause too — the LL hotkey hook,
        // process suspend and pause screen are all emulator-independent; only the AHK scripts and
        // the per-emulator field lookups fall back to global / per-game defaults when it's null.
        if (proc == null) return;
        // "Use Game Pause Screen" resolved strictly game → emulator → global, symmetric with the startup
        // screen: the global (Settings.xml · Gameplay) is a DEFAULT, not a hard gate — a per-game or
        // per-emulator override can re-ENABLE pause even when the global is off (and vice-versa). The
        // per-game value comes from the LaunchedGame snapshot (captured pre-drop), like LB's Edit Game panel.
        var snap0 = LaunchedGame.Current;
        bool globalUse = emulator != null ? Gameplay.GameplaySettings.PauseEnabledGlobal()
                                          : Gameplay.GameplaySettings.NonEmuUsePause();
        bool usePause = snap0 is { PauseOverride: true } ? snap0.PauseUse : FieldBool(emulator, "UsePauseScreen", globalUse);
        if (!usePause)
        { Console.WriteLine("[pause] pause screen disabled (game/emulator/global) — off"); return; }

        lock (_lock)
        {
            DisarmLocked();
            _proc = proc; _emu = emulator; _game = game;
            _paused = _suspended = false;

            string? emuId = Safe(() => emulator?.Id);
            string? gameId = Safe(() => game?.Id);

            // Keyboard pause hotkey, resolved game → emulator → global (litebox-options.db): a game or
            // emulator can override the combo or disable it outright ("None"). Empty ⇒ no keyboard
            // trigger, but the CONTROLLER trigger below is independent (either can pause).
            string pauseKey = Data.LiteBoxOption.ResolveString("PauseHotkey", emuId, Gameplay.GameplaySettings.PauseKey(), gameId);
            if (!string.IsNullOrWhiteSpace(pauseKey) && !pauseKey.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                var (mod, vk, label) = ParseHotkey(pauseKey);
                InstallHook(mod, vk);
                Console.WriteLine($"[pause] armed — hotkey {label} (LL keyboard hook)");
            }
            else Console.WriteLine("[pause] keyboard pause hotkey disabled");

            // Controller pause trigger (XInput 0), resolved game → emulator → global. Enabled +
            // button both overridable per game/emulator, same tri-state as the rest. Off by default.
            bool padOn = Data.LiteBoxOption.ResolveBool("PadPauseEnabled", emuId, Gameplay.GameplaySettings.PadPauseEnabled(), gameId);
            if (padOn)
            {
                string combo = Data.LiteBoxOption.ResolveString("PadPauseButton", emuId, Gameplay.GameplaySettings.PadPauseButton(), gameId);
                PadPauseWatcher.Start(combo, OnHotkey);
            }
        }
    }

    /// <summary>Tears down the hotkey + screen and resumes a still-suspended process
    /// (a killed game must never leave a frozen orphan). Idempotent.</summary>
    public static void Disarm()
    {
        lock (_lock) DisarmLocked();
    }

    private static void DisarmLocked()
    {
        try { if (_suspended) ResumeTargets(); }   // a killed game must never leave a frozen orphan
        catch { }
        UnmuteIfWeMuted();   // a game killed while paused must not stay muted (session may outlive us)
        PadPauseWatcher.Stop();
        _suspended = false; _paused = false;
        UninstallHook();
        var s = _screen; _screen = null;
        UiThread.Invoke(() => { try { s?.Close(); } catch { } });
        _proc = null; _emu = null; _game = null;
    }

    // ── Low-level keyboard hook (physical pause interception) ────────────

    private static void InstallHook(uint mod, uint vk)
    {
        _hkVk = vk; _hkMod = mod; _hkLatched = false;
        System.Threading.Interlocked.Exchange(ref _passThroughUntilTicks, 0);
        // Install on the UiThread: the LL-hook callback is delivered on the installing thread, which must
        // keep pumping messages (UiThread runs Application.Run) or the hook is silently timed out.
        UiThread.Invoke(() =>
        {
            try
            {
                _hookProc = HookProc;   // hold the delegate so the GC can't collect it under native code
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) Console.WriteLine("[pause] SetWindowsHookEx failed — pause hotkey unavailable");
            }
            catch (Exception ex) { Console.WriteLine("[pause] hook install failed: " + ex.Message); }
        });
    }

    private static void UninstallHook()
    {
        var h = _hook; _hook = IntPtr.Zero; _hookProc = null;
        if (h == IntPtr.Zero) return;
        UiThread.Invoke(() => { try { UnhookWindowsHookEx(h); } catch { } });
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == _hkVk)
            {
                if (msg == WM_KEYUP || msg == WM_SYSKEYUP) _hkLatched = false;
                else if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    // Capture BOTH hardware and software keys (RDP delivers the pause key as injected).
                    // The one class we must never re-catch is our OWN AHK sends: AHK tags them with its
                    // KEY_IGNORE dwExtraInfo, and on-demand scripts also run inside the pass-through window.
                    ulong ei = unchecked((ulong)(long)data.dwExtraInfo);
                    bool fromAhk = ei >= AHK_EXTRAINFO_MIN && ei <= AHK_EXTRAINFO_MAX;
                    bool passThrough = DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref _passThroughUntilTicks);
                    if (!fromAhk && !passThrough && ModifiersMatch(_hkMod))
                    {
                        // Fire once per press (swallow auto-repeat), off the hook thread, and SUPPRESS:
                        // return 1 so the game never receives this key. An on-pause script can re-send it
                        // (during the pass-through window) to reach the game itself.
                        if (!_hkLatched) { _hkLatched = true; try { System.Threading.Tasks.Task.Run(OnHotkey); } catch { } }
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool ModifiersMatch(uint mod)
    {
        bool ctrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool alt   = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool win   = ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0;
        return ctrl == ((mod & MOD_CONTROL) != 0) && alt == ((mod & MOD_ALT) != 0)
            && shift == ((mod & MOD_SHIFT) != 0) && win == ((mod & MOD_WIN) != 0);
    }

    /// <summary>Run an on-demand pause script with keyboard interception PAUSED (≤5s) so the script's own
    /// key sends — even "physical"-looking ones AHK can emit — reach the game instead of being caught by
    /// our hook. Interception resumes when the script process exits or after 5s, whichever comes first.
    /// (The persistent RUNNING script is NOT routed here, so pause stays interceptable during the game.)</summary>
    private static Process? RunScriptPassThrough(string script)
    {
        System.Threading.Interlocked.Exchange(ref _passThroughUntilTicks, DateTime.UtcNow.AddSeconds(5).Ticks);
        var p = AhkScript.RunOneOff(script, _lbRoot);
        if (p == null) { System.Threading.Interlocked.Exchange(ref _passThroughUntilTicks, 0); return null; }
        try { p.EnableRaisingEvents = true; p.Exited += (_, _) => System.Threading.Interlocked.Exchange(ref _passThroughUntilTicks, 0); } catch { }
        return p;
    }

    // ── Hotkey → toggle ─────────────────────────────────────────────────

    private static void OnHotkey()
    {
        lock (_lock)
        {
            // Only the process is required — _emu is legitimately null for store / direct-exe / DOSBox
            // games (all the _emu reads below fall back to global / per-game defaults).
            if (_proc == null) return;
            try { if (_proc.HasExited) return; } catch { return; }
            if (_paused) ResumeLocked();
            else PauseLocked();
        }
    }

    private static void PauseLocked()
    {
        _paused = true;
        Console.WriteLine("[pause] pausing");

        // 0. Mute the emulator's audio session(s) FIRST — LB's "Mute Audio During
        //    Transitions": the whole transition (pause script, freeze, screen fade)
        //    is silent. Skipped when the app was already muted by the user (we then
        //    must not unmute it on resume either). AppAudio = the BigBoxProfile
        //    per-process mute building block.
        _mutedByUs = false;
        if (Gameplay.GameplaySettings.PauseMutingGlobal())
        {
            try
            {
                if (_proc is { HasExited: false } && AppAudio.GetMute(_proc.Id) != true)
                    _mutedByUs = AppAudio.SetMute(_proc.Id, true);
            }
            catch { }
        }

        // 1. Pause script BEFORE the freeze (it usually sends the emulator's own
        //    pause key, so the process must still be running).
        var p = RunScriptPassThrough(ScriptStr("PauseAutoHotkeyScript"));
        if (p != null) { try { p.WaitForExit(1500); } catch { } }

        // 2. Freeze default (ON, like LB; per-game override wins). Non-emulator games (no _emu) take the
        //    global "suspend non-emu games" default instead of a hardcoded true.
        var snapS = LaunchedGame.Current;
        bool suspendDef = _emu != null ? true : Gameplay.GameplaySettings.NonEmuSuspend();
        bool doSuspend = snapS is { PauseOverride: true } ? snapS.PauseSuspend : FieldBool(_emu, "SuspendProcessOnPause", suspendDef);

        // 3. Screen. Cosmetics come from the LaunchedGame snapshot (captured before the launch memory
        //    drop) — never from the store / cache. Forceful-activation default is non-emu-aware too.
        var snap = LaunchedGame.Current;
        bool forceDef = _emu != null ? false : Gameplay.GameplaySettings.NonEmuForceful();
        var ctx = new PauseContext
        {
            GameTitle = snap?.Title ?? Safe(() => _game?.Title) ?? "",
            Platform = snap?.Platform ?? Safe(() => _game?.Platform) ?? "",
            Developer = snap?.Developer ?? "",
            ReleaseYear = snap?.ReleaseYear ?? 0,
            FanartPath = snap?.FanartPath,
            ClearLogoPath = snap?.ClearLogoPath,
            BoxFrontPath = snap?.BoxFrontPath,
            SessionStartUtc = snap?.LaunchedAtUtc ?? DateTime.UtcNow,
            CanViewManual = !string.IsNullOrEmpty(snap?.ManualPath),
            CanSaveState = !AhkScript.IsScriptEmpty(ScriptStr("SaveStateAutoHotkeyScript")),
            CanLoadState = !AhkScript.IsScriptEmpty(ScriptStr("LoadStateAutoHotkeyScript")),
            CanReset = !AhkScript.IsScriptEmpty(ScriptStr("ResetAutoHotkeyScript")),
            CanSwapDiscs = !AhkScript.IsScriptEmpty(ScriptStr("SwapDiscsAutoHotkeyScript")),
            ForcefulActivation = snap is { PauseOverride: true } ? snap.PauseForceful : FieldBool(_emu, "ForcefulPauseScreenActivation", forceDef),
            EmulatorMainWindow = EmulatorWindow(),
            OnAction = a => System.Threading.Tasks.Task.Run(() => OnScreenAction(a)),
        };
        void ShowScreen() => UiThread.Invoke(() =>
        {
            _screen ??= PauseScreenFactory.Create(_cfg!);
            _screen.Show(ctx);
        });
        void Freeze() => SuspendTargets();

        // Order freeze vs. screen per the timing option. "Before" (default) paints the overlay over the
        // STILL-RUNNING game then freezes — no flash of a frozen frame; "after" freezes first. The offset (ms)
        // tunes the gap so the overlay lands exactly on the frozen frame.
        if (doSuspend)
        {
            var (showBefore, offMs) = Gameplay.GameplaySettings.ResolvePauseScreenFreezeTiming(Safe(() => _emu?.Id), Safe(() => _game?.Id));
            if (showBefore) { ShowScreen(); if (offMs > 0) { try { Thread.Sleep(offMs); } catch { } } Freeze(); }
            else            { Freeze();     if (offMs > 0) { try { Thread.Sleep(offMs); } catch { } } ShowScreen(); }
        }
        else ShowScreen();
    }

    private static void ResumeLocked(bool runResumeScript = true, bool refocus = true)
    {
        _paused = false;
        // 1. UNFREEZE FIRST, while the pause overlay still covers the screen, and give the game a beat to
        //    actually start running again (re-acquire exclusive fullscreen / render a live frame) BEFORE we
        //    reveal it. Revealing a still-frozen game flashes a dead frame, and refocusing one that hasn't
        //    resumed yet can bounce it. Only then do the rest (close the overlay, resume script, refocus).
        ResumeTargets();
        try { System.Threading.Thread.Sleep(100); } catch { }
        // 2. Reveal — close the overlay now that the game is live behind it.
        UiThread.Invoke(() => { try { _screen?.Close(); } catch { } });
        if (runResumeScript)
        {
            var p = RunScriptPassThrough(ScriptStr("ResumeAutoHotkeyScript"));
            if (p != null) { try { p.WaitForExit(1500); } catch { } }
        }
        // Unmute AFTER the resume script — the whole transition stays silent, the game
        // comes back audible only once it is actually running again. Only if WE muted.
        UnmuteIfWeMuted();
        // 3. Refocus only if the game didn't already regain the foreground (FocusEmulator checks).
        if (refocus) FocusEmulator();
        Console.WriteLine("[pause] resumed");
    }

    // "Mute Audio During Transitions" bookkeeping: true only when the PAUSE muted the
    // process (an app the user muted himself stays muted through pause/resume).
    private static bool _mutedByUs;

    private static void UnmuteIfWeMuted()
    {
        if (!_mutedByUs) return;
        _mutedByUs = false;
        try { if (_proc is { HasExited: false }) AppAudio.SetMute(_proc.Id, false); } catch { }
    }

    // ── Screen actions ──────────────────────────────────────────────────

    private static void OnScreenAction(PauseAction a)
    {
        lock (_lock)
        {
            if (_proc == null) return;   // _emu null is fine (store / direct-exe / DOSBox game)
            switch (a)
            {
                case PauseAction.Resume:
                    ResumeLocked();
                    break;

                case PauseAction.ViewManual:
                    // The game STAYS paused (LB behaviour) — just open the manual in
                    // the default viewer; the overlay yields TopMost while unfocused
                    // (see the screen's Deactivate handler) so the viewer is readable.
                    try
                    {
                        var man = LaunchedGame.Current?.ManualPath;
                        if (!string.IsNullOrEmpty(man) && System.IO.File.Exists(man))
                            Process.Start(new ProcessStartInfo(man) { UseShellExecute = true });
                    }
                    catch (Exception ex) { Console.WriteLine("[pause] manual open failed: " + ex.Message); }
                    break;

                case PauseAction.SaveState:   RunActionScript("SaveStateAutoHotkeyScript"); break;
                case PauseAction.LoadState:   RunActionScript("LoadStateAutoHotkeyScript"); break;
                case PauseAction.Reset:       RunActionScript("ResetAutoHotkeyScript"); break;
                case PauseAction.SwapDiscs:   RunActionScript("SwapDiscsAutoHotkeyScript"); break;

                case PauseAction.ExitGame:
                    ExitGameLocked();
                    break;
            }
        }
    }

    /// <summary>LB behaviour for pause-menu actions: close + resume + focus the
    /// emulator, then fire the one-off script (no auto re-pause).</summary>
    private static void RunActionScript(string field)
    {
        var script = ScriptStr(field);
        ResumeLocked(runResumeScript: false);
        try { Thread.Sleep(200); } catch { }   // let the emulator window settle into the foreground
        RunScriptPassThrough(script);
    }

    /// <summary>The AHK script for a pause action, resolved PER-SCRIPT: when the game's pause override
    /// is active, a NON-BLANK game script replaces the emulator's default; a BLANK one inherits the
    /// emulator's. A lone comment line (";…") is non-blank ⇒ it replaces AND, being a no-op to
    /// <see cref="AhkScript.IsScriptEmpty"/>, disables the default entirely. Off / no override ⇒ the
    /// emulator's field. Covers the full set INCLUDING ExitAutoHotkeyScript (a per-game exit script wins over
    /// the emulator's; used by ExitGameLocked to decide graceful-close vs force-kill).</summary>
    private static string ScriptStr(string field)
    {
        var snap = LaunchedGame.Current;
        if (snap is { PauseOverride: true } && snap.PauseScripts.TryGetValue(field, out var s) && !string.IsNullOrWhiteSpace(s))
            return s;
        return FieldStr(_emu!, field);
    }

    // LB's default exit-game behaviour when the emulator has no custom exit
    // script: send Escape to the (focused) emulator — most emulators (RetroArch,
    // …) quit on it; the kill-tree below stays as the fallback. RetroArch's
    // pushed comment spells this out: "no custom exit script is necessary …
    // since it already uses the Escape key by default".
    private const string DefaultExitScript = "Send {Escape}";

    private static void ExitGameLocked()
    {
        Console.WriteLine("[pause] exit game requested");
        ResumeLocked(runResumeScript: false, refocus: true);

        var exitScript = ScriptStr("ExitAutoHotkeyScript");          // per-game override wins, else the emulator's
        bool hasCustomExit = !AhkScript.IsScriptEmpty(exitScript);   // a user-authored exit script (game or emulator)
        if (!hasCustomExit) exitScript = DefaultExitScript;
        bool graceful = false;
        try { Thread.Sleep(200); } catch { }   // let the emulator settle into the foreground
        var ahk = AhkScript.RunOneOff(exitScript, _lbRoot);

        // LiteBox "exit screen early": cover the display with the end screen X ms AFTER the exit
        // script runs, instead of waiting for the process to fully exit (which can leave a flash of
        // the emulator closing / desktop). -1 = off (default). Per-emulator. Runs off-thread so the
        // exit sequence below (WaitForExit / kill) is unaffected; ShowEndBlocking reuses this cover.
        string? emuId = null; try { emuId = _emu?.Id; } catch { }
        string? gid = null; try { gid = _game?.Id; } catch { }
        int eagerMs = Gameplay.GameplaySettings.ResolveExitScreenEagerMs(emuId, gid);
        if (eagerMs >= 0)
        {
            var snap = LaunchedGame.Current;
            new Thread(() =>
            {
                try { ahk?.WaitForExit(4000); } catch { }   // "after the exit AHK code executes"
                try { if (eagerMs > 0) Thread.Sleep(eagerMs); } catch { }
                try { Gameplay.GameScreens.ShowEndEager(snap); } catch { }
            }) { IsBackground = true, Name = "litebox-exitcover" }.Start();
        }

        if (hasCustomExit)
        {
            // The emulator has a real, user-authored exit script: it OWNS closing the game (it may prompt to
            // save, animate, or simply take longer than any timeout we'd pick). Respect it — NEVER force-kill.
            // The process exits on its own terms and HostLaunch's WaitForExit then runs the normal cleanup.
            Console.WriteLine("[pause] custom exit script ran — not force-killing; leaving the game to close itself");
        }
        else
        {
            // Default path (Send {Escape}, or a non-emulator game with no script): give it a moment, then
            // force-kill the whole tree if it's still up — nothing is responsible for a graceful close.
            try { graceful = _proc!.WaitForExit(5000); } catch { }
            if (graceful) Console.WriteLine("[pause] emulator closed by the default exit key");
            else
            {
                try { if (_proc is { HasExited: false }) { _proc.Kill(entireProcessTree: true); Console.WriteLine("[pause] emulator killed"); } }
                catch (Exception ex) { Console.WriteLine("[pause] kill failed: " + ex.Message); }
            }
        }
        // HostLaunch's WaitForExit returns → its finally runs Disarm + cleanup.
    }

    // ── Freeze target (which process the pause suspends) ─────────────────

    /// <summary>The PID to freeze, resolved game → emulator → global. "smartcapture" (default) targets the
    /// process that OWNS the SmartCapture-detected game window (its real render process — right for store
    /// games and launcher→game handoffs), falling back to the launched process when nothing was detected.
    /// "process" always uses the launched process (the old behaviour).</summary>
    private static int PauseTargetPid()
    {
        string target = Data.LiteBoxOption.ResolveString("PauseTarget", Safe(() => _emu?.Id),
            Gameplay.GameplaySettings.PauseTargetGlobal(), Safe(() => _game?.Id));
        if (!string.Equals(target, "process", StringComparison.OrdinalIgnoreCase))
        {
            var hwnd = Gameplay.SmartCapture.DetectedGameWindow;
            if (hwnd != IntPtr.Zero) { GetWindowThreadProcessId(hwnd, out uint wpid); if (wpid != 0) return (int)wpid; }
        }
        try { return _proc?.Id ?? 0; } catch { return 0; }
    }

    private static bool PauseFreezeTree()
        => Data.LiteBoxOption.ResolveBool("PauseFreezeTree", Safe(() => _emu?.Id),
            Gameplay.GameplaySettings.PauseFreezeTreeGlobal(), Safe(() => _game?.Id));

    /// <summary>Every descendant PID of <paramref name="root"/> (Toolhelp parent-PID walk).</summary>
    private static System.Collections.Generic.List<int> DescendantPids(int root)
    {
        var res = new System.Collections.Generic.List<int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return res;
        try
        {
            var byParent = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
            var pe = new PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe))
                do { int pid = (int)pe.th32ProcessID, ppid = (int)pe.th32ParentProcessID; if (!byParent.TryGetValue(ppid, out var l)) byParent[ppid] = l = new(); l.Add(pid); }
                while (Process32Next(snap, ref pe));
            var q = new System.Collections.Generic.Queue<int>(); q.Enqueue(root);
            var seen = new System.Collections.Generic.HashSet<int> { root };
            while (q.Count > 0) { var p = q.Dequeue(); if (byParent.TryGetValue(p, out var kids)) foreach (var k in kids) if (seen.Add(k)) { res.Add(k); q.Enqueue(k); } }
        }
        catch { }
        finally { CloseHandle(snap); }
        return res;
    }

    /// <summary>Freeze the resolved target (its whole tree if the option is on). Records the exact PID set so
    /// resume undoes precisely what it suspended.</summary>
    private static void SuspendTargets()
    {
        _suspendedPids.Clear();
        int root = PauseTargetPid();
        if (root <= 0) return;
        var pids = new System.Collections.Generic.List<int> { root };
        if (PauseFreezeTree()) pids.AddRange(DescendantPids(root));
        foreach (var pid in pids)
        {
            try { var pr = Process.GetProcessById(pid); if (!pr.HasExited) { NtSuspendProcess(pr.Handle); _suspendedPids.Add(pid); } }
            catch (Exception ex) { Console.WriteLine($"[pause] suspend pid={pid} failed: " + ex.Message); }
        }
        _suspended = _suspendedPids.Count > 0;
        if (_suspended) Console.WriteLine($"[pause] frozen pid(s) [{string.Join(",", _suspendedPids)}] (target={root})");
    }

    private static void ResumeTargets()
    {
        foreach (var pid in _suspendedPids)
        { try { var pr = Process.GetProcessById(pid); if (!pr.HasExited) NtResumeProcess(pr.Handle); } catch { } }
        _suspendedPids.Clear();
        _suspended = false;
    }

    /// <summary>The game window handle, or Zero. Mirrors the freeze-target resolution: prefer the
    /// SmartCapture-detected window (what we actually covered/froze and that minimised itself) unless the
    /// target is pinned to "process". Works on a suspended process too — the window still exists, only its
    /// threads are frozen.</summary>
    private static IntPtr EmulatorWindow()
    {
        // The launched process's CURRENT main window is the safe default — fresh and correct for the common
        // case (emulator / single-process game, where _proc IS the game). The SmartCapture-detected window is
        // only preferred when it belongs to a DIFFERENT process (store / launcher hand-off, where _proc isn't
        // the game): using a possibly-stale detected handle for a same-process game would send
        // SetForegroundWindow to the wrong window and make a "minimise on focus loss" game minimise itself.
        IntPtr procWin = IntPtr.Zero;
        try { if (_proc is { HasExited: false }) { _proc.Refresh(); procWin = _proc.MainWindowHandle; } } catch { }
        try
        {
            string target = Data.LiteBoxOption.ResolveString("PauseTarget", Safe(() => _emu?.Id),
                Gameplay.GameplaySettings.PauseTargetGlobal(), Safe(() => _game?.Id));
            if (!string.Equals(target, "process", StringComparison.OrdinalIgnoreCase))
            {
                var hwnd = Gameplay.SmartCapture.DetectedGameWindow;
                if (hwnd != IntPtr.Zero && IsWindow(hwnd))
                {
                    GetWindowThreadProcessId(hwnd, out uint wpid);
                    if (wpid != 0 && (int)wpid != (_proc?.Id ?? -1)) return hwnd;   // different process → the detected window is the game
                }
            }
        }
        catch { }
        return procWin;
    }

    private static void FocusEmulator()
    {
        try
        {
            var h = EmulatorWindow();
            if (h == IntPtr.Zero || !IsWindow(h)) return;
            // Only act if the game window does NOT already have the foreground (checked AFTER the resume
            // settle delay). When closing the overlay already handed focus back to the game we must leave it
            // ALONE — a redundant SetForegroundWindow can bounce a "minimise on focus loss" fullscreen game
            // and produces the "resumes only every other time" flip-flop. We step in only when something else
            // grabbed the foreground (LiteBox's own window revealed behind the overlay, or the game minimised
            // itself): restore it if minimised, then bring it forward.
            IntPtr fg = GetForegroundWindow();
            if (fg == h) { Console.WriteLine("[pause] refocus: game already foreground — leaving it"); return; }
            GetWindowThreadProcessId(h, out uint gamePid);
            if (fg != IntPtr.Zero && gamePid != 0)
            {
                GetWindowThreadProcessId(fg, out uint fgPid);
                if (fgPid == gamePid) { Console.WriteLine("[pause] refocus: game process already foreground — leaving it"); return; }
            }
            bool ic = IsIconic(h);
            if (ic) ShowWindow(h, SW_RESTORE);
            bool ok = SetForegroundWindow(h);
            Console.WriteLine($"[pause] refocus: forced game window 0x{h.ToInt64():X} (wasIconic={ic}) SetForeground={ok}");
        }
        catch { }
    }

    // ── Emulator fields beyond the SDK surface (ILiteBoxFields dict) ────

    private static string? FieldStr(IEmulator? emu, string xmlName)
    {
        if (emu == null) return null;   // non-emulator game (store / direct-exe / DOSBox): no emulator scripts
        try
        {
            // SDK-surfaced scripts first (works for any IEmulator implementation).
            switch (xmlName)
            {
                case "PauseAutoHotkeyScript": return emu.PauseAutoHotkeyScript;
                case "ResumeAutoHotkeyScript": return emu.ResumeAutoHotkeyScript;
                case "SaveStateAutoHotkeyScript": return emu.SaveStateAutoHotkeyScript;
                case "LoadStateAutoHotkeyScript": return emu.LoadStateAutoHotkeyScript;
                case "ResetAutoHotkeyScript": return emu.ResetAutoHotkeyScript;
                case "SwapDiscsAutoHotkeyScript": return emu.SwapDiscsAutoHotkeyScript;
                case "ExitAutoHotkeyScript": return emu.ExitAutoHotkeyScript;
            }
        }
        catch { }
        try { return (emu as ILiteBoxFields)?.GetField(xmlName); } catch { return null; }
    }

    private static bool FieldBool(IEmulator? emu, string xmlName, bool def)
    {
        if (emu == null) return def;    // non-emulator game: fall back to the caller's default
        try
        {
            var v = (emu as ILiteBoxFields)?.GetField(xmlName);
            if (string.IsNullOrEmpty(v)) return def;
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return def; }
    }

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }

    // ── Hotkey parsing ("Pause", "F12", "Ctrl+Shift+P", …) ──────────────

    private static (uint mod, uint vk, string label) ParseHotkey(string s)
    {
        uint mod = 0; Keys key = Keys.Pause;
        var parts = (s ?? "Pause").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mod |= MOD_CONTROL; break;
                case "alt": mod |= MOD_ALT; break;
                case "shift": mod |= MOD_SHIFT; break;
                case "win": mod |= MOD_WIN; break;
                default:
                    if (Enum.TryParse<Keys>(part, true, out var k)) key = k;
                    else Console.WriteLine($"[pause] unknown key \"{part}\" in PauseHotkey — using Pause");
                    break;
            }
        }
        string label = (mod & MOD_CONTROL) != 0 ? "Ctrl+" : "";
        label += (mod & MOD_ALT) != 0 ? "Alt+" : "";
        label += (mod & MOD_SHIFT) != 0 ? "Shift+" : "";
        label += (mod & MOD_WIN) != 0 ? "Win+" : "";
        return (mod, (uint)key, label + key);
    }

    // ── Message-only window receiving WM_HOTKEY (lives on the UI thread) ─

    private sealed class HotkeyWindow : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action _cb;
        public HotkeyWindow(Action cb)
        {
            _cb = cb;
            CreateHandle(new CreateParams());   // message-only-ish hidden window
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
            {
                try { System.Threading.Tasks.Task.Run(_cb); } catch { }
                return;
            }
            base.WndProc(ref m);
        }
    }
}
