// Options → "LB · Reader": LaunchBox 14's Reader configuration, edited from LiteBox.
//
// Everything here reads and writes LB'S OWN store (Media/ReaderSettingsDb — the Reader's SQLite
// database under System\Software\.data\<PluginId>), so a change made here is the change LaunchBox,
// Big Box and the Reader itself see, and vice versa. Nothing is duplicated on the LiteBox side.
//
// The section only exists when that store does (LB 14+ with the Reader deployed) — on an older
// LaunchBox LiteBox opens documents with the default program and the section stays hidden.
//
// Three tabs, mirroring LB's own pages:
//   • Reader              — provider + launch/global/fixed-layout/EPUB defaults (plain OptionItems).
//   • Keyboard Mappings   — GENERATED from the InputBindings table (LB's own DisplayName /
//   • Controller Mappings   GroupName / Description / SortOrder), so LiteBox never hardcodes the
//                           action list and picks up whatever a Reader update adds.
//
// Mapping semantics follow LB's: a capture APPENDS an alternative to the action (its list reads
// "Escape, X, V"), Clear empties it, Reset All drops every user override so the shipped defaults
// apply again. Press-and-release records a Press mapping; keeping the input held records a Hold
// (LongPress) mapping with the measured duration.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Media;


namespace LbApiHost.Host.Options;

internal static class ReaderOptions
{
    /// <summary>Adds the Reader section when this install exposes the Reader's settings store.</summary>
    public static void AddSections(OptionsWindow w, bool readOnly, float dpiS)
    {
        if (!ReaderSettingsDb.Available) return;
        var g = ReaderSettingsDb.LoadGlobal();
        if (g == null) return;

        var tabs = new List<(string, object, Action?)>
        {
            ("Reader", BuildReaderItems(g, readOnly), readOnly ? null : () => ReaderSettingsDb.SaveGlobal(g)),
            ("Keyboard Mappings", BuildMappingPanel("Keyboard", readOnly, dpiS), null),
            ("Controller Mappings", BuildMappingPanel("Controller", readOnly, dpiS), null),
        };
        w.AddTabbedSection("LB · Reader", tabs);
    }

    // ── Tab 1: the Reader page (same fields and wording as LB's own) ──────

