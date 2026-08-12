// Standalone parental-control config editor (Tools → "Parental control: Settings…").
//
// A replica of the vanilla-relevant knobs, editing the shared Core\litebox-parental.dat directly (ParentalDat):
//   • Enable parental control
//   • Filter mode: Whitelist (show only matching ratings) / Blacklist (hide matching ratings)
//   • Rating rules (wildcard patterns * and ?) — Add / Remove
//   • Hidden platforms (while locked) — pick from the library's platforms, Add / Remove
//   • PIN — set / change / clear BigBox's parental PIN (one PIN everywhere)
//
// Save rewrites the .dat atomically, preserving the LiteBox-only knobs it doesn't show. Rating/hide changes
// take effect on the next LaunchBox launch (the native .bin reads the .dat at arm time) — noted in the UI.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LiteBoxParental
{
    internal sealed class ParentalConfigForm : Form
    {
        private readonly ParentalDat _dat;
        private readonly CheckBox _enabled;
        private readonly ComboBox _mode;
        private readonly CheckBox _hideUninstalled;
        private readonly Label _enableHint;
        private readonly ListBox _rules;
        private readonly TextBox _ruleInput;
        private readonly ListBox _hidden;
        private readonly ComboBox _platformPick;
        private readonly Label _pinStatus;

        public ParentalConfigForm()
        {
            _dat = ParentalDat.Load();

            Text = "Parental control settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(520, 560);
            Font = new Font("Segoe UI", 9f);

            int x = 16, w = 488, y = 14;

            _enabled = new CheckBox { Text = "Enable parental control", AutoSize = true, Location = new Point(x, y), Checked = _dat.Enabled };
            Controls.Add(_enabled);
            _enableHint = new Label { AutoSize = true, ForeColor = Color.Firebrick, Location = new Point(x + 170, y + 1) };
            Controls.Add(_enableHint);
            y += 28;

            _hideUninstalled = new CheckBox { Text = "Hide not-installed games", AutoSize = true, Location = new Point(x, y), Checked = _dat.HideUninstalled };
            Controls.Add(_hideUninstalled);
            y += 32;

            Controls.Add(new Label { Text = "Filter mode:", AutoSize = true, Location = new Point(x, y + 4) });
            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x + 90, y), Width = 200 };
            _mode.Items.AddRange(new object[] { "Whitelist (show only matching ratings)", "Blacklist (hide matching ratings)" });
            _mode.SelectedIndex = _dat.Blacklist ? 1 : 0;
            Controls.Add(_mode);
            y += 36;

            // ── Rating rules ──
            Controls.Add(new Label { Text = "Rating rules (wildcards *, ?):", AutoSize = true, Location = new Point(x, y) });
            y += 22;
            _rules = new ListBox { Location = new Point(x, y), Size = new Size(w - 100, 96) };
            foreach (var r in _dat.Rules) _rules.Items.Add(r);
            Controls.Add(_rules);
            var ruleRemove = new Button { Text = "Remove", Location = new Point(x + w - 92, y), Width = 92 };
            ruleRemove.Click += (_, __) => { if (_rules.SelectedIndex >= 0) _rules.Items.RemoveAt(_rules.SelectedIndex); };
            Controls.Add(ruleRemove);
            y += 100;
            _ruleInput = new TextBox { Location = new Point(x, y), Width = w - 100 };
            Controls.Add(_ruleInput);
            var ruleAdd = new Button { Text = "Add", Location = new Point(x + w - 92, y - 1), Width = 92 };
            ruleAdd.Click += (_, __) => AddRule();
            Controls.Add(ruleAdd);
            y += 36;

            // ── Hidden platforms (locked) ──
            Controls.Add(new Label { Text = "Hidden platforms (while locked):", AutoSize = true, Location = new Point(x, y) });
            y += 22;
            _hidden = new ListBox { Location = new Point(x, y), Size = new Size(w - 100, 96) };
            foreach (var n in _dat.HideOn) _hidden.Items.Add(n);
            Controls.Add(_hidden);
            var hideRemove = new Button { Text = "Remove", Location = new Point(x + w - 92, y), Width = 92 };
            hideRemove.Click += (_, __) => { if (_hidden.SelectedIndex >= 0) _hidden.Items.RemoveAt(_hidden.SelectedIndex); };
            Controls.Add(hideRemove);
            y += 100;
            _platformPick = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Width = w - 100 };
            foreach (var p in AllPlatformNames()) _platformPick.Items.Add(p);
            if (_platformPick.Items.Count > 0) _platformPick.SelectedIndex = 0;
            Controls.Add(_platformPick);
            var hideAdd = new Button { Text = "Add", Location = new Point(x + w - 92, y - 1), Width = 92 };
            hideAdd.Click += (_, __) => AddHidden();
            Controls.Add(hideAdd);
            y += 36;

            // ── PIN ──
            _pinStatus = new Label { AutoSize = true, Location = new Point(x, y + 4) };
            Controls.Add(_pinStatus);
            var pinBtn = new Button { Text = "Set / Change PIN…", Location = new Point(x + 220, y), Width = 130 };
            pinBtn.Click += (_, __) => ChangePin();
            Controls.Add(pinBtn);
            var pinClear = new Button { Text = "Clear PIN", Location = new Point(x + 356, y), Width = 92 };
            pinClear.Click += (_, __) => ClearPin();
            Controls.Add(pinClear);
            RefreshPinStatus();
            y += 40;

            var note = new Label
            {
                Text = "Rating / platform changes apply on the next LaunchBox launch.",
                AutoSize = false, Location = new Point(x, y), Size = new Size(w, 18), ForeColor = Color.DimGray
            };
            Controls.Add(note);
            y += 26;

            var browse = new Button { Text = "Restricted games…", Location = new Point(x, y), Width = 150 };
            browse.Click += (_, __) => OpenBrowser();
            Controls.Add(browse);

            var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(x + w - 188, y), Width = 90 };
            save.Click += (_, __) => DoSave();
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(x + w - 90, y), Width = 90 };
            Controls.Add(save); Controls.Add(cancel);
            AcceptButton = save; CancelButton = cancel;
        }

        /// <summary>Open the per-game restriction browser. The current form settings are applied to the shared
        /// model IN MEMORY first (no disk write) so the browser's rating/platform chips reflect exactly what's
        /// configured here; the browser edits the SAME model and persists on its own manual toggles. Nothing is
        /// written to disk merely by opening the browser — the config form's Save (or a browser toggle) does that.</summary>
        private void OpenBrowser()
        {
            ApplyToDat();
            using (var b = new RestrictionBrowser(_dat)) b.ShowDialog(this);
        }

        private void AddRule()
        {
            var t = (_ruleInput.Text ?? "").Trim();
            if (t.Length == 0) return;
            if (!_rules.Items.Cast<object>().Any(o => string.Equals(o.ToString(), t, StringComparison.OrdinalIgnoreCase)))
                _rules.Items.Add(t);
            _ruleInput.Clear();
        }

        private void AddHidden()
        {
            var t = _platformPick.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(t)) return;
            if (!_hidden.Items.Cast<object>().Any(o => string.Equals(o.ToString(), t, StringComparison.OrdinalIgnoreCase)))
                _hidden.Items.Add(t);
        }

        private void RefreshPinStatus()
        {
            _pinStatus.Text = HasPinNow() ? "PIN: set" : "PIN: none";
            RefreshEnableGate();
        }

        // Parental can't be enabled without a PIN (no PIN → no unlock path → permanent lockout). Grey the Enable
        // box until a PIN exists, and force it off if the PIN was cleared.
        private void RefreshEnableGate()
        {
            bool hasPin = HasPinNow();
            _enabled.Enabled = hasPin;
            if (!hasPin) _enabled.Checked = false;
            _enableHint.Text = hasPin ? "" : "← set a PIN first";
        }

        // The PIN is BigBox's own <LockPin>: set/cleared through the live BigBoxSettings model (PinVerify.SetPin),
        // which LaunchBox persists to BigBoxSettings.xml itself. It applies IMMEDIATELY — no dependency on Save.
        private bool HasPinNow() => PinVerify.HasPin;

        private void ChangePin()
        {
            if (!PinVerify.CanSetPin) { Warn("Can't reach BigBox settings to store the PIN."); return; }
            using (var dlg = new PinSetDialog(HasPinNow()))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                if (HasPinNow() && !PinVerify.Verify(dlg.Current)) { Warn("Current PIN is wrong."); return; }
                if (dlg.NewPin != dlg.Confirm) { Warn("The new PIN and confirmation don't match."); return; }
                if (dlg.NewPin.Length < 4) { Warn("Use at least 4 digits."); return; }
                if (!PinVerify.SetPin(dlg.NewPin)) { Warn("Couldn't store the PIN in BigBox settings."); return; }
                RefreshPinStatus();
                Info("PIN set.");
            }
        }

        private void ClearPin()
        {
            if (!HasPinNow()) return;
            using (var dlg = new PinSetDialog(requireCurrent: true, clearMode: true))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                if (!PinVerify.Verify(dlg.Current)) { Warn("Current PIN is wrong."); return; }
                if (!PinVerify.SetPin("")) { Warn("Couldn't update BigBox settings."); return; }
                RefreshPinStatus();
                Info("PIN cleared.");
            }
        }

        /// <summary>Map the current form controls into the shared .dat model (without writing).</summary>
        private void ApplyToDat()
        {
            _dat.Enabled = _enabled.Checked;
            _dat.HideUninstalled = _hideUninstalled.Checked;
            _dat.Blacklist = _mode.SelectedIndex == 1;
            _dat.Rules.Clear();
            foreach (var o in _rules.Items) _dat.Rules.Add(o.ToString());
            _dat.HideOn.Clear();
            foreach (var o in _hidden.Items) _dat.HideOn.Add(o.ToString());
        }

        private void DoSave()
        {
            ApplyToDat();
            if (!_dat.Save()) { Warn("Couldn't write the config file (read-only, or the file is locked)."); return; }
            RestartNotice.Show(this);   // saved, but the .bin only re-filters at the next LaunchBox launch
        }

        private static List<string> AllPlatformNames()
        {
            var list = new List<string>();
            try
            {
                foreach (var p in PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>())
                {
                    try { var n = p?.Name; if (!string.IsNullOrWhiteSpace(n)) list.Add(n); } catch { }
                }
            }
            catch (Exception ex) { Log.Line("[ConfigForm] platforms: " + ex.Message); }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static void Info(string m) => MessageBox.Show(m, "Parental control", MessageBoxButtons.OK, MessageBoxIcon.Information);
        private static void Warn(string m) => MessageBox.Show(m, "Parental control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>PIN entry for set / change / clear. Shows a "current PIN" field when one already exists, and a
    /// new+confirm pair unless in clear mode.</summary>
    internal sealed class PinSetDialog : Form
    {
        private readonly TextBox _cur, _new, _conf;
        public string Current => _cur?.Text.Trim() ?? "";
        public string NewPin => _new?.Text.Trim() ?? "";
        public string Confirm => _conf?.Text.Trim() ?? "";

        public PinSetDialog(bool requireCurrent, bool clearMode = false)
        {
            Text = clearMode ? "Clear parental PIN" : "Set parental PIN";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            int y = 16;
            var rows = new List<Control>();
            if (requireCurrent)
            {
                rows.Add(new Label { Text = "Current PIN:", AutoSize = true, Location = new Point(16, y + 3) });
                _cur = Digits(new Point(130, y)); Controls.Add(_cur); y += 34;
            }
            if (!clearMode)
            {
                rows.Add(new Label { Text = "New PIN:", AutoSize = true, Location = new Point(16, y + 3) });
                _new = Digits(new Point(130, y)); Controls.Add(_new); y += 34;
                rows.Add(new Label { Text = "Confirm:", AutoSize = true, Location = new Point(16, y + 3) });
                _conf = Digits(new Point(130, y)); Controls.Add(_conf); y += 34;
            }
            foreach (var l in rows) Controls.Add(l);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(130, y + 4), Width = 84 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(222, y + 4), Width = 84 };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(330, y + 44);
        }

        private static TextBox Digits(Point at)
        {
            var t = new TextBox { UseSystemPasswordChar = true, MaxLength = 8, Location = at, Width = 170 };
            t.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            return t;
        }
    }
}
