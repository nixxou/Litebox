// Run AHK script — BigBoxProfile's ExecuteAHK on the C# rule's exact model: the same four slots
// (transform real / transform example / before launch / after exit), a display name, the syntax-
// colored editors, the documentation tab, and an export button producing a standalone testable
// .ahk. Runs OUT-OF-PROCESS through the AutoHotkey v1.1 exe LaunchBox itself ships (ThirdParty\
// AutoHotkey — no new payload), or a user-provided v2 exe in the same folder; see AhkScriptEngine
// for the prelude/result contract. A background Before script is the resident case (hotkeys,
// overlays): left running during the game, killed by the after-launch batch when it exits.

#nullable enable

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Rules.Scripting;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class AhkAction : IRuleAction
{
    private const string Tag = "ahk";

    public string Type => LaunchRule.TypeAhkScript;
    public string AddLabel => "Add: Run AHK script…";
    public string DialogTitle => "AHK script rule";

    public bool IsConfigured(LaunchRule r)
        => r.AhkReal.Length > 0 || r.AhkExample.Length > 0
        || r.AhkBefore.Length > 0 || r.AhkAfter.Length > 0;

    public string Describe(LaunchRule r)
    {
        var slots = new System.Collections.Generic.List<string>();
        if (r.AhkReal.Length > 0) slots.Add("transform");
        if (r.AhkExample.Length > 0) slots.Add("example");
        if (r.AhkBefore.Length > 0) slots.Add("before" + (r.AhkBeforeBackground ? "(resident)" : ""));
        if (r.AhkAfter.Length > 0) slots.Add("after");
        string label = $"AHK {(r.AhkV2 ? "v2" : "v1")} script: " + (slots.Count > 0 ? string.Join(" + ", slots) : "(empty)");
        return r.AhkName.Length > 0 ? $"“{r.AhkName}” — {label}" : label;
    }

    private static AhkScriptData MakeData(RuleCmd cmd, bool preview)
    {
        var ctx = RulePipeline.CurrentContext;
        string S(Func<object?> f) { try { return f()?.ToString() ?? ""; } catch { return ""; } }
        dynamic? game = ctx?.Game, emu = ctx?.Emulator, ver = ctx?.Version;
        return new AhkScriptData(cmd.Exe, cmd.Args,
            ctx?.OriginalExe ?? cmd.Exe, ctx?.OriginalArgs ?? cmd.Args,
            S(() => game?.Title), S(() => game?.Platform), S(() => game?.Id),
            S(() => emu?.Title), S(() => ver?.Name),
            preview || (ctx?.Preview ?? false));
    }

    private static RuleCmd Transform(LaunchRule r, RuleCmd cmd, string body, bool preview)
    {
        if (body.Length == 0) return cmd;
        var (ok, exe, args, error) = AhkScriptEngine.RunTransform(v1: !r.AhkV2, body, MakeData(cmd, preview));
        if (!ok) { LbLog.Warn(Tag, $"AHK {(preview ? "example" : "transform")} failed: {error} — line untouched"); return cmd; }
        return new RuleCmd(exe, args);
    }

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => Transform(r, cmd, r.AhkReal, preview: false);

    /// <summary>The preview runs ONLY the example slot — never the real script (the BBP split).</summary>
    public RuleCmd ApplyExample(LaunchRule r, RuleCmd cmd)
        => r.AhkExample.Length > 0 ? Transform(r, cmd, r.AhkExample, preview: true) : cmd;

    public void ExecuteBefore(LaunchRule r, RuleCmd cmd)
    {
        if (r.AhkBefore.Length > 0)
        {
            var data = MakeData(cmd, preview: false);
            if (r.AhkBeforeBackground)
            {
                var (ok, error, resident) = AhkScriptEngine.RunSideEffect(v1: !r.AhkV2, r.AhkBefore, data, wait: false);
                if (!ok) LbLog.Warn(Tag, $"AHK before(resident) failed: {error}");
                else if (resident != null)
                {
                    var p = resident;
                    RulePipeline.RegisterAfterLaunch(() =>
                    {
                        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); LbLog.Info(Tag, "resident AHK script killed at game exit"); } }
                        catch { }
                        finally { p.Dispose(); }
                    });
                }
            }
            else
            {
                var (ok, error, _) = AhkScriptEngine.RunSideEffect(v1: !r.AhkV2, r.AhkBefore, data, wait: true);
                if (!ok) LbLog.Warn(Tag, $"AHK before failed: {error}");
            }
        }
        if (r.AhkAfter.Length > 0)
        {
            var data = MakeData(cmd, preview: false);   // the line as this rule saw it
            bool v2 = r.AhkV2;
            string body = r.AhkAfter;
            RulePipeline.RegisterAfterLaunch(() =>
            {
                var (ok, error, _) = AhkScriptEngine.RunSideEffect(v1: !v2, body, data, wait: true);
                if (!ok) LbLog.Warn(Tag, $"AHK after failed: {error}");
            });
        }
    }

    // ── UI: name + version, four AHK tabs, documentation ──────────────────────

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(880) };

        var intro = new Label
        {
            Text = "AutoHotkey scripts, run out-of-process at launch. The prelude injects Exe, Args,"
                 + " OriginalExe/Args, GameTitle/GamePlatform/GameId, EmulatorTitle, VersionName and Preview;"
                 + " a transform slot assigns Exe/Args and the change IS the transform — see Documentation.",
            AutoSize = false, Location = new Point(0, S(2)), Size = new Size(S(878), S(34)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(intro);

        body.Controls.Add(new Label
        {
            Text = "Name (shown in the rule list):", AutoSize = true, Location = new Point(0, S(41)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        });
        var name = new TextBox
        {
            Text = r.AhkName, Location = new Point(S(170), S(38)), Width = S(330),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(name);

        var version = new ComboBox
        {
            Location = new Point(S(512), S(38)), Width = S(210), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        version.Items.Add("AutoHotkey v1.1 (LaunchBox's)");
        version.Items.Add("AutoHotkey v2 (your exe)");
        version.SelectedIndex = r.AhkV2 ? 1 : 0;
        body.Controls.Add(version);
        var avail = new Label
        {
            AutoSize = true, Location = new Point(S(728), S(41)), BackColor = LiteBoxTheme.Bg,
        };
        void SyncAvail()
        {
            bool ok = AhkScriptEngine.IsAvailable(v1: version.SelectedIndex == 0);
            avail.Text = ok ? "interpreter found" : "interpreter MISSING";
            avail.ForeColor = ok ? Color.FromArgb(120, 190, 120) : Color.FromArgb(220, 160, 90);
        }
        version.SelectedIndexChanged += (_, _) => SyncAvail();
        SyncAvail();
        body.Controls.Add(avail);

        var tabs = new TabControl
        {
            Location = new Point(0, S(68)), Size = new Size(S(878), S(470)),
        };
        body.Controls.Add(tabs);

        (RichTextBox Editor, TabPage Page) ScriptTab(string title, string code)
        {
            var page = new TabPage(title) { BackColor = LiteBoxTheme.Bg };
            var editor = CodeEditorBox.CreateEditor(code, ahk: true);
            var bar = new Panel { Dock = DockStyle.Bottom, Height = S(30), BackColor = LiteBoxTheme.Bg };
            var check = new Button
            {
                Text = "Check syntax", Location = new Point(0, S(3)), Size = new Size(S(110), S(24)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            check.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            var export = new Button
            {
                Text = "Export .ahk…", Location = new Point(S(116), S(3)), Size = new Size(S(96), S(24)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            export.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            var status = new Label
            {
                Text = "", AutoSize = false, Location = new Point(S(220), S(7)), Size = new Size(S(458), S(20)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg, AutoEllipsis = true,
            };
            check.Click += (_, _) =>
            {
                var form = body.FindForm();
                if (form != null) form.Cursor = Cursors.WaitCursor;
                try
                {
                    var (ok, msg) = AhkScriptEngine.Check(version.SelectedIndex == 0, editor.Text);
                    status.ForeColor = ok ? Color.FromArgb(120, 190, 120) : Color.FromArgb(230, 120, 110);
                    status.Text = msg.ReplaceLineEndings("  ");
                    new ToolTip().SetToolTip(status, msg);
                }
                finally { if (form != null) form.Cursor = Cursors.Default; }
            };
            export.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(BuildAhkScaffold(editor.Text, version.SelectedIndex == 0));
                    status.ForeColor = Color.FromArgb(120, 190, 120);
                    status.Text = "Standalone .ahk copied — paste into a file, tune the sample values, run it; copy the body back.";
                }
                catch (Exception ex) { status.ForeColor = Color.FromArgb(230, 120, 110); status.Text = ex.Message; }
            };
            bar.Controls.Add(check); bar.Controls.Add(export); bar.Controls.Add(status);
            page.Controls.Add(editor); page.Controls.Add(bar);
            tabs.TabPages.Add(page);
            return (editor, page);
        }

        var (real, _) = ScriptTab("Transform (real)", r.AhkReal);
        var (example, _) = ScriptTab("Transform (example)", r.AhkExample);
        var (before, beforePage) = ScriptTab("Before launch", r.AhkBefore);
        var (after, _) = ScriptTab("After exit", r.AhkAfter);

        var resident = new CheckBox
        {
            Text = "resident (keep running during the game; killed at exit)", Checked = r.AhkBeforeBackground,
            AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        var beforeBar = (Panel)beforePage.Controls[1];
        resident.Location = new Point(S(520), S(6));
        beforeBar.Controls.Add(resident);

        var docPage = new TabPage("Documentation") { BackColor = LiteBoxTheme.Bg };
        docPage.Controls.Add(CodeEditorBox.CreateDocView(DocText, ahk: true));
        tabs.TabPages.Add(docPage);

        int h = S(68) + S(474);
        body.Height = h;
        return (body, h, () =>
        {
            r.AhkName = name.Text.Trim();
            r.AhkV2 = version.SelectedIndex == 1;
            r.AhkReal = real.Text;
            r.AhkExample = example.Text;
            r.AhkBefore = before.Text;
            r.AhkAfter = after.Text;
            r.AhkBeforeBackground = resident.Checked;
        });
    }

    /// <summary>Standalone .ahk: the tab's body between SCRIPT BODY markers, a dummy prelude with
    /// sample values above, a MsgBox epilogue showing the resulting line — run it anywhere.</summary>
    internal static string BuildAhkScaffold(string body, bool v1)
    {
        var d = new AhkScriptData(
            @"C:\emulators\retroarch\retroarch.exe",
            "-L \"cores\\snes.dll\" \"C:\\roms\\game.zip\"",
            @"C:\emulators\retroarch\retroarch.exe",
            "-L \"cores\\snes.dll\" \"C:\\roms\\game.zip\"",
            "Sample Game", "Super Nintendo Entertainment System", "00000000-0000-0000-0000-000000000000",
            "RetroArch", "", true);
        string epilogue = v1
            ? "MsgBox % \"Exe = \" . Exe . \"`n\" . \"Args = \" . Args"
            : "MsgBox(\"Exe = \" . Exe . \"`n\" . \"Args = \" . Args)";
        return "; LiteBox AHK scaffold — tune the sample values, run this file with AutoHotkey "
             + (v1 ? "v1.1" : "v2") + ",\r\n"
             + "; edit ONLY between the SCRIPT BODY markers, then copy that part back into LiteBox.\r\n"
             + AhkScriptEngine.BuildPrelude(d, v1).ReplaceLineEndings("\r\n")
             + "; ==== SCRIPT BODY — copy back everything between these two markers ====\r\n"
             + (body.Length == 0 ? "; (empty script)" : body.ReplaceLineEndings("\r\n")) + "\r\n"
             + "; ==== END SCRIPT BODY ====\r\n"
             + epilogue + "\r\n";
    }

    private const string DocText = @"AHK SCRIPT RULES — HOW IT WORKS
===============================

Same model as the C# script rule, in AutoHotkey. Each tab is one script, run
OUT-OF-PROCESS with the AutoHotkey v1.1 exe LaunchBox already ships (ThirdParty\
AutoHotkey\AutoHotkey.exe) — or a v2 exe YOU drop in that same folder (AutoHotkey64.exe).
Waited scripts are killed after 10 s; failures log to [ahk] and skip the rule.

THE FOUR SLOTS
  Transform (real)     runs at launch. Assign Exe and/or Args — the assigned values ARE
                       the transformed line (an epilogue reads them back).
  Transform (example)  what the page preview runs INSTEAD of the real script.
  Before launch        side effects before the spawn. 'resident' keeps the script ALIVE
                       during the game — hotkeys, overlays — and kills it at exit.
  After exit           runs when the emulator exits.

WHAT A SCRIPT SEES (injected by the generated prelude)
  Exe, Args                    the line at this rule's position (assign them to transform)
  OriginalExe, OriginalArgs    the line before ANY rule ran
  GameTitle, GamePlatform,     the launch's game (empty in the page preview)
  GameId
  EmulatorTitle, VersionName
  Preview                      1 on the example channel, 0 at a real launch

EIGHT EXAMPLES (v1.1 syntax — the default interpreter)
------------------------------------------------------
1) Append an argument for one game
     if InStr(GameTitle, ""Duck Hunt"")
         Args := Args . "" --lightgun""

2) Force fullscreen unless the test build runs
     if !InStr(Exe, ""debug"")
         Args := ""--fullscreen "" . Args

3) Regex-rewrite the core path
     Args := RegExReplace(Args, ""cores\\\\([\\w]+)\\.dll"", ""altcores\\$1.dll"")

4) Start a companion tool and give it time (Before slot)
     Run, C:\tools\ledblinky.exe
     Sleep, 500

5) Resident hotkeys DURING the game (Before slot, 'resident' ticked)
     F9::Send {Volume_Down}
     F10::Send {Volume_Up}
     F12::Run, C:\tools\screenshot.exe

6) Wait for the emulator window, then force it active (Before slot, 'resident')
     WinWait, ahk_exe retroarch.exe,, 15
     WinActivate, ahk_exe retroarch.exe

7) Write a per-game ini before launch (Before slot)
     IniWrite, %GameTitle%, C:\emu\session.ini, session, title
     IniWrite, %GamePlatform%, C:\emu\session.ini, session, platform

8) Cleanup at exit (After slot)
     Process, Close, ledblinky.exe
     FileDelete, C:\emu\session.ini

NOTES
  • 'Export .ahk…' copies a STANDALONE script: dummy prelude + your body between
    markers + a MsgBox showing the resulting line. Run it directly to test.
  • 'Check syntax' uses v2's /validate; v1 has no validate mode — use the export.
  • The C# rule is the richer tool (HID objects, media, JSON/XML): reach for AHK
    when you want AHK's strengths — Send, hotkeys, window management, residency.";
}
