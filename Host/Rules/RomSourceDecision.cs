// Alias validation for a RELOCATED rom (see RomTokenSearch: same file name, another directory) —
// the part the databases hang on. Validation = byte-size equality against the original: stat'ed
// when it still exists, else its recorded size in rom-archive-cache.db (the database remembering it
// is the whole trick). Validated → the extractor runs under the ORIGINAL's identity while reading
// the relocated bytes: no duplicate cache entry, no RA re-hash, ArchiveHistory continuity. Unproven
// → null: an honest new entry keyed on the relocated path, never an unverified alias — serving
// another archive's cache under a borrowed identity is the one corruption this check exists to
// prevent. (A second, content-based guard sits in RomExtractor at the listing-miss point.)

#nullable enable

using System;

namespace LbApiHost.Host.Rules;

internal static class RomSourceDecision
{
    /// <summary>The identity path to extract under (= the original), or null when the relocated
    /// file could not be validated as the same content.</summary>
    public static string? ValidateAlias(
        string original, string relocated,
        Func<string, long?> sizeOf, Func<string, long?> recordedSizeOf)
    {
        long? relocSize = Try(sizeOf, relocated);
        long? origSize = Try(sizeOf, original) ?? Try(recordedSizeOf, original);
        return relocSize != null && origSize != null && relocSize == origSize ? original : null;
    }

    private static long? Try(Func<string, long?> f, string p)
    {
        try { return f(p); } catch { return null; }
    }
}
