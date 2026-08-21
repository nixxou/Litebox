// Self-test for GameCachePatch (--patch-selftest [<game title>], headless).
//
// The one invariant the incremental patch rests on: a patched image array must be INDISTINGUISHABLE
// from the one a real scan would have produced. If it ever drifts, the media cache starts answering
// things the disk does not say — and every surface that reads it (the poster, the matrix, the detail
// pane) inherits the lie. So the test does not check the patch against its own idea of correctness:
// it patches, dumps the array, runs a genuine RebuildPlatform, dumps again, and compares.
//
// It writes 2×2 PNGs into an EMPTY slot of a real game and removes them again, so it leaves the
// library exactly as it found it. Three scenarios, mirroring what the editor actually does:
//   A. a plain "{title}-NN.png"                       — add, then delete
//   B. a GUID-named "{title}.{guid}-NN.png"           — add, then delete
//   C. the GUID file landing on top of the plain one  — the per-type GUID filter, which Freeze applies
//      and the patch has to apply too, and then the reverse: deleting that GUID file has to bring the
//      plain one back, which means reading it off the folder (the array never held it).
//   D. "-95" beside "-095", and E. ".{guid}-94" beside ".{guid}-231-359-94" — two files that share a
//      number but not a name. The scanner keeps the suffix width and the middle segments and rebuilds
//      file names out of them, so a slot key that stops at the number silently merges two real files.
//   F. scenario C in the order the editor actually produces (GUID first, plain after).
//
// Why the scan is polled instead of trusted on the first try
//   When Everything is available the scan reads ITS index, not the directory — and that index catches
//   up with a file we just wrote or deleted a moment later. That lag is real (this test caught it) but
//   it is not what is under test, so each comparison waits for the scan to agree with the shape the
//   disk is in before it compares. A patch that is right only because the scan was stale would still
//   fail here: the two dumps are compared in full, once the scan has settled.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Gc
{
    internal static class GameCachePatchSelfTest
    {
        private static int _fail;

        /// <summary>Runs the scenarios. Returns the number of failures (0 = pass).</summary>
        public static int Run(string wantedTitle)
        {
            _fail = 0;
            if (!HostGameCache.Enabled) { Log("SKIP: the game cache is off (UseGameCache=false) — nothing to patch"); return 0; }

            for (int i = 0; i < 60 && !GameCache.IsGlobalReady; i++) Thread.Sleep(500);
            if (!GameCache.IsGlobalReady) { Log("FAIL: the cache never became ready"); return 1; }

            var subject = Pick(wantedTitle);
            if (subject == null) { Log("SKIP: no game with a resolvable empty slot to write into"); return 0; }
            var (game, plat, id, regroupement, type, folder, sani) = subject.Value;
            Log($"subject: \"{Safe(() => game.Title)}\" [{plat}] · slot {regroupement} (type \"{type}\") · {folder}");

            string plain = Path.Combine(folder, $"{sani}-97.png");
            string guid = Path.Combine(folder, $"{sani}.{id}-96.png");
            string plainRow = $"{type}|97|none|plain|Png|";
            string guidRow = $"{type}|96|none|guid|Png|";

            // Two files the SLOT key must not confuse — same number, different filename. Both are shapes the
            // scanner keeps apart (it stores the suffix width and the GUID middle, and rebuilds the file name
            // out of them), so the patch has to keep them apart too.
            string pad = Path.Combine(folder, $"{sani}-95.png");        // "-95"
            string padWide = Path.Combine(folder, $"{sani}-095.png");   // "-095": same number, wider suffix
            string padRow = $"{type}|95|none|plain|Png|";
            string padWideRow = $"{type}|095|none|plain|Png|";

            string mid = Path.Combine(folder, $"{sani}.{id}-94.png");
            string midSeg = Path.Combine(folder, $"{sani}.{id}-231-359-94.png");   // same number, middle segments
            string midRow = $"{type}|94|none|guid|Png|";
            string midSegRow = $"{type}|94|none|guid|Png|-231-359";

            string late = Path.Combine(folder, $"{sani}-92.png");       // a plain file added AFTER a GUID one
            string lateRow = $"{type}|92|none|plain|Png|";
            string first = Path.Combine(folder, $"{sani}.{id}-93.png");
            string firstRow = $"{type}|93|none|guid|Png|";

            try
            {
                Log("A · plain name");
                Write(plain); Patch(plain);
                Compare("A: after add", plat, id, (plainRow, true), (guidRow, false));
                Delete(plain); Patch(plain);
                Compare("A: after delete", plat, id, (plainRow, false), (guidRow, false));

                Log("B · GUID name");
                Write(guid); Patch(guid);
                Compare("B: after add", plat, id, (guidRow, true), (plainRow, false));
                Delete(guid); Patch(guid);
                Compare("B: after delete", plat, id, (guidRow, false), (plainRow, false));

                // C — both at once: the GUID file must hide the plain one, in the patch exactly as in a scan.
                Log("C · GUID over plain");
                Write(plain); Patch(plain);
                Write(guid); Patch(guid);
                Compare("C: both present", plat, id, (guidRow, true), (plainRow, false));

                // Removing the last GUID file of a type brings back what it hid — the plain file is still
                // on disk and pickable again, and the array never held it. This is the case the patch has
                // to read back from the folder itself.
                Delete(guid); Patch(guid);
                Compare("C: GUID gone, plain back", plat, id, (plainRow, true), (guidRow, false));
                Delete(plain); Patch(plain);
                Compare("C: after cleanup", plat, id, (plainRow, false), (guidRow, false));

                // D — "-95" and "-095" are two files with the same number. A slot key that stops at the
                // number swallows the second add and takes both out on the first delete.
                Log("D · same number, different suffix width");
                Write(pad); Patch(pad);
                Write(padWide); Patch(padWide);
                Compare("D: both present", plat, id, (padRow, true), (padWideRow, true));
                Delete(padWide); Patch(padWide);
                Compare("D: the wide one gone", plat, id, (padRow, true), (padWideRow, false));
                Delete(pad); Patch(pad);
                Compare("D: after cleanup", plat, id, (padRow, false), (padWideRow, false));

                // E — same, for the segments LaunchBox can put between the GUID and the number.
                Log("E · same number, GUID middle segments");
                Write(mid); Patch(mid);
                Write(midSeg); Patch(midSeg);
                Compare("E: both present", plat, id, (midRow, true), (midSegRow, true));
                Delete(midSeg); Patch(midSeg);
                Compare("E: the segmented one gone", plat, id, (midRow, true), (midSegRow, false));
                Delete(mid); Patch(mid);
                Compare("E: after cleanup", plat, id, (midRow, false), (midSegRow, false));

                // F — scenario C in the other order, which is the one the editor really produces: the GUID
                // file is already there (in ANOTHER region folder, in the field) and a plain file is added
                // afterwards. A scan hides it; the patch must hide it too, and hand it back when the GUID
                // file goes away.
                Log("F · plain added while a GUID file already exists");
                Write(first); Patch(first);
                Compare("F: GUID alone", plat, id, (firstRow, true), (lateRow, false));
                Write(late); Patch(late);
                Compare("F: plain stays hidden", plat, id, (firstRow, true), (lateRow, false));
                Delete(first); Patch(first);
                Compare("F: GUID gone, plain back", plat, id, (firstRow, false), (lateRow, true));
                Delete(late); Patch(late);
                Compare("F: after cleanup", plat, id, (firstRow, false), (lateRow, false));
            }
            catch (Exception ex) { Fail("threw: " + ex); }
            finally
            {
                foreach (var p in new[] { plain, guid, pad, padWide, mid, midSeg, late, first })
                { try { if (File.Exists(p)) File.Delete(p); } catch { } }
                Rebuild(plat);   // whatever happened above, leave the cache matching the disk
            }

            Log(_fail == 0 ? "PASS" : $"FAIL: {_fail} mismatch(es)");
            return _fail;
        }

        /// <summary>THE assertion: the patched array, then the same array as a real scan builds it. The
        /// expectations pin the shape both are supposed to have, so a scan that has not caught up yet is
        /// waited for rather than compared against.</summary>
        private static void Compare(string what, string plat, Guid id, params (string row, bool present)[] expected)
        {
            string patched = Dump(plat, id);
            foreach (var (row, present) in expected)
                if (Holds(patched, row) != present)
                    Fail($"{what}: the PATCHED array should{(present ? "" : " not")} hold \"{row}\"\n     patched: {patched}");

            string scanned = null;
            for (int i = 0; i < 20; i++)
            {
                Rebuild(plat);
                scanned = Dump(plat, id);
                if (expected.All(e => Holds(scanned, e.row) == e.present)) break;
                Thread.Sleep(500);   // Everything's index has not caught up with the disk yet
                scanned = null;
            }
            if (scanned == null) { Fail($"{what}: the scan never came to agree with the disk (Everything index lag?)"); return; }

            if (patched == scanned) { Log($"   ok: {what} — {Count(patched)} image(s), patch == scan"); return; }
            Fail($"{what}: patch != scan\n     patched: {patched}\n     scanned: {scanned}");
        }

        /// <summary>The game's image array, normalized. FileSize is left out on purpose: the scan fills it
        /// lazily (-1 until someone asks), the patch knows it outright, and neither changes what is picked.</summary>
        private static string Dump(string plat, Guid id)
        {
            var g = Game(plat, id);
            if (g == null) return "(game gone)";
            var rows = g.Images.Select(i =>
                $"{i.GetImageTypeName()}|{i.GetNumText()}|{i.Region}|{(i.HasGuid ? "guid" : "plain")}|{i.Ext}|{i.GuidMiddle ?? ""}")
                .OrderBy(s => s, StringComparer.Ordinal);
            return string.Join("  ", rows);
        }

        private static int Count(string dump) => string.IsNullOrEmpty(dump) ? 0 : dump.Split("  ").Length;

        /// <summary>Whole-row membership, never a substring: a row with no GUID middle segment is a PREFIX
        /// of the same row WITH one, so "contains" would call a missing entry present.</summary>
        private static bool Holds(string dump, string row)
            => !string.IsNullOrEmpty(dump) && dump.Split("  ").Any(r => string.Equals(r, row, StringComparison.Ordinal));

        /// <summary>--patch-bench [platform]: what a BULK session costs. Patching is per file now, so the
        /// honest question is whether N patches beat the one platform sweep they replace. Both are timed
        /// here on the same platform, over its real image files. Reads only — nothing is written.</summary>
        public static void Bench(string wantedPlatform)
        {
            if (!HostGameCache.Enabled) { Log("SKIP: the game cache is off"); return; }
            for (int i = 0; i < 60 && !GameCache.IsGlobalReady; i++) Thread.Sleep(500);
            if (!GameCache.IsGlobalReady) { Log("the cache never became ready"); return; }

            // The busiest platform, or the named one — the bigger its Images tree, the more the sweep costs
            // and the less the patch does.
            var plat = (GameCache.Platforms ?? new Dictionary<string, GameCachePlatform>())
                .Where(kv => string.IsNullOrEmpty(wantedPlatform)
                             || kv.Key.IndexOf(wantedPlatform, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(kv => kv.Value?.GamesByUUID?.Values.Sum(g => g.Images.Length) ?? 0)
                .Select(kv => kv.Value).FirstOrDefault();
            if (plat?.GamesByUUID == null) { Log("no platform to measure"); return; }

            var paths = new List<string>();
            foreach (var g in plat.GamesByUUID.Values)
                foreach (var img in g.Images)
                {
                    string p = plat.ResolveImagePath(g, img);
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
            if (paths.Count == 0) { Log($"\"{plat.Name}\" has no image to measure"); return; }

            Log($"platform \"{plat.Name}\": {plat.GamesByUUID.Count} games, {paths.Count} image file(s) on record");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int placed = 0;
            foreach (var p in paths) if (GameCacheBridge.PatchImage(p, out _)) placed++;
            sw.Stop();
            double perFile = sw.Elapsed.TotalMilliseconds / paths.Count;
            Log($"patch: {paths.Count} file(s) in {sw.ElapsedMilliseconds} ms — {perFile:0.000} ms each ({placed} placed)");

            var p2 = PluginHelper.DataManager?.GetPlatformByName(plat.Name);
            if (p2 == null) { Log("platform object gone — cannot time the sweep"); return; }
            sw.Restart();
            GameCache.RebuildPlatform(p2, wait: true).Wait();
            sw.Stop();
            Log($"sweep ({(EverythingBridge.IsEverythingAvailable() ? "Everything" : "directory walk")})"
                + $": ONE RebuildPlatform in {sw.ElapsedMilliseconds} ms"
                + $" — the patch breaks even at {(perFile > 0 ? (int)(sw.Elapsed.TotalMilliseconds / perFile) : 0)} file(s)");
        }

        private static GameCacheGame Game(string plat, Guid id)
            => GameCache.Platforms != null && GameCache.Platforms.TryGetValue(plat, out var p) && p?.GamesByUUID != null
               && p.GamesByUUID.TryGetValue(id, out var g) ? g : null;

        private static void Rebuild(string plat)
        {
            try
            {
                var p = PluginHelper.DataManager?.GetPlatformByName(plat);
                if (p != null) GameCache.RebuildPlatform(p, wait: true).Wait();
            }
            catch (Exception ex) { Fail("rebuild threw: " + ex.Message); }
        }

        private static void Patch(string path)
        {
            if (!GameCacheBridge.PatchImage(path, out var resolved))
                Fail($"the patch could not place \"{Path.GetFileName(path)}\" (platform={resolved ?? "?"})");
        }

        private static void Write(string path)
        {
            using var bmp = new Bitmap(2, 2);
            bmp.SetPixel(0, 0, Color.Magenta);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bmp.Save(path, ImageFormat.Png);
        }

        private static void Delete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Fail($"could not delete \"{path}\": {ex.Message}"); }
        }

        /// <summary>A game whose chosen slot is EMPTY in the cache — so an added file is unambiguously the
        /// one being resolved, and removing it puts the slot back exactly where it was.</summary>
        private static (IGame game, string plat, Guid id, string regroupement, string type, string folder, string sani)? Pick(string wantedTitle)
        {
            IGame[] games;
            try { games = PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>(); } catch { return null; }
            if (!string.IsNullOrEmpty(wantedTitle))
                games = games.Where(g => (Safe(() => g.Title) ?? "").IndexOf(wantedTitle, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

            var regroupements = SettingsWatcher.GetImageRegroupementPriorities();
            foreach (var g in games)
            {
                string plat = Safe(() => g.Platform) ?? "";
                if (plat.Length == 0 || !HostGameCache.Ready(plat)) continue;
                if (!Guid.TryParse(Safe(() => g.Id) ?? "", out var id)) continue;
                string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
                if (sani.Length == 0) continue;

                foreach (var kv in regroupements)
                {
                    if (kv.Value == null || kv.Value.Count == 0) continue;
                    if (HostGameCache.BestImageTypeFirst(plat, id, kv.Key) != null) continue;   // not empty
                    string type = kv.Value[0];
                    string folder = MediaResolver.TypeFolder(plat, type);
                    if (string.IsNullOrEmpty(folder)) continue;
                    return (g, plat, id, kv.Key, type, folder, sani);
                }
            }
            return null;
        }

        private static void Fail(string msg) { _fail++; Log("FAIL: " + msg); }
        private static void Log(string msg) => Console.WriteLine("[patchtest] " + msg);

        private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    }
}
