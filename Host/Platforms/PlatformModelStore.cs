// Read/write a platform's 3D box <ModelSettings> block in Data\Platforms.xml. LaunchBox stores these as a
// ROOT-level <ModelSettings> element (sibling of <Platform>), keyed by <PlatformName> — NOT a child field of
// the platform. LiteBox's op-log write-back edits <Platform> nodes surgically (by Name) and leaves unknown root
// elements untouched, so a direct XDocument edit here is safe and survives a field flush. Field schema fully
// decoded (see memory reference-lb-3d-box-models): ModelType, ModelSizeString "W;H;D", CaseColor/CoverColor
// (ARGB int32), FullImageSpineWidth, UseFullScanImages/FullScanIsLandscape, FrontSpineImage/FrontSpineIsClear,
// DoubleSpineImageMode, LogoFont, SpineRotation/LogoRotation ("Left,Top,Right,Bottom" — value if drawn, empty if not).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins;
using LbApiHost.Host.Media;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Platforms;

internal static class PlatformModelStore
{
    private static string FilePath => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");

    /// <summary>The platform's persisted ModelSettings as a field→value map (element order lost, irrelevant),
    /// or null when the platform has no override.
    ///
    /// Answered from the LIVE platform object, not from the file: the write is journalled now, so the XML
    /// may not have caught up yet, and reading it would show the value the user just replaced. Falls back
    /// to the XML only when there is no catalogue to ask (the render probes, the self-tests).</summary>
    public static Dictionary<string, string>? Read(string platformName)
    {
        try
        {
            // Data.HostPlatform explicitly: LbApiHost.Host also declares a HostPlatform (the dummy
            // catalogue in HostServices), and the enclosing namespace beats the using import.
            var hp = PluginHelper.DataManager?.GetPlatformByName(platformName) as Data.HostPlatform;
            if (hp != null) return hp.ModelSettings;
        }
        catch { }
        return ReadFromXml(platformName);
    }

