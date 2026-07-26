// Inventory + maintenance of everything LiteBox writes under <LaunchBox>\Core\litebox\ — the data model
// behind the Options → Caches page. One declarative catalog (databases, cache dirs, logs, config/state files,
// essential dirs) drives both the UI and the actions, so adding a new file is one catalog row.
//
// Action policy (validated with the user):
//   • Cache DIRECTORIES / LOGS / STATE files → cleared IMMEDIATELY (best-effort; locked files skipped).
//   • DATABASES → deletion is SCHEDULED for the next restart. Several dbs hold a persistent connection or a
//     WAL sidecar while LiteBox runs, so deleting the live file is unsafe. RunPendingCleanup() deletes the
//     flagged files (+ their -wal/-shm) at boot BEFORE any db is opened.
//   • CONFIG files + ESSENTIAL dirs (web/config/thirdparty) → info only, never a destructive target.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LbApiHost.Host.Data;

internal static class DataMaintenance
{
    public enum Kind { Database, CacheDir, Log, StateFile, ConfigFile, EssentialDir }

    /// <summary>What the row's button does. None = info only (config / essential).</summary>
    public enum ActionType { None, ClearDirNow, DeleteFileNow, ResetDbOnRestart }

    public sealed class Item
    {
        public string Name = "";           // display name (also the on-disk name when Rel is null)
        public string Role = "";           // one-line description
        public Kind Kind;
        public ActionType Action;
        public bool IsDir;
        public string? Warning;            // extra confirmation line (data loss); null = safe
        public string? Rel;                // path RELATIVE to litebox\ when it differs from Name
                                           // (e.g. "cache/romcache" after the R3 cache reorg)

        // Rel wins when set (the cache dirs live under cache\ but display as their bare name); else Name.
        public string FullPath => IsDir ? Path.Combine(LiteBoxPaths.Data, Rel ?? Name) : LiteBoxPaths.File(Rel ?? Name);
    }

