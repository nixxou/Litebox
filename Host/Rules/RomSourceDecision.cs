// The rom-source decision — what a ChangeRomPath relocation MEANS for the extraction machinery,
// settled with Mehdi over one long design pass because this is exactly where careless code would
// rot the databases:
//
//   FILE NAME DIFFERS   → an explicit SUBSTITUTION, not a relocation. The path goes to the emulator
//                         VERBATIM: no m3u planning, no extraction, not one write into any cache or
//                         history — nothing can be presumed about that file, so nothing is recorded.
//   NAME SAME, VALIDATED→ the same archive through another door. Validation = byte size equality
//                         against the original (stat'ed when it still exists, else its recorded size
//                         in rom-archive-cache.db — the database remembering it is the whole trick).
//                         The extractor then runs under the ORIGINAL's identity (signature, listing
//                         key, recorded source) while READING the relocated bytes: no duplicate
//                         cache entry, no RA re-hash, ArchiveHistory continuity — the mirror is just
//                         a transport.
//   NAME SAME, UNPROVEN → an honest NEW entry keyed on the relocated path. Never an unverified
//                         alias: serving another archive's cache under a borrowed identity is the
//                         one corruption this table exists to prevent.

#nullable enable

using System;
using System.IO;

namespace LbApiHost.Host.Rules;

internal enum RomSourceMode
{
    /// <summary>No relocation happened — normal launch flow.</summary>
    Untouched,
    /// <summary>Relocated, identity unproven — normal flow on the relocated path (new cache entry).</summary>
    Normal,
    /// <summary>Substituted under another NAME — pass verbatim, skip resolution entirely.</summary>
    Verbatim,
    /// <summary>Relocated and VALIDATED — extraction under the original's identity, reads relocated.</summary>
    Alias,
}

internal static class RomSourceDecision
{
    public static (RomSourceMode Mode, string? IdentityPath) Decide(
        string original, string relocated,
        Func<string, long?> sizeOf, Func<string, long?> recordedSizeOf)
    {
        if (string.Equals(relocated, original, StringComparison.OrdinalIgnoreCase))
            return (RomSourceMode.Untouched, null);

        string origName, relocName;
        try { origName = Path.GetFileName(original); relocName = Path.GetFileName(relocated); }
        catch { return (RomSourceMode.Normal, null); }
        if (!string.Equals(origName, relocName, StringComparison.OrdinalIgnoreCase))
            return (RomSourceMode.Verbatim, null);

        long? relocSize = Try(sizeOf, relocated);
        long? origSize = Try(sizeOf, original) ?? Try(recordedSizeOf, original);
        return relocSize != null && origSize != null && relocSize == origSize
            ? (RomSourceMode.Alias, original)
            : (RomSourceMode.Normal, null);
    }

    private static long? Try(Func<string, long?> f, string p)
    {
        try { return f(p); } catch { return null; }
    }
}
