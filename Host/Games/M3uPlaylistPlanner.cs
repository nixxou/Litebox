using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Games;

/// <summary>
/// Selects the logical media set for an auto-generated M3U. The launched version is the anchor:
/// disc versions compete one-per-disc, side versions one-per-side, and combined versions one-per
/// (disc, side). Region and Version only PRIORITISE candidates; they never exclude a fallback.
///
/// Identity is FIELDS ONLY — IAdditionalApplication.Disc and SideA/SideB. The combine already
/// parsed every filename once, with the measured LaunchBox parser, when it created the versions
/// (the root included, via its self-version); re-parsing names here could only disagree with it.
/// A default launch (no version chosen) borrows its fields from the version registered on the
/// launched file.
///
/// Degraded mode: an identified anchor whose siblings carry NO disc/side fields means the fields
/// are broken, not the game — every dropped version is appended instead, best score first, then
/// filename order. No identifiable anchor, or a single-entry set → no playlist.
/// </summary>
internal static class M3uPlaylistPlanner
{
    private sealed class Candidate
    {
        public string Path = "";
        public int? Disc;
        public char? Side;
        public string Region = "";
        public string Version = "";
        public string Name = "";
        public int Order;
    }

    private readonly record struct MediaKey(int? Disc, char? Side);

    /// <summary>
    /// Returns the selected paths in launch order (the anchor's bucket first, always the exact
    /// launched path), or null when no version identifies the launched file as a disc or a side —
    /// or when the set would hold a single line (a one-entry playlist is never worth writing).
    /// </summary>
    internal static IReadOnlyList<string> Plan(
        IGame game,
        IAdditionalApplication selectedApp,
        string launchedAbsolutePath,
        IEnumerable<IAdditionalApplication> additionalApps,
        Func<string, string> resolveAbsolute,
        Action<string> log = null)
    {
        if (game == null || string.IsNullOrWhiteSpace(launchedAbsolutePath)) return null;
        resolveAbsolute ??= p => p;

        var apps = (additionalApps ?? Array.Empty<IAdditionalApplication>()).Where(a => a != null).ToList();

        Candidate anchor;
        if (selectedApp != null) anchor = FromApp(selectedApp, launchedAbsolutePath, -1);
        else
        {
            // Default launch: the combine gave the root a self-version pointing at this same file —
            // that version carries the Disc/Side fields IGame itself does not have.
            var self = apps
                .Where(a => !IsAutoRun(a) && SamePath(Resolve(resolveAbsolute, Safe(() => a.ApplicationPath)), launchedAbsolutePath))
                .Select(a => FromApp(a, launchedAbsolutePath, -1)).ToList();
            anchor = self.FirstOrDefault(c => c.Disc.HasValue || c.Side.HasValue) ?? self.FirstOrDefault();
        }
        if (anchor == null || (!anchor.Disc.HasValue && !anchor.Side.HasValue)) return null;

        var candidates = new List<Candidate> { anchor };
        int order = 0;
        foreach (var app in apps)
        {
            int currentOrder = order++;
            if (SameApp(app, selectedApp) || IsAutoRun(app)) continue;
            string path = Resolve(resolveAbsolute, Safe(() => app.ApplicationPath));
            if (string.IsNullOrWhiteSpace(path) || SamePath(path, anchor.Path)) continue;
            candidates.Add(FromApp(app, path, currentOrder));
        }

        // Disc-only, side-only and disc+side releases are different shapes. This is the sole eligibility
        // filter: Region/Version are deliberately scoring signals, so a different release remains a fallback.
        var shaped = candidates.Where(c => c.Disc.HasValue == anchor.Disc.HasValue
                                         && c.Side.HasValue == anchor.Side.HasValue).ToList();

        var anchorKey = new MediaKey(anchor.Disc, anchor.Side);
        var keys = shaped.Select(KeyOf).Distinct()
            .OrderBy(k => k.Disc ?? int.MaxValue)
            .ThenBy(k => k.Side ?? char.MaxValue)
            .ToList();
        keys.Remove(anchorKey);
        keys.Insert(0, anchorKey);

        var result = new List<string>();
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var winner = key.Equals(anchorKey) ? anchor
                : shaped.Where(c => KeyOf(c).Equals(key))
                    .OrderByDescending(c => Score(c, anchor))
                    .ThenBy(c => NameDistance(c.Path, anchor.Path))
                    .ThenBy(c => c.Order)
                    .FirstOrDefault();
            if (winner == null || !seen.Add(winner.Path)) continue;
            result.Add(winner.Path);
            lines.Add($"{Show(key)} -> {winner.Name} (score={Score(winner, anchor)})");
        }

