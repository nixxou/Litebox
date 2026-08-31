// Devices + per-device save sync state — the Grout contract.
//
// A device registers a fingerprint (mac/hostname/platform) and gets a stable string id; every save then
// carries per-device sync rows so the handheld can decide push vs pull ("is_current" = the device's
// recorded state is at least as new as the save). The whole table is a handful of devices and their
// sync marks, so it lives as one JSON value in the options DB — not a store of its own.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Romm;

internal sealed class RommDevice
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Platform { get; set; }
    public string? Client { get; set; }
    public string? ClientVersion { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public string? Hostname { get; set; }
    public string SyncMode { get; set; } = "manual";
    public bool SyncEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}

internal sealed class RommDeviceSync
{
    public string DeviceId { get; set; } = "";
    public int AssetId { get; set; }
    public DateTime LastSyncedUtc { get; set; }
    public bool Untracked { get; set; }
}

internal static class RommDevices
{
    private const string DevicesKey = "Romm.Devices";
    private const string SyncsKey = "Romm.DeviceSyncs";
    private static readonly object _lock = new();

    // ── Devices ───────────────────────────────────────────────────────────────

    public static List<RommDevice> All()
    {
        try
        {
            var raw = LiteBoxOptionsDb.GetGlobal(DevicesKey);
            if (string.IsNullOrEmpty(raw)) return new List<RommDevice>();
            return JsonSerializer.Deserialize<List<RommDevice>>(raw!) ?? new List<RommDevice>();
        }
        catch { return new List<RommDevice>(); }
    }

    private static void SaveAll(List<RommDevice> devices)
    {
        try { LiteBoxOptionsDb.SetGlobal(DevicesKey, JsonSerializer.Serialize(devices)); }
        catch (Exception ex) { LbLog.Warn("romm", "device store failed: " + ex.Message); }
    }

    public static RommDevice? ById(string id) => All().FirstOrDefault(d => d.Id == id);

    /// <summary>Fingerprint match, upstream's dedup rule: same mac+hostname+platform is the same device.</summary>
    public static RommDevice? ByFingerprint(string? mac, string? hostname, string? platform)
        => All().FirstOrDefault(d =>
            string.Equals(d.MacAddress ?? "", mac ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Hostname ?? "", hostname ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Platform ?? "", platform ?? "", StringComparison.OrdinalIgnoreCase));

    public static RommDevice Register(RommDevice d)
    {
        lock (_lock)
        {
            var all = All();
            d.Id = Guid.NewGuid().ToString();
            d.CreatedUtc = DateTime.UtcNow;
            d.LastSeenUtc = DateTime.UtcNow;
            all.Add(d);
            SaveAll(all);
            return d;
        }
    }

    public static RommDevice? Update(string id, Action<RommDevice> mutate)
    {
        lock (_lock)
        {
            var all = All();
            var d = all.FirstOrDefault(x => x.Id == id);
            if (d == null) return null;
            mutate(d);
            SaveAll(all);
            return d;
        }
    }

    public static bool Delete(string id)
    {
        lock (_lock)
        {
            var all = All();
            int removed = all.RemoveAll(d => d.Id == id);
            if (removed > 0)
            {
                SaveAll(all);
                var syncs = AllSyncs();
                if (syncs.RemoveAll(s => s.DeviceId == id) > 0) SaveSyncs(syncs);
            }
            return removed > 0;
        }
    }

    public static void Touch(string id) => Update(id, d => d.LastSeenUtc = DateTime.UtcNow);

    // ── Sync marks ────────────────────────────────────────────────────────────

    private static List<RommDeviceSync> AllSyncs()
    {
        try
        {
            var raw = LiteBoxOptionsDb.GetGlobal(SyncsKey);
            if (string.IsNullOrEmpty(raw)) return new List<RommDeviceSync>();
            return JsonSerializer.Deserialize<List<RommDeviceSync>>(raw!) ?? new List<RommDeviceSync>();
        }
        catch { return new List<RommDeviceSync>(); }
    }

    private static void SaveSyncs(List<RommDeviceSync> syncs)
    {
        try { LiteBoxOptionsDb.SetGlobal(SyncsKey, JsonSerializer.Serialize(syncs)); }
        catch (Exception ex) { LbLog.Warn("romm", "device-sync store failed: " + ex.Message); }
    }

    public static List<RommDeviceSync> SyncsForAsset(int assetId)
        => AllSyncs().Where(s => s.AssetId == assetId).ToList();

    /// <summary>Records "this device holds this asset as of now" — the confirm-download / post-upload mark.</summary>
    public static void MarkSynced(string deviceId, int assetId)
    {
        lock (_lock)
        {
            var syncs = AllSyncs();
            var s = syncs.FirstOrDefault(x => x.DeviceId == deviceId && x.AssetId == assetId);
            if (s == null) { s = new RommDeviceSync { DeviceId = deviceId, AssetId = assetId }; syncs.Add(s); }
            s.LastSyncedUtc = DateTime.UtcNow;
            s.Untracked = false;
            SaveSyncs(syncs);
        }
    }

    public static void SetTracked(string? deviceId, int assetId, bool tracked)
    {
        lock (_lock)
        {
            var syncs = AllSyncs();
            var hits = syncs.Where(x => x.AssetId == assetId && (deviceId == null || x.DeviceId == deviceId)).ToList();
            if (hits.Count == 0 && deviceId != null && !tracked)
            {
                hits.Add(new RommDeviceSync { DeviceId = deviceId, AssetId = assetId, LastSyncedUtc = DateTime.UtcNow });
                syncs.Add(hits[0]);
            }
            foreach (var s in hits) s.Untracked = !tracked;
            SaveSyncs(syncs);
        }
    }
}
