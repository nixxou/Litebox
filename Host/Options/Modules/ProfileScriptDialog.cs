// One monitor-profile script slot: language switch (C# / AHK), the syntax-colored editor from the
// launch rules, a Check button, and an Examples tab written for what these scripts are actually
// for — home automation around a display switch: Home Assistant / node-red webhooks, ADB to wake
// an Android TV, raw HTTP, Wake-on-LAN. The script sees ProfileName (and the Lb API in C#); it
// runs with a 30 s cap around EVERY application of the profile (see MonitorProfileScripts).

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Rules.Actions;
using LbApiHost.Host.Rules.Scripting;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal sealed class ProfileScriptDialog : Form
{
    public string Code { get; private set; }
    public string Lang { get; private set; }

    public ProfileScriptDialog(string title, string code, string lang, float dpiS)
    {
        Code = code; Lang = lang;
        int S(int px) => (int)Math.Round(px * dpiS);

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = LiteBoxTheme.Bg;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(S(860), S(560));

        Controls.Add(new Label
        {
            Text = "Language:", AutoSize = true, Location = new Point(S(12), S(12)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        });
        var langCombo = new ComboBox
        {
            Location = new Point(S(80), S(9)), Width = S(220), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        langCombo.Items.Add("C#  (Roslyn — Lb API, HTTP, JSON…)");
        langCombo.Items.Add("AHK  (LaunchBox's AutoHotkey v1.1)");
        langCombo.SelectedIndex = string.Equals(lang, "ahk", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Controls.Add(langCombo);
        Controls.Add(new Label
        {
            Text = "Runs around EVERY application of this profile (launches and manual switches) — 30 s cap.",
            AutoSize = true, Location = new Point(S(312), S(12)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        });

        var tabs = new TabControl { Location = new Point(S(12), S(40)), Size = new Size(S(836), S(460)) };
        Controls.Add(tabs);

        var scriptPage = new TabPage("Script") { BackColor = LiteBoxTheme.Bg };
        var editorHost = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg };
        scriptPage.Controls.Add(editorHost);
        tabs.TabPages.Add(scriptPage);

        RichTextBox editor = null!;
        void RebuildEditor(string text)
        {
            editorHost.Controls.Clear();
            editor = CodeEditorBox.CreateEditor(text, ahk: langCombo.SelectedIndex == 1);
            editorHost.Controls.Add(editor);
        }
        RebuildEditor(code);
        langCombo.SelectedIndexChanged += (_, _) => RebuildEditor(editor.Text);   // recolor in the new dialect

        var examplesPage = new TabPage("Examples") { BackColor = LiteBoxTheme.Bg };
        var examplesHost = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg };
        examplesPage.Controls.Add(examplesHost);
        tabs.TabPages.Add(examplesPage);
        void RebuildExamples()
        {
            examplesHost.Controls.Clear();
            examplesHost.Controls.Add(CodeEditorBox.CreateDocView(
                langCombo.SelectedIndex == 1 ? AhkExamples : CsExamples, ahk: langCombo.SelectedIndex == 1));
        }
        RebuildExamples();
        langCombo.SelectedIndexChanged += (_, _) => RebuildExamples();

        var check = new Button
        {
            Text = "Check", Location = new Point(S(12), S(510)), Size = new Size(S(90), S(27)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        check.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        var status = new Label
        {
            Text = "", AutoSize = false, Location = new Point(S(110), S(515)), Size = new Size(S(540), S(20)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg, AutoEllipsis = true,
        };
        check.Click += (_, _) =>
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                var (ok, msg) = langCombo.SelectedIndex == 1
                    ? AhkScriptEngine.Check(editor.Text, withPrelude: true)
                    : RuleScriptEngine.Check(editor.Text);
                status.ForeColor = ok ? Color.FromArgb(120, 190, 120) : Color.FromArgb(230, 120, 110);
                status.Text = msg.ReplaceLineEndings("  ");
                new ToolTip().SetToolTip(status, msg);
            }
            finally { Cursor = Cursors.Default; }
        };
        Controls.Add(check); Controls.Add(status);

        var ok2 = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK, Location = new Point(S(662), S(510)), Size = new Size(S(88), S(27)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        ok2.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(S(758), S(510)), Size = new Size(S(88), S(27)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        Controls.Add(ok2); Controls.Add(cancel);
        AcceptButton = ok2; CancelButton = cancel;

        FormClosing += (_, _) =>
        {
            if (DialogResult != DialogResult.OK) return;
            Code = editor.Text;
            Lang = langCombo.SelectedIndex == 1 ? "ahk" : "cs";
        };
    }

    private const string CsExamples = @"C# PROFILE SCRIPTS — HOME-AUTOMATION EXAMPLES
=============================================
In scope: ProfileName, the Lb API (Log, monitor profiles, HID…), HttpClient, UdpClient,
JSON, Process. All fictional — adapt hosts, tokens and entity ids.

1) Home Assistant: turn the TV on (Before script)
     using var hc = new HttpClient();
     hc.DefaultRequestHeaders.Add(""Authorization"", ""Bearer VOTRE_TOKEN_HA"");
     hc.PostAsync(""http://homeassistant.local:8123/api/services/media_player/turn_on"",
         new StringContent(""{\""entity_id\"": \""media_player.tv_salon\""}"",
             Encoding.UTF8, ""application/json"")).Wait();

2) node-red: notify a flow with the profile name (After script)
     using var hc = new HttpClient();
     hc.PostAsync(""http://192.168.1.20:1880/litebox/profile"",
         new StringContent(JsonSerializer.Serialize(new { profile = ProfileName, state = ""applied"" }),
             Encoding.UTF8, ""application/json"")).Wait();

3) Android TV over ADB: wake it and wait until it answers (Before script)
     Process.Start(new ProcessStartInfo(@""C:\tools\adb\adb.exe"", ""connect 192.168.1.50:5555"")
         { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(5000);
     Process.Start(new ProcessStartInfo(@""C:\tools\adb\adb.exe"",
         ""-s 192.168.1.50:5555 shell input keyevent KEYCODE_WAKEUP"")
         { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(5000);

4) Raw HTTP GET (a smart plug, a Tasmota relay…)
     using var hc = new HttpClient();
     hc.GetAsync(""http://192.168.1.60/cm?cmnd=Power%20On"").Wait();

5) Wake-on-LAN magic packet (wake the shield / the projector PC)
     var mac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
     var packet = Enumerable.Repeat((byte)0xFF, 6).Concat(
         Enumerable.Range(0, 16).SelectMany(_ => mac)).ToArray();
     using var udp = new UdpClient();
     udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));

6) Wait until the second screen is really there (Before script; the 30 s cap guards you)
     for (int i = 0; i < 20 && Screen.AllScreens.Length < 2; i++)
         System.Threading.Thread.Sleep(1000);
     Lb.Log($""screens: {Screen.AllScreens.Length}"");

7) Home Assistant at exit: lights back to normal (After script of your DESKTOP profile)
     using var hc = new HttpClient();
     hc.DefaultRequestHeaders.Add(""Authorization"", ""Bearer VOTRE_TOKEN_HA"");
     hc.PostAsync(""http://homeassistant.local:8123/api/services/scene/turn_on"",
         new StringContent(""{\""entity_id\"": \""scene.bureau_normal\""}"",
             Encoding.UTF8, ""application/json"")).Wait();";

    private const string AhkExamples = @"AHK PROFILE SCRIPTS — HOME-AUTOMATION EXAMPLES
==============================================
In scope: ProfileName (and the usual prelude). v1.1 syntax. AHK shines here for
window/process gestures; for HTTP-heavy flows the C# language is the easier tool.

1) HTTP call through WinHttp (node-red webhook)
     req := ComObjCreate(""WinHttp.WinHttpRequest.5.1"")
     req.Open(""POST"", ""http://192.168.1.20:1880/litebox/profile"", false)
     req.SetRequestHeader(""Content-Type"", ""application/json"")
     req.Send(""{""""profile"""": """""" . ProfileName . """""" }"")

2) ADB: wake the Android TV (Before script)
     RunWait, C:\tools\adb\adb.exe connect 192.168.1.50:5555,, Hide
     RunWait, C:\tools\adb\adb.exe -s 192.168.1.50:5555 shell input keyevent KEYCODE_WAKEUP,, Hide

3) Start an ambiance app when the TV profile applies, kill it on the desktop profile
     ; TV profile, after-script:
     Run, C:\tools\ambilight.exe
     ; Desktop profile, after-script:
     Process, Close, ambilight.exe

4) Give the receiver time to switch HDMI before the layout applies (Before script)
     Sleep, 3000";
}