    private static List<OptionItem> BuildReaderItems(ReaderGlobalSettings g, bool readOnly)
    {
        const string S = "reader";
        // A combo over an enum column: the user sees LB's wording, the DB keeps the enum value.
        // The mapping is done in the get/set pair (ChoiceValues is LbGlobalOptions-private).
        OptionItem Enum(string label, (string text, string val)[] choices, Func<string> get, Action<string> set, string? help = null)
            => OptionItem.Choice(S, label, choices.Select(c => c.text).ToArray(),
                   () => choices.FirstOrDefault(c => string.Equals(c.val, get(), StringComparison.OrdinalIgnoreCase)).text ?? choices[0].text,
                   v => set(choices.FirstOrDefault(c => c.text == v).val ?? choices[0].val), help);

        return new List<OptionItem>
        {
            Enum("Reader provider", new[]
                {
                    ("LaunchBox Reader", "LaunchBoxReader"),
                    ("Default application", "DefaultApplication"),
                    ("External reader", "ExternalReader"),
                },
                () => g.ReaderProvider, v => g.ReaderProvider = v,
                "Which viewer opens a game's manuals and documents. LiteBox follows this too — "
                + "\"Default application\" means the program Windows associates with the file type."),
            OptionItem.Text(S, "External reader executable",
                () => g.ExternalReaderExecutablePath, v => g.ExternalReaderExecutablePath = v,
                "Used only when the provider is \"External reader\". The document path is passed as its argument."),

            // Launch defaults — LB's note: these apply to documents opened from now on; a document
            // already opened keeps whatever was changed inside the reader for it.
            OptionItem.Toggle(S, "Open in fullscreen", () => g.FullscreenByDefault, v => g.FullscreenByDefault = v,
                "Defaults for NEW documents. Once a document has been opened, changes made inside the "
                + "reader are remembered for that document and these defaults no longer affect it."),
            OptionItem.Toggle(S, "Resume where left off", () => g.ResumeByDefault, v => g.ResumeByDefault = v),
            OptionItem.Toggle(S, "Show menu when document opens", () => g.ShowMenuOnOpenByDefault, v => g.ShowMenuOnOpenByDefault = v),

            // Global defaults
            Enum("Theme", new[] { ("Black", "Black"), ("Dark", "Dark"), ("Light", "Light"), ("Sepia", "Sepia") },
                () => g.Theme, v => g.Theme = v),
            OptionItem.Toggle(S, "Night mode", () => g.NightModeEnabled, v => g.NightModeEnabled = v),
            Enum("Reading direction", new[] { ("Left to Right", "LeftToRight"), ("Right to Left", "RightToLeft") },
                () => g.ReadingDirection, v => g.ReadingDirection = v),

            // Fixed-layout defaults (PDF / comics / images)
            Enum("Layout", new[] { ("Single Page", "SinglePage"), ("Two Page", "Spread"), ("Stacked", "Stacked") },
                () => g.LayoutMode, v => g.LayoutMode = v),
            Enum("Stacked direction", new[] { ("Vertical", "Vertical"), ("Horizontal", "Horizontal") },
                () => g.StackedDirection, v => g.StackedDirection = v),
            Enum("Fit mode", new[] { ("Best Fit", "Fit"), ("Fit Width", "FitWidth"), ("Fit Height", "FitHeight"), ("Free", "Free") },
                () => g.FitMode, v => g.FitMode = v),
            Enum("Page turn style", new[] { ("Premium 3D", "Premium3D"), ("Instant", "Instant") },
                () => g.PageTurnMode, v => g.PageTurnMode = v),
            Enum("Margin", new[] { ("None", "None"), ("Small", "Small"), ("Medium", "Medium"), ("Large", "Large") },
                () => g.FixedLayoutMargin, v => g.FixedLayoutMargin = v),

            // EPUB defaults (LB calls this group "EPUB Defaults (Advanced)")
            Enum("EPUB flow", new[] { ("Paginated", "Paginated"), ("Continuous", "Continuous") },
                () => g.FlowMode, v => g.FlowMode = v),
            Enum("EPUB page layout", new[] { ("Auto", "Auto"), ("Single Page", "SinglePage"), ("Two Page", "TwoPage") },
                () => g.EpubPageLayoutMode, v => g.EpubPageLayoutMode = v),
            Enum("EPUB font", new[] { ("Serif", "Serif"), ("Sans Serif", "SansSerif"), ("Monospace", "Monospace") },
                () => g.BookFontFamily, v => g.BookFontFamily = v),
            OptionItem.Number(S, "EPUB text scale (%)", () => g.EpubTextScalePercent, v => g.EpubTextScalePercent = v, 50, 300),
            OptionItem.Number(S, "EPUB margin level", () => g.MarginLevel, v => g.MarginLevel = v, 0, 4),
            OptionItem.Number(S, "EPUB line spacing level", () => g.LineSpacingLevel, v => g.LineSpacingLevel = v, 0, 4),
        };
    }

    // ── Tabs 2 & 3: mapping editors, generated from the bindings table ────

    private static Control BuildMappingPanel(string deviceKind, bool readOnly, float dpiS)
    {
        int Sc(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg };

        var head = new Panel { Dock = DockStyle.Top, Height = Sc(46), BackColor = ModulePanelKit.Bg };
        head.Controls.Add(new Label
        {
            Text = deviceKind == "Keyboard"
                ? "Select a mapping to change it. Press and release for a normal mapping, or keep holding to create a Hold mapping."
                : "Select a mapping to change it. Press and release a button (or several at once) — keep holding for a Hold mapping.",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = ModulePanelKit.Sub,
            Padding = new Padding(Sc(4), Sc(6), Sc(150), 0), Font = new Font("Segoe UI", 9f),
        });
        var resetAll = ModulePanelKit.Button("Reset All", dpiS, readOnly);
        resetAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        resetAll.Location = new Point(head.Width - Sc(140), Sc(6));
        head.Resize += (_, _) => resetAll.Location = new Point(head.ClientSize.Width - Sc(140), Sc(6));
        head.Controls.Add(resetAll);
        resetAll.BringToFront();

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = ModulePanelKit.Bg, Padding = new Padding(0, Sc(4), 0, 0),
        };
        root.Controls.Add(flow);
        root.Controls.Add(head);

