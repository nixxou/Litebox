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
    Choice,   // combobox over Choices
}

internal sealed class OptionItem
{
    public string Section;
    public string Label;
    public OptionKind Kind;
    public string? Help;
    public string[] Choices = Array.Empty<string>();   // Choice kind

    public Func<string> Get = () => "";
    public Action<string> Set = _ => { };
    public Action? ApplyLive;

    public OptionItem(string section, string label, OptionKind kind)
    { Section = section; Label = label; Kind = kind; }

    // Bool helpers (stored as "true"/"false").
    public static OptionItem Toggle(string section, string label, Func<bool> get, Action<bool> set, string? help = null, Action? applyLive = null)
        => new(section, label, OptionKind.Bool)
        {
            Help = help,
            Get = () => get() ? "true" : "false",
            Set = v => set(string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)),
            ApplyLive = applyLive,
        };

    public static OptionItem Text(string section, string label, Func<string> get, Action<string> set, string? help = null, Action? applyLive = null)
        => new(section, label, OptionKind.Text) { Help = help, Get = get, Set = set, ApplyLive = applyLive };

    public static OptionItem Choice(string section, string label, string[] choices, Func<string> get, Action<string> set, string? help = null, Action? applyLive = null)
        => new(section, label, OptionKind.Choice) { Choices = choices, Help = help, Get = get, Set = set, ApplyLive = applyLive };
}