    // ── Catalog ──────────────────────────────────────────────────────────────────────────────────────
    private static readonly List<Item> _catalog = new()
    {
        // Databases (destructive = scheduled at restart).
        new() { Name = "rom-archive-cache.db",           Kind = Kind.Database, Action = ActionType.ResetDbOnRestart,
                Role = "Archive listing + RetroAchievements engine cache. Rebuilt on demand." },
        new() { Name = "rom-archive-history.db",          Kind = Kind.Database, Action = ActionType.ResetDbOnRestart,
                Role = "Your archive favourites + recently-played markers.",
                Warning = "You will lose your in-archive ROM favourites and recently-played history." },
        new() { Name = "launch-history.db",               Kind = Kind.Database, Action = ActionType.ResetDbOnRestart,
                Role = "Last emulator / version / ROM launched, per game.",
                Warning = "The launch button will forget the last choice per game until each is replayed." },
        new() { Name = "LiteBox.pending.db",              Kind = Kind.Database, Action = ActionType.ResetDbOnRestart,
                Role = "Queue of write-backs to LaunchBox not yet flushed.",
                Warning = "Any not-yet-saved changes to LaunchBox data will be lost." },
        new() { Name = "litebox-options.db",              Kind = Kind.Database, Action = ActionType.ResetDbOnRestart,
                Role = "Per-emulator / per-game overrides + DB-managed settings.",
                Warning = "You will lose ALL per-emulator and per-game overrides." },
        new() { Name = "LaunchBox.Extended.Metadata.db",  Kind = Kind.Database, Action = ActionType.ResetDbOnRestart,
                Role = "The extended metadata database (very large).",
                Warning = "It will be re-downloaded on the next boot — a large download." },

        // Cache directories (cleared immediately).
        new() { Name = "cache",             Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true,
                Role = "The whole rebuildable-cache tree (thumbnails, 3D models, download staging, and the individual caches below)." },
        new() { Name = "romcache",          Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/romcache",
                Role = "ROMs extracted from archives (self-evicting). Re-extracted on next launch." },
        new() { Name = "emumovies",         Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/emumovies",
                Role = "Cached EmuMovies API responses (time-limited)." },
        new() { Name = "webview2-yt",       Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/webview2-yt",
                Role = "YouTube player browser profile (cookies / cache)." },
        new() { Name = "webview2-yt-page",  Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/webview2-yt-page",
                Role = "Generated YouTube player page (regenerated each play)." },
        new() { Name = "webview2-kiosk",    Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/webview2-kiosk",
                Role = "Kiosk web-view browser profile (cookies / cache)." },
        new() { Name = "ra-cache",          Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/ra-cache",
                Role = "RetroAchievements catalogue JSON." },
        new() { Name = "ra-badges",         Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/ra-badges",
                Role = "Downloaded RetroAchievements badge images." },
        new() { Name = "store-ach-cache",   Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/store-ach-cache",
                Role = "Steam / GOG achievement data (time-limited)." },
        new() { Name = "store-ach-badges",  Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/store-ach-badges",
                Role = "Store achievement badge images." },
        new() { Name = "steam",             Kind = Kind.CacheDir, Action = ActionType.ClearDirNow, IsDir = true, Rel = "cache/steam",
                Role = "Cached Steam store media JSON (time-limited)." },

        // Logs (deleted immediately).
        new() { Name = "litebox-debug.log", Kind = Kind.Log, Action = ActionType.DeleteFileNow,
                Role = "Debug trace (only written when debug logging is on)." },
        new() { Name = "litebox-store.log", Kind = Kind.Log, Action = ActionType.DeleteFileNow,
                Role = "Store selection / sync trace." },
        new() { Name = "saves-diag.log",    Kind = Kind.Log, Action = ActionType.DeleteFileNow,
                Role = "Game-saves sync diagnostic trace." },

        // State (safe to reset).
        new() { Name = "rom-selection.json", Kind = Kind.StateFile, Action = ActionType.DeleteFileNow,
                Role = "Pending in-archive ROM picks per game. Re-picked on next launch." },

        // Config — INFO ONLY (managed in their own options pages; deleting loses settings).
        new() { Name = "LiteBox.ini",                 Kind = Kind.ConfigFile, Action = ActionType.None,
                Role = "Main settings: accounts (encrypted), plugins, keys, versions." },
        new() { Name = "youtube.json",                Kind = Kind.ConfigFile, Action = ActionType.None,
                Role = "YouTube / yt-dlp preferences." },
        new() { Name = "parental-lists.json",         Kind = Kind.ConfigFile, Action = ActionType.None,
                Role = "Parental-control allow / block lists." },
        new() { Name = "ra-panel.json",               Kind = Kind.ConfigFile, Action = ActionType.None,
                Role = "RetroAchievements login/session + per-platform enable flags." },
        new() { Name = "ra-platform-overrides.json",  Kind = Kind.ConfigFile, Action = ActionType.None,
                Role = "Manual platform → RA console mappings." },
        new() { Name = "rom-profiles.json",           Kind = Kind.ConfigFile, Action = ActionType.None,
                Role = "ROM-extractor profiles." },
        new() { Name = "game-suggester.json",         Kind = Kind.ConfigFile, Action = ActionType.None,
                Role = "Edited game-suggestion scoring rules." },

        // Essential directories — INFO ONLY (not caches).
        new() { Name = "web",        Kind = Kind.EssentialDir, Action = ActionType.None, IsDir = true,
                Role = "Deployed web-frontend themes served by the web module." },
        new() { Name = "config",     Kind = Kind.EssentialDir, Action = ActionType.None, IsDir = true,
                Role = "Media-signing secret (media-token.key)." },
        new() { Name = "thirdparty", Kind = Kind.EssentialDir, Action = ActionType.None, IsDir = true,
                Role = "Deployed yt-dlp + native binaries." },
    };

