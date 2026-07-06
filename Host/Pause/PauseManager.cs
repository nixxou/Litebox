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
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);

    private const int HotkeyId = 0xB0B;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private static readonly object _lock = new();
    private static LiteBoxConfig? _cfg;
    private static string _lbRoot = "";
    private static HotkeyWindow? _hkWin;
    private static IPauseScreen? _screen;

    // Armed launch state.
    private static Process? _proc;
    private static IEmulator? _emu;
    private static IGame? _game;
    private static bool _paused, _suspended;

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

    /// <summary>Registers the pause hotkey for this launch. No-op when pause is
    /// disabled (LiteBox.ini PauseEnabled=false) or the emulator opts out
    /// (UsePauseScreen=false).</summary>
    public static void Arm(Process proc, IEmulator emulator, IGame game)
    {
        if (proc == null || emulator == null) return;
        // Global master switch now lives in Settings.xml (LB · Gameplay → Use Game
        // Pause Screen); read fresh so an options change applies to the next launch.
        if (!Gameplay.GameplaySettings.PauseEnabledGlobal()) return;
        // Per-game pause override (LaunchedGame snapshot, captured pre-drop) wins
        // over the emulator's setting — exactly LB's Edit Game pause panel.
        var snap0 = LaunchedGame.Current;
        bool usePause = snap0 is { PauseOverride: true } ? snap0.PauseUse : FieldBool(emulator, "UsePauseScreen", true);
        if (!usePause)
        { Console.WriteLine("[pause] pause screen disabled (game/emulator setting) — off"); return; }

        lock (_lock)
        {
            DisarmLocked();
            _proc = proc; _emu = emulator; _game = game;
            _paused = _suspended = false;

            var (mod, vk, label) = ParseHotkey(Gameplay.GameplaySettings.PauseKey());
            UiThread.Invoke(() =>
            {
                _hkWin = new HotkeyWindow(OnHotkey);
                if (!RegisterHotKey(_hkWin.Handle, HotkeyId, mod | MOD_NOREPEAT, vk))
                { Console.WriteLine($"[pause] RegisterHotKey({label}) failed — pause hotkey unavailable"); _hkWin.DestroyHandle(); _hkWin = null; }
                else Console.WriteLine($"[pause] armed — hotkey {label}");
            });
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
        try
        {
            if (_suspended && _proc is { HasExited: false }) { NtResumeProcess(_proc.Handle); }
        }
        catch { }
        UnmuteIfWeMuted();   // a game killed while paused must not stay muted (session may outlive us)
        _suspended = false; _paused = false;
        var w = _hkWin; _hkWin = null;
        var s = _screen; _screen = null;
        UiThread.Invoke(() =>
        {
            try { s?.Close(); } catch { }
            try { if (w != null) { UnregisterHotKey(w.Handle, HotkeyId); w.DestroyHandle(); } } catch { }
        });
        _proc = null; _emu = null; _game = null;
    }

    // ── Hotkey → toggle ─────────────────────────────────────────────────

    private static void OnHotkey()
    {
        lock (_lock)
        {
            if (_proc == null || _emu == null) return;
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
        var p = AhkScript.RunOneOff(FieldStr(_emu!, "PauseAutoHotkeyScript"), _lbRoot);
        if (p != null) { try { p.WaitForExit(1500); } catch { } }

        // 2. Freeze (default ON, like LB; per-game override wins).
        var snapS = LaunchedGame.Current;
        bool doSuspend = snapS is { PauseOverride: true } ? snapS.PauseSuspend : FieldBool(_emu!, "SuspendProcessOnPause", true);
        if (doSuspend)
        {
            try { if (_proc is { HasExited: false }) { NtSuspendProcess(_proc.Handle); _suspended = true; } }
            catch (Exception ex) { Console.WriteLine("[pause] suspend failed: " + ex.Message); }
        }

        // 3. Screen. Cosmetics come from the LaunchedGame snapshot (captured before
        //    the launch memory drop) — never from the store / cache.
        var snap = LaunchedGame.Current;
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
            CanSaveState = !AhkScript.IsScriptEmpty(FieldStr(_emu!, "SaveStateAutoHotkeyScript")),
            CanLoadState = !AhkScript.IsScriptEmpty(FieldStr(_emu!, "LoadStateAutoHotkeyScript")),
            CanReset = !AhkScript.IsScriptEmpty(FieldStr(_emu!, "ResetAutoHotkeyScript")),
            CanSwapDiscs = !AhkScript.IsScriptEmpty(FieldStr(_emu!, "SwapDiscsAutoHotkeyScript")),
            ForcefulActivation = snap is { PauseOverride: true } ? snap.PauseForceful : FieldBool(_emu!, "ForcefulPauseScreenActivation", true),
            EmulatorMainWindow = EmulatorWindow(),
            OnAction = a => System.Threading.Tasks.Task.Run(() => OnScreenAction(a)),
        };
        UiThread.Invoke(() =>
        {
            _screen ??= PauseScreenFactory.Create(_cfg!);
            _screen.Show(ctx);
        });
    }

    private static void ResumeLocked(bool runResumeScript = true, bool refocus = true)
    {
        _paused = false;
        UiThread.Invoke(() => { try { _screen?.Close(); } catch { } });
        try { if (_suspended && _proc is { HasExited: false }) NtResumeProcess(_proc.Handle); }
        catch (Exception ex) { Console.WriteLine("[pause] resume failed: " + ex.Message); }
        _suspended = false;
        if (runResumeScript)
        {
            var p = AhkScript.RunOneOff(FieldStr(_emu!, "ResumeAutoHotkeyScript"), _lbRoot);
            if (p != null) { try { p.WaitForExit(1500); } catch { } }
        }
        // Unmute AFTER the resume script — the whole transition stays silent, the game
        // comes back audible only once it is actually running again. Only if WE muted.
        UnmuteIfWeMuted();
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
            if (_emu == null || _proc == null) return;
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
        var script = FieldStr(_emu!, field);
        ResumeLocked(runResumeScript: false);
        try { Thread.Sleep(200); } catch { }   // let the emulator window settle into the foreground
        AhkScript.RunOneOff(script, _lbRoot);
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

        var exitScript = FieldStr(_emu!, "ExitAutoHotkeyScript");
        if (AhkScript.IsScriptEmpty(exitScript)) exitScript = DefaultExitScript;
        bool graceful = false;
        try { Thread.Sleep(200); } catch { }   // let the emulator settle into the foreground
        AhkScript.RunOneOff(exitScript, _lbRoot);
        try { graceful = _proc!.WaitForExit(5000); } catch { }
        if (graceful) Console.WriteLine("[pause] emulator closed by the exit script");
        else
        {
            try { if (_proc is { HasExited: false }) { _proc.Kill(entireProcessTree: true); Console.WriteLine("[pause] emulator killed"); } }
            catch (Exception ex) { Console.WriteLine("[pause] kill failed: " + ex.Message); }
        }
        // HostLaunch's WaitForExit returns → its finally runs Disarm + cleanup.
    }

    /// <summary>The emulator's main window handle, or Zero. Works on a suspended
    /// process too — the window still exists, only its threads are frozen.</summary>
    private static IntPtr EmulatorWindow()
    {
        try
        {
            if (_proc is { HasExited: false }) { _proc.Refresh(); return _proc.MainWindowHandle; }
        }
        catch { }
        return IntPtr.Zero;
    }

    private static void FocusEmulator()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Refresh();
                var h = _proc.MainWindowHandle;
                if (h != IntPtr.Zero) SetForegroundWindow(h);
            }
        }
        catch { }
    }

    // ── Emulator fields beyond the SDK surface (ILiteBoxFields dict) ────

    private static string? FieldStr(IEmulator emu, string xmlName)
    {
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

    private static bool FieldBool(IEmulator emu, string xmlName, bool def)
    {
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
