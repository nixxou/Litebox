// The result of the badge pass, on disk.
//
// The pass costs ~85 µs a game — 400 ms on a 5000-game library, 25 to 40 SECONDS on 300 000. That
// price is paid at every start today, and it would be paid again after every game if the cache were
// dropped to free memory during play. What it produces, though, is tiny: one int per game plus a few
// packed bytes per distinct combination. Cheap to compute is not the same as cheap to store, and here
// the ratio runs the other way round from the usual cache — 3 000× cheaper to read back than to
// recompute, where a composed badge strip is 3 to 30× more expensive to read than to redraw. That is
// the whole reason this file exists and a strip cache on disk does not.
//
// VALIDITY is the difficult half. The file is only reused when nothing that feeds a badge can have
// moved behind our back:
//   • the catalog itself — the badge ids, in index order (an index shift would re-point every packed
//     slot at the wrong badge), plus the pack the images come from;
//   • the library — every platform XML's name, length and last-write time, and the game count. That
//     covers what LaunchBox can change while LiteBox is closed, INCLUDING the sub-entities the store
//     does not model as fields (controller support, additional applications), which no per-field
//     digest would have caught.
// Anything else — a badge toggled, reordered, the display options — is applied at draw time and never
// enters the file, so it cannot invalidate it.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Badges;

internal static class BadgeSnapshot
{
    private const uint Magic = 0x47444242;   // "BBDG"
    private const int Version = 1;

    public static string Path => System.IO.Path.Combine(LiteBoxPaths.Cache, "badges.bin");

    /// <summary>Identity of everything the file depends on. Cheap: a few dozen file and directory
    /// stats ONCE at startup — this is not the per-game I/O the badge rules forbid.
    ///
    /// Two kinds of input feed a badge. What lives in the STORE (fields, sub-entities) is covered by
    /// the platform XML stamps. What lives OUTSIDE it — the pack's art, the Manuals folders, the
    /// installed hiscore.dat, the save vault — is watched by events while the app runs, but events
    /// see nothing while the app is CLOSED: a PNG dropped into the pack overnight would restore
    /// combinations that skipped that badge, forever, with nothing left to notice. Their stats close
    /// that hole. Directory mtimes move on direct child add/remove/rename — exactly the changes that
    /// alter what the pass would find (an edit INSIDE an existing manual changes no badge).</summary>
    public static string Stamp(GameStore? store)
    {
        var sb = new StringBuilder(512);
        sb.Append(Version).Append('|').Append(BadgeSettings.Pack).Append('|');
        foreach (var b in BadgeCatalog.All) sb.Append(b.Id).Append(',');
        sb.Append('|');
        if (store != null)
        {
            sb.Append(store.Rows.Length).Append('|');
            foreach (var (name, len, ticks) in store.PlatformFileStamps())
                sb.Append(name).Append(':').Append(len).Append(':').Append(ticks).Append(';');
        }
        sb.Append('|');

        // Badge art: one stat per pack folder (file count + newest write). Catches an added,
        // replaced or deleted PNG — the review's exact counter-example.
        try
        {
            var root = BadgeImages.PacksRoot;
            if (root != null && Directory.Exists(root))
                foreach (var pack in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    StampDirFiles(sb, pack, "*.png");
        }
        catch { }
        sb.Append('|');

        // Manuals: the Documents badge reads Manuals\<platform>\. Directory mtime, not a file walk.
        try
        {
            if (store != null)
                foreach (var plat in store.ByPlatform.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var dir = Media.MediaResolver.ManualsFolder(plat);
                    if (dir != null && Directory.Exists(dir))
                        sb.Append(plat).Append(':').Append(Directory.GetLastWriteTimeUtc(dir).Ticks).Append(';');
                }
        }
        catch { }
        sb.Append('|');

        // MAME High Scores: the installed hiscore.dat files decide which machines qualify.
        try
        {
            foreach (var dat in Mame.HiscoreDat.DatFiles().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(dat);
                if (fi.Exists) sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
        }
        catch { }
        sb.Append('|');

        // Save vault: LiteBox's own index file — the save badges answer from it, not from disk walks.
        try
        {
            var fi = new FileInfo(LiteBoxPaths.File("saves-vault.json"));
            if (fi.Exists) sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
        }
        catch { }
        return sb.ToString();
    }

    // count + newest write over a folder's direct files: rename, add, delete and replace all move it.
    private static void StampDirFiles(StringBuilder sb, string dir, string pattern)
    {
        try
        {
            int n = 0; long newest = 0;
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            { n++; var t = File.GetLastWriteTimeUtc(f).Ticks; if (t > newest) newest = t; }
            sb.Append(System.IO.Path.GetFileName(dir)).Append(':').Append(n).Append(':').Append(newest).Append(';');
        }
        catch { }
    }

    // ── write ────────────────────────────────────────────────────────────────

    public static void Save(GameStore? store, BadgeTable table)
    {
        if (store == null) return;
        try
        {
            table.Snapshot(out var blob, out int blobLen, out var offset, out int count, out var variants);
            string stamp = Stamp(store);
            string tmp = Path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic); w.Write(Version); w.Write(stamp);

                w.Write(variants.Count);
                foreach (var v in variants) { w.Write(v.Image); w.Write(v.Detail); w.Write((int)v.Tint); }

                w.Write(count);
                w.Write(blobLen);
                w.Write(blob, 0, blobLen);
                for (int c = 0; c < count; c++) w.Write(offset[c]);

                // The per-game half: id → combination. Written by id rather than by row order, so a
                // library that gained or lost a game elsewhere cannot silently shift every badge.
                var rows = store.Rows;
                w.Write(rows.Length);
                for (int i = 0; i < rows.Length; i++)
                {
                    w.Write(rows[i].Id.ToByteArray());
                    w.Write(rows[i].BadgeCombo);
                }
            }
            try { File.Delete(Path); } catch { }
            File.Move(tmp, Path);
            long bytes = new FileInfo(Path).Length;
            LbLog.Info("badges", $"snapshot saved: {count - 1} combos, {store.Rows.Length} games, {bytes / 1024} KB");
        }
        catch (Exception ex) { LbLog.Warn("badges", "snapshot save failed: " + ex.Message); }
    }

