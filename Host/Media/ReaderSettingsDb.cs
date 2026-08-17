// LaunchBox 14's Reader settings — READ AND WRITTEN IN LB'S OWN STORE, so LiteBox and LaunchBox
// always show and honour the same configuration (no LiteBox-side copy to drift).
//
// The store is a SQLite database in the Reader's plugin data directory. Both the path scheme and
// the schema were established against the real v14 install (LB's own resolvers invoked by
// reflection: LaunchBoxReaderSettingsPaths.SettingsDatabasePath and
// PluginInstallLocationResolver.GetDataDirectory(pluginId, "SystemSoftware")):
//
//     <LB>\System\Software\.data\<Reader PluginId>\reader-settings.db
//
// The PluginId is read from the Reader's own manifest.json (never hardcoded — a reinstall through
// the Plugin Manager keeps the id, but reading it costs nothing and cannot go stale).
//
//   • GlobalSettings — ONE row (Id=1): the Options → Reader page (provider, launch defaults,
//     theme/night mode/direction, fixed-layout defaults, EPUB defaults).
//   • InputBindings  — the Keyboard / Controller mapping pages. Rows are self-describing
//     (DisplayName / GroupName / Description / SortOrder / BindingContext), so LiteBox's editor is
//     GENERATED from the table instead of hardcoding LB's action list.
//
//     Defaults are rows `default:<device>:<context>:<action>:<mode>:<inputs>` (IsUserOverride=0).
//     A user mapping REPLACES a default group with rows keyed
//     `user:<device>:<context>:<action>:<n>` carrying OverrideOfBindingKey =
//     `<device>:<context>:<action>` (IsUserOverride=1) — one row per alternative input (n = 0,1,2…;
//     "Escape, X, V" is three rows), Input1..Input3 within a row being a CHORD (Select+Start).
//     Verified by diffing the DB before/after real edits made in LaunchBox's own options.
//
// Fail-soft by construction: pre-14 install, Reader not deployed, DB missing/locked/schema drift →
// every read returns null/empty and every write is a no-op, so callers need no version test.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Media;

/// <summary>One row of the Reader's GlobalSettings (LB's Options → Reader page).</summary>
internal sealed class ReaderGlobalSettings
{
    public string ReaderProvider = "LaunchBoxReader";   // LaunchBoxReader | DefaultApplication | ExternalReader
    public string ExternalReaderExecutablePath = "";
    public bool FullscreenByDefault = true;
    public bool ResumeByDefault = true;
    public bool ShowMenuOnOpenByDefault;
    public string Theme = "Dark";                        // Black | Dark | Light | Sepia
    public bool NightModeEnabled;
    public string ReadingDirection = "LeftToRight";      // LeftToRight | RightToLeft
    public string LayoutMode = "Spread";                 // SinglePage | Spread | Stacked
    public string FitMode = "Fit";                       // Fit | FitWidth | FitHeight | Free
    public string FixedLayoutMargin = "Medium";          // None | Small | Medium | Large
    public string StackedDirection = "Vertical";         // Vertical | Horizontal
    public string PageTurnMode = "Premium3D";            // Premium3D | Instant
    public string FlowMode = "Paginated";                // Paginated | Continuous
    public string EpubPageLayoutMode = "Auto";           // Auto | SinglePage | TwoPage
    public string BookFontFamily = "Serif";              // Serif | SansSerif | Monospace
    public int EpubTextScalePercent = 100;
    public int MarginLevel = 1;
    public int LineSpacingLevel = 2;
}

/// <summary>One InputBindings row (a single alternative input for an action).</summary>
internal sealed class ReaderBinding
{
    public long Id;
    public string BindingKey = "";
    public string OverrideOfBindingKey = "";
    public string DeviceKind = "";        // Keyboard | Controller
    public string BindingContext = "";    // Global | Reading | Overlay | EpubContinuous …
    public string Action = "";
    public string Input1 = "", Input2 = "", Input3 = "";   // Input1..3 = one chord
    public string ActivationMode = "Press";                 // Press | LongPress
    public int HoldDurationMs;
    public string DisplayName = "", GroupName = "", Description = "";
    public int SortOrder;
    public bool IsUserOverride, IsEnabled = true;

