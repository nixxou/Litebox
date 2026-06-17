// A read-only textbox that CAPTURES a hotkey instead of accepting typed text.
// Click it (or tab into it) → "Press a key…"; the next non-modifier key press is
// recorded as a "Ctrl+Shift+F12"-style combo (the format PauseManager/ScreenCapture
// parse). Esc CLEARS the binding (sets it to none/disabled); leaving the field
// (clicking elsewhere) aborts and reverts to the previously committed value.
// Modifier-only presses keep waiting.

#nullable enable

namespace LbApiHost.Host.Options;

internal sealed class HotkeyCaptureBox : TextBox
{
    private string _value;
    private bool _capturing;

    public HotkeyCaptureBox(string? initial)
    {
        _value = (initial ?? "").Trim();
        ReadOnly = true;
        Cursor = Cursors.Hand;
        ShortcutsEnabled = false;   // no paste/undo while capturing
        Text = Display(_value);
    }

    /// <summary>The committed combo string (e.g. "Ctrl+F12"), or "" when unset.</summary>
    public string HotkeyValue => _value;

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); BeginCapture(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!_capturing) BeginCapture(); }
    protected override void OnLostFocus(EventArgs e) { EndCapture(); base.OnLostFocus(e); }

    private void BeginCapture()
    {
        if (_capturing) return;
        _capturing = true;
        Text = "Press a key…  (Esc to clear)";
        try { SelectionLength = 0; } catch { }
    }

    private void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        Text = Display(_value);
    }

    // ProcessCmdKey sees every key-down before normal processing — the right hook to
    // grab Esc/arrows/Enter and any combo while in capture mode.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);

        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Escape)   // clear the binding (none / disabled)
        {
            _value = "";
            _capturing = false;
            Text = Display(_value);
            return true;
        }

        // Ignore modifier-only presses — keep waiting for the real key.
        if (key is Keys.None or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
                 or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
                 or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin)
            return true;

        _value = Build(keyData & Keys.Modifiers, key);
        _capturing = false;
        Text = Display(_value);
        return true;
    }

    private static string Build(Keys mods, Keys key)
    {
        var sb = new System.Text.StringBuilder();
        if ((mods & Keys.Control) != 0) sb.Append("Ctrl+");
        if ((mods & Keys.Alt) != 0) sb.Append("Alt+");
        if ((mods & Keys.Shift) != 0) sb.Append("Shift+");
        sb.Append(key.ToString());
        return sb.ToString();
    }

    private static string Display(string v) => string.IsNullOrEmpty(v) ? "(none — click to set)" : v;
}
