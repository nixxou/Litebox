// The LIVE value of a LaunchBox setting — the one the user last chose, not the one on disk.
//
// LiteBox journals its edits to Settings.xml and can only write them back once LaunchBox releases the
// files, which can be days (an idle LaunchBox on ANY install blocks it). Until then the file says one
// thing and the options window another, and every reader that parses the XML itself quietly serves the
// stale answer: that is how the Add-image dialog kept offering two regions while Options showed five,
// restart included — a restart re-reads the journal but never flushes it.
//
// So: one place to ask. LbSettingsStore installs itself as the reader when it loads (file + pending
// journal), and announces every change; readers that cache derive their invalidation from Changed.
// A null reader (early boot, tests) simply means "no answer" and each caller keeps its own fallback.

#nullable enable

using System;

namespace LbApiHost.Host.Data;

internal static class LiveSettings
{
    /// <summary>Set once, when the settings store is built. Returns the field's current value —
    /// journal included — or null/empty when it has none.</summary>
    public static Func<string, string?>? Reader;

    /// <summary>Every field at once — file + journal — for readers that want the whole block rather
    /// than named keys. Null when nothing is installed yet.</summary>
    public static Func<System.Collections.Generic.Dictionary<string, string>>? Snapshot;

    /// <summary>Raised after any setting is written. Anything that memoises a value derived from
    /// Settings.xml must re-read on this rather than live with what it captured at boot.</summary>
    public static event Action? Changed;

    /// <summary>Did anything answer, and with what? True even for an EMPTY value: "the user cleared
    /// this list" is an answer, and treating it as silence sent the caller back to the file — where a
    /// stale non-empty value was waiting to override the choice that had just been made.</summary>
    public static bool TryGet(string key, out string value)
    {
        value = "";
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            var r = Reader;
            if (r == null) return false;
            var v = r(key);
            if (v == null) return false;   // reader has no such field
            value = v;
            return true;
        }
        catch { return false; }
    }

    /// <summary>The live value, or null when nothing can answer OR the value is empty. Convenience for
    /// callers whose fallback is a default rather than the file; anything that must honour a cleared
    /// value uses <see cref="TryGet"/>.</summary>
    public static string? Get(string key)
        => TryGet(key, out var v) && v.Length > 0 ? v : null;

    public static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
