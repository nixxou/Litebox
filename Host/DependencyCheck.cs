// Launch-time dependency pre-check, modelled on LaunchBox's "Missing Dependency
// Files" dialog. The data comes from the emulator's INTEGRATION plugin
// (EmulatorPlugin.GetBiosFilesForPlatform → EmulatorBiosFile{Location, FileName,
// Required, Md5}); emulators without a plugin are never blocked.
//
// Flow (HostLaunch, right before the main spawn, on the launch worker):
//   • required bios files for (emulator, game.platform) that are MISSING on disk
//     → a modal dark dialog (on the UI thread): "Play Anyway" continues the
//     launch, "Cancel Launch" aborts; a "Don't show this again for this
//     platform/emulator" checkbox persists the skip in LiteBox.ini
//     (SkipDepCheck.<emulatorId>.<platform>=true).
//   • LB's "Verify Dependency Files" (auto-download) is NOT replicated yet —
//     that ties into the InstallEmulator flow (Edit Emulator V3+).
//
// Master switch: LiteBox.ini CheckDependencies (default true).

#nullable enable

using System.IO;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal static class DependencyCheck
{
    private static LiteBoxConfig? _cfg;
    private static string _lbRoot = "";

    public static void Configure(LiteBoxConfig cfg, string lbRoot) { _cfg = cfg; _lbRoot = lbRoot; }

    /// <summary>True → continue the launch; false → user cancelled. Never throws,
    /// never blocks emulators without an integration plugin.</summary>
    public static bool PreLaunchCheck(IEmulator emulator, IGame game)
    {
        try
        {
            if (_cfg != null && !_cfg.GetBool("CheckDependencies", true)) return true;
            string platform = "";
            try { platform = game?.Platform ?? ""; } catch { }
            if (platform.Length == 0) return true;

            string emuId = "";
            try { emuId = emulator?.Id ?? ""; } catch { }
            string skipKey = $"SkipDepCheck.{emuId}.{platform}";
            if (_cfg != null && _cfg.GetBool(skipKey, false)) return true;

            var missing = MissingRequiredFiles(emulator!, platform);
            if (missing.Count == 0) return true;

            Console.WriteLine($"[deps] {missing.Count} required dependency file(s) missing for \"{platform}\":");
            foreach (var m in missing) Console.WriteLine("  - " + m);

            bool play = true, dontShowAgain = false;
            UiThread.Invoke(() =>
            {
                using var dlg = new MissingDepsDialog(missing);
                play = dlg.ShowDialog() == DialogResult.OK;
                dontShowAgain = dlg.DontShowAgain;
            });
            if (dontShowAgain && _cfg != null) { _cfg.SetBool(skipKey, true); _cfg.Save(); }
            if (!play) Console.WriteLine("[deps] launch cancelled by the user.");
            return play;
        }
        catch (Exception ex) { Console.WriteLine("[deps] check failed (launch continues): " + ex.Message); return true; }
    }

    /// <summary>Missing dependency lines, with LB's GROUP semantics (probed on the
    /// RetroArch plugin):
    ///   • a file in NO group (or in a group with AllItemsRequired): its own
    ///     Required flag decides — missing → one line per file;
    ///   • a file in a group with IsGroupRequired + !AllItemsRequired: AT LEAST
    ///     ONE file of the group must exist (e.g. PS1 "Regional BIOS" —
    ///     scph5500/5501/5502, any region suffices) — the per-file Required flag
    ///     is superseded by the group rule.
    /// Paths resolve against the emulator's folder (Location is emulator-relative,
    /// e.g. "system").</summary>
    private static List<string> MissingRequiredFiles(IEmulator emulator, string platform)
    {
        var result = new List<string>();
        var files = EmuPlugins.BiosFiles(emulator, platform).ToList();
        if (files.Count == 0) return result;

        string emuDir = "";
        try
        {
            var p = emulator.ApplicationPath ?? "";
            if (!Path.IsPathRooted(p)) p = Path.GetFullPath(Path.Combine(_lbRoot, p));
            emuDir = Path.GetDirectoryName(p) ?? "";
        }
        catch { }
        if (emuDir.Length == 0) return result;

        string Rel(EmulatorBiosFile f)
        {
            string loc = "", name = "";
            try { loc = f.Location ?? ""; name = f.FileName ?? ""; } catch { }
            return Path.Combine(loc.Trim('\\', '/'), name).Replace('/', '\\');
        }
        bool Exists(EmulatorBiosFile f)
        {
            try { return File.Exists(Path.Combine(emuDir, Rel(f))); } catch { return false; }
        }

        // At-least-one groups (IsGroupRequired, not AllItemsRequired).
        var atLeastOneGroups = files
            .Where(f => Safe(() => f.ApplicableGroup) is { IsGroupRequired: true, AllItemsRequired: false })
            .GroupBy(f => Safe(() => f.ApplicableGroup!.Id) ?? "");
        var grouped = new HashSet<EmulatorBiosFile>();
        foreach (var g in atLeastOneGroups)
        {
            var members = g.ToList();
            foreach (var m in members) grouped.Add(m);
            if (!members.Any(Exists))
            {
                string desc = Safe(() => members[0].ApplicableGroup!.Description) ?? "";
                result.Add($"one of:  {string.Join("  ·  ", members.Select(m => "\\" + Rel(m)))}"
                           + (desc.Length > 0 ? $"   ({desc})" : ""));
            }
        }

        // Ungrouped (or all-items groups): per-file Required.
        foreach (var f in files)
        {
            if (grouped.Contains(f)) continue;
            bool required = Safe(() => f.Required);
            var grp = Safe(() => f.ApplicableGroup);
            if (grp is { IsGroupRequired: true, AllItemsRequired: true }) required = true;
            if (!required) continue;
            if (!Exists(f)) result.Add("\\" + Rel(f));
        }
        return result;
    }

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }

    // ── The dialog (dark, LB-style wording) ──────────────────────────────
    // Derives from LiteBoxForm (shared theme + DPI scale factor) instead of hand-rolling both, and
    // stacks its content in a top-down FlowLayoutPanel instead of manual Point math - the same fix
    // applied to OptionsWindow.cs: a fixed Y position baked in before the (DPI-correct, so
    // potentially bigger) text is measured is exactly what caused this session's original overlap
    // bug. AutoScroll on the body absorbs any size misestimate instead of controls overlapping.
    private sealed class MissingDepsDialog : LiteBoxForm
    {
        public bool DontShowAgain { get; private set; }

        public MissingDepsDialog(List<string> missing)
        {
            Text = "Missing Dependency Files";
            ClientSize = new Size(S(560), S(260 + Math.Min(5, missing.Count) * 20));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; TopMost = true;

            var body = new Panel
            {
                Dock = DockStyle.Fill, AutoScroll = true, BackColor = LiteBoxTheme.Bg,
                Padding = new Padding(S(16), S(14), S(16), S(8)),
            };
            var stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                BackColor = LiteBoxTheme.Bg,
            };

            var head = new Label
            {
                Text = "Heads Up!  There are missing dependency files that may be required to play this game.",
                AutoSize = true, MaximumSize = new Size(S(515), 0), Margin = new Padding(0, 0, 0, S(12)),
            };
            stack.Controls.Add(head);

            var list = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Width = S(515), Height = S(Math.Min(5, missing.Count) * 20 + 24),
                BackColor = LiteBoxTheme.Panel2, ForeColor = Color.FromArgb(235, 150, 150),
                BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, S(12)),
                Text = string.Join("\r\n", missing),
            };
            stack.Controls.Add(list);

            var chk = new CheckBox { Text = "Don't show this again for this platform/emulator", AutoSize = true };
            chk.CheckedChanged += (_, _) => DontShowAgain = chk.Checked;
            stack.Controls.Add(chk);

            body.Controls.Add(stack);
            Controls.Add(body);

            var footer = new FooterBar();
            var cancel = footer.AddButton("Cancel Launch", LiteBoxTheme.CancelBtn, (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
            var play = footer.AddButton("Play Anyway", LiteBoxTheme.Ok, (_, _) => { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(footer);
            AcceptButton = play; CancelButton = cancel;
        }
    }
}
