// Low-level keyboard hook (WH_KEYBOARD_LL) shared by every hotkey-capture field.
//
// A plain KeyDown / ProcessCmdKey handler never sees the reserved keys: F12 is claimed by the debugger,
// and the Win key / F10 / Alt are eaten by the shell or the menu system before they reach a control — so
// those keys were impossible to bind. This hook sees (and swallows) each keystroke FIRST, so ANY key —
// including F12 — binds cleanly, and pressing it never triggers its side effect (debugger, Start menu…).
//
// It is active ONLY while a capture field is focused/capturing and removed the instant it isn't, so keys
// flow normally everywhere else. The hook callback is dispatched on the installing (UI) thread, so the
// onKey callback may touch controls directly.

#nullable enable

using System;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.Options;

internal sealed class KeyCaptureHook
{
    private readonly Action<Keys> _onKey;   // receives (vk | current-modifier flags) per keydown
    private IntPtr _hook = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;     // kept alive against GC while installed

    public KeyCaptureHook(Action<Keys> onKey) { _onKey = onKey; }

    /// <summary>True while the hook is installed (capture in progress).</summary>
    public bool Active => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        try
        {
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) _proc = null;   // failed → caller's KeyDown/ProcessCmdKey fallback stays
        }
        catch { _hook = IntPtr.Zero; _proc = null; }
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    /// <summary>Drive the hook off a control's focus lifetime — installed while focused, removed on blur or
    /// dispose. For fields that re-bind on every focused keypress (parental hotkey, kiosk keys).</summary>
    public static KeyCaptureHook OnFocus(Control box, Action<Keys> onKey)
    {
        var h = new KeyCaptureHook(onKey);
        box.GotFocus += (_, _) => h.Start();
        box.LostFocus += (_, _) => h.Stop();
        box.HandleDestroyed += (_, _) => h.Stop();
        if (box.IsHandleCreated && box.Focused) h.Start();
        return h;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hook != IntPtr.Zero)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                try
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    _onKey((Keys)data.vkCode | CurrentModifiers());
                }
                catch { }
            }
            return (IntPtr)1;   // swallow keydown AND keyup while capturing: no debugger/Start-menu side effects
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>The Ctrl/Alt/Shift currently held, as a WinForms modifier mask (Win is not part of the format).</summary>
    private static Keys CurrentModifiers()
    {
        Keys m = Keys.None;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) m |= Keys.Control;   // VK_CONTROL
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) m |= Keys.Alt;       // VK_MENU
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) m |= Keys.Shift;     // VK_SHIFT
        return m;
    }

    // ── Native ──────────────────────────────────────────────────────────────────────────────────────
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;   // F10 / Alt combos arrive as SYSKEYDOWN

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
