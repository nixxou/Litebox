// Run C# script — the successor of BigBoxProfile's ExecuteAHK, on Roslyn. Same four slots as the
// original's four boxes, one tab each, plus a Documentation tab:
//
//   Transform (real)     Modify on the REAL channel — assign Exe/Args in the script, the change IS
//                        the transform, visible to every rule after (the branching bus).
//   Transform (example)  the PREVIEW's transform. The preview NEVER runs the real script (side
//                        effects); an empty example slot leaves the preview line untouched — BBP's
//                        exact split (ahkCodeExemple / ahkCodeReal).
//   Before launch        side effects before the spawn, probe-gated; optional background task
//                        (BBP's runbeforebackground). Line mutations here are ignored.
//   After exit           runs once the emulator exits, via the pipeline's after-launch batch —
//                        with the line as it stood at this rule's position.
//
// Scripts see RuleScriptGlobals (current + original line, IGame/IEmulator/version, Preview, the
// Lb Swiss-army API); compilation is cached per session; a 10 s watchdog abandons runaways; every
// failure logs and skips the rule, never the launch. Details and ten examples: the Documentation tab.

#nullable enable

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Rules.Scripting;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class ScriptAction : IRuleAction
{
    private const string Tag = "script";

    public string Type => LaunchRule.TypeScript;
    public string AddLabel => "Add: Run C# script…";
    public string DialogTitle => "C# script rule";

    public bool IsConfigured(LaunchRule r)
        => r.ScriptReal.Length > 0 || r.ScriptExample.Length > 0
        || r.ScriptBefore.Length > 0 || r.ScriptAfter.Length > 0;

    public string Describe(LaunchRule r)
    {
        var slots = new System.Collections.Generic.List<string>();
        if (r.ScriptReal.Length > 0) slots.Add("transform");
        if (r.ScriptExample.Length > 0) slots.Add("example");
        if (r.ScriptBefore.Length > 0) slots.Add("before" + (r.ScriptBeforeBackground ? "(bg)" : ""));
        if (r.ScriptAfter.Length > 0) slots.Add("after");
        return "C# script: " + (slots.Count > 0 ? string.Join(" + ", slots) : "(empty)");
    }

    private static RuleScriptGlobals MakeGlobals(LaunchRule r, RuleCmd cmd, bool preview)
    {
        var ctx = RulePipeline.CurrentContext;
        var g = new RuleScriptGlobals
        {
            Exe = cmd.Exe, Args = cmd.Args,
            OriginalExe = ctx?.OriginalExe ?? cmd.Exe,
            OriginalArgs = ctx?.OriginalArgs ?? cmd.Args,
            Game = ctx?.Game, Emulator = ctx?.Emulator, Version = ctx?.Version,
            Preview = preview || (ctx?.Preview ?? false),
        };
        g.Lb = new LbScriptApi(g, r);
        return g;
    }

    private static RuleCmd RunTransform(LaunchRule r, RuleCmd cmd, string code, bool preview)
    {
        if (code.Length == 0) return cmd;
        var g = MakeGlobals(r, cmd, preview);
        var (ok, err) = RuleScriptEngine.Run(code, g);
        if (!ok) { LbLog.Warn(Tag, $"script {(preview ? "example" : "transform")} failed: {err} — line untouched"); return cmd; }
        return new RuleCmd(g.Exe, g.Args);
    }

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => RunTransform(r, cmd, r.ScriptReal, preview: false);

    /// <summary>The preview runs ONLY the example slot — never the real script (BBP's split).</summary>
    public RuleCmd ApplyExample(LaunchRule r, RuleCmd cmd)
        => r.ScriptExample.Length > 0 ? RunTransform(r, cmd, r.ScriptExample, preview: true) : cmd;

    public void ExecuteBefore(LaunchRule r, RuleCmd cmd)
    {
        // Globals are built NOW, while the walk's context (Game/Emulator/original line) is live —
        // the background task and the after-exit closure run when it is gone.
        if (r.ScriptBefore.Length > 0)
        {
            var g = MakeGlobals(r, cmd, preview: false);
            if (r.ScriptBeforeBackground) Task.Run(() => RunPrepared(g, r.ScriptBefore, "before(bg)"));
            else RunPrepared(g, r.ScriptBefore, "before");
        }
        if (r.ScriptAfter.Length > 0)
        {
            var g = MakeGlobals(r, cmd, preview: false);   // the line as this rule saw it — documented
            RulePipeline.RegisterAfterLaunch(() => RunPrepared(g, r.ScriptAfter, "after"));
        }
    }

    private static void RunPrepared(RuleScriptGlobals g, string code, string slot)
    {
        var (ok, err) = RuleScriptEngine.Run(code, g);
        if (!ok) LbLog.Warn(Tag, $"script {slot} failed: {err}");
    }

    // ── UI: four script tabs + documentation ──────────────────────────────────

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        // Wide on purpose — this is a code editor, not a form field; the rule dialog follows the
        // body's width (see RuleDialog).
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(880) };

        var intro = new Label
        {
            Text = "C# scripts (Roslyn). Assign Exe / Args to transform the line; Game, Emulator, Version,"
                 + " OriginalArgs, Preview and the Lb API (HID devices, monitor profiles, variables, log)"
                 + " are in scope — see the Documentation tab. Compiled once per session; 10 s watchdog.",
            AutoSize = false, Location = new Point(0, S(2)), Size = new Size(S(878), S(34)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(intro);

        var tabs = new TabControl
        {
            Location = new Point(0, S(38)), Size = new Size(S(878), S(470)),
        };
        body.Controls.Add(tabs);

        (RichTextBox Editor, TabPage Page) ScriptTab(string title, string code)
        {
            var page = new TabPage(title) { BackColor = LiteBoxTheme.Bg };
            var editor = CodeEditorBox.CreateEditor(code);
            var bar = new Panel { Dock = DockStyle.Bottom, Height = S(30), BackColor = LiteBoxTheme.Bg };
            var check = new Button
            {
                Text = "Check compile", Location = new Point(0, S(3)), Size = new Size(S(110), S(24)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            check.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            var status = new Label
            {
                Text = "", AutoSize = false, Location = new Point(S(118), S(7)), Size = new Size(S(560), S(20)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg, AutoEllipsis = true,
            };
            check.Click += (_, _) =>
            {
                var form = body.FindForm();
                if (form != null) form.Cursor = Cursors.WaitCursor;
                try
                {
                    var (ok, msg) = RuleScriptEngine.Check(editor.Text);
                    status.ForeColor = ok ? Color.FromArgb(120, 190, 120) : Color.FromArgb(230, 120, 110);
                    status.Text = msg.ReplaceLineEndings("  ");
                    var tip = new ToolTip();
                    tip.SetToolTip(status, msg);
                }
                finally { if (form != null) form.Cursor = Cursors.Default; }
            };
            var vscode = new Button
            {
                Text = "VS Code…", Location = new Point(S(116), S(3)), Size = new Size(S(84), S(24)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            vscode.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            vscode.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(BuildVsCodeScaffold(editor.Text));
                    status.ForeColor = Color.FromArgb(120, 190, 120);
                    status.Text = "Scaffold copied — paste into a .cs in VS Code, edit between the SCRIPT BODY markers, copy that part back.";
                }
                catch (Exception ex) { status.ForeColor = Color.FromArgb(230, 120, 110); status.Text = ex.Message; }
            };
            status.Location = new Point(S(208), S(7));
            status.Size = new Size(S(470), S(20));
            bar.Controls.Add(check); bar.Controls.Add(vscode); bar.Controls.Add(status);
            page.Controls.Add(editor); page.Controls.Add(bar);
            tabs.TabPages.Add(page);
            return (editor, page);
        }

        var (real, _) = ScriptTab("Transform (real)", r.ScriptReal);
        var (example, _) = ScriptTab("Transform (example)", r.ScriptExample);
        var (before, beforePage) = ScriptTab("Before launch", r.ScriptBefore);
        var (after, _) = ScriptTab("After exit", r.ScriptAfter);

        var bg = new CheckBox
        {
            Text = "background (do not wait)", Checked = r.ScriptBeforeBackground, AutoSize = true,
            ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        // ride the before-tab's bottom bar, right side
        var beforeBar = (Panel)beforePage.Controls[1];
        bg.Location = new Point(S(690), S(6));
        beforeBar.Controls.Add(bg);

        var docPage = new TabPage("Documentation") { BackColor = LiteBoxTheme.Bg };
        docPage.Controls.Add(CodeEditorBox.CreateDocView(DocText));
        tabs.TabPages.Add(docPage);

        int h = S(38) + S(474);
        body.Height = h;
        return (body, h, () =>
        {
            r.ScriptReal = real.Text;
            r.ScriptExample = example.Text;
            r.ScriptBefore = before.Text;
            r.ScriptAfter = after.Text;
            r.ScriptBeforeBackground = bg.Checked;
        });
    }

    /// <summary>A self-contained .cs the user pastes into VS Code: the tab's code between SCRIPT
    /// BODY markers, everything LiteBox provides mirrored as dummies with sample data — IntelliSense
    /// matches, `dotnet run` executes, and the body copies back verbatim (indentation untouched).</summary>
    internal static string BuildVsCodeScaffold(string body)
        => ScaffoldTemplate.Replace("__SCRIPT_BODY__", body.Length == 0 ? "// (empty script)" : body.ReplaceLineEndings("\r\n"));

    private const string ScaffoldTemplate = """"
// ─────────────────────────────────────────────────────────────────────────────
// LiteBox C# script scaffold
//   1. Paste this WHOLE file over the Program.cs of a `dotnet new console` project
//      (or any scratch .cs — IntelliSense works either way).
//   2. Edit ONLY between the SCRIPT BODY markers. Everything else is dummies that
//      mirror what LiteBox provides at launch — tune their sample values to test,
//      then `dotnet run` executes your body and prints the resulting line.
//   3. Copy the body (between the markers) back into the LiteBox script tab.
// In LiteBox, Game/Emulator are LaunchBox's live IGame/IEmulator (dynamic — more
// members than these dummies) and Lb queries REAL devices/profiles.
// ─────────────────────────────────────────────────────────────────────────────
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public static class LiteBoxScript
{
    // ── the world (sample values — tune freely) ──
    public static string Exe = @"C:\emulators\retroarch\retroarch.exe";
    public static string Args = @"-L ""cores\snes.dll"" ""C:\roms\game.zip""";
    public static string[] ArgList => SplitArgs(Args);
    public static string OriginalExe = @"C:\emulators\retroarch\retroarch.exe";
    public static string OriginalArgs = @"-L ""cores\snes.dll"" ""C:\roms\game.zip""";
    public static string[] OriginalArgList => SplitArgs(OriginalArgs);
    public static dynamic Game = new DummyGame();
    public static dynamic Emulator = new DummyEmulator();
    public static dynamic Version = null;
    public static bool Preview = true;
    public static LbApi Lb = new LbApi();

    public static object Run()
    {
        // ==== SCRIPT BODY — copy back everything between these two markers ====
__SCRIPT_BODY__
        // ==== END SCRIPT BODY ====
        return null;
    }

    public static void Main()
    {
        Run();
        Console.WriteLine();
        Console.WriteLine("Exe  = " + Exe);
        Console.WriteLine("Args = " + Args);
    }

    static string[] SplitArgs(string s) => Regex.Matches(s, @"""([^""]*)""|(\S+)")
        .Cast<Match>().Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
}

// ── dummies mirroring LiteBox's objects ──────────────────────────────────────
public class DummyGame
{
    public string Id = "00000000-0000-0000-0000-000000000000";
    public string Title = "Sample Game";
    public string Platform = "Super Nintendo Entertainment System";
    public string ApplicationPath = @"C:\roms\game.zip";
}
public class DummyEmulator
{
    public string Id = "00000000-0000-0000-0000-000000000001";
    public string Title = "RetroArch";
    public string ApplicationPath = @"C:\emulators\retroarch\retroarch.exe";
}

public sealed record HidDeviceInfo(string Name, int VendorId, int ProductId, string Path);
public sealed record Ds4DeviceInfo(int VendorId, int ProductId, string Path, bool Usb);
public sealed record BtDeviceInfo(string Name, string ClassOfDevice, string Address);
public sealed record XInputSlotInfo(int Slot, string SubType, string Signature,
    int VendorId, int ProductId, int RevisionId,
    string Ds4Mac, string Ds4Type, string Ds4Connection, int Ds4InputSlot);
public sealed record DInputDeviceInfo(int Index, string ProductName, string Type,
    Guid InstanceGuid, string InstanceName, string InterfacePath);
public sealed record SdlDeviceInfo(int Index, string Name, string CapsSignature,
    string Serial, string Guid, int VendorId, int ProductId);

public class LbApi
{
    public string LbRoot = @"C:\LaunchBox";
    public void Log(string message) => Console.WriteLine("[script] " + message);
    public string Var(string name) => "";
    public string ExpandVars(string text) => text;
    public List<HidDeviceInfo> HidDevices() => new()
        { new("Sample Pad", 1118, 767, @"\\?\hid#vid_045e&pid_02ff#sample") };
    public List<Ds4DeviceInfo> Ds4Devices() => new()
        { new(1356, 2508, @"\\?\hid#vid_054c&pid_09cc#sample", true) };
    public List<BtDeviceInfo> BluetoothDevices() => new()
        { new("Wireless Controller", "9480", "001122334455") };
    public List<XInputSlotInfo> XInputSlots(string ds4WinLogPath = "") => new()
        { new(1, "Gamepad", "A1B2C3", 0x045E, 0x02FF, 1, "", "", "", 0) };
    public List<DInputDeviceInfo> DInputDevices() => new()
        { new(0, "Sample Stick", "Gamepad", Guid.NewGuid(), "Sample Stick", @"\\?\hid#sample") };
    public List<SdlDeviceInfo> SdlDevices(bool rawInputOff = false) => new()
        { new(0, "Sample Pad", "ABC123", "SER01", "0300aabbccdd", 0x054C, 0x09CC) };
    public void RescanDevices() { }
    public string[] MonitorProfileNames() => new[] { "TV 4K", "Desk" };
    public bool ApplyMonitorProfile(string name)
    { Console.WriteLine("[script] ApplyMonitorProfile(" + name + ")"); return true; }
}
"""";

    private const string DocText = @"C# SCRIPT RULES — HOW IT WORKS
==============================

Each tab is one script, compiled once per session (first compile ~1 s, then instant).
A script is plain C# (Roslyn scripting): statements at top level, no class needed.
Imports already in scope: System, IO, Linq, Collections.Generic, Text, RegularExpressions,
Text.Json, Xml.Linq, Diagnostics — JSON, XML and regex work out of the box.

THE FOUR SLOTS
  Transform (real)     runs at launch, at this rule's position. Assign Exe and/or Args:
                       the assigned values ARE the transformed line, and every rule after
                       this one probes the result (the marker/branching mechanics apply).
  Transform (example)  what the page preview runs INSTEAD of the real script (which may
                       have side effects). Empty = the preview shows the line unchanged.
  Before launch        side effects before the spawn (files, processes, devices).
                       'background' fires it without waiting. Line changes are ignored.
  After exit           runs when the emulator exits. Sees the line as this rule saw it.

WHAT A SCRIPT SEES
  Exe, Args            the line as it stands HERE (mutable in transform slots)
  ArgList              Args split into arguments (read-only snapshot)
  OriginalExe/Args     the line before ANY rule ran (+ OriginalArgList)
  Game, Emulator,      the live LaunchBox objects, dynamic: write Game.Title directly
  Version              (cast to IGame etc. for static typing). Null in the page preview!
  Preview              true on the example channel and sandbox runs
  Lb                   the toolbox below

THE Lb TOOLBOX
  Lb.Log(msg)                       LiteBox log, tag [script]
  Lb.LbRoot                         the LaunchBox install root
  Lb.Var(""NAME"") / Lb.ExpandVars(s)  this rule's variables, resolved now
  Lb.HidDevices()                   HidSharp view    → Name, VendorId, ProductId, Path
  Lb.SdlDevices(rawInputOff=false)  SDL view         → Index, Name, CapsSignature, Serial, Guid, VendorId, ProductId
  Lb.XInputSlots(ds4LogPath="""")     XInput view      → Slot, SubType, Signature, VID/PID/Rev, DS4 link
  Lb.DInputDevices()                DirectInput view → Index, ProductName, Type, InstanceGuid, InterfacePath
  Lb.Ds4Devices()                   DualShock 4      → VendorId, ProductId, Path, Usb
  Lb.BluetoothDevices()             connected BT (SLOW: live inquiry) → Name, Class, Address
  Lb.RescanDevices()                drop the per-launch device cache
  Lb.MonitorProfileNames()          every saved monitor profile
  Lb.ApplyMonitorProfile(""name"")    switch now; restored at game exit. No-op in Preview.
  Devices are queried ON DEMAND — a script that never asks never pays a scan.

TEN FICTIONAL EXAMPLES
----------------------
1) Append an argument for one game
     if (Game != null && Game.Title.Contains(""Duck Hunt"")) Args += "" --lightgun"";

2) Force fullscreen except on the test build
     if (!Exe.Contains(""debug"")) Args = ""--fullscreen "" + Args;

3) Regex-rewrite an argument (swap any core path to another folder)
     Args = Regex.Replace(Args, @""cores\\([\w]+)\.dll"", @""altcores\\$1.dll"");

4) A lightgun is plugged in → inject the marker downstream rules key on
     if (Lb.HidDevices().Any(d => d.VendorId == 0x2341)) Args += "" --sinden-marker"";

5) Pick the controller index the emulator wants (SDL numbering)
     var pad = Lb.SdlDevices().FirstOrDefault(d => d.Name.Contains(""8BitDo""));
     if (pad != null) Args += $"" --controller={pad.Index}"";

6) Write a per-game JSON config next to the emulator
     var cfg = new { rom = ArgList.LastOrDefault(), vibrance = 80 };
     if (!Preview) File.WriteAllText(Path.Combine(Path.GetDirectoryName(Exe)!, ""game.json""),
                                     JsonSerializer.Serialize(cfg));

7) Patch an XML setting before launch (Before slot)
     var doc = XDocument.Load(@""C:\emu\settings.xml"");
     doc.Root!.Element(""video"")!.SetElementValue(""vsync"", ""true"");
     doc.Save(@""C:\emu\settings.xml"");

8) Two screens connected → switch the monitor profile (Before slot)
     if (Screen.AllScreens.Length > 1) Lb.ApplyMonitorProfile(""TV 4K"");
     // (add: using System.Windows.Forms — or test Lb.MonitorProfileNames() instead)

9) Kill a companion app at exit (After slot)
     foreach (var p in Process.GetProcessesByName(""ledblinky"")) { p.Kill(); }

10) XInput slot of the DS4Windows pad → tell the emulator (uses the DS4 link)
     var slot = Lb.XInputSlots(@""C:\DS4Windows\Logs"").FirstOrDefault(s => s.Signature == ""DS4WIN"");
     if (slot != null) Args += $"" --pad={slot.Slot}"";

NOTES
  • The ""VS Code…"" button copies a SELF-CONTAINED .cs to the clipboard: your code between
    SCRIPT BODY markers, everything else mirrored as dummies with sample data — IntelliSense
    matches, `dotnet run` executes. Edit there, copy the body back.
  • Runtime errors and timeouts (10 s) log to [script] and SKIP the rule — never the launch.
  • The real/preview split is yours to honour: mirror what matters into the example slot.
  • Writing to Game/Emulator is possible but on you — LiteBox does not undo it.";
}
