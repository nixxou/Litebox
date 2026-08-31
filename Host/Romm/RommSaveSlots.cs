// The RomM "slot" of a vault copy — a free-form channel name, kept apart from LaunchBox's slot NUMBER.
//
// Two fields, one word, and they mean different things:
//
//   • LaunchBox's Slot is the savestate NUMBER (0, 1, 2…). It lives in the <GameSave> record, it is what
//     an emulator plugin writes and reads, and a save file has none at all.
//   • RomM's slot is a STRING naming a sync channel. /api/sync/negotiate pairs saves on (rom_id, slot),
//     and upstream recommends a stable name; Freegosy sends "freegosy" on every push. A null slot is
//     deliberately excluded from pairing upstream — it marks an archival or manual upload.
//
// Conflating them is what this store exists to stop. Until now the DTO reported the LaunchBox number in
// RomM's field, and an upload's "freegosy" failed int.TryParse and was silently dropped, so the channel
// a client wrote to never came back to it. Nothing broke, because Freegosy uses exactly one channel and
// never filters on it — but the moment a client uses two, or we lean on negotiate, it would.
//
// It cannot live in the record: LaunchBox deserialises the whole platform file and re-serialises it, so
// an unknown element on a <GameSave> is dropped on the next write. Measured. So it sits beside the other
// RomM-only state, keyed by the copy's vault path — stable, unique, and already how a copy is addressed.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Romm;

internal sealed class RommSaveSlot
{
    /// <summary>The copy's path relative to the LB root — the same string a vault asset id carries.</summary>
    public string VaultPath { get; set; } = "";

    /// <summary>RomM's channel name, verbatim. Never parsed, never normalised: it is the client's word.</summary>
    public string Slot { get; set; } = "";
}

internal static class RommSaveSlots
{
    private const string Key = "Romm.SaveSlots";
    private static readonly object _lock = new();

    public static List<RommSaveSlot> All()
    {
        try
        {
            var raw = LiteBoxOptionsDb.GetGlobal(Key);
            if (string.IsNullOrEmpty(raw)) return new List<RommSaveSlot>();
            return JsonSerializer.Deserialize<List<RommSaveSlot>>(raw!) ?? new List<RommSaveSlot>();
        }
        catch { return new List<RommSaveSlot>(); }
    }

    private static void SaveAll(List<RommSaveSlot> rows)
    {
        try { LiteBoxOptionsDb.SetGlobal(Key, JsonSerializer.Serialize(rows)); }
        catch (Exception ex) { LbLog.Warn("romm", "save-slot store failed: " + ex.Message); }
    }

    /// <summary>The channel this copy was pushed to, or null when it was not made by a RomM client.</summary>
    public static string? Of(string? vaultPath)
    {
        if (string.IsNullOrEmpty(vaultPath)) return null;
        var hit = All().FirstOrDefault(r =>
            string.Equals(r.VaultPath, vaultPath, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(hit?.Slot) ? null : hit!.Slot;
    }

    /// <summary>Records the channel a copy belongs to. An empty slot forgets it rather than storing a
    /// blank — upstream treats a null slot as "not part of any sync channel", and so should we.</summary>
    public static void Set(string? vaultPath, string? slot)
    {
        if (string.IsNullOrEmpty(vaultPath)) return;
        lock (_lock)
        {
            var all = All();
            all.RemoveAll(r => string.Equals(r.VaultPath, vaultPath, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(slot))
                all.Add(new RommSaveSlot { VaultPath = vaultPath!, Slot = slot!.Trim() });
            SaveAll(all);
        }
    }

    /// <summary>Drops a copy's channel — called when the copy itself is deleted, so the table does not
    /// accumulate rows for files that no longer exist.</summary>
    public static void Forget(string? vaultPath)
    {
        if (string.IsNullOrEmpty(vaultPath)) return;
        lock (_lock)
        {
            var all = All();
            if (all.RemoveAll(r => string.Equals(r.VaultPath, vaultPath, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveAll(all);
        }
    }
}
