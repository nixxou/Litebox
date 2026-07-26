// Per-game 3D-model IMAGE OVERRIDES (Edit Game → 3D Model Settings → Image Selection tab): which exact
// image file feeds each texture slot of the 3D case, instead of the automatic type→region→number pick.
//
// STORAGE — LiteBox's own options DB (LiteBoxOptionsDb, Core\litebox\litebox-options.db):
//     options(scope='game', entity_id=<gameId>, key='Model3dImages',
//             value=compact JSON {"front":"Images\\...","spine":"Images\\..."})
// Values are LB-ROOT-RELATIVE paths (portable across a library move, cf. the portable cache keys).
// The options DB is the home for LiteBox-own per-entity data: guid-keyed, and — unlike a foreign element
// in the <Game> XML — it survives REAL LaunchBox sessions (LB's fixed-schema rewrite strips unknown
// elements). LiteBox-only settings; the ExtendDB plugin never touches this key.
//
// Slots: front / back / spine / logo / full. Rules (decided with the user):
//   • "full" is exclusive with front/spine/back (the full scan replaces the three) — the UI enforces it,
//     and the renderer suppresses the three auto-resolved scans when a full override is active;
//   • an override is only honoured while EVERY selected file still exists — one missing file invalidates
//     the WHOLE override (Effective returns null → full auto resolution). The raw row is kept, so
//     restoring the file restores the override.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Model3d;

internal static class Model3dImageStore
{
    public static readonly string[] Slots = { "front", "back", "spine", "logo", "full" };

    /// <summary>The options-DB key (game scope).</summary>
    public const string OptionKey = "Model3dImages";

    private static string? LbRoot()
    {
        try { return Media.MediaResolver.LbRoot; } catch { return null; }
    }

    private static string ToStored(string absPath)
    {
        var root = LbRoot();
        if (!string.IsNullOrEmpty(root))
        {
            try
            {
                string full = Path.GetFullPath(absPath);
                string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return full.Substring(r.Length);
            }
            catch { }
        }
        return absPath;   // outside the LB tree (shouldn't happen — the picker only offers LB\Images) → absolute
    }

    private static string ToAbsolute(string stored)
    {
        if (Path.IsPathRooted(stored)) return stored;
        var root = LbRoot();
        return string.IsNullOrEmpty(root) ? stored : Path.Combine(root, stored);
    }

    private static string? IdOf(IGame g)
    {
        try { return g.Id; } catch { return null; }
    }

    /// <summary>The RAW stored selection (slot → ABSOLUTE path), or null. Missing files NOT filtered.</summary>
    public static Dictionary<string, string>? Read(IGame g)
    {
        if (IdOf(g) is not { Length: > 0 } id) return null;
        try
        {
            string? json = Data.LiteBoxOptionsDb.Get("game", id, OptionKey);
            if (string.IsNullOrEmpty(json)) return null;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json!);
            if (d == null || d.Count == 0) return null;
            var sel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d)
                if (Array.IndexOf(Slots, kv.Key.ToLowerInvariant()) >= 0 && !string.IsNullOrEmpty(kv.Value))
                    sel[kv.Key.ToLowerInvariant()] = ToAbsolute(kv.Value);
            return sel.Count > 0 ? sel : null;
        }
        catch (Exception ex) { Console.WriteLine("[model3d] image store read: " + ex.Message); return null; }
    }

    /// <summary>The override to actually APPLY: null when the game has none OR when any selected file is
    /// missing (one absent image invalidates the whole override — full auto resolution instead).</summary>
    public static Dictionary<string, string>? Effective(IGame g)
    {
        var sel = Read(g);
        if (sel == null) return null;
        foreach (var p in sel.Values)
        {
            try { if (!File.Exists(p)) return null; }
            catch { return null; }
        }
        return sel;
    }

    /// <summary>Persist a game's selection (null/empty = remove the row).</summary>
    public static void Write(IGame g, Dictionary<string, string>? sel)
    {
        if (IdOf(g) is not { Length: > 0 } id) return;
        try
        {
            if (sel == null || sel.Count == 0) { Data.LiteBoxOptionsDb.Set("game", id, OptionKey, null); return; }
            var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in sel)
                if (!string.IsNullOrEmpty(kv.Value)) stored[kv.Key.ToLowerInvariant()] = ToStored(kv.Value);
            Data.LiteBoxOptionsDb.Set("game", id, OptionKey, stored.Count == 0 ? null : JsonSerializer.Serialize(stored));
        }
        catch (Exception ex) { Console.WriteLine("[model3d] image store write: " + ex.Message); }
    }
}
