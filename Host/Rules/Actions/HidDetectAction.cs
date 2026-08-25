// HID device detector — BigBoxProfile's HidDeviceDetector, the flagship: at launch it enumerates
// what is ACTUALLY plugged in (through the same seven libraries emulators read — see Hid\*) and
// appends one argument per matched device, so the same emulator line self-adapts to the hardware of
// the moment. Four quota buckets (controller / lightgun / wheel / other), each with its own arg
// prefix ("--lightgun%NUM%=" + the matcher's suffix, %NUM% = 1-based per-bucket counter) and its
// own "marker only" flag — BBP's forceRemoveArg*: the generated argument stays VISIBLE to every
// downstream rule's probe (the branching bus) and is stripped in the pipeline's final pass.
// Matchers run in priority order; the first fills its bucket first; duplicate args are dropped but
// — faithfully — still consume their %NUM% (the original's counter incremented before the dedup).
//
// The real channel clears the device cache first (fresh hardware truth per launch, BBP's
// ClearCache); the EXAMPLE channel reuses the cache so the page preview scans once and stays
// responsive. Detection is read-only either way (one kept side effect: the DS4 Bluetooth wake — see
// Ds4Backend). Settings live in rule.HidData as one JSON blob.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using LbApiHost.Host.Rules.Hid;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

/// <summary>The detector's whole configuration — serialized as rule.HidData.</summary>
internal sealed class HidDetectSettings
{
    public int NumController { get; set; } = 4;
    public int NumLightgun { get; set; } = 2;
    public int NumWheel { get; set; } = 1;
    public int NumOther { get; set; } = 100;
    public string PrefixController { get; set; } = "--controller%NUM%=";
    public string PrefixLightgun { get; set; } = "--lightgun%NUM%=";
    public string PrefixWheel { get; set; } = "--wheel%NUM%=";
    public string PrefixOther { get; set; } = "";
    public bool ForceRemoveController { get; set; }
    public bool ForceRemoveLightgun { get; set; }
    public bool ForceRemoveWheel { get; set; }
    public bool ForceRemoveOther { get; set; }
    public string Ds4WinLogPath { get; set; } = "";
    public List<HidMatcher> Matchers { get; set; } = new();

    public static HidDetectSettings Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HidDetectSettings();
        try { return JsonSerializer.Deserialize<HidDetectSettings>(json) ?? new HidDetectSettings(); }
        catch { return new HidDetectSettings(); }
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public (int Cap, string Prefix, bool ForceRemove) Bucket(string deviceType) => deviceType switch
    {
        "lightgun" => (NumLightgun, PrefixLightgun, ForceRemoveLightgun),
        "wheel" => (NumWheel, PrefixWheel, ForceRemoveWheel),
        "other" => (NumOther, PrefixOther, ForceRemoveOther),
        _ => (NumController, PrefixController, ForceRemoveController),
    };
}

internal sealed class HidDetectAction : IRuleAction
{
    public string Type => LaunchRule.TypeHidDetect;
    public string AddLabel => "Add: HID device detector…";
    public string DialogTitle => "HID device detector rule";

    public bool IsConfigured(LaunchRule r) => HidDetectSettings.Parse(r.HidData).Matchers.Count > 0;

