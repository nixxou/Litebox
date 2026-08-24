// ChangeRomPath — relocate roms whose stored path went stale (a moved drive, a network mirror).
// The original's algorithm, kept whole: every argument containing the SOUGHT path is split into
// before + after (the remainder past it); HIGH-priority candidates are tried first and win even
// when the original still exists (a faster local mirror); if none hits and the ORIGINAL is missing,
// LOW-priority candidates are the fallback. Each candidate is tried with the full remainder, then
// with the bare filename (the rom moved without its subfolder). Existence checks ping UNC servers
// first, as the original did — a dead server answers in one ping instead of a filesystem timeout.
//
// The example channel is the real treatment: it only READS the disk, and showing the actual
// resolution is precisely what the preview is for. Candidates are stored "|||"-separated, the
// original's own format.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class ChangeRomPathAction : IRuleAction
{
    public const string Separator = "|||";

    public string Type => LaunchRule.TypeChangeRomPath;
    public string AddLabel => "Add: Change rom path…";
    public string DialogTitle => "Change rom path rule";

    public bool IsConfigured(LaunchRule r) => r.RomPathFind.Length > 0;

    public string Describe(LaunchRule r) => $"Will replace {r.RomPathFind} with another path if needed";

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd)
    {
        var args = RuleArgs.Split(cmd.Args);
        var high = SplitPaths(r.RomPathHigh);
        var low = SplitPaths(r.RomPathLow);

        for (int i = 0; i < args.Length; i++)
        {
            string elem = args[i];
            int at = elem.IndexOf(r.RomPathFind, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            string before = elem.Substring(0, at);
            string after = elem.Substring(at + r.RomPathFind.Length);
            if (after == "\\") after = "";
            after = after.TrimStart('\\').TrimEnd('"');

            string? hit = TryCandidates(high, after, elem);
            if (hit == null && !PathExists(Path.Combine(r.RomPathFind, after)))
                hit = TryCandidates(low, after, elem);
            if (hit != null) args[i] = before + hit;
        }
        return cmd with { Args = RuleArgs.Join(args) };
    }

    /// <summary>Each candidate with the remainder, then with the bare filename. First hit wins.</summary>
    private static string? TryCandidates(string[] candidates, string after, string elem)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                string full = after.Length > 0 ? Path.Combine(candidate, after) : candidate;
                if (PathExists(full)) return full;
                string flat = Path.Combine(candidate, Path.GetFileName(elem.Trim()));
                if (PathExists(flat)) return flat;
            }
            catch { /* an invalid candidate is a miss, not a crash */ }
        }
        return null;
    }

    private static string[] SplitPaths(string joined)
        => joined.Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

    /// <summary>BigBoxUtils.NetworkPathExists: UNC paths ping their server before the filesystem is
    /// asked — a dead server fails in one ping instead of a long share timeout.</summary>
    private static bool PathExists(string path)
    {
        try
        {
            if (path.StartsWith(@"\\"))
            {
                string server = path.Split('\\').ElementAtOrDefault(2) ?? "";
                if (server.Length == 0) return false;
                using var ping = new Ping();
                if (ping.Send(server, 1000)?.Status != IPStatus.Success) return false;
            }
            return File.Exists(path) || Directory.Exists(path);
        }
        catch { return false; }
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { Size = new Size(S(576), S(196)), BackColor = LiteBoxTheme.Bg };
        int y = 0;

        (TextBox box, Button? manage) Field(string caption, string value, bool withManage)
        {
            var cap = new Label
            {
                Text = caption, AutoSize = true, Location = new Point(0, y + S(2)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            var text = new TextBox
            {
                Text = value, Location = new Point(0, y + S(22)), Width = withManage ? S(486) : S(574),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            Button? manage = null;
            if (withManage)
            {
                manage = new Button
                {
                    Text = "Manage…", Location = new Point(S(492), y + S(20)), Size = new Size(S(82), S(25)),
                    BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
                };
                manage.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
                manage.Click += (_, _) =>
                {
                    using var dlg = new ManageItemsDialog(text.Text, dpiS, Separator);
                    if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) text.Text = dlg.Value;
                };
                body.Controls.Add(manage);
            }
            body.Controls.Add(cap);
            body.Controls.Add(text);
            y += S(54);
            return (text, manage);
        }

        var (find, _) = Field("Path to relocate (matched inside every argument):", r.RomPathFind, withManage: false);
        var (high, _) = Field("High-priority replacements — tried even when the original exists (\"|||\"-separated):", r.RomPathHigh, withManage: true);
        var (low, _) = Field("Low-priority replacements — only when the original is MISSING (\"|||\"-separated):", r.RomPathLow, withManage: true);
        var hint = new Label
        {
            Text = "Each candidate is tried with the remainder path, then with the bare file name.",
            AutoSize = true, Location = new Point(0, y + S(2)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(hint);

        return (body, S(196), () =>
        {
            r.RomPathFind = find.Text.Trim();
            r.RomPathHigh = high.Text.Trim();
            r.RomPathLow = low.Text.Trim();
        });
    }
}
