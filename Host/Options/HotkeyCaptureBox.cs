// A read-only textbox that CAPTURES a hotkey instead of accepting typed text.
// Click it (or tab into it) → "Press a key…"; the next non-modifier key press is
// recorded as a "Ctrl+Shift+F12"-style combo (the format PauseManager/ScreenCapture
// parse). Esc CLEARS the binding (sets it to none/disabled); leaving the field
// (clicking elsewhere) aborts and reverts to the previously committed value.
// Modifier-only presses keep waiting.
//
// Capture uses the shared low-level keyboard hook (KeyCaptureHook) while the field is capturing, so ANY
// key — including reserved ones like F12 / the Win key / F10 — can be bound; ProcessCmdKey alone never
// sees those. The hook is removed the instant a key is committed or the field loses focus; ProcessCmdKey
// stays as the fallback for the rare case the hook fails to install.

#nullable enable

using System;

namespace LbApiHost.Host.Options;

internal sealed class HotkeyCaptureBox : TextBox
{
    private string _value;
    private bool _capturing;
    private readonly KeyCaptureHook _hook;

    public HotkeyCaptureBox(string? initial)
    {
        _value = (initial ?? "").Trim();
        ReadOnly = true;
        Cursor = Cursors.Hand;
        ShortcutsEnabled = false;   // no paste/undo while capturing
        _hook = new KeyCaptureHook(keyData => Commit(keyData & Keys.KeyCode, keyData & Keys.Modifiers));
        Text = Display(_value);
    }

    /// <summary>The committed combo string (e.g. "Ctrl+F12"), or "" when unset.</summary>
    public string HotkeyValue => _value;

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); BeginCapture(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!_capturing) BeginCapture(); }
    protected override void OnLostFocus(EventArgs e) { EndCapture(); base.OnLostFocus(e); }
    protected override void OnHandleDestroyed(EventArgs e) { _hook.Stop(); base.OnHandleDestroyed(e); }

    private void BeginCapture()
    {
        if (_capturing) return;
        _capturing = true;
        Text = "Press a key…  (Esc to clear)";
        try { SelectionLength = 0; } catch { }
        _hook.Start();
    }

    private void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        _hook.Stop();
        Text = Display(_value);
    }

    /// <summary>Commit a captured virtual-key (with the modifiers held). Runs on the UI thread (both the
    /// hook callback and ProcessCmdKey are dispatched there).</summary>
    private void Commit(Keys key, Keys mods)
    {
        if (!_capturing) return;
        if (key == Keys.Escape)   // clear the binding (none / disabled)
        {
            _value = "";
            EndCapture();
            return;
        }

        // Ignore modifier-only presses — keep waiting for the real key.
        if (key is Keys.None or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
                 or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
                 or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin)
            return;

        _value = Build(mods, key);
        EndCapture();
    }

    // ProcessCmdKey is the FALLBACK path (used only when the hook failed to install): it catches ordinary
    // keys but not the reserved ones. When the hook is active it swallows keys first, so this won't fire.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturing || _hook.Active) return base.ProcessCmdKey(ref msg, keyData);
        Commit(keyData & Keys.KeyCode, keyData & Keys.Modifiers);
        return true;   // swallow the press while capturing
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
