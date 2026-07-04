// Shared dark palette for every LiteBox window. Previously each Form duplicated its own copy of
// these colors; one definition here so a palette tweak doesn't need to be hunted down per-file.
//
// The colors are now MUTABLE + config-backed so the Options → Colors editor can override them.
// LiteBoxTheme.Load(cfg) MUST run before any window reads the palette (HostBoot calls it just before
// `new MainWindow`) because consumers copy these values into their own static fields at type init.
// Changes therefore take effect on the next launch for already-built windows.

#nullable enable

using LbApiHost.Host;

namespace LbApiHost.Host.UiKit;

internal static class LiteBoxTheme
{
    // Live palette (mutable — the Colors editor overrides these). The literals here are the DEFAULTS,
    // captured into each Swatch.Default below (field initializers run top-to-bottom, so these are set
    // before the Swatches array reads them).
    public static Color Bg        = Color.FromArgb(30, 30, 30);
    public static Color PanelC    = Color.FromArgb(37, 37, 38);
    public static Color Panel2    = Color.FromArgb(45, 45, 48);
    public static Color Fg        = Color.FromArgb(222, 222, 222);
    public static Color SubFg     = Color.FromArgb(150, 150, 152);
    public static Color Accent    = Color.FromArgb(0, 122, 204);
    public static Color Ok        = Color.FromArgb(50, 110, 65);
    public static Color CancelBtn = Color.FromArgb(60, 60, 75);
    public static Color Danger    = Color.FromArgb(225, 95, 95);

    /// <summary>One editable palette entry: a stable INI key, a display name, its factory default, and
    /// live get/set onto the field above.</summary>
    public sealed class Swatch
    {
        public string Key = "";
        public string Name = "";
        public Color Default;
        public Func<Color> Get = null!;
        public Action<Color> Set = null!;
    }

    // The editable set (Options → Colors), in display order.
    public static readonly Swatch[] Swatches =
    {
        new() { Key = "Bg",        Name = "Window background",   Default = Bg,        Get = () => Bg,        Set = c => Bg = c },
        new() { Key = "PanelC",    Name = "Panel / header",      Default = PanelC,    Get = () => PanelC,    Set = c => PanelC = c },
        new() { Key = "Panel2",    Name = "Field / input",       Default = Panel2,    Get = () => Panel2,    Set = c => Panel2 = c },
        new() { Key = "Fg",        Name = "Text",                Default = Fg,        Get = () => Fg,        Set = c => Fg = c },
        new() { Key = "SubFg",     Name = "Secondary text",      Default = SubFg,     Get = () => SubFg,     Set = c => SubFg = c },
        new() { Key = "Accent",    Name = "Accent / selection",  Default = Accent,    Get = () => Accent,    Set = c => Accent = c },
        new() { Key = "Ok",        Name = "OK / confirm button", Default = Ok,        Get = () => Ok,        Set = c => Ok = c },
        new() { Key = "CancelBtn", Name = "Cancel button",       Default = CancelBtn, Get = () => CancelBtn, Set = c => CancelBtn = c },
        new() { Key = "Danger",    Name = "Danger / delete",     Default = Danger,    Get = () => Danger,    Set = c => Danger = c },
    };

    /// <summary>Apply saved overrides (INI Color.&lt;key&gt; = #RRGGBB). Call BEFORE any window builds.</summary>
    public static void Load(LiteBoxConfig? cfg)
    {
        if (cfg == null) return;
        foreach (var s in Swatches)
        {
            var v = cfg.Get("Color." + s.Key);
            if (!string.IsNullOrWhiteSpace(v) && TryParseHex(v, out var c)) s.Set(c);
        }
    }

    /// <summary>Persist the palette: a color equal to its default is written empty (so it stays default
    /// and a previously-overridden-then-reset color is cleared).</summary>
    public static void Save(LiteBoxConfig? cfg)
    {
        if (cfg == null) return;
        foreach (var s in Swatches)
            cfg.Set("Color." + s.Key, s.Get() == s.Default ? "" : ToHex(s.Get()));
        cfg.Save();
    }

    public static void ResetAll() { foreach (var s in Swatches) s.Set(s.Default); }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParseHex(string? s, out Color c)
    {
        c = default;
        s = s?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(s) || s.Length != 6) return false;
        if (int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            c = Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }
        return false;
    }

    /// <summary>Current-monitor scale factor (2.25 at 225%) for a Control that isn't a top-level Form
    /// and so can't derive from LiteBoxForm (a ListView, a Panel, ...). One shared definition instead
    /// of each control hand-rolling its own "DeviceDpi / 96f".</summary>
    public static float DpiScale(Control c) => c.DeviceDpi / 96f;
}
