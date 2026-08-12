// ─────────────────────────────────────────────────────────────────────────────
// The shared parental config file — LB\Core\litebox-parental.dat (read + write).
// ─────────────────────────────────────────────────────────────────────────────
//
// This flat key=value file (UTF-8, no BOM, CRLF) is now the SINGLE SOURCE OF TRUTH for parental control,
// shared by THREE readers: the native .bin (loads before .NET, only a dumb C++ line reader), LiteBox
// (ParentalConfig + ParentalGameFlag read/write it here instead of LiteBox.ini / parental-lists.json /
// the SQLite Options DB), and the standalone plugin's config UI. Unknown keys are ignored by every reader,
// so the LiteBox-web-only knobs (AllowRatings/…) ride along without disturbing the .bin.
//
// Written atomically (tmp + File.Move) so no reader ever sees a half-written file.
//
// Format (repeated keys build lists):
//   # comment
//   Version=1
//   Enabled=1 / 0
//   Mode=Whitelist | Blacklist
//   PinSet=1 / 0                (derived: a BigBox <LockPin> is present — the .bin's BigBox cold-start gate)
//   AllowRatings / AllowFavorites / ForceWebHideAll = 1 / 0   (LiteBox web/desktop only; the .bin ignores)
//   WriteMode=Block | Merge     (LiteBox only)
//   HotKey=<int>                (LiteBox only)
//   ConfigVersion=<x.y.z>
//   Rule=<pattern>              (repeated; wildcard rating patterns * and ?)
//   HideName=<name>             (repeated; platforms hidden WHEN LOCKED — the .bin purges these)
//   HideNameOff=<name>          (repeated; hidden WHEN UNLOCKED — LiteBox/web only, the .bin ignores)
//   BlockedId=<guid>            (repeated; per-game "requires parental" IDs)

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

/// <summary>Parsed contents of litebox-parental.dat. Scalars belong to ParentalConfig; BlockedIds belong to
/// ParentalGameFlag — both read from the SAME parse.</summary>
internal sealed class ParentalDatData
{
    public bool Enabled;
    public ParentalMode Mode = ParentalMode.Whitelist;
    public bool HideUninstalled = true;   // DEFAULT ON — hide games with Installed=false while parental active
    public bool AllowRatings, AllowFavorites, ForceWebHideAll, BlockInstall;
    public ParentalWriteMode WriteMode = ParentalWriteMode.Block;
    public int HotKey;
    public string ConfigVersion = "0.0.0";
    public List<string> Rules = new();
    public List<string> HideOn = new();    // HideName= (locked)
    public List<string> HideOff = new();   // HideNameOff= (unlocked)
    public List<string> BlockedIds = new();
}

internal static class ParentalNativeExport
{
    internal const string FileName = "litebox-parental.dat";

    /// <summary>LB\Core\litebox-parental.dat. Null when the LB root is unknown.</summary>
    private static string? TargetPath()
    {
        var root = LbApiHost.Host.Media.MediaResolver.LbRoot;
        if (string.IsNullOrEmpty(root)) return null;
        try { return Path.Combine(root!, "Core", FileName); } catch { return null; }
    }

    // ── Read ────────────────────────────────────────────────────────────────