    /// <summary>`<device>:<context>:<action>` — the group a default belongs to and the value a user
    /// row's OverrideOfBindingKey carries. The unit the mapping UI edits.</summary>
    public string GroupKey => $"{DeviceKind.ToLowerInvariant()}:{BindingContext.ToLowerInvariant()}:{Action.ToLowerInvariant()}";

    /// <summary>Display form of this row's chord ("Select + Start"), "" when unbound.</summary>
    public string Chord
    {
        get
        {
            var parts = new List<string>(3);
            foreach (var i in new[] { Input1, Input2, Input3 }) if (!string.IsNullOrEmpty(i)) parts.Add(i);
            return string.Join(" + ", parts);
        }
    }
}

internal static class ReaderSettingsDb
{
    /// <summary>The Reader's settings DB, or null when this install has none (pre-14, Reader not
    /// deployed, unreadable manifest). Never throws.</summary>
    public static string? DatabasePath
    {
        get
        {
            try
            {
                string root = MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                string readerDir = Path.Combine(root, "System", "Software", "LaunchBox Reader");
                string? id = ManifestPluginId(readerDir);
                if (id == null) return null;
                string db = Path.Combine(root, "System", "Software", ".data", id, "reader-settings.db");
                return File.Exists(db) ? db : null;
            }
            catch { return null; }
        }
    }

    /// <summary>True when this install exposes the Reader's settings (⇒ show the Reader options).</summary>
    public static bool Available => DatabasePath != null;

    private static string? ManifestPluginId(string dir)
    {
        try
        {
            var f = Path.Combine(dir, "manifest.json");
            if (!File.Exists(f)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(f), "\"PluginId\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static SqliteConnection? Open(bool write)
    {
        var db = DatabasePath;
        if (db == null) return null;
        try
        {
            var c = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
            }.ToString());
            c.Open();
            return c;
        }
        catch (Exception ex) { Console.WriteLine("[reader-db] open failed: " + ex.Message); return null; }
    }

    // ── GlobalSettings ────────────────────────────────────────────────────

    /// <summary>The Reader's global settings, or null when unavailable.</summary>
    public static ReaderGlobalSettings? LoadGlobal()
    {
        using var c = Open(write: false);
        if (c == null) return null;
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "select * from GlobalSettings where Id = 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            string S(string n, string d) { try { int i = r.GetOrdinal(n); return r.IsDBNull(i) ? d : r.GetString(i); } catch { return d; } }
            int I(string n, int d) { try { int i = r.GetOrdinal(n); return r.IsDBNull(i) ? d : Convert.ToInt32(r.GetValue(i)); } catch { return d; } }
            bool B(string n, bool d) => I(n, d ? 1 : 0) != 0;
            return new ReaderGlobalSettings
            {
                ReaderProvider = S("ReaderProvider", "LaunchBoxReader"),
                ExternalReaderExecutablePath = S("ExternalReaderExecutablePath", ""),
                FullscreenByDefault = B("FullscreenByDefault", true),
                ResumeByDefault = B("ResumeByDefault", true),
                ShowMenuOnOpenByDefault = B("ShowMenuOnOpenByDefault", false),
                Theme = S("Theme", "Dark"),
                NightModeEnabled = B("NightModeEnabled", false),
                ReadingDirection = S("ReadingDirection", "LeftToRight"),
                LayoutMode = S("LayoutMode", "Spread"),
                FitMode = S("FitMode", "Fit"),
                FixedLayoutMargin = S("FixedLayoutMargin", "Medium"),
                StackedDirection = S("StackedDirection", "Vertical"),
                PageTurnMode = S("PageTurnMode", "Premium3D"),
                FlowMode = S("FlowMode", "Paginated"),
                EpubPageLayoutMode = S("EpubPageLayoutMode", "Auto"),
                BookFontFamily = S("BookFontFamily", "Serif"),
                EpubTextScalePercent = I("EpubTextScalePercent", 100),
                MarginLevel = I("MarginLevel", 1),
                LineSpacingLevel = I("LineSpacingLevel", 2),
            };
        }
        catch (Exception ex) { Console.WriteLine("[reader-db] load global failed: " + ex.Message); return null; }
    }

