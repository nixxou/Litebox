// Shared dark palette for every LiteBox window. Previously each Form duplicated its own
// copy of these colors (OptionsWindow had one, others had their own near-duplicates) -
// one definition here so a palette tweak doesn't need to be hunted down per-file.

#nullable enable

namespace LbApiHost.Host.UiKit;

internal static class LiteBoxTheme
{
    public static readonly Color Bg = Color.FromArgb(30, 30, 30);
    public static readonly Color PanelC = Color.FromArgb(37, 37, 38);
    public static readonly Color Panel2 = Color.FromArgb(45, 45, 48);
    public static readonly Color Fg = Color.FromArgb(222, 222, 222);
    public static readonly Color SubFg = Color.FromArgb(150, 150, 152);
    public static readonly Color Accent = Color.FromArgb(0, 122, 204);
    public static readonly Color Ok = Color.FromArgb(50, 110, 65);
    public static readonly Color CancelBtn = Color.FromArgb(60, 60, 75);
    public static readonly Color Danger = Color.FromArgb(225, 95, 95);

    /// <summary>Current-monitor scale factor (2.25 at 225%) for a Control that isn't a top-level
    /// Form and so can't derive from LiteBoxForm (a ListView, a Panel, ...). Every such control in
    /// this codebase was hand-rolling its own "DeviceDpi / 96f" - same value, same formula,
    /// computed independently in half a dozen places. One shared definition here instead.</summary>
    public static float DpiScale(Control c) => c.DeviceDpi / 96f;
}
