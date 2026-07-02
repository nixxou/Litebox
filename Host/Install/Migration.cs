// Boot-time config migration + upgrade detection. Runs once, early (before the config/db are used).
//   • reads the installed version from Core\litebox\LiteBox.ini ("Version=");
//   • wipes LiteBox.ini if that version is below ResetConfigBelow (a fresh template is written on next load);
//   • stamps the current version back into the ini (raw line edit → keeps the commented template intact);
//   • returns whether the ThirdParty natives need a refresh pass (installed < RefreshNativesBelow).
// The DB schema version lives in the DB itself (OpLog, PRAGMA user_version) — handled there, not here.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace LbApiHost.Host.Install;

internal static class Migration
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <summary>Applies the config reset rule, stamps the current version, and reports whether the
    /// native payload should be re-verified. Never throws.</summary>
    public static bool MigrateConfigAndNeedNatives()
    {
        try
        {
            string ini = LiteBoxPaths.File("LiteBox.ini");
            Version installed = ReadIniVersion(ini);
            var cur = LiteBoxVersion.Current;

            if (installed < LiteBoxVersion.ResetConfigBelow)
            {
                try { File.Delete(ini); } catch { }
                Console.WriteLine($"[migrate] config reset ({installed} < {LiteBoxVersion.ResetConfigBelow})");
            }

            bool needNatives = installed < LiteBoxVersion.RefreshNativesBelow;

            if (installed != cur)
            {
                LiteBoxConfig.LoadForExe();          // ensure the ini exists (writes the commented template if absent)
                StampIniVersion(ini, cur);           // add/update the Version= line, keeping the rest
                Console.WriteLine($"[migrate] version {installed} → {cur}");
            }
            return needNatives;
        }
        catch (Exception ex) { Console.WriteLine("[migrate] failed: " + ex.Message); return false; }
    }

    private static Version ReadIniVersion(string ini)
    {
        try
        {
            if (File.Exists(ini))
                foreach (var line in File.ReadAllLines(ini))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Version=", OIC) && Version.TryParse(t.Substring(8).Trim(), out var v))
                        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
                }
        }
        catch { }
        return new Version(0, 0, 0);
    }

    private static void StampIniVersion(string ini, Version v)
    {
        try
        {
            var lines = File.Exists(ini) ? new List<string>(File.ReadAllLines(ini)) : new List<string>();
            int idx = lines.FindIndex(l => l.TrimStart().StartsWith("Version=", OIC));
            if (idx >= 0) lines[idx] = "Version=" + v;
            else lines.Add("Version=" + v);
            File.WriteAllText(ini, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch { }
    }
}