    // ── read ─────────────────────────────────────────────────────────────────

    /// <summary>Restore the table and every game's combination. False when the file is missing, from
    /// another shape of catalog, or from a library that has moved since — the caller then runs the
    /// pass, which is exactly what used to happen every time.</summary>
    public static bool TryLoad(GameStore? store, BadgeTable table)
    {
        if (store == null || !File.Exists(Path)) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) return false;
            string stamp = r.ReadString();
            string now = Stamp(store);
            if (!string.Equals(stamp, now, StringComparison.Ordinal))
            {
                // Say WHICH part moved: "it changed" is not actionable, and a stamp that silently
                // never matches would turn this whole file into dead weight nobody notices.
                LbLog.Info("badges", "snapshot ignored: " + Difference(stamp, now));
                return false;
            }

            int nv = r.ReadInt32();
            var variants = new List<(string, string, BadgeTint)>(nv);
            for (int i = 0; i < nv; i++)
                variants.Add((r.ReadString(), r.ReadString(), (BadgeTint)r.ReadInt32()));

            int count = r.ReadInt32();
            int blobLen = r.ReadInt32();
            var blob = r.ReadBytes(blobLen);
            var offset = new int[Math.Max(count, 1)];
            for (int c = 0; c < count; c++) offset[c] = r.ReadInt32();

            int games = r.ReadInt32();
            var idBytes = new byte[16];
            var combos = new Dictionary<Guid, int>(games);
            for (int i = 0; i < games; i++)
            {
                if (r.Read(idBytes, 0, 16) != 16) return false;
                combos[new Guid(idBytes)] = r.ReadInt32();
            }

            table.Restore(blob, blobLen, offset, count, variants);
            int applied = store.ApplyBadgeCombos(combos);
            LbLog.Info("badges", $"snapshot loaded: {count - 1} combos, {applied}/{store.Rows.Length} games, "
                               + $"{sw.ElapsedMilliseconds} ms");
            return applied > 0;
        }
        catch (Exception ex)
        {
            LbLog.Warn("badges", "snapshot load failed: " + ex.Message);
            return false;
        }
    }

    // Which field of the stamp moved, in words. The layout is
    // version|pack|catalog|game count|platform files|pack art|manuals|hiscore dats|save vault.
    private static string Difference(string saved, string now)
    {
        var a = saved.Split('|');
        var b = now.Split('|');
        string[] names = { "the snapshot format", "the badge pack", "the badge catalog", "the game count",
                           "!files", "the badge pack's art", "a Manuals folder", "the installed hiscore.dat", "the save vault" };
        for (int i = 0; i < Math.Min(a.Length, b.Length) && i < names.Length; i++)
        {
            if (string.Equals(a[i], b[i], StringComparison.Ordinal)) continue;
            if (names[i] != "!files") return names[i] + " changed";
            var fa = a[i].Split(';');
            var fb = b[i].Split(';');
            for (int j = 0; j < Math.Max(fa.Length, fb.Length); j++)
            {
                string x = j < fa.Length ? fa[j] : "(missing)", y = j < fb.Length ? fb[j] : "(missing)";
                if (x != y) return $"a platform file changed: {x} -> {y}";
            }
            return "the platform files changed";
        }
        return a.Length != b.Length ? "the stamp layout changed" : "the stamp changed";
    }

    public static void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { }
    }
}
