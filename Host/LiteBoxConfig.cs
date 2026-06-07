// Tiny dependency-free INI config, stored next to the exe (LiteBox.ini). No JSON,
// no extra packages — just key=value lines (';' / '#' comments, optional [sections]
// are ignored/flattened). A commented default file is written on first run so the
// user can discover the keys.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace LbApiHost.Host;

internal sealed class LiteBoxConfig
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly string _path;
    private readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);

    public LiteBoxConfig(string path)
    {
        _path = path;
        if (File.Exists(_path)) Load();
        else WriteTemplate();
    }

    /// <summary>LiteBox.ini next to the running exe (falls back to the app base dir).</summary>
    public static LiteBoxConfig LoadForExe()
    {
        string ini;
        try
        {
            var exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            ini = Path.ChangeExtension(exe, ".ini");
        }
        catch { ini = Path.Combine(AppContext.BaseDirectory, "LiteBox.ini"); }
        return new LiteBoxConfig(ini);
    }

    private void Load()
    {
        try
        {
            foreach (var raw in File.ReadAllLines(_path))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                _kv[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("; LiteBox configuration");
            foreach (var kv in _kv) sb.AppendLine($"{kv.Key}={kv.Value}");
            File.WriteAllText(_path, sb.ToString());
        }
        catch { }
    }

    private void WriteTemplate()
    {
        // Seed defaults + comments so the file is self-documenting.
        _kv["ReadOnly"] = "true";
        _kv["ShowGameRunningScreen"] = "true";
        _kv["UnloadListDuringGame"] = "true";
        _kv["UseImageCache"] = "true";
        _kv["UseGameCache"] = "true";
        _kv["UnloadGameCacheDuringGame"] = "true";
        _kv["Use16:9ForMainScreenshot"] = "true";
        _kv["GameRunningText"] = "Game running...";
        _kv["GameRunningColor"] = "#0F0F12";
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("; LiteBox configuration");
            sb.AppendLine("; ReadOnly              : never write to the LaunchBox XMLs; favorites/ratings/play");
            sb.AppendLine(";                         changes stay in memory for this run only. Set false to persist.");
            sb.AppendLine("; ShowGameRunningScreen : show a fanart/colour screen while a game runs");
            sb.AppendLine("; UnloadListDuringGame  : free the game list while a game runs, reload after");
            sb.AppendLine("; UseImageCache         : use the shared degraded-thumbnail cache for UI images");
            sb.AppendLine("; UseGameCache          : build & use an in-memory media cache (Everything-backed) when");
            sb.AppendLine(";                          ExtendDB is NOT loaded (ExtendDB's own cache is preferred when present).");
            sb.AppendLine("; UnloadGameCacheDuringGame : free the host game cache while a game runs, rebuild on exit.");
            sb.AppendLine("; Use16:9ForMainScreenshot : reserve a 16:9 area for the main media (true);");
            sb.AppendLine(";                            false reserves a poster-ratio (2:3) area instead.");
            sb.AppendLine("; GameRunningText       : message shown on the running screen");
            sb.AppendLine("; GameRunningColor      : base colour (#RRGGBB) behind the fanart");
            sb.AppendLine($"ReadOnly={_kv["ReadOnly"]}");
            sb.AppendLine($"ShowGameRunningScreen={_kv["ShowGameRunningScreen"]}");
            sb.AppendLine($"UnloadListDuringGame={_kv["UnloadListDuringGame"]}");
            sb.AppendLine($"UseImageCache={_kv["UseImageCache"]}");
            sb.AppendLine($"UseGameCache={_kv["UseGameCache"]}");
            sb.AppendLine($"UnloadGameCacheDuringGame={_kv["UnloadGameCacheDuringGame"]}");
            sb.AppendLine($"Use16:9ForMainScreenshot={_kv["Use16:9ForMainScreenshot"]}");
            sb.AppendLine($"GameRunningText={_kv["GameRunningText"]}");
            sb.AppendLine($"GameRunningColor={_kv["GameRunningColor"]}");
            File.WriteAllText(_path, sb.ToString());
        }
        catch { }
    }

    // ── Raw accessors ────────────────────────────────────────────────────────
    public string Get(string key, string def = null) => _kv.TryGetValue(key, out var v) ? v : def;
    public void Set(string key, string val) => _kv[key] = val ?? "";

    public bool GetBool(string key, bool def)
    {
        var v = Get(key);
        if (v == null) return def;
        return v == "1" || v.Equals("true", OIC) || v.Equals("yes", OIC) || v.Equals("on", OIC);
    }
    public void SetBool(string key, bool val) => _kv[key] = val ? "true" : "false";

    public int GetInt(string key, int def)
        => int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : def;
    public void SetInt(string key, int val) => _kv[key] = val.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ── Typed options ────────────────────────────────────────────────────────
    public bool ReadOnly              { get => GetBool("ReadOnly", true); set => SetBool("ReadOnly", value); }
    public bool ShowGameRunningScreen { get => GetBool("ShowGameRunningScreen", true); set => SetBool("ShowGameRunningScreen", value); }
    public bool UnloadListDuringGame  { get => GetBool("UnloadListDuringGame", true); set => SetBool("UnloadListDuringGame", value); }
    public bool UseImageCache         { get => GetBool("UseImageCache", true); set => SetBool("UseImageCache", value); }
    public bool UseGameCache          { get => GetBool("UseGameCache", true); set => SetBool("UseGameCache", value); }
    public bool UnloadGameCacheDuringGame { get => GetBool("UnloadGameCacheDuringGame", true); set => SetBool("UnloadGameCacheDuringGame", value); }
    // true → reserve a 16:9 area for the main media; false → a poster-ratio (2:3) area.
    public bool Use169ForMainScreenshot { get => GetBool("Use16:9ForMainScreenshot", true); set => SetBool("Use16:9ForMainScreenshot", value); }
    public string GameRunningText     => Get("GameRunningText", "Game running...");
    public Color GameRunningColor     => ParseColor(Get("GameRunningColor", "#0F0F12"), Color.FromArgb(15, 15, 18));

    private static Color ParseColor(string s, Color def)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim();
        try
        {
            if (s.StartsWith("#") && s.Length == 7)
                return Color.FromArgb(
                    Convert.ToInt32(s.Substring(1, 2), 16),
                    Convert.ToInt32(s.Substring(3, 2), 16),
                    Convert.ToInt32(s.Substring(5, 2), 16));
            if (s.Contains(","))
            {
                var p = s.Split(',');
                if (p.Length == 3) return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
            }
            var named = Color.FromName(s);
            if (named.IsKnownColor || named.A != 0) return named;
        }
        catch { }
        return def;
    }
}
