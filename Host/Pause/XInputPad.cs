// Minimal XInput reader for the pause screen's controller navigation. Polls the
// first connected pad (slots 0-3) and exposes edge-detected directional /
// button events with hold auto-repeat for the directions (initial 350ms delay,
// then 150ms), so a held D-pad/stick scrolls the menu naturally.
//
// P/Invokes xinput1_4.dll (Windows 8+) with a 9.1.0 fallback. No dependency on
// any input framework — the pause overlay is the only consumer; LiteBox's main
// GUI stays mouse/keyboard.

#nullable enable

using System.Runtime.InteropServices;

namespace LbApiHost.Host.Pause;

internal sealed class XInputPad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState14(uint i, out XINPUT_STATE s);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState910(uint i, out XINPUT_STATE s);

    private static bool _use910;
    private static bool TryGetState(uint i, out XINPUT_STATE s)
    {
        try { if (!_use910) return GetState14(i, out s) == 0; }
        catch (DllNotFoundException) { _use910 = true; }
        try { return GetState910(i, out s) == 0; }
        catch { s = default; return false; }
    }

    // XInput button masks.
    public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    public const ushort Start = 0x0010, Back = 0x0020;
    public const ushort LBumper = 0x0100, RBumper = 0x0200;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    // ── Static button read + combo mapping (for the in-game pause-trigger watcher) ──────────
    // The pause watcher only needs controller 0's raw button mask; the pause-screen navigation
    // still uses the instance Poll above. Names mirror BigBoxProfile's XInput flag map.
    private static readonly (string name, ushort mask)[] _named =
    {
        ("A", A), ("B", B), ("X", X), ("Y", Y),
        ("Start", Start), ("Back", Back), ("LBumper", LBumper), ("RBumper", RBumper),
        ("Dpad_Up", DPadUp), ("Dpad_Down", DPadDown), ("Dpad_Left", DPadLeft), ("Dpad_Right", DPadRight),
    };

    /// <summary>Controller 0's current button mask, or 0 when it isn't connected. Cheap — a
    /// single XInputGetState; the watcher calls this at a gentle rate, not in a tight loop.</summary>
    public static ushort ReadButtons0()
        => TryGetState(0, out var s) ? s.Gamepad.wButtons : (ushort)0;

    /// <summary>"Back+Start"-style combo → OR of the button masks (0 if none parsed).</summary>
    public static ushort ComboToMask(string? combo)
    {
        ushort m = 0;
        foreach (var part in (combo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var (name, mask) in _named)
                if (string.Equals(name, part, StringComparison.OrdinalIgnoreCase)) { m |= mask; break; }
        return m;
    }

    /// <summary>The button-combo presets offered in the config UI.</summary>
    public static readonly string[] ComboPresets =
    {
        "Back+Start", "Back", "Start", "LBumper+RBumper", "A", "B", "X", "Y", "LBumper", "RBumper",
    };

    private const short StickThreshold = 16000;
    private const int RepeatDelayMs = 350, RepeatRateMs = 150;

    private ushort _prevButtons;
    private int _prevDirY;                 // -1 / 0 / +1 (stick + dpad merged)
    private DateTime _dirHeldSince, _lastRepeat;

    /// <summary>Polls the pad and reports: dirY (-1 up press/repeat, +1 down,
    /// 0 none this tick), plus newly-PRESSED buttons (edge). All zero when no
    /// pad is connected.</summary>
    public (int dirY, ushort pressed) Poll()
    {
        XINPUT_STATE st = default;
        bool ok = false;
        for (uint i = 0; i < 4 && !ok; i++) ok = TryGetState(i, out st);
        if (!ok) { _prevButtons = 0; _prevDirY = 0; return (0, 0); }

        var g = st.Gamepad;
        ushort pressed = (ushort)(g.wButtons & ~_prevButtons);
        _prevButtons = g.wButtons;

        // Merge dpad + left stick into one vertical direction.
        int dir = 0;
        if ((g.wButtons & DPadUp) != 0 || g.sThumbLY > StickThreshold) dir = -1;
        else if ((g.wButtons & DPadDown) != 0 || g.sThumbLY < -StickThreshold) dir = 1;

        int outDir = 0;
        var now = DateTime.UtcNow;
        if (dir != 0)
        {
            if (dir != _prevDirY) { outDir = dir; _dirHeldSince = now; _lastRepeat = now; }   // fresh press
            else if ((now - _dirHeldSince).TotalMilliseconds >= RepeatDelayMs
                  && (now - _lastRepeat).TotalMilliseconds >= RepeatRateMs) { outDir = dir; _lastRepeat = now; }
        }
        _prevDirY = dir;
        return (outDir, pressed);
    }
}