    private static Dictionary<string, string>? ReadFromXml(string platformName)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var doc = XDocument.Load(FilePath);
            var el = doc.Root?.Elements("ModelSettings")
                .FirstOrDefault(e => string.Equals((string?)e.Element("PlatformName"), platformName, StringComparison.OrdinalIgnoreCase));
            if (el == null) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in el.Elements()) map[c.Name.LocalName] = c.Value;
            return map;
        }
        catch { return null; }
    }

    /// <summary>Write (override ON) or remove (fields == null, override OFF) the platform's ModelSettings block.
    /// PlatformName is always stamped. Empty-valued fields are written as empty elements (LB keeps them).
    /// Guarded INTERNALLY (read-only / LB running) — the greyed UI is convenience, this is the mechanism.</summary>
    public static bool Write(string platformName, Dictionary<string, string>? fields)
    {
        try
        {
            var dm = PluginHelper.DataManager as HostDataManagerXml;
            var store = dm?.Store;
            if (store == null) return false;
            if (store.ReadOnly) { Console.WriteLine("[3dstore] refused: read-only"); return false; }

            // Build the row the way LaunchBox writes it — its own fields, then PlatformName, then an
            // empty GameId (that pair is what tells a platform block from a game one).
            var rows = new List<Dictionary<string, string>>();
            if (fields != null)
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in fields)
                    if (!string.Equals(kv.Key, "PlatformName", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(kv.Key, "GameId", StringComparison.OrdinalIgnoreCase))
                        row[kv.Key] = kv.Value ?? "";
                row["PlatformName"] = platformName;
                row["GameId"] = "";
                rows.Add(row);
            }
            // Journalled (lands on the next safe flush) + applied to the live platform NOW, which is
            // what Read answers from — so the 3D preview redraws with the new settings immediately
            // even while LaunchBox holds Platforms.xml. An empty row list removes the override.
            store.RecordKeyedReplace("ModelSettings", FilePath, "PlatformName", platformName,
                                     JsonSerializer.Serialize(rows));
            if (PluginHelper.DataManager?.GetPlatformByName(platformName) is Data.HostPlatform hp)
                hp.SetModelSettings(rows.Count > 0 ? rows[0] : null);
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[3dstore] platform write: " + ex.Message); return false; }
    }

    // ── per-GAME override (decoded empirically 2026-07-22, '88 Games test): same root-level <ModelSettings>
    // block and field schema, but in the game's platform file Data\Platforms\<Platform>.xml, keyed by <GameId>
    // (filled) with <PlatformName> empty — the exact mirror of the platform block. LB removes the block when
    // "Override Default Settings" is unchecked. Coexists with <GameSave> rows; the GameStore flush edits
    // <Game> nodes surgically and preserves unknown root elements (proven by the save-management feature).

    private static string GamePlatformFile(string platformName)
    {
        string name = platformName ?? "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms", name + ".xml");
    }

    /// <summary>The game's persisted ModelSettings (its own override), or null when the game has none.
    ///
    /// From the STORE, which has captured these blocks as generic per-game sub-entities since the
    /// beginning — the 3D code simply never used them and re-parsed the platform file instead. They are
    /// Tier-1, so a launched game does not free them under a running pass. Falls back to the XML when
    /// there is no store (probes, self-tests).</summary>
    public static Dictionary<string, string>? ReadGame(string platformName, string gameId)
    {
        try
        {
            var store = (PluginHelper.DataManager as HostDataManagerXml)?.Store;
            if (store != null && Guid.TryParse(gameId, out var gid))
            {
                var rows = store.GetSubEntities(gid, "ModelSettings");
                return rows.Count > 0 ? new Dictionary<string, string>(rows[0], StringComparer.OrdinalIgnoreCase) : null;
            }
        }
        catch { }
        return ReadGameFromXml(platformName, gameId);
    }

    private static Dictionary<string, string>? ReadGameFromXml(string platformName, string gameId)
    {
        try
        {
            string file = GamePlatformFile(platformName);
            if (!File.Exists(file) || string.IsNullOrEmpty(gameId)) return null;
            var el = XDocument.Load(file).Root?.Elements("ModelSettings")
                .FirstOrDefault(e => string.Equals((string?)e.Element("GameId"), gameId, StringComparison.OrdinalIgnoreCase));
            if (el == null) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in el.Elements()) map[c.Name.LocalName] = c.Value;
            return map;
        }
        catch { return null; }
    }

    /// <summary>ALL of one platform's per-game ModelSettings overrides in ONE parse (gameId → map).
    /// The per-pass bulk read for Model3dKeyIndex — calling ReadGame per game re-parsed the (multi-MB)
    /// platform XML thousands of times. Empty dict when the file has none.</summary>
    public static Dictionary<string, Dictionary<string, string>> ReadAllGameOverrides(string platformName)
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Straight off the store's per-platform index — no file parse at all, where this used to
            // re-read a multi-MB platform XML. Overrides are rare (one in the whole reference library),
            // so this walks the platform's rows and finds almost nothing, which is the honest cost.
            var store = (PluginHelper.DataManager as HostDataManagerXml)?.Store;
            if (store != null && store.ByPlatform.TryGetValue(platformName, out var idxs))
            {
                foreach (var i in idxs)
                {
                    if (i < 0 || i >= store.Count) continue;
                    var gid = store.Rows[i].Id;
                    var rows = store.GetSubEntities(gid, "ModelSettings");
                    if (rows.Count > 0) all[gid.ToString()] = new Dictionary<string, string>(rows[0], StringComparer.OrdinalIgnoreCase);
                }
                return all;
            }
        }
        catch { }
        try
        {
            string file = GamePlatformFile(platformName);
            if (!File.Exists(file)) return all;
            foreach (var el in XDocument.Load(file).Root?.Elements("ModelSettings") ?? Enumerable.Empty<XElement>())
            {
                string gid = (string?)el.Element("GameId") ?? "";
                if (gid.Length == 0) continue;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in el.Elements()) map[c.Name.LocalName] = c.Value;
                all[gid] = map;
            }
        }
        catch { }
        return all;
    }

    /// <summary>Write (override ON) or remove (fields == null) the game's ModelSettings block.
    /// Guarded internally, same as <see cref="Write"/>.</summary>
    public static bool WriteGame(string platformName, string gameId, Dictionary<string, string>? fields)
    {
        try
        {
            var store = (PluginHelper.DataManager as HostDataManagerXml)?.Store;
            if (store == null || !Guid.TryParse(gameId, out var gid)) return false;
            if (store.ReadOnly) { Console.WriteLine("[3dstore] refused: read-only"); return false; }

            var rows = new List<Dictionary<string, string>>();
            if (fields != null)
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in fields)
                    if (!string.Equals(kv.Key, "PlatformName", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(kv.Key, "GameId", StringComparison.OrdinalIgnoreCase))
                        row[kv.Key] = kv.Value ?? "";
                row["GameId"] = gameId;
                row["PlatformName"] = "";   // game-level → empty PlatformName (LB parity)
                rows.Add(row);
            }
            // The generic sub-entity path: updates memory (what ReadGame answers from) AND journals a
            // "replace" op the flush re-emits into the platform file. An empty list clears the override,
            // which is what LaunchBox does when "Override Default Settings" is unchecked.
            store.SetSubEntities(gid, "ModelSettings", rows);
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[3dstore] game write: " + ex.Message); return false; }
    }

    // ── ARGB int32 (signed) ↔ Color helpers, matching Color.ToArgb() (e.g. red = -65536) ──
    public static System.Drawing.Color? ParseArgb(string? s)
        => int.TryParse(s, out var v) ? System.Drawing.Color.FromArgb(v) : (System.Drawing.Color?)null;
    public static string ToArgb(System.Drawing.Color c) => c.ToArgb().ToString();

    // ── SpineRotation / LogoRotation CSV "Left,Top,Right,Bottom" (value if drawn, empty if not) ──
    public static (bool draw, int rot)[] ParseSides(string? csv)
    {
        var r = new (bool, int)[4];
        var parts = (csv ?? "").Split(',');
        for (int i = 0; i < 4; i++)
        {
            string p = i < parts.Length ? parts[i].Trim() : "";
            r[i] = p.Length > 0 && int.TryParse(p, out var deg) ? (true, deg) : (false, 0);
        }
        return r;
    }
    public static string BuildSides(IReadOnlyList<(bool draw, int rot)> sides)
        => string.Join(",", Enumerable.Range(0, 4).Select(i => i < sides.Count && sides[i].draw ? sides[i].rot.ToString() : ""));
}
