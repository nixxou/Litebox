// Files that are referenced by an explicit PATH, and are therefore not ours to rename.
//
// Most media are found by CONVENTION: the file is called <title>-NN and whoever wants it derives the
// name from the game. Rename the game, rename the files, everything still resolves. That is the whole
// premise of GameMediaRenamer.
//
// A handful are not. <ManualPath>, <MusicPath>, <VideoPath> and <ThemeVideoPath> on a <Game>, and the
// <ApplicationPath> of a document additional-application, store a path VERBATIM. LaunchBox lets them
// point anywhere, and the name carries no meaning — the reference is the name. Renaming such a file
// does not move a reference, it breaks one, silently and permanently.
//
// The overlap is real: those paths may perfectly well point INSIDE Manuals\<Platform>\ (the document
// editor even prefers to put them there), which is exactly where the renamer works, and a file called
// "Foo-01.pdf" there looks conventional whether or not something points at it.
//
// So the rule is not "leave manuals alone" — 1375 manuals in the real library are conventional and
// would be stranded by their game the moment it is renamed. The rule is narrower and follows the
// reason: a file someone has pinned by path is skipped, whatever folder it sits in.
//
// FAIL-SAFE, NOT FAIL-OPEN. If this cannot answer, it must claim MORE files rather than fewer — an
// over-protected file is merely left behind, an under-protected one is a broken reference. Hence the
// XML on disk as the default source: it needs nothing initialised and cannot silently return empty
// because a probe skipped the boot sequence. Provider only ADDS to it.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace LbApiHost.Host.Media;

internal static class PinnedMedia
{
    /// <summary>The four &lt;Game&gt; fields that store a path verbatim instead of deriving one.</summary>
    private static readonly string[] GamePathFields =
        { "ManualPath", "MusicPath", "VideoPath", "ThemeVideoPath" };

    /// <summary>Optional hook so pinned paths can include edits not yet flushed to disk. It ADDS to
    /// what the XML says; it never replaces it, so a provider that returns nothing cannot un-pin
    /// anything.</summary>
    internal static Func<string, IEnumerable<string>>? Provider;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, (DateTime Stamp, long Size, HashSet<string> Paths)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Absolute paths that something references by name, for one platform.</summary>
    public static HashSet<string> For(string lbRoot, string platform)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(lbRoot) || string.IsNullOrEmpty(platform)) return set;

        string xml = Path.Combine(lbRoot, "Data", "Platforms", platform + ".xml");
        foreach (var p in FromXml(xml)) Add(set, lbRoot, p);

        if (Provider != null)
            try { foreach (var p in Provider(platform)) Add(set, lbRoot, p); }
            catch { /* the XML already answered; a failing hook must not shrink the set */ }

        return set;
    }

    /// <summary>True when this file is referenced by path and must not be renamed, moved or deleted
    /// by anything that works by convention.</summary>
    public static bool IsPinned(IReadOnlyCollection<string>? pinned, string file)
        => pinned != null && pinned.Count > 0 && pinned.Contains(Full(file));

    // Re-parsing the platform XML on every rename would be wasteful and the file rarely changes, so
    // the result is cached against its timestamp AND its length — a same-second write that keeps the
    // stamp is common enough on Windows that the stamp alone is not a safe key.
    private static IEnumerable<string> FromXml(string xml)
    {
        FileInfo fi;
        try { fi = new FileInfo(xml); if (!fi.Exists) return Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }

        lock (_lock)
            if (_cache.TryGetValue(xml, out var hit) && hit.Stamp == fi.LastWriteTimeUtc && hit.Size == fi.Length)
                return hit.Paths;

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Load(xml);
            var root = doc.Root;
            if (root != null)
            {
                foreach (var g in root.Elements("Game"))
                    foreach (var f in GamePathFields)
                    {
                        string v = ((string?)g.Element(f) ?? "").Trim();
                        if (v.Length > 0) found.Add(v);
                    }
                // Documents are additional applications; their ApplicationPath is a real path, while a
                // VERSION's ApplicationPath is a rom. Only the documents are pinned here — a rom is not
                // media and never lands in the folders the renamer walks.
                foreach (var a in root.Elements("AdditionalApplication"))
                {
                    if (!string.Equals(((string?)a.Element("Section") ?? "").Trim(), "Document",
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    string v = ((string?)a.Element("ApplicationPath") ?? "").Trim();
                    if (v.Length > 0) found.Add(v);
                }
            }
        }
        catch { return Array.Empty<string>(); }

        lock (_lock) _cache[xml] = (fi.LastWriteTimeUtc, fi.Length, found);
        return found;
    }

    /// <summary>LaunchBox stores these relative to the LB root when they sit under it, absolute
    /// otherwise — the same two-shape rule the document editor writes with.</summary>
    private static void Add(HashSet<string> set, string lbRoot, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return;
        try
        {
            set.Add(Path.IsPathRooted(stored)
                ? Path.GetFullPath(stored)
                : Path.GetFullPath(Path.Combine(lbRoot, stored)));
        }
        catch { /* an unparseable path pins nothing; it also names no file we could move */ }
    }

    private static string Full(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }
}
