// One matcher's editor — BBP's HidDeviceDetector_PopupEdit plus the sandbox this subject demands:
// "Scan" dumps the checked libraries' device lines into the box (each line is exactly what the
// regex sees), and the match result recomputes LIVE as the regex/suffix change against the cached
// dump — you write the pattern while looking at the real lines it must catch. Devices are only
// enumerated on explicit Scan (Bluetooth inquiries take seconds); the scan shares the launch cache,
// warmed here, reused by the page preview.

#nullable enable

using System;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LbApiHost.Host.Rules.Hid;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class HidMatcherDialog : Form
{
    public HidMatcher Result { get; private set; }

    private readonly TextBox _regex, _suffix, _dump, _match;
    private readonly ComboBox _type;
    private readonly NumericUpDown _max;
    private readonly CheckBox _unique;
    private readonly CheckBox[] _libs;
    private static readonly (string Label, Action<HidMatcher, bool> Set, Func<HidMatcher, bool> Get)[] LibDefs =
    {
        ("HidSharp", (m, v) => m.UseHidSharp = v, m => m.UseHidSharp),
        ("DS4", (m, v) => m.UseDs4Lib = v, m => m.UseDs4Lib),
        ("Bluetooth", (m, v) => m.UseBt = v, m => m.UseBt),
        ("XInput", (m, v) => m.UseXInput = v, m => m.UseXInput),
        ("DInput", (m, v) => m.UseDInput = v, m => m.UseDInput),
        ("SDL", (m, v) => m.UseSdl = v, m => m.UseSdl),
        ("SDL-noRI", (m, v) => m.UseSdlNoRI = v, m => m.UseSdlNoRI),
    };
    private readonly string _ds4LogPath;

    public HidMatcherDialog(HidMatcher m, float dpiS, string ds4LogPath)
    {
        _ds4LogPath = ds4LogPath;
        Result = m;
        int S(int px) => (int)Math.Round(px * dpiS);

        Text = "HID matcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = LiteBoxTheme.Bg;
        ClientSize = new Size(S(620), S(478));
        Font = new Font("Segoe UI", 9f);

        int y = S(10);
        Label Cap(string t, int x = 10)
        {
            var l = new Label
            {
                Text = t, AutoSize = true, Location = new Point(S(x), y + S(2)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            Controls.Add(l);
            return l;
        }
        TextBox Field(int x, int w, string value, bool multi = false, bool readOnly = false, int h = 23)
        {
            var t = new TextBox
            {
                Text = value, Location = new Point(S(x), y), Width = S(w),
                Multiline = multi, Height = S(h), ReadOnly = readOnly,
                ScrollBars = multi ? ScrollBars.Both : ScrollBars.None, WordWrap = false,
                BackColor = readOnly ? LiteBoxTheme.Bg : LiteBoxTheme.Panel2,
                ForeColor = readOnly ? LiteBoxTheme.SubFg : LiteBoxTheme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(t);
            return t;
        }

        Cap("Regex over each device line:"); y += S(20);
        _regex = Field(10, 600, m.RegexToMatch); y += S(30);

        Cap("Suffix appended after the bucket's prefix (\\1..\\9 = capture groups):"); y += S(20);
        _suffix = Field(10, 600, m.Suffix); y += S(30);

        Cap("Device type:");
        _type = new ComboBox
        {
            Location = new Point(S(90), y), Width = S(110), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        _type.Items.AddRange(new object[] { "controller", "lightgun", "wheel", "other" });
        _type.SelectedItem = _type.Items.Contains(m.DeviceType) ? m.DeviceType : "controller";
        Controls.Add(_type);

        var maxCap = new Label
        {
            Text = "Max matches (0 = all):", AutoSize = true, Location = new Point(S(216), y + S(2)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        Controls.Add(maxCap);
        _max = new NumericUpDown
        {
            Minimum = 0, Maximum = 999, Value = Math.Min(999, Math.Max(0, m.MaxMatch)),
            Location = new Point(S(352), y), Width = S(52),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(_max);
        _unique = new CheckBox
        {
            Text = "unique suffixes only", Checked = m.UniqueMatch, AutoSize = true,
            Location = new Point(S(420), y + S(2)), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        Controls.Add(_unique);
        y += S(30);

        Cap("Libraries this matcher reads:"); y += S(20);
        _libs = new CheckBox[LibDefs.Length];
        for (int i = 0; i < LibDefs.Length; i++)
        {
            _libs[i] = new CheckBox
            {
                Text = LibDefs[i].Label, Checked = LibDefs[i].Get(m), AutoSize = true,
                Location = new Point(S(10 + (i % 4) * 152), y + S((i / 4) * 24)),
                ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            };
            Controls.Add(_libs[i]);
        }
        y += S(50);

        // ── sandbox ──
        var scan = new Button
        {
            Text = "Scan devices (selected libraries)", Location = new Point(S(10), y), Size = new Size(S(240), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        scan.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        Controls.Add(scan);
        var rescan = new Button
        {
            Text = "Rescan (fresh)", Location = new Point(S(256), y), Size = new Size(S(120), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        rescan.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        Controls.Add(rescan);
        y += S(30);

        _dump = Field(10, 600, "(click Scan — each line below is what the regex sees)", multi: true, readOnly: true, h: 110);
        y += S(116);

        Cap("Matches (live):"); y += S(20);
        _match = Field(10, 600, "", multi: true, readOnly: true, h: 48);
        y += S(56);

        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK, Location = new Point(S(448), y), Size = new Size(S(78), S(27)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(S(532), y), Size = new Size(S(78), S(27)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;

        void DoScan(bool fresh)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                if (fresh) HidInfoCache.Clear();
                string data = Snapshot().LibData(_ds4LogPath);
                _dump.Text = data.Length == 0 ? "(no device line from the selected libraries)" : data;
            }
            catch (Exception ex) { _dump.Text = "Scan failed: " + ex.Message; }
            finally { Cursor = Cursors.Default; RecalcMatch(); }
        }
        scan.Click += (_, _) => DoScan(fresh: false);
        rescan.Click += (_, _) => DoScan(fresh: true);
        _regex.TextChanged += (_, _) => RecalcMatch();
        _suffix.TextChanged += (_, _) => RecalcMatch();
        _max.ValueChanged += (_, _) => RecalcMatch();
        _unique.CheckedChanged += (_, _) => RecalcMatch();

        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (!IsValidRegex(_regex.Text)) { MessageBox.Show(this, "Invalid regex."); e.Cancel = true; return; }
            Result = Snapshot();
        };
    }

    private HidMatcher Snapshot()
    {
        var m = new HidMatcher
        {
            RegexToMatch = _regex.Text,
            Suffix = _suffix.Text,
            DeviceType = _type.SelectedItem?.ToString() ?? "controller",
            MaxMatch = (int)_max.Value,
            UniqueMatch = _unique.Checked,
        };
        for (int i = 0; i < LibDefs.Length; i++) LibDefs[i].Set(m, _libs[i].Checked);
        return m;
    }

    /// <summary>Live test against the DUMP BOX content (whatever was scanned — never rescans).</summary>
    private void RecalcMatch()
    {
        if (!IsValidRegex(_regex.Text)) { _match.Text = "(invalid regex)"; return; }
        if (_dump.ReadOnly && (_dump.Text.StartsWith("(") || _dump.Text.StartsWith("Scan failed"))) { _match.Text = ""; return; }
        try
        {
            var m = Snapshot();
            var suffixes = new System.Collections.Generic.List<string>();
            int count = 0;
            foreach (var line in _dump.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line, m.RegexToMatch);
                if (!match.Success) continue;
                string suffixOut = m.Suffix;
                for (int i = 1; i <= match.Groups.Count; i++)
                    suffixOut = suffixOut.Replace($"\\{i}", match.Groups[i].Value);
                if (m.UniqueMatch) { if (!suffixes.Contains(suffixOut)) { suffixes.Add(suffixOut); count++; } }
                else { suffixes.Add(suffixOut); count++; }
                if (count == m.MaxMatch) break;
            }
            _match.Text = suffixes.Count == 0 ? "(no match)" : string.Join("\r\n", suffixes.Select(sx => "→ " + sx));
        }
        catch (Exception ex) { _match.Text = "(error: " + ex.Message + ")"; }
    }

    private static bool IsValidRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try { Regex.Match("", pattern); return true; }
        catch (ArgumentException) { return false; }
    }
}
