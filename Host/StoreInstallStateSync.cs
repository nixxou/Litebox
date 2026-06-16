// Reconciles store games' install state in LiteBox's data — because LiteBox runs WITHOUT
// LaunchBox.exe, so nothing else flips Installed / ApplicationPath when a game is (un)installed.
// We do NOT download anything; we just READ the clients' local state and write back:
//   GOG   — C:\ProgramData\GOG.com\Galaxy\storage\galaxy-2.0.db, table InstalledBaseProducts
//           (productId → installationPath). Installed ⇒ ApplicationPath = "<path>\Launch *.lnk".
//   Steam — {library}\steamapps\appmanifest_{appid}.acf, StateFlags & 4 (fully installed).
//           ApplicationPath stays steam://rungameid/{appid} (never changes); only the flag flips.
//   Epic  — {AppDataPath}\Manifests\*.item (JSON; AppName → InstallLocation, bIsIncompleteInstall).
//           ApplicationPath stays com.epicgames.launcher://apps/{appName}?action=launch (never changes).
// Changes route through GameStore.SetGameField → op-log → XML flush (when safe). Fail-soft.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LbApiHost.Host.Data;

namespace LbApiHost.Host;

internal static class StoreInstallStateSync
{
    /// <summary>Reconcile install state. Returns the number of fields changed (0 = already in sync).
    /// quiet=true suppresses the summary log on a no-op (used by the per-selection poll).</summary>
    public static int Sync(GameStore store, bool quiet = false)
    {
        if (store == null) return 0;
        try { return SyncCore(store, quiet); }
        catch (Exception ex) { Console.WriteLine("[storesync] failed: " + ex.Message); return 0; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]   // typed Sqlite refs isolated for soft-fail JIT
    private static int SyncCore(GameStore store, bool quiet)
    {
        var gog = ReadGogInstalled(out bool gogOk);      // gogAppId(string) → installationPath
        var steam = ReadSteamInstalled(out bool steamOk); // set of installed steam appids
        var epic = ReadEpicInstalled(out bool epicOk);   // epic appName → InstallLocation (complete installs)
        var uplay = ReadUplayInstalled(out bool uplayOk); // uplay id → InstallDir (registry)
        var ea = ReadEaInstalled(out bool eaOk);          // ea contentId → install folder (installerdata.xml)
        int changed = 0;

        // CRITICAL: only act on a store whose client state we actually READ. If the GOG DB / Steam
        // registry couldn't be read (client running & locking the file, not installed, copy failed),
        // the collection is empty NOT because nothing is installed but because we don't know — and
        // downgrading every game to "uninstalled" (clearing the GOG ApplicationPath) would be wrong
        // and destructive. So skip that store entirely this round.
        for (int i = 0; i < store.Count; i++)
        {
            var kind = StoreSupport.KindOf(store.Str(store.Rows[i].SourceIdx));
            if (kind == StoreKind.Gog)
            {
                if (!gogOk) continue;
                var id = store.Str(store.Rows[i].GogAppIdIdx).Trim();
                if (id.Length == 0) continue;
                bool curInstalled = store.Rows[i].Installed == 2;
                string curApp = store.Str(store.Rows[i].AppPathIdx);
                string title = store.Str(store.Rows[i].TitleIdx);
                if (gog.TryGetValue(id, out var path))
                {
                    string lnk = FindLaunchLnk(path);
                    if (!curInstalled) { store.SetGameField(i, "Installed", "true"); changed++; StoreTrace.Log($"GOG '{title}' [{id}] Installed false->true"); }
                    if (lnk.Length > 0 && !string.Equals(curApp, lnk, StringComparison.OrdinalIgnoreCase))
                    { store.SetGameField(i, "ApplicationPath", lnk); changed++; StoreTrace.Log($"GOG '{title}' [{id}] AppPath -> {lnk}"); }
                }
                else
                {
                    if (curInstalled) { store.SetGameField(i, "Installed", "false"); changed++; StoreTrace.Log($"GOG '{title}' [{id}] Installed true->false (not in galaxy DB)"); }
                    if (curApp.Length > 0) { store.SetGameField(i, "ApplicationPath", ""); changed++; StoreTrace.Log($"GOG '{title}' [{id}] AppPath cleared"); }
                }
            }
            else if (kind == StoreKind.Steam)
            {
                if (!steamOk) continue;
                var appid = StoreSupport.SteamAppId(store.Str(store.Rows[i].AppPathIdx));
                if (appid == null) continue;
                bool curInstalled = store.Rows[i].Installed == 2;
                bool installed = steam.Contains(appid);
                if (installed != curInstalled)
                { store.SetGameField(i, "Installed", installed ? "true" : "false"); changed++; StoreTrace.Log($"STEAM '{store.Str(store.Rows[i].TitleIdx)}' [{appid}] Installed {curInstalled}->{installed}"); }
                // Steam ApplicationPath is steam://rungameid/{appid} regardless of install state — unchanged.
            }
            else if (kind == StoreKind.Epic)
            {
                if (!epicOk) continue;
                var appName = StoreSupport.EpicAppName(store.Str(store.Rows[i].AppPathIdx));
                if (appName == null) continue;
                bool curInstalled = store.Rows[i].Installed == 2;
                bool installed = epic.ContainsKey(appName);
                if (installed != curInstalled)
                { store.SetGameField(i, "Installed", installed ? "true" : "false"); changed++; StoreTrace.Log($"EPIC '{store.Str(store.Rows[i].TitleIdx)}' [{appName}] Installed {curInstalled}->{installed}"); }
                // Epic ApplicationPath is the constant launch URI regardless of install state — unchanged.
            }
            else if (kind == StoreKind.Uplay)
            {
                if (!uplayOk) continue;
                var id = StoreSupport.UplayId(store.Str(store.Rows[i].AppPathIdx));
                if (id == null) continue;
                bool curInstalled = store.Rows[i].Installed == 2;
                bool installed = uplay.ContainsKey(id);
                if (installed != curInstalled)
                { store.SetGameField(i, "Installed", installed ? "true" : "false"); changed++; StoreTrace.Log($"UPLAY '{store.Str(store.Rows[i].TitleIdx)}' [{id}] Installed {curInstalled}->{installed}"); }
                // Uplay ApplicationPath is uplay://launch/{id} regardless of install state — unchanged.
            }
            else if (kind == StoreKind.Ea)
            {
                if (!eaOk) continue;
                var id = StoreSupport.EaId(store.Str(store.Rows[i].AppPathIdx));
                if (id == null) continue;
                bool curInstalled = store.Rows[i].Installed == 2;
                bool installed = ea.ContainsKey(id);
                if (installed != curInstalled)
                { store.SetGameField(i, "Installed", installed ? "true" : "false"); changed++; StoreTrace.Log($"EA '{store.Str(store.Rows[i].TitleIdx)}' [{id}] Installed {curInstalled}->{installed}"); }
                // EA ApplicationPath is ea://{id} regardless of install state — unchanged.
            }
        }
        StoreTrace.Log($"sync gog(ok={gogOk},n={gog.Count}) steam(ok={steamOk},n={steam.Count}) epic(ok={epicOk},n={epic.Count}) uplay(ok={uplayOk},n={uplay.Count}) ea(ok={eaOk},n={ea.Count}) changed={changed}");
        if (!quiet || changed > 0 || !gogOk || !steamOk || !epicOk || !uplayOk || !eaOk)
            Console.WriteLine($"[storesync] gog(ok={gogOk},n={gog.Count}) steam(ok={steamOk},n={steam.Count}) epic(ok={epicOk},n={epic.Count}) uplay(ok={uplayOk},n={uplay.Count}) ea(ok={eaOk},n={ea.Count}) fieldsChanged={changed}");
        return changed;
    }

    // ── GOG: galaxy-2.0.db InstalledBaseProducts ────────────────────────────
    // Result cache on top of the SHARED GalaxyDb snapshot. ok=true only when we
    // actually have an authoritative map (so empty ⇒ "really nothing installed").
    // While the source DB + -wal are unchanged (Galaxy idle/closed — the common
    // case) we return the previous map with ZERO query and ZERO copy; a real
    // (un)install changes the signature → re-query, and GalaxyDb re-copies the DB
    // only then (that copy shared with any other galaxy reader, e.g. achievements).
    private static string? _gogSig;
    private static Dictionary<string, string>? _gogResult;

    private static Dictionary<string, string> ReadGogInstalled(out bool ok)
    {
        ok = false;
        var src = GalaxyDb.SourceDbPath();
        if (src == null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // GOG not installed

        string sig = GalaxyDb.Sig(src);
        if (_gogResult != null && sig == _gogSig) { ok = true; return _gogResult; }   // nothing changed

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool got = GalaxyDb.Read(con =>
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT productId, installationPath FROM InstalledBaseProducts";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pid = r.IsDBNull(0) ? null : r.GetValue(0)?.ToString();
                var path = r.IsDBNull(1) ? "" : r.GetString(1);
                if (!string.IsNullOrEmpty(pid)) map[pid!] = path;
            }
        });
        if (!got) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ok = true;
        _gogResult = map;
        _gogSig = sig;
        return map;
    }

