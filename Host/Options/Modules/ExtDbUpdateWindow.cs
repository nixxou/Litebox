// The Extended-database update window — the ExtendDB-style progress dialog (scrolling log + Cancel/Close),
// shown at BOOT when a download/adoption actually starts and by the Base panel's "Update from GitHub" button.
//
// It is a VIEWER over ExtDbDownloader's single-flight shared operation: opening it starts the operation (or
// joins the one already running — e.g. the boot auto-update) and streams its progress lines; Cancel cancels
// the shared operation for every observer; Close is enabled once the operation ends. Singleton — a second
// ShowOrFocus focuses the existing window instead of stacking dialogs.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Options;

internal sealed class ExtDbUpdateWindow : Form
{
    private static ExtDbUpdateWindow? _open;

    private readonly TextBox _log;
    private readonly Button _cancel;
    private readonly Button _close;
    private readonly CheckBox _autoClose;
    private System.Windows.Forms.Timer? _autoTimer;
    private bool _done;

    /// <summary>Opens (or focuses) the update window and starts/joins the shared update operation.
    /// Returns the shared task so a caller can refresh its status when the operation ends.</summary>
    public static System.Threading.Tasks.Task<bool> ShowOrFocus(IWin32Window? owner = null)
    {
        if (_open is { IsDisposed: false })
        {
            try { _open.Activate(); } catch { }
            return ExtDbDownloader.RunSharedAsync();
        }
        var w = new ExtDbUpdateWindow();
        _open = w;
        try { if (owner is Form f && !f.IsDisposed) w.Show(f); else w.Show(); } catch { w.Show(); }
        return w.Run();
    }

    private ExtDbUpdateWindow()
    {
        float dpiS = 1f; try { dpiS = DeviceDpi / 96f; } catch { }
        int S(int px) => (int)Math.Round(px * dpiS);

        Text = "Extended database — update";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = true; ShowIcon = false; ShowInTaskbar = true;
        ClientSize = new Size(S(560), S(320));
        BackColor = ModulePanelKit.Bg; ForeColor = ModulePanelKit.Fg;

        _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Dock = DockStyle.Fill, BackColor = ModulePanelKit.Panel, ForeColor = ModulePanelKit.Fg,
            BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f), TabStop = false,
        };
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(12)), BackColor = ModulePanelKit.Bg };
        pad.Controls.Add(_log);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = ModulePanelKit.Bg };
        _autoClose = new CheckBox
        {
            Text = "Auto-close on completion", Checked = true, AutoSize = true,
            Location = new Point(S(14), S(14)), ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f),
        };
        _cancel = MakeButton("Cancel", S);
        _cancel.Location = new Point(S(332), S(8));
        _cancel.Click += (_, _) => { _cancel.Enabled = false; CancelAutoClose(); ExtDbDownloader.CancelShared(); Append("Cancelling…"); };
        _close = MakeButton("Close", S);
        _close.Location = new Point(S(444), S(8));
        _close.Enabled = false;
        _close.Click += (_, _) => Close();
        bar.Controls.Add(_autoClose); bar.Controls.Add(_cancel); bar.Controls.Add(_close);

        Controls.Add(pad); Controls.Add(bar);
        FormClosed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };
        FormClosing += (_, e) =>
        {
            // While running, closing just hides the window — the shared operation keeps going (boot parity:
            // the download must survive the viewer). Cancel is the explicit stop.
            if (!_done) { e.Cancel = true; Hide(); }
        };
    }

    private static Button MakeButton(string text, Func<int, int> S)
    {
        var b = new Button
        {
            Text = text, Size = new Size(S(104), S(30)), FlatStyle = FlatStyle.Flat,
            BackColor = ModulePanelKit.Panel, ForeColor = ModulePanelKit.Fg,
        };
        b.FlatAppearance.BorderColor = ModulePanelKit.Panel;
        return b;
    }

    private System.Threading.Tasks.Task<bool> Run()
    {
        var last = ExtDbDownloader.LastProgress;
        if (!string.IsNullOrEmpty(last)) Append(last);   // joining mid-operation → show where it stands

        var task = ExtDbDownloader.RunSharedAsync(m => Ui(() => Append(m)));
        _ = task.ContinueWith(t => Ui(() =>
        {
            _done = true;
            bool ok = !t.IsFaulted && t.Result;
            Append(t.IsFaulted ? "Failed: " + (t.Exception?.GetBaseException().Message ?? "error")
                 : t.Result ? "Done." : "Not completed.");
            _cancel.Enabled = false;
            _close.Enabled = true;
            // ExtendDB parity: success + checkbox → auto-dismiss after a short beat so the user sees the log.
            if (ok && _autoClose.Checked) ArmAutoClose();
        }));
        return task;
    }

    /// <summary>Arms the 2.5 s auto-close (only after a successful completion). A single-shot WinForms timer so
    /// it ticks on the UI thread; disarmed if the user unchecks the box, cancels, or the window is gone.</summary>
    private void ArmAutoClose()
    {
        CancelAutoClose();
        _autoTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _autoTimer.Tick += (_, _) =>
        {
            CancelAutoClose();
            if (!IsDisposed && _autoClose.Checked) { try { Close(); } catch { } }
        };
        _autoTimer.Start();
    }

    private void CancelAutoClose()
    {
        try { _autoTimer?.Stop(); _autoTimer?.Dispose(); } catch { }
        _autoTimer = null;
    }

    private void Append(string line)
    {
        if (_log.IsDisposed || string.IsNullOrEmpty(line)) return;
        _log.AppendText(line + Environment.NewLine);
    }

    private void Ui(Action a)
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
        catch { }
    }
}
