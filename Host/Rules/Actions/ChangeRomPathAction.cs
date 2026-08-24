// ChangeRomPath — relocate roms whose stored path went stale (a moved drive, a network mirror).
// The original's algorithm, kept whole: every argument containing the SOUGHT path is split into
// before + after (the remainder past it); HIGH-priority candidates are tried first and win even
// when the original still exists (a faster local mirror); if none hits and the ORIGINAL is missing,
// LOW-priority candidates are the fallback. Each candidate is tried with the full remainder, then
// with the bare filename (the rom moved without its subfolder). Existence checks ping UNC servers
// first, as the original did — a dead server answers in one ping instead of a filesystem timeout.
//
// An m3u argument gets its CONTENT relocated too (the original's UseM3UContent contract) — read,
// per-entry relocation, and a TEMP COPY swapped into the line; the original file is never touched.
// The example channel relocates plain arguments (disk READS only) but skips the m3u rewrite: it
// writes nothing, ever. Candidates are stored "|||"-separated, the original's own format.

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

    /// <summary>Relocating must happen BEFORE extraction reads the archive — the whole point when
    /// the stale path is the archive's own (BigBoxProfile: place ChangeRomPath before RomExtractor).</summary>
    public bool AppliesToRomSource => true;

    public string Describe(LaunchRule r) => $"Will replace {r.RomPathFind} with another path if needed";

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => Apply(r, cmd, rewriteM3u: true);

    /// <summary>The example channel relocates plain arguments (disk READS only, the preview showing
    /// the actual resolution) but never touches an m3u: rewriting one means WRITING a temp copy, and
    /// the example channel writes nothing — BigBoxProfile's preview skipped its m3u machinery too
    /// (it lived in EmulatorLauncher.Exec, not in CalculateExemple).</summary>
    public RuleCmd ApplyExample(LaunchRule r, RuleCmd cmd) => Apply(r, cmd, rewriteM3u: false);

    /// <summary>Rom-source phase: PATH relocation only. An m3u under the sought path relocates as a
    /// file — the mirror's own m3u loads with its (typically relative) entries resolving beside it —
    /// and is never content-rewritten here. Caveat, documented in the dialog: a mirror m3u carrying
    /// ABSOLUTE entries to the dead original location will feed those to the per-entry pipeline
    /// before the line phase can relocate them.</summary>
    public RuleCmd ApplyRomSource(LaunchRule r, RuleCmd cmd) => Apply(r, cmd, rewriteM3u: false);

    private RuleCmd Apply(LaunchRule r, RuleCmd cmd, bool rewriteM3u)
    {
        var args = RuleArgs.Split(cmd.Args);
        var high = SplitPaths(r.RomPathHigh);
        var low = SplitPaths(r.RomPathLow);

        for (int i = 0; i < args.Length; i++)
        {
            // An m3u in the line: relocate its CONTENT (the original's UseM3UContent contract). By
            // the time rules run, the launch has already prepared everything — the m3u exists on
            // disk, absolute, in the command line — so this is a read, a per-entry relocation, and a
            // TEMP COPY swapped in; the original file is never modified.
            if (rewriteM3u && args[i].EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
            {
                string? rewritten = TryRewriteM3u(r, args[i], high, low);
                if (rewritten != null) { args[i] = rewritten; continue; }
            }
            string? relocated = RelocateElement(r, args[i], high, low);
            if (relocated != null) args[i] = relocated;
        }
        return cmd with { Args = RuleArgs.Join(args) };
    }

    /// <summary>One element through the original's algorithm. Null = untouched.</summary>
    private static string? RelocateElement(LaunchRule r, string elem, string[] high, string[] low)
    {
        int at = elem.IndexOf(r.RomPathFind, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        string before = elem.Substring(0, at);
        string after = elem.Substring(at + r.RomPathFind.Length);
        if (after == "\\") after = "";
        after = after.TrimStart('\\').TrimEnd('"');

        string? hit = TryCandidates(high, after, elem);
        if (hit == null && !PathExists(Path.Combine(r.RomPathFind, after)))
            hit = TryCandidates(low, after, elem);
        return hit != null ? before + hit : null;
    }

    /// <summary>Relocates the m3u's entries; when at least one moved, writes a temp copy (entries
    /// ABSOLUTIZED against the original's folder — relative ones would break from another directory)
    /// and returns its path. Null = nothing to relocate, keep the original argument.</summary>
    private static string? TryRewriteM3u(LaunchRule r, string m3uPath, string[] high, string[] low)
    {
        try
        {
            if (!File.Exists(m3uPath)) return null;
            string dir = Path.GetDirectoryName(m3uPath) ?? "";
            var lines = File.ReadAllLines(m3uPath);
            bool changed = false;

            // Pass 1: absolutize each entry against the original's folder, relocate it, remember
            // both. A rewrite happens only when at least one entry actually RELOCATED — but once it
            // does, every untouched entry is written absolutized too, since a relative entry would
            // break from the temp copy's directory.
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string abs;
                try { abs = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line)); }
                catch { continue; }
                string? hit = RelocateElement(r, abs, high, low);
                if (hit != null) changed = true;
                lines[i] = hit ?? abs;
            }
            if (!changed) return null;

            string outDir = Path.Combine(Path.GetTempPath(), "litebox-rules-m3u");
            Directory.CreateDirectory(outDir);
            // Named after the original plus a path hash — two same-named m3u never collide, and the
            // next launch of the same one just overwrites its copy.
            string name = Path.GetFileNameWithoutExtension(m3uPath)
                + "-" + (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(m3uPath) + ".m3u";
            string outPath = Path.Combine(outDir, name);
            File.WriteAllLines(outPath, lines);
            return outPath;
        }
        catch { return null; }
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
        var body = new Panel { Size = new Size(S(576), S(232)), BackColor = LiteBoxTheme.Bg };
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
            Text = "Each candidate is tried with the remainder path, then with the bare file name.\n"
                 + "Same file name = treated as the original (extraction, caches, history continue);\n"
                 + "a DIFFERENT name = explicit substitution, passed to the emulator verbatim, no extraction.\n"
                 + "An m3u relocates as a file — keep mirror m3u entries RELATIVE so they resolve beside it.",
            AutoSize = true, Location = new Point(0, y + S(2)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(hint);

        return (body, S(232), () =>
        {
            r.RomPathFind = find.Text.Trim();
            r.RomPathHigh = high.Text.Trim();
            r.RomPathLow = low.Text.Trim();
        });
    }
}
