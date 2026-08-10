// ─────────────────────────────────────────────────────────────────────────────
// Flat export for the native ASI — LB\Core\litebox-parental.dat
// ─────────────────────────────────────────────────────────────────────────────
//
// The native ASI (litebox-parentalcontrol.asi) loads into LaunchBox.exe / BigBox.exe
// BEFORE .NET and cannot read LiteBox.ini's sectioned format, parental-lists.json, or
// the SQLite Options DB. So everything it needs is flattened here into one dumb
// key=value-per-line file, UTF-8 no BOM, that a C++ line reader parses trivially —
// the same contract ExtendDB used with ExtendDBParental.dat, LiteBox-owned.
//
// LiteBox is the SOLE writer; the file is a DERIVED artifact — delete it and the next
// config save / block-flag change recreates it. It is regenerated on:
//   • ParentalConfig.Save() (rules / scopes / mode changed),
//   • an Edit Game save that toggled the per-game "requires parental" flag,
//   • the install flow (WS6) and boot, so a fresh install has it before the ASI reads.
//
// Format:
//   # comment
//   Version=1
//   LaunchBoxEnabled=1        BigBoxEnabled=1        (cold-start gate — filter iff its scope is on)
//   Mode=Whitelist            (or Blacklist)         (rating rule semantics)
//   PinSet=1                  (a BigBox LockPin is present — the BigBox cold-start gate)
//   Rule=M*                   (repeated; wildcard rating patterns, * and ?)
//   BlockedId=<guid>          (repeated; per-game "requires parental" IDs)

#nullable enable

using System;
using System.IO;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

internal static class ParentalNativeExport
{
    internal const string FileName = "litebox-parental.dat";

    /// <summary>LB\Core\litebox-parental.dat, next to LaunchBox.exe / BigBox.exe where the ASI loads.
    /// Null when the LB root is unknown.</summary>
    private static string? TargetPath()
    {
        var root = LbApiHost.Host.Media.MediaResolver.LbRoot;
        if (string.IsNullOrEmpty(root)) return null;
        try { return Path.Combine(root!, "Core", FileName); } catch { return null; }
    }

    /// <summary>Regenerate the flat file from the current config + blocked-ID set. Best-effort;
    /// atomic (tmp + move) so the ASI never reads a half-written file.</summary>
    public static void Write()
    {
        var path = TargetPath();
        if (path == null) return;
        try
        {
            var cfg = ParentalConfig.Instance;
            var sb = new StringBuilder(512);
            sb.Append("# LiteBox parental control — generated, do not edit\r\n");
            sb.Append("Version=1\r\n");
            sb.Append("LaunchBoxEnabled=").Append(cfg.LaunchBoxEnabled ? '1' : '0').Append("\r\n");
            sb.Append("BigBoxEnabled=").Append(cfg.BigBoxEnabled ? '1' : '0').Append("\r\n");
            sb.Append("Mode=").Append(cfg.Mode == ParentalMode.Blacklist ? "Blacklist" : "Whitelist").Append("\r\n");
            sb.Append("PinSet=").Append(PinPresent() ? '1' : '0').Append("\r\n");
            foreach (var r in cfg.Rules)
                if (!string.IsNullOrWhiteSpace(r)) sb.Append("Rule=").Append(OneLine(r)).Append("\r\n");
            foreach (var id in ParentalGameFlag.AllBlockedIds())
                if (!string.IsNullOrWhiteSpace(id)) sb.Append("BlockedId=").Append(OneLine(id)).Append("\r\n");

            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            try { File.Move(tmp, path, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { } }
            Log("wrote " + path);
        }
        catch (Exception ex) { Log("write failed: " + ex.Message); }
    }

    private static bool PinPresent()
    {
        try { return Data.BigBoxPin.Available && Data.BigBoxPin.Current().Length > 0; }
        catch { return false; }
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
    private static void Log(string m) => LbLog.Info("parental", "native-export: " + m);
}