        void Rebuild()
        {
            flow.SuspendLayout();
            foreach (Control c in flow.Controls.Cast<Control>().ToList()) { flow.Controls.Remove(c); c.Dispose(); }
            string? lastGroup = null;
            foreach (var (groupKey, rows) in ReaderSettingsDb.EffectiveGroups(deviceKind))
            {
                var first = rows[0];
                if (!string.Equals(lastGroup, first.GroupName, StringComparison.Ordinal))
                {
                    lastGroup = first.GroupName;
                    flow.Controls.Add(new Label
                    {
                        Text = first.GroupName, AutoSize = true, ForeColor = ModulePanelKit.Fg,
                        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(Sc(4), Sc(12), 0, Sc(4)),
                    });
                }
                flow.Controls.Add(BuildRow(deviceKind, groupKey, rows, readOnly, dpiS, Rebuild));
            }
            flow.ResumeLayout();
        }

        resetAll.Click += (_, _) =>
        {
            if (readOnly) return;
            if (MessageBox.Show(root, $"Reset every {deviceKind.ToLowerInvariant()} mapping to the LaunchBox Reader defaults?",
                    "Reset All", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ReaderSettingsDb.ResetAllBindings(deviceKind);
            Rebuild();
        };

        Rebuild();
        return root;
    }

    /// <summary>One action row: label + description on the left, the current chords in a capture box,
    /// and Clear. Edits write straight through to the Reader's DB (LB's pages behave the same — there
    /// is no separate Apply for mappings).</summary>
    private static Control BuildRow(string deviceKind, string groupKey, List<ReaderBinding> rows,
                                    bool readOnly, float dpiS, Action rebuild)
    {
        int Sc(int px) => ModulePanelKit.Sc(dpiS, px);
        var first = rows[0];
        var row = new Panel { Width = Sc(880), Height = Sc(54), BackColor = ModulePanelKit.Bg, Margin = new Padding(Sc(4), 0, 0, Sc(2)) };

        row.Controls.Add(new Label
        {
            Text = first.DisplayName, AutoSize = false, Size = new Size(Sc(210), Sc(20)), Location = new Point(0, Sc(4)),
            ForeColor = ModulePanelKit.Fg, Font = new Font("Segoe UI", 9.5f),
        });
        row.Controls.Add(new Label
        {
            Text = first.BindingContext, AutoSize = false, Size = new Size(Sc(210), Sc(16)), Location = new Point(0, Sc(24)),
            ForeColor = ModulePanelKit.Sub, Font = new Font("Segoe UI", 8f),
        });

        var box = new BindingCaptureBox(deviceKind, rows, dpiS)
        {
            Location = new Point(Sc(220), Sc(4)), Width = Sc(520), ReadOnly = true,
            Enabled = !readOnly,
        };
        row.Controls.Add(box);

        if (!string.IsNullOrEmpty(first.Description))
            row.Controls.Add(new Label
            {
                Text = first.Description, AutoSize = false, Size = new Size(Sc(520), Sc(18)),
                Location = new Point(Sc(220), Sc(32)), ForeColor = ModulePanelKit.Sub, Font = new Font("Segoe UI", 8f),
            });

        var clear = ModulePanelKit.Button("Clear", dpiS, readOnly);
        clear.Size = new Size(Sc(90), Sc(26));
        clear.Location = new Point(Sc(752), Sc(4));
        clear.Click += (_, _) =>
        {
            ReaderSettingsDb.SetGroupBinding(groupKey, first, Array.Empty<(string, string, string, string, int)>());
            rebuild();
        };
        row.Controls.Add(clear);

        // A capture APPENDS an alternative (LB's own behaviour: its lists read "Escape, X, V").
        box.Captured += chord =>
        {
            var chords = rows.Select(r => (r.Input1, r.Input2, r.Input3, r.ActivationMode, r.HoldDurationMs)).ToList();
            chords.Add(chord);
            ReaderSettingsDb.SetGroupBinding(groupKey, first, chords);
            rebuild();
        };
        return row;
    }

    // ── The capture control ──────────────────────────────────────────────

    /// <summary>Shows an action's current inputs ("Escape, X, V") and captures a new one on click:
    /// every key/button held together forms ONE chord (up to 3, LB's column count); releasing within
    /// the hold threshold records a Press mapping, keeping it held records a Hold mapping carrying
    /// the measured duration. Escape (keyboard) / B (controller) cancels.</summary>
    private sealed class BindingCaptureBox : TextBox
    {
        private const int HoldMs = 600;   // beyond this the mapping is recorded as a Hold

        public event Action<(string I1, string I2, string I3, string Mode, int HoldMs)>? Captured;

        private readonly string _device;
        private readonly List<ReaderBinding> _rows;
        private bool _capturing;
        private readonly List<string> _held = new();
        private DateTime _firstDown;
        private System.Windows.Forms.Timer? _padTimer;
        private Pause.XInputPad? _pad;

        public BindingCaptureBox(string device, List<ReaderBinding> rows, float dpiS)
        {
            _device = device; _rows = rows;
            BackColor = ModulePanelKit.Field; ForeColor = ModulePanelKit.Fg;
            BorderStyle = BorderStyle.FixedSingle; Font = new Font("Segoe UI", 9.5f);
            Text = Summary();
            Click += (_, _) => BeginCapture();
            Enter += (_, _) => BeginCapture();
            LostFocus += (_, _) => EndCapture(commit: false);
        }

        private string Summary()
        {
            if (_rows.Count == 0) return "";
            return string.Join(", ", _rows.Select(r => r.Chord + (r.ActivationMode == "LongPress" ? " (hold)" : "")));
        }

        private void BeginCapture()
        {
            if (_capturing || ReadOnly && !Enabled) return;
            _capturing = true;
            _held.Clear();
            _firstDown = default;
            Text = _device == "Keyboard" ? "Press a key…" : "Press a button…";
            BackColor = ModulePanelKit.Accent;
            if (_device == "Controller") StartPadPolling();
        }

        private void EndCapture(bool commit, (string, string, string, string, int)? chord = null)
        {
            if (!_capturing) return;
            _capturing = false;
            StopPadPolling();
            BackColor = ModulePanelKit.Field;
            Text = Summary();
            if (commit && chord != null) Captured?.Invoke(chord.Value);
        }

        // ── keyboard capture ──
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_capturing || _device != "Keyboard") return base.ProcessCmdKey(ref msg, keyData);
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Escape) { EndCapture(commit: false); return true; }
            if (_firstDown == default) _firstDown = DateTime.UtcNow;
            var names = new List<string>();
            if ((keyData & Keys.Control) != 0) names.Add("Ctrl");
            if ((keyData & Keys.Shift) != 0) names.Add("Shift");
            if ((keyData & Keys.Alt) != 0) names.Add("Alt");
            var main = KeyName(key);
            if (main != null && !names.Contains(main)) names.Add(main);
            if (names.Count == 0) return true;
            while (names.Count > 3) names.RemoveAt(names.Count - 1);
            int held = (int)(DateTime.UtcNow - _firstDown).TotalMilliseconds;
            EndCapture(commit: true, (names.ElementAtOrDefault(0) ?? "", names.ElementAtOrDefault(1) ?? "",
                                      names.ElementAtOrDefault(2) ?? "",
                                      held >= HoldMs ? "LongPress" : "Press", held >= HoldMs ? held : 0));
            return true;
        }

        /// <summary>WinForms Keys → the Reader's own input names (its stored vocabulary: Escape, Left,
        /// PageUp, OemMinus, Subtract, Add, Space, …). Null for keys with no stable name.</summary>
        private static string? KeyName(Keys k) => k switch
        {
            Keys.Escape => "Escape", Keys.Space => "Space", Keys.Enter => "Enter", Keys.Tab => "Tab",
            Keys.Back => "Backspace", Keys.Left => "Left", Keys.Right => "Right", Keys.Up => "Up", Keys.Down => "Down",
            Keys.Home => "Home", Keys.End => "End", Keys.PageUp => "PageUp", Keys.PageDown => "PageDown",
            Keys.OemMinus => "OemMinus", Keys.Oemplus => "OemPlus", Keys.Add => "Add", Keys.Subtract => "Subtract",
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "Shift",
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "Ctrl",
            Keys.Menu or Keys.LMenu or Keys.RMenu => "Alt",
            >= Keys.A and <= Keys.Z => k.ToString(),
            >= Keys.D0 and <= Keys.D9 => k.ToString().Substring(1),
            >= Keys.F1 and <= Keys.F12 => k.ToString(),
            _ => null,
        };

        // ── controller capture (XInput polling, like the pause screen) ──
        private void StartPadPolling()
        {
            _pad ??= new Pause.XInputPad();
            _padTimer ??= new System.Windows.Forms.Timer { Interval = 60 };
            _padTimer.Tick -= OnPadTick;
            _padTimer.Tick += OnPadTick;
            _padTimer.Start();
        }

        private void StopPadPolling() { try { _padTimer?.Stop(); } catch { } }

        private void OnPadTick(object? sender, EventArgs e)
        {
            if (!_capturing || _pad == null) return;
            var (_, pressed) = _pad.Poll();
            if (pressed == 0) return;
            if ((pressed & Pause.XInputPad.B) != 0 && _held.Count == 0) { EndCapture(commit: false); return; }
            foreach (var (mask, name) in PadNames)
                if ((pressed & mask) != 0 && !_held.Contains(name) && _held.Count < 3) _held.Add(name);
            if (_held.Count == 0) return;
            if (_firstDown == default) _firstDown = DateTime.UtcNow;
            int held = (int)(DateTime.UtcNow - _firstDown).TotalMilliseconds;
            EndCapture(commit: true, (_held.ElementAtOrDefault(0) ?? "", _held.ElementAtOrDefault(1) ?? "",
                                      _held.ElementAtOrDefault(2) ?? "",
                                      held >= HoldMs ? "LongPress" : "Press", held >= HoldMs ? held : 0));
        }

        /// <summary>XInput bit → the Reader's controller input names (its stored vocabulary, read off
        /// the bindings table: A/B/X/Y, Start, Select, Dpad*, Left/RightShoulder, …). Stick-button
        /// masks aren't exposed by XInputPad, so those two are captured through their raw bits.</summary>
        private const ushort LeftThumbMask = 0x0040, RightThumbMask = 0x0080;
        private static readonly (ushort mask, string name)[] PadNames =
        {
            (Pause.XInputPad.A, "A"), (Pause.XInputPad.B, "B"), (Pause.XInputPad.X, "X"), (Pause.XInputPad.Y, "Y"),
            (Pause.XInputPad.Start, "Start"), (Pause.XInputPad.Back, "Select"),
            (Pause.XInputPad.DPadUp, "DpadUp"), (Pause.XInputPad.DPadDown, "DpadDown"),
            (Pause.XInputPad.DPadLeft, "DpadLeft"), (Pause.XInputPad.DPadRight, "DpadRight"),
            (Pause.XInputPad.LBumper, "LeftShoulder"), (Pause.XInputPad.RBumper, "RightShoulder"),
            (LeftThumbMask, "LeftStickButton"), (RightThumbMask, "RightStickButton"),
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _padTimer?.Stop(); _padTimer?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