        // Degraded mode: the anchor is a disc/side but nothing else in the set is identifiable →
        // the fields are broken, not the game. Append everything the shape filter dropped, best
        // score first, then filename order (siblings of one release sort into disc order).
        if (result.Count < 2)
        {
            foreach (var c in candidates.Except(shaped)
                .OrderByDescending(c => Score(c, anchor))
                .ThenBy(c => FileNameOf(c.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Order))
            {
                if (!seen.Add(c.Path)) continue;
                result.Add(c.Path);
                lines.Add($"untagged -> {c.Name} (score={Score(c, anchor)})");
            }
        }

        if (result.Count < 2) { log?.Invoke("single-entry set → no m3u"); return null; }
        foreach (var line in lines) log?.Invoke(line);
        return result;
    }

    private static Candidate FromApp(IAdditionalApplication app, string path, int order) => new()
    {
        Path = path,
        Disc = SafeNullable(() => app.Disc),
        Side = SideFrom(Safe(() => app.SideA), Safe(() => app.SideB)),
        Region = Safe(() => app.Region) ?? "",
        Version = Safe(() => app.Version) ?? "",
        Name = FirstNonEmpty(Safe(() => app.Name), Path.GetFileName(path), path),
        Order = order,
    };

    /// <summary>One flag → that side. Neither, or both (ambiguous) → no side.</summary>
    private static char? SideFrom(bool sideA, bool sideB)
        => sideA != sideB ? (sideA ? 'A' : 'B') : (char?)null;

    private static int Score(Candidate candidate, Candidate anchor)
    {
        int score = 0;
        if (Regions(candidate.Region).Overlaps(Regions(anchor.Region))) score += 100;
        var anchorTokens = Tokens(anchor.Version);
        foreach (var token in Tokens(candidate.Version))
            if (anchorTokens.Contains(token)) score++;
        return score;
    }

    /// <summary>The Region field split on its list separators: "USA, Europe" matches "USA".</summary>
    private static HashSet<string> Regions(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (value ?? "").Split(',', ';', '/'))
            if (part.Trim() is { Length: > 0 } p) result.Add(p);
        return result;
    }

    /// <summary>Version tokens, case-insensitive. A (…)/[…] group is ONE token — "[CustomVersion]
    /// (Rev 1)" gives {customversion, rev 1} — so tags match whole or not at all. Outside any group
    /// a lone number glues to the word before it ("Rev 1" stays one token; a stray digit can't
    /// cross-match).</summary>
    private static HashSet<string> Tokens(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return result;
        string rest = Regex.Replace(value, @"[(\[]([^)\]]*)[)\]]", m =>
        {
            string norm = Normalize(m.Groups[1].Value);
            if (norm.Length > 0) result.Add(norm);
            return " ";
        });
        string prev = null;
        foreach (Match match in Regex.Matches(rest, "[A-Za-z0-9]+"))
        {
            string tok = match.Value;
            if (prev != null && tok.All(char.IsDigit)) { result.Remove(prev); prev += " " + tok; }
            else prev = tok;
            result.Add(prev);
        }
        return result;
    }

    /// <summary>Inside-a-group normalisation: separators collapse to single spaces (the set handles
    /// the case) — "(Disc 1)", "(Disc-1)" and "( disc  1 )" are the same token.</summary>
    private static string Normalize(string s)
        => Regex.Replace(s ?? "", "[^A-Za-z0-9]+", " ").Trim();

    /// <summary>Same score → the candidate whose file NAME is closest to the launched one wins:
    /// sibling discs of one release differ by a digit, a rival release by its whole tag set.</summary>
    private static int NameDistance(string path, string anchorPath)
        => Levenshtein(FileNameOf(path).ToLowerInvariant(), FileNameOf(anchorPath).ToLowerInvariant());

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static string FileNameOf(string p)
    { try { return Path.GetFileName(p) is { Length: > 0 } n ? n : p; } catch { return p ?? ""; } }

    private static MediaKey KeyOf(Candidate c) => new(c.Disc, c.Side);
    private static string Show(MediaKey k) => k.Disc.HasValue && k.Side.HasValue ? $"disc {k.Disc} side {k.Side}"
        : k.Disc.HasValue ? $"disc {k.Disc}" : $"side {k.Side}";

    private static bool IsAutoRun(IAdditionalApplication a)
        => Safe(() => a.AutoRunBefore) || Safe(() => a.AutoRunAfter);

    private static bool SameApp(IAdditionalApplication a, IAdditionalApplication b)
    {
        if (b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        string aid = Safe(() => a.Id), bid = Safe(() => b.Id);
        return !string.IsNullOrEmpty(aid) && !string.IsNullOrEmpty(bid)
            && string.Equals(aid, bid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Resolve(Func<string, string> resolver, string path)
    { try { return string.IsNullOrWhiteSpace(path) ? "" : resolver(path) ?? path; } catch { return path ?? ""; } }
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static T Safe<T>(Func<T> read) { try { return read(); } catch { return default; } }
    private static int? SafeNullable(Func<int?> read) { try { return read(); } catch { return null; } }
}
