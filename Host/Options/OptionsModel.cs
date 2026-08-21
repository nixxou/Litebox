// Option model for the settings windows. Each option DECLARES its UI (kind, label,
// help) and binds to its storage through plain delegates — the storage can be
// LiteBox.ini today and migrate to LaunchBox's Settings.xml / emulator / game
// fields later WITHOUT touching the window: only the binding changes.
//
//   new OptionItem("Pause", "Pause hotkey", OptionKind.Text)
//   {
//       Help = "Global hotkey opening the pause screen (e.g. Pause, Ctrl+F12).",
//       Get = () => cfg.Get("PauseHotkey", "Pause"),
//       Set = v => cfg.Set("PauseHotkey", v),
//   }
//
// ApplyLive (optional) runs after Set on Apply/OK for options that take effect
// immediately (aspect ratio, read-only toggle, cache enable, …).

#nullable enable

namespace LbApiHost.Host.Options;

internal enum OptionKind
{
    Bool,     // checkbox
    Text,     // single-line textbox
    Number,   // numeric-only spinner (NumericUpDown), clamped to [NumMin, NumMax]
    Choice,   // combobox over Choices
    Button,   // a plain button that runs OnClick (e.g. open a sub-dialog)
    Path,     // textbox + Browse… (file picker), for an executable / file setting
}

internal sealed class OptionItem
{
    public string Section;
    public string Label;
    public OptionKind Kind;
    public string? Help;
    public string[] Choices = Array.Empty<string>();   // Choice kind (display labels)
    /// <summary>Optional stored values parallel to <see cref="Choices"/> — when set,
    /// the combo shows Choices[i] but Get/Set speak ChoiceValues[i] (e.g. label
    /// "Windows Notifications" ↔ stored "1").</summary>
    public string[]? ChoiceValues;
    /// <summary>True for LaunchBox-only options LiteBox never reads: the window
    /// shows a red "No impact on LiteBox" note under the control (the value still
    /// round-trips to Settings.xml for LaunchBox's benefit).</summary>
    public bool NoImpact;

    /// <summary>Number kind: inclusive range + spinner step. Get/Set still speak strings (the numeric value
    /// formatted invariant).</summary>
    public int NumMin, NumMax = 100, NumStep = 1;

    /// <summary>Path kind: the OpenFileDialog filter for the Browse… picker (null = programs).</summary>
    public string? FileFilter;

    /// <summary>Bool kind only: a SECOND checkbox rendered on the same line, to the right — a qualifier of
    /// this one ("…with sound" → "…if no music is playing"). It is greyed while this one is unchecked, and
    /// its own Help is printed under the pair.</summary>
    public OptionItem? Companion;

    public Func<string> Get = () => "";
    public Action<string> Set = _ => { };
    public Action? ApplyLive;
    public Action? OnClick;   // Button kind: runs when clicked

    public OptionItem(string section, string label, OptionKind kind)
    { Section = section; Label = label; Kind = kind; }

    // Bool helpers (stored as "true"/"false").
    public static OptionItem Toggle(string section, string label, Func<bool> get, Action<bool> set, string? help = null,
                                    Action? applyLive = null, OptionItem? companion = null)
        => new(section, label, OptionKind.Bool)
        {
            Help = help,
            Get = () => get() ? "true" : "false",
            Set = v => set(string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)),
            ApplyLive = applyLive,
            Companion = companion,
        };

    public static OptionItem Text(string section, string label, Func<string> get, Action<string> set, string? help = null, Action? applyLive = null)
        => new(section, label, OptionKind.Text) { Help = help, Get = get, Set = set, ApplyLive = applyLive };

    /// <summary>A file path with a Browse… picker beside the box (the value stays freely typeable).
    /// <paramref name="filter"/> is an OpenFileDialog filter; the default offers programs.</summary>
    public static OptionItem PathPick(string section, string label, Func<string> get, Action<string> set,
                                      string? help = null, string? filter = null, Action? applyLive = null)
        => new(section, label, OptionKind.Path)
        { Help = help, Get = get, Set = set, ApplyLive = applyLive, FileFilter = filter };

    // Numeric spinner (digits only, clamped to [min, max]). Get/Set speak the integer as an invariant string.
    public static OptionItem Number(string section, string label, Func<int> get, Action<int> set,
                                    int min, int max, int step = 1, string? help = null, Action? applyLive = null)
        => new(section, label, OptionKind.Number)
        {
            Help = help, NumMin = min, NumMax = max, NumStep = step, ApplyLive = applyLive,
            Get = () => get().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Set = v => { if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) set(n); },
        };

    public static OptionItem Choice(string section, string label, string[] choices, Func<string> get, Action<string> set, string? help = null, Action? applyLive = null)
        => new(section, label, OptionKind.Choice) { Choices = choices, Help = help, Get = get, Set = set, ApplyLive = applyLive };

    // A plain action button (opens a sub-dialog, etc.). No storage binding.
    public static OptionItem Action(string section, string label, Action onClick, string? help = null)
        => new(section, label, OptionKind.Button) { OnClick = onClick, Help = help };
}
