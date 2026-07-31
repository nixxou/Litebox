// Proves LbXml.Save produces what LaunchBox produces, rather than merely claiming it.
//
// The synthetic half is the guard that always runs: a document shaped like a platform file, written
// out, compared byte for byte against the exact bytes LaunchBox emits — declaration, BOM (absent),
// CRLF, two-space indent, no trailing newline, and non-ASCII stored raw.
//
// The sweep half is the honest one: given a real LaunchBox root, it loads every XML LaunchBox
// itself wrote and rewrites it through us. Byte-identical output means the two writers are
// interchangeable on real data, which no synthetic case can establish on its own. Files carrying a
// BOM are skipped and counted — those were written by an older LiteBox, so they are not evidence
// about LaunchBox either way.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using LbApiHost.Host.Data;

namespace LbApiHost.Tools;

internal static class LbXmlSelfTest
{
    public static int Run(string? lbRoot)
    {
        int fail = 0;
        fail += Synthetic();
        if (!string.IsNullOrEmpty(lbRoot)) fail += Sweep(lbRoot);
        if (!string.IsNullOrEmpty(lbRoot)) fail += ChildRoundTrip(lbRoot);
        Console.WriteLine(fail == 0 ? "[lbxml] ALL PASS" : $"[lbxml] {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }

    // The one that actually guards ChildExtras. A game's child collections are rebuilt from scratch
    // whenever any of them is edited, so this forces that rebuild for every game in a real platform
    // file — without changing anything — and demands the bytes come back identical. A field the
    // model does not know about (LaunchBox's GogAppId / OriginAppId / OriginInstallPath today, its
    // next addition tomorrow) fails this the moment it stops round-tripping.
    private static int ChildRoundTrip(string lbRoot)
    {
        string srcDir = Path.Combine(lbRoot, "Data", "Platforms");
        if (!Directory.Exists(srcDir)) { Console.WriteLine("[lbxml] no Platforms dir, child round-trip skipped"); return 0; }

        // One platform per child entity — the smallest that contains it, so all four rebuild paths
        // are covered without reloading the whole library. A file serving two entities is tested once.
        // BOM-carrying files were written by an older LiteBox, so they are not a reference for what
        // LaunchBox emits — comparing against one would only re-measure the BOM we now drop.
        var files = Directory.EnumerateFiles(srcDir, "*.xml")
            .Where(f => { try { using var s = File.OpenRead(f); return !(s.ReadByte() == 0xEF && s.ReadByte() == 0xBB && s.ReadByte() == 0xBF); } catch { return false; } })
            .OrderBy(f => new FileInfo(f).Length).ToList();
        var picked = new List<string>();
        foreach (string ent in new[] { "AdditionalApplication", "AlternateName", "Mount", "CustomField" })
        {
            string? hit = files.FirstOrDefault(f =>
            { try { return File.ReadAllText(f).Contains("<" + ent + ">"); } catch { return false; } });
            if (hit == null) Console.WriteLine($"[lbxml] no platform has <{ent}> — not covered");
            else if (!picked.Contains(hit)) picked.Add(hit);
        }
        if (picked.Count == 0) { Console.WriteLine("[lbxml] no platform with child entities, round-trip skipped"); return 0; }

        int bad = 0;
        foreach (string s in picked) bad += RoundTripOne(s);
        bad += PlaylistRoundTrip(lbRoot);
        bad += FutureField(lbRoot, picked);
        return bad;
    }

    // The question this whole design exists to answer: if a future LaunchBox writes a field we have
    // never heard of, does it still exist after LiteBox has edited that entry?
    //
    // So invent one. Inject <LiteBoxFutureField> — once with a value, once empty, since the two are
    // handled by different branches — into every child element of a real file, force the rebuild,
    // and count how many come back. Reading the code cannot answer this; only running it can.
    private static int FutureField(string lbRoot, List<string> platformFiles)
    {
        const string Tag = "LiteBoxFutureField", Val = "survivor";
        int bad = 0;

        foreach (string src in platformFiles)
        {
            string work = Path.Combine(Path.GetTempPath(), "lbxml-fut-" + Guid.NewGuid().ToString("N"));
            string plats = Path.Combine(work, "Platforms");
            Directory.CreateDirectory(plats);
            string copy = Path.Combine(plats, Path.GetFileName(src));
            try
            {
                // Inject into the child entities only — <Game> takes a different (in-place) path.
                string text = File.ReadAllText(src);
                int injected = 0, injectedEmpty = 0;
                foreach (string ent in new[] { "AdditionalApplication", "AlternateName", "Mount", "CustomField" })
                {
                    string open = "<" + ent + ">\r\n";
                    injected += Count(text, open);
                    text = text.Replace(open, open + $"    <{Tag}>{Val}</{Tag}>\r\n    <{Tag}Empty />\r\n");
                    injectedEmpty = injected;
                }
                if (injected == 0) continue;
                File.WriteAllText(copy, text, new UTF8Encoding(false));

                var store = GameStore.Load(plats, Path.Combine(work, "ops.db"));
                store.ReadOnly = false;
                foreach (var row in store.Rows)
                    foreach (string entity in new[] { "AdditionalApplication", "AlternateName", "Mount", "CustomField" })
                        store.RecordChildReplace(row.Id, entity);
                store.Flush();
                store.CloseLog();

                string after = File.ReadAllText(copy);
                int kept = Count(after, $"<{Tag}>{Val}</{Tag}>");
                int keptEmpty = Count(after, $"<{Tag}Empty />");
                string name = Path.GetFileName(src);
                if (kept == injected && keptEmpty == injectedEmpty)
                    Console.WriteLine($"[lbxml] future field: {name}, {kept}/{injected} with a value and {keptEmpty}/{injectedEmpty} empty survived a rebuild");
                else
                {
                    Console.WriteLine($"[lbxml] FAIL future field: {name}, {kept}/{injected} with a value, {keptEmpty}/{injectedEmpty} empty");
                    bad++;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[lbxml] FAIL future field: {ex.Message}"); bad++; }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
        return bad;
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // Playlist children are rebuilt by the same replace machinery, from a different branch. Same
    // demand: force the rebuild, change nothing, expect the same bytes.
    private static int PlaylistRoundTrip(string lbRoot)
    {
        string srcDir = Path.Combine(lbRoot, "Data", "Playlists");
        if (!Directory.Exists(srcDir)) { Console.WriteLine("[lbxml] no Playlists dir, skipped"); return 0; }

        string work = Path.Combine(Path.GetTempPath(), "lbxml-pl-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(work, "Data");
        Directory.CreateDirectory(Path.Combine(data, "Playlists"));
        Directory.CreateDirectory(Path.Combine(data, "Platforms"));
        try
        {
            var originals = new Dictionary<string, byte[]>();
            foreach (string f in Directory.EnumerateFiles(srcDir, "*.xml"))
            {
                string dst = Path.Combine(data, "Playlists", Path.GetFileName(f));
                File.Copy(f, dst);
                originals[dst] = File.ReadAllBytes(dst);
            }
            if (originals.Count == 0) { Console.WriteLine("[lbxml] no playlists, skipped"); return 0; }

            var store = GameStore.Load(Path.Combine(data, "Platforms"), Path.Combine(work, "ops.db"));
            store.ReadOnly = false;
            int games = 0, filters = 0;
            foreach (var pl in PlaylistCatalog.Load(data, Path.Combine(work, "Images")))
            {
                pl.Attach(store);
                pl.RecordGames();
                pl.RecordFilters();
                games += pl.GetAllPlaylistGames().Length; filters += pl.GetAllPlaylistFilters().Length;
            }
            store.Flush();
            store.CloseLog();

            int differ = originals.Count(kv => !File.ReadAllBytes(kv.Key).SequenceEqual(kv.Value));
            if (differ == 0)
            {
                Console.WriteLine($"[lbxml] playlist round-trip: {originals.Count} files, {games} games + {filters} filters rebuilt, byte-identical");
                return 0;
            }
            foreach (var kv in originals.Where(kv => !File.ReadAllBytes(kv.Key).SequenceEqual(kv.Value)).Take(3))
                Console.WriteLine($"[lbxml] FAIL playlist round-trip: {Path.GetFileName(kv.Key)} differs");
            return differ;
        }
        catch (Exception ex) { Console.WriteLine($"[lbxml] FAIL playlist round-trip: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static int RoundTripOne(string src)
    {

        string work = Path.Combine(Path.GetTempPath(), "lbxml-child-" + Guid.NewGuid().ToString("N"));
        string plats = Path.Combine(work, "Platforms");
        Directory.CreateDirectory(plats);
        string copy = Path.Combine(plats, Path.GetFileName(src));
        try
        {
            File.Copy(src, copy);
            byte[] before = File.ReadAllBytes(copy);

            var store = GameStore.Load(plats, Path.Combine(work, "ops.db"));
            store.ReadOnly = false;
            int touched = 0;
            foreach (var row in store.Rows)
                foreach (string entity in new[] { "AdditionalApplication", "AlternateName", "Mount", "CustomField" })
                {
                    int n = entity switch
                    {
                        "AdditionalApplication" => store.AddAppsFor(row.Id).Count,
                        "AlternateName" => store.AltNamesFor(row.Id).Count,
                        "Mount" => store.MountsFor(row.Id).Count,
                        _ => store.CustomFieldsFor(row.Id).Count,
                    };
                    if (n == 0) continue;
                    store.RecordChildReplace(row.Id, entity);
                    touched += n;
                }
            store.Flush();
            store.CloseLog();

            byte[] after = File.ReadAllBytes(copy);
            if (after.SequenceEqual(before))
            {
                Console.WriteLine($"[lbxml] child round-trip: {Path.GetFileName(src)}, {touched} children rebuilt, byte-identical");
                return 0;
            }

            int i = 0;
            while (i < after.Length && i < before.Length && after[i] == before[i]) i++;
            Console.WriteLine($"[lbxml] FAIL child round-trip: {Path.GetFileName(src)} differs at byte {i}");
            Console.WriteLine($"[lbxml]   was:  {Excerpt(before, i)}");
            Console.WriteLine($"[lbxml]   now:  {Excerpt(after, i)}");
            return 1;
        }
        catch (Exception ex) { Console.WriteLine($"[lbxml] FAIL child round-trip: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static string Excerpt(byte[] b, int at)
    {
        int from = Math.Max(0, at - 40), len = Math.Min(110, b.Length - from);
        return len <= 0 ? "<end>" : new UTF8Encoding(false).GetString(b, from, len).Replace("\r", "").Replace("\n", "⏎");
    }

    private static int Synthetic()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),          // deliberately wrong: must be ignored
            new XElement("LaunchBox",
                new XElement("Game",
                    new XElement("Title", "Ridge Racer"),
                    new XElement("Status", "ROM importée"),    // non-ASCII, stored raw
                    new XElement("Series"))));                 // empty → self-closing

        const string want =
            "<?xml version=\"1.0\" standalone=\"yes\"?>\r\n" +
            "<LaunchBox>\r\n" +
            "  <Game>\r\n" +
            "    <Title>Ridge Racer</Title>\r\n" +
            "    <Status>ROM importée</Status>\r\n" +
            "    <Series />\r\n" +
            "  </Game>\r\n" +
            "</LaunchBox>";

        string tmp = Path.Combine(Path.GetTempPath(), "lbxml-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            LbXml.Save(doc, tmp);
            byte[] got = File.ReadAllBytes(tmp);
            byte[] expect = new UTF8Encoding(false).GetBytes(want);
            if (got.SequenceEqual(expect)) { Console.WriteLine("[lbxml] synthetic: byte-exact"); return 0; }

            Console.WriteLine($"[lbxml] FAIL synthetic: {got.Length} bytes, expected {expect.Length}");
            int i = 0;
            while (i < got.Length && i < expect.Length && got[i] == expect[i]) i++;
            Console.WriteLine($"[lbxml]   first difference at byte {i}: got {Show(got, i)} want {Show(expect, i)}");
            return 1;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static int Sweep(string lbRoot)
    {
        string data = Path.Combine(lbRoot, "Data");
        if (!Directory.Exists(data)) { Console.WriteLine($"[lbxml] no Data under {lbRoot}, sweep skipped"); return 0; }

        int same = 0, differ = 0, skipped = 0;
        foreach (string f in Directory.EnumerateFiles(data, "*.xml", SearchOption.AllDirectories))
        {
            byte[] original;
            try { original = File.ReadAllBytes(f); } catch { continue; }
            if (original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF) { skipped++; continue; }

            string tmp = Path.Combine(Path.GetTempPath(), "lbxml-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                LbXml.Save(XDocument.Load(f, LoadOptions.None), tmp);
                if (File.ReadAllBytes(tmp).SequenceEqual(original)) same++;
                else { differ++; Console.WriteLine($"[lbxml] FAIL differs: {Path.GetFileName(f)}"); }
            }
            catch (Exception ex) { differ++; Console.WriteLine($"[lbxml] FAIL {Path.GetFileName(f)}: {ex.Message}"); }
            finally { try { File.Delete(tmp); } catch { } }
        }
        Console.WriteLine($"[lbxml] sweep: {same} byte-identical, {differ} differ, {skipped} skipped (BOM — not LaunchBox's)");
        return differ;
    }

    private static string Show(byte[] b, int i) => i < b.Length ? $"0x{b[i]:X2}" : "<end>";
}
