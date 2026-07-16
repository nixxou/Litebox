// LB-library-backed "what does the user own?" lookups for the database site's owned-only filter.
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Backend/OwnedLookup.cs. Reads through the in-process LaunchBox
// plugin SDK (PluginHelper.DataManager → LiteBox's own HostDataManagerXml); no reflection, no plugin. Each
// call goes straight to the in-memory library — the counts must reflect live edits, so nothing is cached.
// A game is "owned" whenever it exists in the library at all (hidden / broken included).

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Diag;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class OwnedLookup
{
    private const bool IncludeHidden = true;
    private const bool IncludeBroken = true;

    /// <summary>Game count the user has on <paramref name="platformName"/>; 0 when the platform is catalog-only.</summary>
    public static int GetCountForPlatform(string platformName)
    {
        if (string.IsNullOrEmpty(platformName)) return 0;
        var p = SafeGetPlatform(platformName);
        if (p == null) return 0;
        try { return p.GetGameCount(IncludeHidden, IncludeBroken); }
        catch (Exception ex) { Log($"GetGameCount({platformName}): {ex.Message}"); return 0; }
    }

    /// <summary>Bulk owned-count lookup: one GetAllPlatforms sweep, then per-name counts.</summary>
    public static Dictionary<string, int> GetCountsForPlatforms(IEnumerable<string> platformNames)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (platformNames == null) return result;

        IPlatform[] lbPlatforms;
        try { lbPlatforms = PluginHelper.DataManager.GetAllPlatforms(); }
        catch (Exception ex) { Log($"GetAllPlatforms: {ex.Message}"); return result; }
        if (lbPlatforms == null) return result;

        var byName = new Dictionary<string, IPlatform>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in lbPlatforms)
            if (p?.Name != null) byName[p.Name] = p;

        foreach (var name in platformNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!byName.TryGetValue(name, out var lbp)) continue;
            try { result[name] = lbp.GetGameCount(IncludeHidden, IncludeBroken); }
            catch (Exception ex) { Log($"GetGameCount({name}): {ex.Message}"); }
        }
        return result;
    }

    /// <summary>LaunchBoxDbId set for every owned game on <paramref name="platformName"/> (custom games skipped).</summary>
    public static HashSet<int> GetIdsForPlatform(string platformName)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrEmpty(platformName)) return result;
        var p = SafeGetPlatform(platformName);
        if (p == null) return result;

        try
        {
            var games = p.GetAllGames(IncludeHidden, IncludeBroken);
            if (games == null) return result;
            foreach (var g in games)
                if (g?.LaunchBoxDbId is int id && id > 0) result.Add(id);
        }
        catch (Exception ex) { Log($"GetAllGames({platformName}): {ex.Message}"); }
        return result;
    }

    /// <summary>The owned DatabaseIDs as a JSON array ("[1,2,3]" / "[]") for the games-query json_each filter.</summary>
    public static string GetJsonArrayForPlatform(string platformName)
    {
        var ids = GetIdsForPlatform(platformName);
        return ids.Count == 0 ? "[]" : "[" + string.Join(",", ids) + "]";
    }

    private static IPlatform SafeGetPlatform(string name)
    {
        try { return PluginHelper.DataManager.GetPlatformByName(name); }
        catch (Exception ex) { Log($"GetPlatformByName({name}): {ex.Message}"); return null; }
    }

    private static void Log(string msg) => LbLog.Info("web", "[OwnedLookup] " + msg);
}
