// The plugin's own reader/writer for the SHARED parental config, Core\litebox-parental.dat.
//
// This is the SAME flat key=value file LiteBox writes (Host/Parental/ParentalNativeExport) and the
// native .bin reads before .NET. The plugin uses it to run standalone — no LiteBox required. It parses
// EVERY known key (so a plugin save preserves the LiteBox-only knobs it doesn't expose: AllowRatings,
// WriteMode, HotKey, …) and re-emits the whole file atomically (tmp + File.Move), last-writer-wins with
// LiteBox. The blocked-game IDs live here too (BlockedId=), so the restriction browser reads/writes them
// without any second store.
//
// Loaded ONCE into a cached model when a config/browser modal opens; re-read only on explicit Reload().
// The Core directory is the folder holding the running host exe (LaunchBox.exe / BigBox.exe).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LiteBoxParental
{
    internal sealed class ParentalDat
    {
        public const string FileName = "litebox-parental.dat";

        // ── Model (mirrors the .dat keys) ────────────────────────────────────
        public bool Enabled;
        public bool Blacklist = true;          // Mode: false = Whitelist, true = Blacklist — DEFAULT Blacklist
        public bool HideUninstalled = true;    // DEFAULT ON — hide games with Installed=false while parental active
        public bool AllowRatings, AllowFavorites, ForceWebHideAll, BlockInstall;   // LiteBox-web only — preserved
        public bool WriteModeMerge;            // WriteMode: false = Block, true = Merge (preserved)
        public int  HotKey;                    // preserved
        public string ConfigVersion = "0.0.0";
        public readonly List<string> Rules   = new List<string>();   // Rule=
        public readonly List<string> HideOn  = new List<string>();   // HideName=  (locked)
        public readonly List<string> HideOff = new List<string>();   // HideNameOff= (unlocked; preserved)
        public readonly HashSet<string> BlockedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Location ─────────────────────────────────────────────────────────

        /// <summary>The LB\Core directory — where the running host exe lives. Null when unresolved.</summary>
        public static string CoreDir()
        {
            try
            {
                var core = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "");
                return string.IsNullOrEmpty(core) ? null : core;
            }
            catch { return null; }
        }

        /// <summary>Full path to Core\litebox-parental.dat, or null when Core is unresolved.</summary>
        public static string Path_()
        {
            var core = CoreDir();
            return core == null ? null : System.IO.Path.Combine(core, FileName);
        }

        // ── Read ─────────────────────────────────────────────────────────────

        /// <summary>Parse the shared .dat. Returns a fresh model with defaults when the file is absent
        /// (fresh install → the plugin writes it on first save). Never throws.</summary>
        public static ParentalDat Load()
        {
            var d = new ParentalDat();
            try
            {
                var path = Path_();
                if (path == null || !File.Exists(path)) return d;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "enabled":         d.Enabled = Bool(val); break;
                        case "mode":            d.Blacklist = val.Equals("Blacklist", StringComparison.OrdinalIgnoreCase); break;
                        case "hideuninstalled": d.HideUninstalled = Bool(val); break;
                        case "allowratings":    d.AllowRatings = Bool(val); break;
                        case "allowfavorites":  d.AllowFavorites = Bool(val); break;
                        case "forcewebhideall": d.ForceWebHideAll = Bool(val); break;
                        case "blockinstall":    d.BlockInstall = Bool(val); break;
                        case "writemode":       d.WriteModeMerge = val.Equals("Merge", StringComparison.OrdinalIgnoreCase); break;
                        case "hotkey":          d.HotKey = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0; break;
                        case "configversion":   d.ConfigVersion = val; break;
                        case "rule":            if (val.Length > 0) d.Rules.Add(val); break;
                        case "hidename":        if (val.Length > 0) d.HideOn.Add(val); break;
                        case "hidenameoff":     if (val.Length > 0) d.HideOff.Add(val); break;
                        case "blockedid":       if (val.Length > 0) d.BlockedIds.Add(val); break;
                        // pinset / version: derived / informational — ignored on read.
                    }
                }
            }
            catch (Exception ex) { Log.Line("[ParentalDat] load failed: " + ex.Message); }
            return d;
        }

        // ── Write ────────────────────────────────────────────────────────────

        /// <summary>Rewrite the whole shared .dat from this model, atomically. Emits every known key so the
        /// LiteBox-only knobs ride along untouched. PinSet= is recomputed from the live BigBox PIN. Returns
        /// true on success.</summary>
        public bool Save()
        {
            var path = Path_();
            if (path == null) { Log.Line("[ParentalDat] save skipped: Core dir unresolved"); return false; }
            try
            {
                var sb = new StringBuilder(512);
                sb.Append("# LiteBox parental control — shared config, edited by LiteBox and the plugin\r\n");
                sb.Append("Version=1\r\n");
                sb.Append("Enabled=").Append(Enabled ? '1' : '0').Append("\r\n");
                sb.Append("Mode=").Append(Blacklist ? "Blacklist" : "Whitelist").Append("\r\n");
                sb.Append("HideUninstalled=").Append(HideUninstalled ? '1' : '0').Append("\r\n");
                // The PIN is BigBox's own <LockPin> (Data\BigBoxSettings.xml) — the .bin only needs PinSet as its
                // cold-start arm gate. Derive it from the live PIN; the blob itself never lives in the .dat.
                sb.Append("PinSet=").Append(PinVerify.HasPin ? '1' : '0').Append("\r\n");
                sb.Append("AllowRatings=").Append(AllowRatings ? '1' : '0').Append("\r\n");
                sb.Append("AllowFavorites=").Append(AllowFavorites ? '1' : '0').Append("\r\n");
                sb.Append("ForceWebHideAll=").Append(ForceWebHideAll ? '1' : '0').Append("\r\n");
                sb.Append("BlockInstall=").Append(BlockInstall ? '1' : '0').Append("\r\n");
                sb.Append("WriteMode=").Append(WriteModeMerge ? "Merge" : "Block").Append("\r\n");
                sb.Append("HotKey=").Append(HotKey.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("ConfigVersion=").Append(OneLine(ConfigVersion)).Append("\r\n");
                foreach (var r in Rules)   if (!string.IsNullOrWhiteSpace(r)) sb.Append("Rule=").Append(OneLine(r)).Append("\r\n");
                foreach (var n in HideOn)  if (!string.IsNullOrWhiteSpace(n)) sb.Append("HideName=").Append(OneLine(n)).Append("\r\n");
                foreach (var n in HideOff) if (!string.IsNullOrWhiteSpace(n)) sb.Append("HideNameOff=").Append(OneLine(n)).Append("\r\n");
                foreach (var id in BlockedIds) if (!string.IsNullOrWhiteSpace(id)) sb.Append("BlockedId=").Append(OneLine(id)).Append("\r\n");

                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                try { File.Move(tmp, path, true); }
                catch { try { if (File.Exists(path)) File.Delete(path); File.Move(tmp, path); } catch { try { File.Delete(tmp); } catch { } return false; } }
                Log.Line("[ParentalDat] wrote " + path);
                return true;
            }
            catch (Exception ex) { Log.Line("[ParentalDat] save failed: " + ex.Message); return false; }
        }

        // ── helpers ──────────────────────────────────────────────────────────
        private static bool Bool(string v)
        {
            v = (v ?? "").Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