    public string Describe(LaunchRule r)
    {
        var s = HidDetectSettings.Parse(r.HidData);
        var types = s.Matchers.Select(m => m.DeviceType).Distinct().ToList();
        return $"HID device detector ({s.Matchers.Count} matcher{(s.Matchers.Count > 1 ? "s" : "")}"
             + (types.Count > 0 ? $": {string.Join(", ", types)})" : ")");
    }

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd)
    {
        HidInfoCache.Clear();   // fresh hardware truth for the real launch — BBP's ClearCache
        return Append(r, cmd);
    }

    /// <summary>Example channel: same detection, through the WARM cache — the preview scans devices
    /// once, then recalcs instantly. The result is honest (real devices), just not re-enumerated.</summary>
    public RuleCmd ApplyExample(LaunchRule r, RuleCmd cmd) => Append(r, cmd);

    private static RuleCmd Append(LaunchRule r, RuleCmd cmd)
    {
        var s = HidDetectSettings.Parse(r.HidData);
        var generated = Compute(s);
        if (generated.Count == 0) return cmd;
        var parts = RuleArgs.Split(cmd.Args).ToList();
        foreach (var (arg, forceRemove) in generated)
        {
            parts.Add(arg);
            if (forceRemove) RulePipeline.AddDynamicMarker(arg);
        }
        return cmd with { Args = RuleArgs.Join(parts) };
    }

    /// <summary>The original Modify's bucket walk, whole: matchers in priority order, per-bucket
    /// caps checked BEFORE evaluating (a full bucket skips the scan), %NUM% consumed even when the
    /// dedup drops a duplicate, final order controller → lightgun → wheel → other.</summary>
    internal static List<(string Arg, bool ForceRemove)> Compute(HidDetectSettings s)
    {
        var buckets = new Dictionary<string, List<string>>
        {
            ["controller"] = new(), ["lightgun"] = new(), ["wheel"] = new(), ["other"] = new(),
        };
        var counters = new Dictionary<string, int> { ["controller"] = 0, ["lightgun"] = 0, ["wheel"] = 0, ["other"] = 0 };

        foreach (var matcher in s.Matchers)
        {
            string type = buckets.ContainsKey(matcher.DeviceType) ? matcher.DeviceType : "controller";
            var (cap, prefix, _) = s.Bucket(type);
            if (counters[type] >= cap) continue;
            var result = matcher.Match(s.Ds4WinLogPath);
            if (result == null) continue;
            foreach (var suffix in result)
            {
                counters[type]++;
                string fullarg = (prefix + suffix).Replace("%NUM%", counters[type].ToString()).Trim();
                if (!buckets[type].Contains(fullarg)) buckets[type].Add(fullarg);
                if (counters[type] >= cap) break;
            }
        }

        var final = new List<(string, bool)>();
        foreach (var type in new[] { "controller", "lightgun", "wheel", "other" })
        {
            bool force = s.Bucket(type).ForceRemove;
            foreach (var a in buckets[type])
                if (!final.Any(f => f.Item1 == a)) final.Add((a, force));
        }
        return final;
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var s = HidDetectSettings.Parse(r.HidData);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(576) };
        int y = 0;

        Label Cap(string t, int extraLines = 0)
        {
            var l = new Label
            {
                Text = t, AutoSize = false, Location = new Point(0, y + S(2)),
                Size = new Size(S(574), S(16 + 14 * extraLines)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            body.Controls.Add(l);
            y += S(20 + 14 * extraLines);
            return l;
        }
        Button Btn(string t, int x, int yy, int w)
        {
            var b = new Button
            {
                Text = t, Location = new Point(x, yy), Size = new Size(w, S(25)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            body.Controls.Add(b);
            return b;
        }

        Cap("Scans the devices actually plugged in (same libraries emulators read: HidSharp, DS4, Bluetooth,"
            + " XInput, DirectInput, SDL, SDL-noRawInput) and appends one argument per matched device."
            + " Marker-only args stay visible to later rules, then are stripped before launch.", 2);

        // ── quotas / prefixes / marker flags, one row per bucket ──
        var rows = new (string Label, string Key)[]
            { ("Controllers", "controller"), ("Lightguns", "lightgun"), ("Wheels", "wheel"), ("Other", "other") };
        var nums = new Dictionary<string, NumericUpDown>();
        var prefixes = new Dictionary<string, TextBox>();
        var forces = new Dictionary<string, CheckBox>();
        foreach (var (label, key) in rows)
        {
            var (cap, prefix, force) = s.Bucket(key);
            body.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Location = new Point(0, y + S(4)),
                ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            });
            var num = new NumericUpDown
            {
                Minimum = 0, Maximum = 999, Value = Math.Min(999, Math.Max(0, cap)),
                Location = new Point(S(84), y), Width = S(52),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            var pfx = new TextBox
            {
                Text = prefix, Location = new Point(S(142), y), Width = S(288),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            var chk = new CheckBox
            {
                Text = "marker only", Checked = force, AutoSize = true,
                Location = new Point(S(438), y + S(2)), ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            body.Controls.Add(num); body.Controls.Add(pfx); body.Controls.Add(chk);
            nums[key] = num; prefixes[key] = pfx; forces[key] = chk;
            y += S(29);
        }
        y += S(2);

        Cap("DS4Windows log folder (optional — links XInput slots back to real DualShocks, signature \"DS4WIN\"):");
        var ds4Path = new TextBox
        {
            Text = s.Ds4WinLogPath, Location = new Point(0, y), Width = S(486),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(ds4Path);
        var browse = Btn("Browse…", S(492), y - S(1), S(82));
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) ds4Path.Text = dlg.SelectedPath;
        };
        y += S(30);

        // ── the matcher list, priority-ordered ──
        Cap("Matchers (priority order — the first fills its bucket first; double-click edits):");
        var matchers = s.Matchers.Select(Clone).ToList();
        var list = new ListBox
        {
            Location = new Point(0, y), Size = new Size(S(486), S(112)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
        };
        body.Controls.Add(list);
        void Refill(int select = -1)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var m in matchers) list.Items.Add(m.Describe());
            if (select >= 0 && select < list.Items.Count) list.SelectedIndex = select;
            list.EndUpdate();
        }
        Refill();

        int bx = S(492), bw = S(82);
        var addBtn = Btn("Add…", bx, y, bw);
        var editBtn = Btn("Edit…", bx, y + S(28), bw);
        var delBtn = Btn("Remove", bx, y + S(56), bw);
        var upBtn = Btn("Up", bx, y + S(84), S(39));
        var downBtn = Btn("Down", bx + S(43), y + S(84), S(39));
        y += S(118);

        void EditAt(int i)
        {
            using var dlg = new HidMatcherDialog(matchers[i], dpiS, ds4Path.Text);
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) { matchers[i] = dlg.Result; Refill(i); }
        }
        addBtn.Click += (_, _) =>
        {
            using var dlg = new HidMatcherDialog(new HidMatcher(), dpiS, ds4Path.Text);
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) { matchers.Add(dlg.Result); Refill(matchers.Count - 1); }
        };
        editBtn.Click += (_, _) => { if (list.SelectedIndex >= 0) EditAt(list.SelectedIndex); };
        list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) EditAt(list.SelectedIndex); };
        delBtn.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i >= 0) { matchers.RemoveAt(i); Refill(Math.Min(i, matchers.Count - 1)); }
        };
        upBtn.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i > 0) { (matchers[i - 1], matchers[i]) = (matchers[i], matchers[i - 1]); Refill(i - 1); }
        };
        downBtn.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i >= 0 && i < matchers.Count - 1) { (matchers[i + 1], matchers[i]) = (matchers[i], matchers[i + 1]); Refill(i + 1); }
        };

        // ── sandbox: run the whole detector against the real hardware, args shown, nothing launched ──
        var testBtn = Btn("Test: scan devices and show the args this rule would add", 0, y, S(574));
        y += S(30);
        var result = new TextBox
        {
            Location = new Point(0, y), Width = S(574), Multiline = true, ReadOnly = true,
            Height = S(64), ScrollBars = ScrollBars.Vertical,
            BackColor = LiteBoxTheme.Bg, ForeColor = LiteBoxTheme.SubFg, BorderStyle = BorderStyle.FixedSingle,
            Text = "(not scanned yet — devices are only enumerated when you click Test or at launch)",
        };
        body.Controls.Add(result);
        y += S(70);

        HidDetectSettings Snapshot() => new()
        {
            NumController = (int)nums["controller"].Value, NumLightgun = (int)nums["lightgun"].Value,
            NumWheel = (int)nums["wheel"].Value, NumOther = (int)nums["other"].Value,
            PrefixController = prefixes["controller"].Text, PrefixLightgun = prefixes["lightgun"].Text,
            PrefixWheel = prefixes["wheel"].Text, PrefixOther = prefixes["other"].Text,
            ForceRemoveController = forces["controller"].Checked, ForceRemoveLightgun = forces["lightgun"].Checked,
            ForceRemoveWheel = forces["wheel"].Checked, ForceRemoveOther = forces["other"].Checked,
            Ds4WinLogPath = ds4Path.Text.Trim(), Matchers = matchers.ToList(),
        };
        testBtn.Click += (_, _) =>
        {
            var form = body.FindForm();
            if (form != null) form.Cursor = Cursors.WaitCursor;
            try
            {
                HidInfoCache.Clear();
                var args = Compute(Snapshot());
                result.Text = args.Count == 0
                    ? "No argument generated (no matcher fired)."
                    : string.Join("\r\n", args.Select(a => a.Arg + (a.ForceRemove ? "   [marker only]" : "")));
            }
            catch (Exception ex) { result.Text = "Scan failed: " + ex.Message; }
            finally { if (form != null) form.Cursor = Cursors.Default; }
        };

        body.Height = y;
        return (body, y, () => r.HidData = Snapshot().Serialize());
    }

    private static HidMatcher Clone(HidMatcher m) => new()
    {
        RegexToMatch = m.RegexToMatch, Suffix = m.Suffix, DeviceType = m.DeviceType,
        UseHidSharp = m.UseHidSharp, UseDs4Lib = m.UseDs4Lib, UseBt = m.UseBt, UseXInput = m.UseXInput,
        UseDInput = m.UseDInput, UseSdl = m.UseSdl, UseSdlNoRI = m.UseSdlNoRI,
        MaxMatch = m.MaxMatch, UniqueMatch = m.UniqueMatch,
    };
}
