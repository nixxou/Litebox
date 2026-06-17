// Screen Capture: a global hotkey (LB · Gameplay → Screen Capture Key, stored in
// LiteBox.ini as a combo-capable string) armed only while a game runs. On press it
// grabs the monitor the game is on and saves a PNG under <LB>\Screenshots. Mirrors
// PauseManager's RegisterHotKey + UiThread message-pump approach (a message filter
// can't see keys while the emulator has focus).

#nullable enable

using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.Gameplay;

internal static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private const int HotkeyId = 0xB0C;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private static readonly object _lock = new();
    private static string _lbRoot = "";
    private static HotkeyWin? _win;

    public static void Configure(string lbRoot) { _lbRoot = lbRoot ?? ""; UiThread.Start(); }

    public static void Arm()
    {
        var key = GameplaySettings.ScreenCaptureKey();
        if (string.IsNullOrWhiteSpace(key) || key.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
        var (mod, vk, label) = ParseHotkey(key);
        if (vk == 0) return;
        lock (_lock)
        {
            DisarmLocked();
            UiThread.Invoke(() =>
            {
                _win = new HotkeyWin(OnHotkey);
                if (!RegisterHotKey(_win.Handle, HotkeyId, mod | MOD_NOREPEAT, vk))
                { Console.WriteLine($"[screenshot] RegisterHotKey({label}) failed"); _win.DestroyHandle(); _win = null; }
                else Console.WriteLine($"[screenshot] armed — hotkey {label}");
            });
        }
    }

    public static void Disarm()
    {
        lock (_lock) DisarmLocked();
    }

    private static void DisarmLocked()
    {
        var w = _win; _win = null;
        if (w != null) UiThread.Invoke(() => { try { UnregisterHotKey(w.Handle, HotkeyId); w.DestroyHandle(); } catch { } });
    }

    private static void OnHotkey() => System.Threading.Tasks.Task.Run(Capture);

    private static void Capture()
    {
        try
        {
            var fg = GetForegroundWindow();
            Screen scr;
            try { scr = fg != IntPtr.Zero ? Screen.FromHandle(fg) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]); }
            catch { scr = Screen.PrimaryScreen ?? Screen.AllScreens[0]; }
            var b = scr.Bounds;
            using var bmp = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);

            string title = Sanitize(LaunchedGame.Current?.Title ?? "Screenshot");
            string dir = Path.Combine(string.IsNullOrEmpty(_lbRoot) ? "." : _lbRoot, "Screenshots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{title}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            bmp.Save(file, ImageFormat.Png);
            Console.WriteLine("[screenshot] saved " + file);
        }
        catch (Exception ex) { Console.WriteLine("[screenshot] capture failed: " + ex.Message); }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Screenshot";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 0 ? s : "Screenshot";
    }

    // ── Hotkey parsing ("PrintScreen", "F10", "Ctrl+F12", …) ─────────────
    private static (uint mod, uint vk, string label) ParseHotkey(string s)
    {
        uint mod = 0; Keys key = Keys.None;
        foreach (var part in (s ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mod |= MOD_CONTROL; break;
                case "alt": mod |= MOD_ALT; break;
                case "shift": mod |= MOD_SHIFT; break;
                case "win": mod |= MOD_WIN; break;
                default: if (Enum.TryParse<Keys>(part, true, out var k)) key = k; break;
            }
        }
        string label = ((mod & MOD_CONTROL) != 0 ? "Ctrl+" : "") + ((mod & MOD_ALT) != 0 ? "Alt+" : "")
                     + ((mod & MOD_SHIFT) != 0 ? "Shift+" : "") + ((mod & MOD_WIN) != 0 ? "Win+" : "") + key;
        return (mod, (uint)key, label);
    }

    private sealed class HotkeyWin : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action _cb;
        public HotkeyWin(Action cb) { _cb = cb; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId) { try { System.Threading.Tasks.Task.Run(_cb); } catch { } return; }
            base.WndProc(ref m);
        }
    }
}