    /// <summary>Write the global settings back (UpdatedUtc stamped like LB does). False on failure.</summary>
    public static bool SaveGlobal(ReaderGlobalSettings s)
    {
        using var c = Open(write: true);
        if (c == null) return false;
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"update GlobalSettings set
                ReaderProvider=$provider, ExternalReaderExecutablePath=$extPath,
                FullscreenByDefault=$fs, ResumeByDefault=$resume, ShowMenuOnOpenByDefault=$menu,
                Theme=$theme, NightModeEnabled=$night, ReadingDirection=$dir, LayoutMode=$layout,
                FitMode=$fit, FixedLayoutMargin=$margin, StackedDirection=$stacked,
                PageTurnMode=$turn, FlowMode=$flow, EpubPageLayoutMode=$epubLayout,
                BookFontFamily=$font, EpubTextScalePercent=$scale, MarginLevel=$marginLvl,
                LineSpacingLevel=$lineLvl, UpdatedUtc=$now where Id = 1";
            void P(string n, object v) => cmd.Parameters.AddWithValue(n, v);
            P("$provider", s.ReaderProvider); P("$extPath", s.ExternalReaderExecutablePath);
            P("$fs", s.FullscreenByDefault ? 1 : 0); P("$resume", s.ResumeByDefault ? 1 : 0);
            P("$menu", s.ShowMenuOnOpenByDefault ? 1 : 0);
            P("$theme", s.Theme); P("$night", s.NightModeEnabled ? 1 : 0);
            P("$dir", s.ReadingDirection); P("$layout", s.LayoutMode); P("$fit", s.FitMode);
            P("$margin", s.FixedLayoutMargin); P("$stacked", s.StackedDirection);
            P("$turn", s.PageTurnMode); P("$flow", s.FlowMode); P("$epubLayout", s.EpubPageLayoutMode);
            P("$font", s.BookFontFamily); P("$scale", s.EpubTextScalePercent);
            P("$marginLvl", s.MarginLevel); P("$lineLvl", s.LineSpacingLevel);
            P("$now", DateTime.UtcNow.ToString("O"));
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex) { Console.WriteLine("[reader-db] save global failed: " + ex.Message); return false; }
    }

    // ── InputBindings ─────────────────────────────────────────────────────

    /// <summary>Every binding row for one device ("Keyboard" / "Controller"), file order.</summary>
    public static List<ReaderBinding> LoadBindings(string deviceKind)
    {
        var list = new List<ReaderBinding>();
        using var c = Open(write: false);
        if (c == null) return list;
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "select * from InputBindings where DeviceKind = $d order by SortOrder, Id";
            cmd.Parameters.AddWithValue("$d", deviceKind);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string S(string n) { try { int i = r.GetOrdinal(n); return r.IsDBNull(i) ? "" : r.GetString(i); } catch { return ""; } }
                int I(string n) { try { int i = r.GetOrdinal(n); return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i)); } catch { return 0; } }
                list.Add(new ReaderBinding
                {
                    Id = I("Id"), BindingKey = S("BindingKey"), OverrideOfBindingKey = S("OverrideOfBindingKey"),
                    DeviceKind = S("DeviceKind"), BindingContext = S("BindingContext"), Action = S("Action"),
                    Input1 = S("Input1"), Input2 = S("Input2"), Input3 = S("Input3"),
                    ActivationMode = S("ActivationMode"), HoldDurationMs = I("HoldDurationMs"),
                    DisplayName = S("DisplayName"), GroupName = S("GroupName"), Description = S("Description"),
                    SortOrder = I("SortOrder"), IsUserOverride = I("IsUserOverride") != 0, IsEnabled = I("IsEnabled") != 0,
                });
            }
        }
        catch (Exception ex) { Console.WriteLine("[reader-db] load bindings failed: " + ex.Message); }
        return list;
    }

    /// <summary>The bindings a mapping page shows: one entry per ACTION GROUP, carrying the rows in
    /// effect — the user's override rows when the group has any, else the shipped defaults.</summary>
    public static List<(string GroupKey, List<ReaderBinding> Rows)> EffectiveGroups(string deviceKind)
    {
        var all = LoadBindings(deviceKind);
        var byGroup = new Dictionary<string, (List<ReaderBinding> defaults, List<ReaderBinding> user)>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in all)
        {
            string key = b.IsUserOverride && b.OverrideOfBindingKey.Length > 0 ? b.OverrideOfBindingKey : b.GroupKey;
            if (!byGroup.TryGetValue(key, out var e)) byGroup[key] = e = (new(), new());
            (b.IsUserOverride ? e.user : e.defaults).Add(b);
        }
        var result = new List<(string, List<ReaderBinding>)>();
        foreach (var kv in byGroup)
            result.Add((kv.Key, kv.Value.user.Count > 0 ? kv.Value.user : kv.Value.defaults));
        result.Sort((a, b) =>
        {
            int sa = a.Item2.Count > 0 ? a.Item2[0].SortOrder : 0, sb = b.Item2.Count > 0 ? b.Item2[0].SortOrder : 0;
            return sa != sb ? sa.CompareTo(sb) : string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>Replace one action group's user mapping with <paramref name="chords"/> (each chord =
    /// up to 3 simultaneous inputs); an EMPTY list clears the group (LB's "Clear" — the group then
    /// has no binding at all, which is NOT the same as falling back to the default). Mirrors LB's
    /// own write shape: delete the group's user rows, insert `user:<group>:<n>` rows carrying
    /// OverrideOfBindingKey. <paramref name="template"/> supplies the descriptive columns.</summary>
    public static bool SetGroupBinding(string groupKey, ReaderBinding template,
                                       IReadOnlyList<(string I1, string I2, string I3, string Mode, int HoldMs)> chords)
    {
        using var c = Open(write: true);
        if (c == null) return false;
        try
        {
            using var tx = c.BeginTransaction();
            using (var del = c.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "delete from InputBindings where IsUserOverride = 1 and OverrideOfBindingKey = $g";
                del.Parameters.AddWithValue("$g", groupKey);
                del.ExecuteNonQuery();
            }
            for (int n = 0; n < chords.Count; n++)
            {
                var ch = chords[n];
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"insert into InputBindings
                    (BindingKey, OverrideOfBindingKey, DeviceKind, BindingContext, BindingGroupKey, Action,
                     Input1, Input2, Input3, ActivationMode, HoldDurationMs, DisplayName, GroupName,
                     Description, SortOrder, IsUserOverride, IsEnabled, CreatedUtc, UpdatedUtc)
                    values ($key, $ovr, $dev, $ctx, $grp, $act, $i1, $i2, $i3, $mode, $hold, $disp, $gname,
                            $desc, $sort, 1, 1, $now, $now)";
                void P(string p, object v) => ins.Parameters.AddWithValue(p, v);
                P("$key", $"user:{groupKey}:{n}"); P("$ovr", groupKey);
                P("$dev", template.DeviceKind); P("$ctx", template.BindingContext);
                P("$grp", template.GroupName); P("$act", template.Action);
                P("$i1", ch.I1 ?? ""); P("$i2", ch.I2 ?? ""); P("$i3", ch.I3 ?? "");
                P("$mode", string.IsNullOrEmpty(ch.Mode) ? "Press" : ch.Mode); P("$hold", ch.HoldMs);
                P("$disp", template.DisplayName); P("$gname", template.GroupName);
                P("$desc", template.Description); P("$sort", template.SortOrder);
                P("$now", DateTime.UtcNow.ToString("O"));
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[reader-db] set binding failed: " + ex.Message); return false; }
    }

    /// <summary>LB's "Reset All": drop every user override, so the shipped defaults apply again.</summary>
    public static bool ResetAllBindings(string deviceKind)
    {
        using var c = Open(write: true);
        if (c == null) return false;
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "delete from InputBindings where IsUserOverride = 1 and DeviceKind = $d";
            cmd.Parameters.AddWithValue("$d", deviceKind);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[reader-db] reset bindings failed: " + ex.Message); return false; }
    }
}
