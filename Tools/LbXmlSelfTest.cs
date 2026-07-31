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
        Console.WriteLine(fail == 0 ? "[lbxml] ALL PASS" : $"[lbxml] {fail} FAILED");
        return fail == 0 ? 0 : 1;
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
