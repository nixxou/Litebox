// UseFileContent — pointer-file indirection: an argument that names an existing text file is
// replaced by that file's CONTENT (typically a path — a tiny .txt beside the rom holding the real
// target; a relative content resolves against the pointer file's folder, the original's "usefile"
// option, or the current directory). BigBoxProfile's semantics with its four flaws corrected, as
// agreed:
//   1. the EXE is out of reach — the original looped from args[0], so the emulator executable
//      (which always exists) had its BINARY read as text and spliced into the line. Our RuleCmd
//      separates Exe from Args, so the fix falls out of the architecture;
//   2. per-argument try/catch — one unreadable file no longer aborts the whole rule;
//   3. the content is TRIMMED — the original shipped the trailing newline inside the argument;
//   4. sanity guards — only files ≤ 4 KB with NUL-free content are treated as pointer files (the
//      original swallowed any existing file, a .zip rom included), and a relative content is only
//      rooted when the combined path actually EXISTS (the original path-ified plain option text
//      like "--fullscreen" into "C:\dir\--fullscreen").
// A pure line transform: both channels identical, no side effects, nothing to restore.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class UseFileContentAction : IRuleAction
{
    private const long MaxPointerBytes = 4096;   // a pointer file is tiny; a rom is not

    public string Type => LaunchRule.TypeUseFileContent;
    public string AddLabel => "Add: Use file content…";
    public string DialogTitle => "Use file content rule";

    public bool IsConfigured(LaunchRule r) => true;   // the one option has a valid default (BBP too)

    public string Describe(LaunchRule r)
        => "Use file content as argument (relative → " + (r.UseFileDir ? "beside the file)" : "current dir)");

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd)
    {
        var parts = RuleArgs.Split(cmd.Args);
        bool changed = false;
        for (int i = 0; i < parts.Length; i++)
        {
            try
            {
                string? replaced = Resolve(parts[i], r.UseFileDir);
                if (replaced != null) { parts[i] = replaced; changed = true; }
            }
            catch { /* per-arg: an unreadable file leaves its argument alone */ }
        }
        return changed ? cmd with { Args = RuleArgs.Join(parts) } : cmd;
    }

    /// <summary>The pointer resolution for one argument — null when the argument is not a sane
    /// pointer file (missing, too big, binary, empty).</summary>
    internal static string? Resolve(string arg, bool useFileDir)
    {
        var fi = new FileInfo(arg);
        if (!fi.Exists || fi.Length > MaxPointerBytes) return null;
        string content = File.ReadAllText(arg).Trim();
        if (content.Length == 0 || content.Contains('\0')) return null;
        if (!Path.IsPathRooted(content))
        {
            string baseDir = useFileDir
                ? (Path.GetDirectoryName(arg) ?? Directory.GetCurrentDirectory())
                : Directory.GetCurrentDirectory();
            string combined = Path.Combine(baseDir, content);
            // Root only what actually resolves to something — plain option text stays itself.
            if (File.Exists(combined) || Directory.Exists(combined)) content = Path.GetFullPath(combined);
        }
        return content;
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(576) };
        int y = 0;

        Label Cap(string t, int lines = 1)
        {
            var l = new Label
            {
                Text = t, AutoSize = false, Location = new Point(0, y + S(2)),
                Size = new Size(S(574), S(2 + 18 * lines)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            body.Controls.Add(l);
            y += S(6 + 18 * lines);
            return l;
        }

        Cap("Every argument naming an existing small text file (≤ 4 KB) is replaced by the file's"
            + " content — a pointer file holding the real target. A relative content path is rooted"
            + " when it resolves to a real file; anything else is passed through as-is. The emulator"
            + " executable is never touched.", 4);

        var useDir = new CheckBox
        {
            Text = "Resolve a relative content path against the pointer file's folder (else: current directory)",
            Checked = r.UseFileDir, AutoSize = true,
            Location = new Point(0, y), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(useDir);
        y += S(28);

        // ── sandbox: point it at a real file and see the resolved argument ──
        Cap("Sandbox — a pointer file to test (its content becomes the argument):");
        var testFile = new TextBox
        {
            Text = "", Location = new Point(0, y), Width = S(486),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(testFile);
        var browse = new Button
        {
            Text = "Browse…", Location = new Point(S(492), y - S(1)), Size = new Size(S(82), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*" };
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) testFile.Text = dlg.FileName;
        };
        body.Controls.Add(browse);
        y += S(30);

        Cap("The argument would become:");
        var result = new TextBox
        {
            Location = new Point(0, y), Width = S(574), ReadOnly = true,
            BackColor = LiteBoxTheme.Bg, ForeColor = LiteBoxTheme.SubFg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(result);
        y += S(30);

        void Recalc()
        {
            try
            {
                if (testFile.Text.Trim().Length == 0) { result.Text = ""; return; }
                string? resolved = Resolve(testFile.Text.Trim(), useDir.Checked);
                result.Text = resolved ?? "(not a pointer file — argument would stay as-is)";
            }
            catch (Exception ex) { result.Text = "(error: " + ex.Message + ")"; }
        }
        testFile.TextChanged += (_, _) => Recalc();
        useDir.CheckedChanged += (_, _) => Recalc();

        body.Height = y;
        return (body, y, () => r.UseFileDir = useDir.Checked);
    }
}