    public static IReadOnlyList<Item> Catalog => _catalog;
    public static IEnumerable<Item> Of(Kind k) => _catalog.Where(i => i.Kind == k);

    // ── Sizes ────────────────────────────────────────────────────────────────────────────────────────
    public static (int files, long bytes) SizeOf(Item it)
    {
        try
        {
            if (it.IsDir)
            {
                var dir = it.FullPath;
                if (!Directory.Exists(dir)) return (0, 0);
                int f = 0; long b = 0;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                { f++; try { b += new FileInfo(file).Length; } catch { } }
                return (f, b);
            }
            long total = 0; int n = 0;
            foreach (var p in FileAndSidecars(it.Name))
                if (File.Exists(p)) { n++; try { total += new FileInfo(p).Length; } catch { } }
            return (n, total);
        }
        catch { return (0, 0); }
    }

    /// <summary>A database's file plus its WAL sidecars (-wal / -shm); a plain file → just itself.</summary>
    private static IEnumerable<string> FileAndSidecars(string name)
    {
        string full = LiteBoxPaths.File(name);
        yield return full;
        if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            yield return full + "-wal";
            yield return full + "-shm";
        }
    }

    // ── Immediate actions (cache dirs / logs / state) ─────────────────────────────────────────────────
    /// <summary>Delete a directory's CONTENTS (keep the dir itself), best-effort (locked files skipped).</summary>
    public static void ClearDir(Item it)
    {
        try
        {
            var dir = it.FullPath;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                try { File.Delete(f); } catch { }
            foreach (var d in Directory.EnumerateDirectories(dir))
                try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>Delete a single file (log / state), best-effort.</summary>
    public static void DeleteFile(Item it)
    {
        try { var p = it.FullPath; if (File.Exists(p)) File.Delete(p); } catch { }
    }

    // ── Scheduled database cleanup ────────────────────────────────────────────────────────────────────
    private static string PendingPath => LiteBoxPaths.File("pending-cleanup.txt");

    private static HashSet<string> ReadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return new(StringComparer.OrdinalIgnoreCase);
            return new(File.ReadAllLines(PendingPath).Select(l => l.Trim()).Where(l => l.Length > 0),
                       StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void WritePending(HashSet<string> set)
    {
        try
        {
            if (set.Count == 0) { if (File.Exists(PendingPath)) File.Delete(PendingPath); return; }
            File.WriteAllLines(PendingPath, set);
        }
        catch { }
    }

    public static bool IsScheduled(Item it) => it.Kind == Kind.Database && ReadPending().Contains(it.Name);

    /// <summary>Toggle a database's "delete on next restart" flag. Returns the new scheduled state.</summary>
    public static bool ToggleScheduled(Item it)
    {
        if (it.Kind != Kind.Database) return false;
        var set = ReadPending();
        bool now;
        if (set.Contains(it.Name)) { set.Remove(it.Name); now = false; }
        else { set.Add(it.Name); now = true; }
        WritePending(set);
        return now;
    }

    /// <summary>Delete every database flagged for reset (+ its WAL sidecars), then clear the list. Call at
    /// boot BEFORE any database is opened. Only names present in the catalog as databases are honoured.</summary>
    public static void RunPendingCleanup()
    {
        var set = ReadPending();
        if (set.Count == 0) return;
        var dbNames = new HashSet<string>(Of(Kind.Database).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in set)
        {
            if (!dbNames.Contains(name)) continue;   // never touch anything not a known database
            foreach (var p in FileAndSidecars(name))
                try { if (File.Exists(p)) { File.Delete(p); Console.WriteLine("[maintenance] deleted " + Path.GetFileName(p)); } }
                catch (Exception ex) { Console.WriteLine($"[maintenance] delete {Path.GetFileName(p)} failed: {ex.Message}"); }
        }
        try { if (File.Exists(PendingPath)) File.Delete(PendingPath); } catch { }
    }
}