    // Galaxy creates "Launch <ProductName>.lnk" in the install folder (name = GOG's product, not LB's
    // title) — so scan for it rather than build the name.
    private static string FindLaunchLnk(string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "";
            var byName = Directory.GetFiles(folder, "Launch *.lnk");
            if (byName.Length > 0) return byName[0];
            var any = Directory.GetFiles(folder, "*.lnk");
            return any.Length > 0 ? any[0] : "";
        }
        catch { return ""; }
    }

    // ── Steam: appmanifest_{appid}.acf across all libraries ─────────────────
    // ok=true once we've located the Steam install and scanned its libraries (an empty set then means
    // "really nothing installed"). ok=false if Steam isn't found / readable → caller leaves Steam alone.
    private static HashSet<string> ReadSteamInstalled(out bool ok)
    {
        ok = false;
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var libs = SteamLibraries();
            if (libs.Count == 0) return installed;   // Steam not found → ok stays false
            foreach (var lib in libs)
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var txt = File.ReadAllText(acf);
                        var idM = Regex.Match(txt, "\"appid\"\\s*\"(\\d+)\"");
                        var sfM = Regex.Match(txt, "\"StateFlags\"\\s*\"(\\d+)\"");
                        if (!idM.Success) continue;
                        int sf = sfM.Success ? int.Parse(sfM.Groups[1].Value) : 0;
                        if ((sf & 4) != 0) installed.Add(idM.Groups[1].Value);   // 4 = StateFullyInstalled
                    }
                    catch { }
                }
            }
            ok = true;   // located Steam and scanned its libraries — the set is authoritative
        }
        catch (Exception ex) { ok = false; Console.WriteLine("[storesync] Steam read: " + ex.Message); }
        return installed;
    }

    // All Steam library roots (the SteamPath itself + every "path" in libraryfolders.vdf). Empty when
    // Steam isn't found in the registry.
    private static List<string> SteamLibraries()
    {
        var libs = new List<string>();
        try
        {
            string? steamPath = null;
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steamPath = k?.GetValue("SteamPath") as string;
            }
            catch { }
            if (string.IsNullOrEmpty(steamPath)) return libs;
            libs.Add(steamPath!.Replace('/', '\\'));
            var vdf = Path.Combine(libs[0], "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(p) && !libs.Contains(p, StringComparer.OrdinalIgnoreCase)) libs.Add(p);
                }
        }
        catch { }
        return libs;
    }

    /// <summary>Resolve a Steam game's on-disk install folder (library\steamapps\common\&lt;installdir&gt;)
    /// from its appmanifest, or null if not found. Used to watch the game's process for exit.</summary>
    public static string? SteamInstallDir(string appid)
    {
        try
        {
            foreach (var lib in SteamLibraries())
            {
                var acf = Path.Combine(lib, "steamapps", "appmanifest_" + appid + ".acf");
                if (!File.Exists(acf)) continue;
                var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s*\"([^\"]+)\"");
                if (!m.Success) return null;
                var dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
                return Directory.Exists(dir) ? dir : null;
            }
        }
        catch { }
        return null;
    }

    // ── Epic: EGL manifests {AppDataPath}\Manifests\*.item ──────────────────
    // ok=true once the EGL Manifests dir is found & enumerated (an empty map then = nothing installed).
    // ok=false if EGL / its data dir isn't found → caller leaves Epic games alone. A manifest counts as
    // installed only when bIsIncompleteInstall=false (it appears at download START with incomplete=true).
    private static Dictionary<string, string> ReadEpicInstalled(out bool ok)
    {
        ok = false;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manDir = EpicManifestsDir();
            var dataDir = Directory.GetParent(manDir)?.FullName;
            if (dataDir == null || !Directory.Exists(dataDir)) return map;   // EGL not installed → ok=false (leave Epic games alone)
            ok = true;                                                       // EGL present → authoritative (absent/empty Manifests = nothing installed)
            if (!Directory.Exists(manDir)) return map;                       // no Epic game installed yet
            foreach (var item in Directory.GetFiles(manDir, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    var root = doc.RootElement;
                    var appName = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                    var loc = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                    bool incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;
                    if (!string.IsNullOrEmpty(appName) && !incomplete && !string.IsNullOrEmpty(loc))
                        map[appName!] = loc!;
                }
                catch { }
            }
        }
        catch (Exception ex) { ok = false; Console.WriteLine("[storesync] Epic read: " + ex.Message); }
        return map;
    }

    /// <summary>The EGL install folder for an Epic appName (from its manifest), or null. For the exit watcher.</summary>
    public static string? EpicInstallDir(string appName)
    {
        var map = ReadEpicInstalled(out _);
        return map.TryGetValue(appName, out var loc) && Directory.Exists(loc) ? loc : null;
    }

    // ── Ubisoft Connect (Uplay): registry Installs\{id}\InstallDir ──────────
    // Ubisoft Connect (ex-Uplay) is a 32-bit app → it writes its install records
    // under WOW6432Node; we read the 32-bit registry view explicitly. A game is
    // installed when its {id} subkey's InstallDir points to an existing folder
    // (id = the number in uplay://launch/{id}). ok=true once Connect itself is
    // present (so an empty map = nothing installed); absent → ok=false → caller
    // leaves Uplay games alone. The ApplicationPath (uplay://launch/{id}) is
    // constant regardless of install state — only the Installed flag flips.
    private static Dictionary<string, string> ReadUplayInstalled(out bool ok)
    {
        ok = false;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var hklm32 = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var launcher = hklm32.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher");
            if (launcher == null) return map;          // Ubisoft Connect not installed → leave Uplay games alone
            ok = true;                                  // Connect present → authoritative
            using var installs = launcher.OpenSubKey("Installs");
            if (installs == null) return map;           // no Ubisoft game installed yet
            foreach (var id in installs.GetSubKeyNames())
            {
                try
                {
                    using var gk = installs.OpenSubKey(id);
                    var dir = gk?.GetValue("InstallDir") as string;
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var norm = dir!.Replace('/', '\\');
                    if (Directory.Exists(norm)) map[id] = norm;   // only count truly-present installs
                }
                catch { }
            }
        }
        catch (Exception ex) { ok = false; Console.WriteLine("[storesync] Uplay read: " + ex.Message); }
        return map;
    }

    /// <summary>The Ubisoft Connect install folder for a uplay id (registry InstallDir), or null. For the exit watcher.</summary>
    public static string? UplayInstallDir(string id)
    {
        var map = ReadUplayInstalled(out _);
        return map.TryGetValue(id, out var dir) && Directory.Exists(dir) ? dir : null;
    }

    // ── EA app (EA Desktop): contentId → install folder, from per-game manifests ──────
    // Each installed game sits under the EA games root (machine.downloadinplacedir in
    // C:\ProgramData\EA Desktop\machine.ini; default C:\Program Files\EA Games) with a
    // <game>\__Installer\installerdata.xml whose <contentID> entries are the EA offer ids
    // (== the id in ea://{id}). The folder/file vanish on uninstall, so this reflects REAL
    // install state (the Origin Games\{id} registry key is ownership and persists, so it's
    // not used). Legacy Origin (ProgramData\Origin\LocalContent\*.mfst) is EOL (Apr 2025).
    // ok=true once EA Desktop itself is present (machine.ini), so an empty map = nothing
    // installed; absent → ok=false → caller leaves EA games alone. ApplicationPath
    // (ea://{id}) is constant regardless of install state — only the Installed flag flips.
    private static Dictionary<string, string> ReadEaInstalled(out bool ok)
    {
        ok = false;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(@"C:\ProgramData\EA Desktop\machine.ini")) return map;   // EA app not installed
            ok = true;
            var root = EaGamesRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return map;     // present, nothing installed
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var xml = Path.Combine(dir, "__Installer", "installerdata.xml");
                    if (!File.Exists(xml)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(xml), "<contentID>(\\d+)</contentID>"))
                        map[m.Groups[1].Value] = dir;
                }
                catch { }
            }
        }
        catch (Exception ex) { ok = false; Console.WriteLine("[storesync] EA read: " + ex.Message); }
        return map;
    }

    // EA Desktop's games install root: machine.downloadinplacedir, or the default EA Games dir.
    private static string EaGamesRoot()
    {
        try
        {
            const string ini = @"C:\ProgramData\EA Desktop\machine.ini";
            const string key = "machine.downloadinplacedir=";
            if (File.Exists(ini))
                foreach (var line in File.ReadAllLines(ini))
                {
                    var t = line.Trim();
                    if (t.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        return t.Substring(key.Length).Trim();
                }
        }
        catch { }
        return @"C:\Program Files\EA Games";
    }

    /// <summary>The EA install folder for a content id, or null. For the exit watcher.</summary>
    public static string? EaInstallDir(string id)
    {
        var map = ReadEaInstalled(out _);
        return map.TryGetValue(id, out var dir) && Directory.Exists(dir) ? dir : null;
    }

    // {AppDataPath}\Manifests — AppDataPath from the EGL registry key, else the ProgramData default.
    private static string EpicManifestsDir()
    {
        string? dataPath = null;
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
            dataPath = k?.GetValue("AppDataPath") as string;
        }
        catch { }
        if (string.IsNullOrEmpty(dataPath))
            dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                    "Epic", "EpicGamesLauncher", "Data");
        return Path.Combine(dataPath!, "Manifests");
    }
}
