// Game Progress model — LaunchBox's "Game Progress Organization" + "Game Progress Automation"
// settings, all stored in Data\Settings.xml (RE'd against LB 13.28):
//   • ProgressPriorities           the ORGANIZATION: ordered, comma-separated "Category / Value" list
//                                  ("Not Started / Unplayed,…"). Absent → LB's factory list.
//   • EnableAutoProgressTracking   automation master switch
//   • AutoProgressMinPlaytime      minutes threshold; AutoProgressPausePeriod: inactivity days
//   • AutoProgress*Value           per-rule target values; BLANK = that rule is skipped (LB's
//                                  "leave blank to skip auto-setting")
//   • AutoProgressIncludedValues   semicolon-separated MANUAL values automation may still overwrite
// The organization feeds the Edit Game "Progress" combo (MetadataChoicesCache) and the automation
// engine (ProgressAutomation); both option pages (Host/Options/ProgressOptions) edit these fields.

#nullable enable

using Unbroken.LaunchBox.Plugins;

namespace LbApiHost.Host.Data;

internal static class ProgressModel
{
    /// <summary>LB's factory organization — the "Revert to Default" list.</summary>
    public const string DefaultPriorities =
        "Not Started / Unplayed,Not Started / Want to Play,Not Started / Won't Play,"
      + "Active / In Progress,Active / Continuous,Active / Paused,"
      + "Done / Beaten,Done / Completed,Done / Mastered,Done / Dropped";

    /// <summary>The live LB-settings store (lazy singleton on the host data manager), or null.</summary>
    public static LbSettingsStore? Store => (PluginHelper.DataManager as HostDataManagerXml)?.LbSettings;

    /// <summary>The organized "Category / Value" list, in organization order.</summary>
    public static List<string> Values(LbSettingsStore? s)
    {
        string raw = s?.Get("ProgressPriorities") is { Length: > 0 } v ? v : DefaultPriorities;
        return raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    public static void SetValues(LbSettingsStore s, IEnumerable<string> values)
        => s.Set("ProgressPriorities", string.Join(",", values.Select(v => v.Trim()).Where(v => v.Length > 0)));

    /// <summary>Splits "Category / Value" → (category, value). No separator → ("", whole).</summary>
    public static (string category, string value) Split(string entry)
    {
        int i = entry.IndexOf(" / ", StringComparison.Ordinal);
        return i < 0 ? ("", entry) : (entry.Substring(0, i), entry.Substring(i + 3));
    }
}
