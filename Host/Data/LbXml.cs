// Writing a LaunchBox XML file the way LaunchBox writes one.
//
// Nearly everything about XDocument's default output already matches theirs — CRLF, two-space
// indent, no trailing newline — except the two things it decides on your behalf: it prepends a
// UTF-8 BOM and injects encoding="utf-8" into the declaration. LaunchBox does neither. Its files
// are UTF-8 all the same, stored as raw bytes with no announcement ("ROM importée" is C3 A9 in
// their own output), which is precisely what a declaration without an encoding means per the XML
// spec — and what every reader on both sides already assumes.
//
// Left alone the two writers differ on line 1 of every file either has ever touched: 55 of them
// carry LaunchBox's form, 10 carry ours. Nothing breaks — both readers cope with both — but it
// makes a LiteBox-written file impossible to diff against a LaunchBox-written one, and that
// comparison is how this project checks its own work against the real thing.
//
// Cost is nil: same document, same serializer, one less BOM.

#nullable enable

using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LbApiHost.Host.Data;

internal static class LbXml
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The declaration LaunchBox writes, verbatim. Emitted by hand because an XmlWriter
    /// always advertises its own encoding, which is the difference we are removing.</summary>
    private const string Declaration = "<?xml version=\"1.0\" standalone=\"yes\"?>\r\n";

    /// <summary>Serializes <paramref name="doc"/> to <paramref name="path"/> byte-for-byte the way
    /// LaunchBox would.</summary>
    public static void Save(XDocument doc, string path)
    {
        // Encoding is not set here on purpose: XmlWriter.Create(TextWriter) takes the writer's, and
        // stating a second one would only suggest it had a say.
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
        };

        using var stream = new StreamWriter(path, append: false, Utf8NoBom);
        stream.Write(Declaration);
        using var writer = XmlWriter.Create(stream, settings);
        doc.Save(writer);
    }
}