    /// <summary>Parse the shared .dat. Returns null when the file is absent (fresh install → callers use
    /// their defaults). Never throws.</summary>
    public static ParentalDatData? Read()
    {
        var path = TargetPath();
        if (path == null || !File.Exists(path)) return null;
        var d = new ParentalDatData();
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "enabled":         d.Enabled = ParseBool(val); break;
                    case "mode":            d.Mode = val.Equals("Blacklist", StringComparison.OrdinalIgnoreCase) ? ParentalMode.Blacklist : ParentalMode.Whitelist; break;
                    case "hideuninstalled": d.HideUninstalled = ParseBool(val); break;
                    case "allowratings":    d.AllowRatings = ParseBool(val); break;
                    case "allowfavorites":  d.AllowFavorites = ParseBool(val); break;
                    case "forcewebhideall": d.ForceWebHideAll = ParseBool(val); break;
                    case "blockinstall":    d.BlockInstall = ParseBool(val); break;
                    case "writemode":       d.WriteMode = val.Equals("Merge", StringComparison.OrdinalIgnoreCase) ? ParentalWriteMode.Merge : ParentalWriteMode.Block; break;
                    case "hotkey":          d.HotKey = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0; break;
                    case "configversion":   d.ConfigVersion = val; break;
                    case "rule":            if (val.Length > 0) d.Rules.Add(val); break;
                    case "hidename":        if (val.Length > 0) d.HideOn.Add(val); break;
                    case "hidenameoff":     if (val.Length > 0) d.HideOff.Add(val); break;
                    case "blockedid":       if (val.Length > 0) d.BlockedIds.Add(val); break;
                    // pinset / version: derived / informational — ignored on read.
                }
            }
            return d;
        }
        catch (Exception ex) { Log("read failed: " + ex.Message); return null; }
    }

    private static bool ParseBool(string v)
    {
        v = v.Trim();
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    // ── Write ───────────────────────────────────────────────────────────────

    /// <summary>Regenerate the whole shared .dat from the current config + the blocked-ID set. Best-effort;
    /// atomic (tmp + move). The single writer both ParentalConfig.Save and ParentalGameFlag.SetBlocked call.</summary>
    public static void Write()
    {
        var path = TargetPath();
        if (path == null) return;
        try
        {
            var cfg = ParentalConfig.Instance;
            var sb = new StringBuilder(512);
            sb.Append("# LiteBox parental control — shared config, edited by LiteBox and the plugin\r\n");
            sb.Append("Version=1\r\n");
            sb.Append("Enabled=").Append(cfg.Enabled ? '1' : '0').Append("\r\n");
            sb.Append("Mode=").Append(cfg.Mode == ParentalMode.Blacklist ? "Blacklist" : "Whitelist").Append("\r\n");
            sb.Append("HideUninstalled=").Append(cfg.HideUninstalled ? '1' : '0').Append("\r\n");
            // The PIN is BigBox's own <LockPin> (Data\BigBoxSettings.xml) — a single credential, never stored in
            // the .dat. PinSet is only the .bin's cold-start arm gate; derive it from that live PIN.
            sb.Append("PinSet=").Append(PinPresent() ? '1' : '0').Append("\r\n");
            sb.Append("AllowRatings=").Append(cfg.AllowLockedModifyRatings ? '1' : '0').Append("\r\n");
            sb.Append("AllowFavorites=").Append(cfg.AllowLockedModifyFavorites ? '1' : '0').Append("\r\n");
            sb.Append("ForceWebHideAll=").Append(cfg.ForceWebHideAll ? '1' : '0').Append("\r\n");
            sb.Append("BlockInstall=").Append(cfg.BlockInstallWhenLocked ? '1' : '0').Append("\r\n");
            sb.Append("WriteMode=").Append(cfg.BigBoxWriteMode == ParentalWriteMode.Merge ? "Merge" : "Block").Append("\r\n");
            sb.Append("HotKey=").Append(cfg.HotKey.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("ConfigVersion=").Append(OneLine(cfg.ConfigVersion)).Append("\r\n");
            foreach (var r in cfg.Rules)
                if (!string.IsNullOrWhiteSpace(r)) sb.Append("Rule=").Append(OneLine(r)).Append("\r\n");
            foreach (var n in cfg.HiddenPlatformsBigBoxOn)
                if (!string.IsNullOrWhiteSpace(n)) sb.Append("HideName=").Append(OneLine(n)).Append("\r\n");
            foreach (var n in cfg.HiddenPlatformsBigBoxOff)
                if (!string.IsNullOrWhiteSpace(n)) sb.Append("HideNameOff=").Append(OneLine(n)).Append("\r\n");
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

    private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    private static void Log(string m) => LbLog.Info("parental", "native-dat: " + m);
}
