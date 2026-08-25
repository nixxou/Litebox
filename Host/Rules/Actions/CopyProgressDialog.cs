// The CopyFile progress window — BigBoxProfile's CopyFile_Task, themed: filename, source and
// destination, a progress bar with percent, copied/total MB, MB/s and an ETA, refreshed while a
// background thread streams the copy; the window closes itself half a second after completion.
// TopMost, like the original — it must surface over the frontend and the NOW LOADING cover, that
// feedback is its whole reason to exist (a multi-GB iso off a NAS looks like a hang otherwise).
//
// Shown by CopyOne only for files ≥ 32 MB: below that the copy is near-instant and the original's
// always-shown window was just a flash (it also kept the selftests' tiny files headless). ShowDialog
// runs on the LAUNCH thread and pumps its own messages there — exactly how the original ran it from
// ExecuteBefore. No cancel, as there: the launch is already committed.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class CopyProgressDialog : Form
{
    public Exception? Error { get; private set; }

    private readonly string _source, _target;
    private long _total, _done;
    private volatile bool _finished;

    public CopyProgressDialog(string source, string target)
    {
        _source = source;
        _target = target;

        Text = "Copying…";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = LiteBoxTheme.Bg;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(520, 150);

        var file = new Label
        {
            Text = Path.GetFileName(source), AutoSize = true, Location = new Point(14, 12),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        var from = new Label
        {
            Text = "Source : " + source, AutoSize = false, Size = new Size(492, 17),
            Location = new Point(14, 42), ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            AutoEllipsis = true,
        };
        var to = new Label
        {
            Text = "Destination : " + target, AutoSize = false, Size = new Size(492, 17),
            Location = new Point(14, 61), ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            AutoEllipsis = true,
        };
        var bar = new ProgressBar { Location = new Point(14, 86), Size = new Size(492, 18), Maximum = 100 };
        var progress = new Label
        {
            Text = "", AutoSize = true, Location = new Point(14, 112),
            ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        var eta = new Label
        {
            Text = "", AutoSize = true, Location = new Point(400, 112),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        Controls.AddRange(new Control[] { file, from, to, bar, progress, eta });

        var started = DateTime.Now;
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            if (_finished)
            {
                timer.Stop();
                bar.Value = Error == null ? 100 : bar.Value;
                var close = new System.Windows.Forms.Timer { Interval = 500 };
                close.Tick += (_, _) => { close.Stop(); Close(); };
                close.Start();
                return;
            }
            if (_total <= 0) return;
            long done = Interlocked.Read(ref _done);
            int pct = (int)(100 * done / _total);
            bar.Value = Math.Min(100, pct);
            double sec = (DateTime.Now - started).TotalSeconds;
            double speed = sec > 0 ? done / (1024.0 * 1024.0 * sec) : 0;
            progress.Text = $"{pct}%  ({done / 1048576.0:F1} MB / {_total / 1048576.0:F1} MB)  {speed:F1} MB/s";
            if (pct > 0)
            {
                var remain = TimeSpan.FromSeconds(sec / pct * (100 - pct));
                eta.Text = $"{remain.Minutes:D2}:{remain.Seconds:D2}";
            }
        };

        Shown += (_, _) =>
        {
            timer.Start();
            new Thread(CopyWorker) { IsBackground = true }.Start();
        };
    }

    private void CopyWorker()
    {
        try
        {
            var fi = new FileInfo(_source);
            _total = fi.Length;
            using (var inFile = new FileStream(_source, FileMode.Open, FileAccess.Read))
            using (var outFile = new FileStream(_target, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = inFile.Read(buffer, 0, buffer.Length)) > 0)
                {
                    outFile.Write(buffer, 0, read);
                    Interlocked.Add(ref _done, read);
                }
            }
            File.SetLastWriteTimeUtc(_target, fi.LastWriteTimeUtc);   // the reuse check keys on it
        }
        catch (Exception ex)
        {
            Error = ex;
            try { File.Delete(_target); } catch { }   // never leave a half-copy the reuse could trust
        }
        finally { _finished = true; }
    }
}
