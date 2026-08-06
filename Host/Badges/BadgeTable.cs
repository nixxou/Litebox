// The badge state of the whole library, packed.
//
// The measurement that drove this: one game's badges, held as objects, cost ~630 bytes — an Entry, a
// BadgeHit[], the derived visible List, a strip-key string and a dictionary node. The INFORMATION in
// there is "which badges apply, in which variant", which fits in about 7 bytes. At 5000 games the
// difference is 3 MB and nobody cares; at 300 000 it is 190 MB against 3, and it decides whether the
// badge cache can stay resident while a game runs.
//
// So nothing here is an object per game or per combination:
//
//   comboOf[game]  — one int, living in GameRow (Tier 1, survives the launch-time drop)
//   _blob          — every distinct combination, concatenated: [count][badge,variant][badge,variant]…
//   _offset[c]     — where combination c starts in _blob
//
//   badge   = the badge's index in BadgeCatalog.All (0..254)
//   variant = 0 for "the badge's own image, its plain label, no tint", else an index into a small
//             pool of (image, detail, tint) — the ~13 progress values and the 3 controller levels.
//
// Objects come back only at MATERIALISATION, and only for what is actually on screen: the ~40 visible
// rows and the selected game. Decoding 7 bytes into a BadgeHit[] costs well under a microsecond,
// against 15.8 µs to compose the strip that goes with it, so it disappears into the noise of work we
// were doing anyway.
//
// Two consequences worth naming: the enabled filter and the user's draw order are no longer STORED
// anywhere (they are applied while materialising, on 40 rows), and the identity of a drawn badge set
// is an int — no string built or compared per row.

#nullable enable

using System;
using System.Collections.Generic;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Badges;

internal sealed class BadgeTable
{
    /// <summary>Not computed yet. A game with no badges gets a real combination id (the empty one),
    /// so "unknown" and "none" are never confused.</summary>
    public const int Unknown = 0;

    private readonly object _lock = new();

    private byte[] _blob = new byte[4096];
    private int _blobLen;
    private int[] _offset = new int[256];      // combo id → offset in _blob; id 0 unused (= Unknown)
    private int _count = 1;

    // combination hash → candidate ids (interning without retaining a key string per combination)
    private readonly Dictionary<int, List<int>> _byHash = new();

    // variant pool: index 0 is implicit (badge's own image, plain label, no tint)
    private readonly List<(string Image, string Detail, BadgeTint Tint)> _variants = new();
    private readonly Dictionary<(string, string, int), int> _variantIdx = new();

    // pooled tooltips, one per (badge, variant) — materialisation must not allocate strings per row
    private readonly Dictionary<int, string> _tips = new();

    public int ComboCount { get { lock (_lock) return _count - 1; } }
    public int VariantCount { get { lock (_lock) return _variants.Count; } }

    /// <summary>Bytes actually held (blob + offsets + the variant/tooltip pools' own overhead).</summary>
    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                long n = _blob.Length + 4L * _offset.Length + 48L * _variants.Count + 64L * _tips.Count;
                foreach (var kv in _byHash) n += 48 + 4L * kv.Value.Count;
                return n;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _blobLen = 0; _count = 1;
            _byHash.Clear(); _variants.Clear(); _variantIdx.Clear(); _tips.Clear();
        }
    }

    // ── writing (the pass) ───────────────────────────────────────────────────

    /// <summary>Pool a (image, detail, tint) triple. Returns 0 — the implicit variant — when the badge
    /// is drawn plainly, which is the case for all but the progress and controller families.</summary>
    public int Variant(string image, string defaultImage, string detail, BadgeTint tint)
    {
        if (tint == BadgeTint.None && detail.Length == 0
            && string.Equals(image, defaultImage, StringComparison.OrdinalIgnoreCase)) return 0;
        var key = (image, detail, (int)tint);
        lock (_lock)
        {
            if (_variantIdx.TryGetValue(key, out var v)) return v;
            if (_variants.Count >= 254) return 0;      // pathological; draw it plainly rather than lie
            _variants.Add((image, detail, tint));
            return _variantIdx[key] = _variants.Count;  // 1-based
        }
    }

    /// <summary>Intern one game's packed slots and return its combination id. <paramref name="slots"/>
    /// holds <paramref name="n"/> pairs (badge index, variant index).</summary>
    public int Intern(byte[] slots, int n)
    {
        int hash = Hash(slots, n);
        lock (_lock)
        {
            if (_byHash.TryGetValue(hash, out var candidates))
                foreach (var known in candidates)
                    if (Same(known, slots, n)) return known;

            int need = 1 + 2 * n;
            if (_blobLen + need > _blob.Length)
                Array.Resize(ref _blob, Math.Max(_blob.Length * 2, _blobLen + need));
            if (_count + 1 > _offset.Length)
                Array.Resize(ref _offset, _offset.Length * 2);

            int id = _count++;
            _offset[id] = _blobLen;
            _blob[_blobLen++] = (byte)n;
            for (int i = 0; i < n; i++) { _blob[_blobLen++] = slots[i * 2]; _blob[_blobLen++] = slots[i * 2 + 1]; }

            if (candidates == null) _byHash[hash] = candidates = new List<int>(1);
            candidates.Add(id);
            return id;
        }
    }

    private static int Hash(byte[] slots, int n)
    {
        int h = 17 + n;
        for (int i = 0; i < n * 2; i++) h = h * 31 + slots[i];
        return h;
    }

    private bool Same(int id, byte[] slots, int n)   // caller holds the lock
    {
        int p = _offset[id];
        if (_blob[p] != n) return false;
        for (int i = 0; i < n * 2; i++) if (_blob[p + 1 + i] != slots[i]) return false;
        return true;
    }

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>How many badges the combination carries, before the enabled filter.</summary>
    public int SlotCount(int combo)
    {
        lock (_lock) { return combo <= 0 || combo >= _count ? 0 : _blob[_offset[combo]]; }
    }

    /// <summary>The combination's badges as drawable hits. <paramref name="filtered"/> applies the
    /// Badges menu and the user's order — they are applied HERE, per visible row, rather than stored
    /// per game, so toggling a badge invalidates nothing but the composed strips.</summary>
    public BadgeHit[] Materialize(int combo, bool filtered)
    {
        var all = BadgeCatalog.All;
        List<BadgeHit>? hits = null;
        lock (_lock)
        {
            if (combo <= 0 || combo >= _count) return Array.Empty<BadgeHit>();
            int p = _offset[combo];
            int n = _blob[p++];
            for (int i = 0; i < n; i++)
            {
                int bi = _blob[p + i * 2], vi = _blob[p + i * 2 + 1];
                if (bi >= all.Count) continue;                       // catalog shrank under us
                var def = all[bi];
                if (filtered && !BadgeSettings.IsEnabled(def.Id)) continue;
                string image = def.Id; string detail = ""; var tint = BadgeTint.None;
                if (vi > 0 && vi <= _variants.Count)
                {
                    var v = _variants[vi - 1];
                    image = v.Image; detail = v.Detail; tint = v.Tint;
                }
                (hits ??= new List<BadgeHit>(n)).Add(new BadgeHit(def.Id, image, Tip(bi, vi, def.Label, detail), tint));
            }
        }
        if (hits == null) return Array.Empty<BadgeHit>();
        if (filtered && hits.Count > 1)
            hits.Sort((a, b) => BadgeSettings.OrderIndex(a.Id).CompareTo(BadgeSettings.OrderIndex(b.Id)));
        return hits.ToArray();
    }

    // "Joystick Support (Required)" built once per (badge, variant), not once per game.
    private string Tip(int badge, int variant, string label, string detail)   // caller holds the lock
    {
        int key = badge << 8 | variant;
        if (_tips.TryGetValue(key, out var t)) return t;
        t = detail.Length == 0 ? label
          : string.Equals(BadgeCatalog.All[badge].Id, "Progress", StringComparison.OrdinalIgnoreCase) ? detail
          : $"{label} ({detail})";
        return _tips[key] = t;
    }

    /// <summary>The widest ENABLED badge count over every combination — the list sizes its strips on
    /// this, so every title lines up. Recomputed when the enabled set changes (a few thousand
    /// combinations, walked byte by byte: microseconds).</summary>
    public int MaxEnabledSlots()
    {
        var all = BadgeCatalog.All;
        int max = 0;
        lock (_lock)
        {
            for (int c = 1; c < _count; c++)
            {
                int p = _offset[c];
                int n = _blob[p++], k = 0;
                for (int i = 0; i < n; i++)
                {
                    int bi = _blob[p + i * 2];
                    if (bi < all.Count && BadgeSettings.IsEnabled(all[bi].Id)) k++;
                }
                if (k > max) max = k;
            }
        }
        return max;
    }

    /// <summary>Occurrence counts, for filling the strip cache with what pays best. The pass hands in
    /// how many games carry each id; kept here so the cache does not have to walk the library.</summary>
    public int[] Occurrences { get; private set; } = Array.Empty<int>();

    public void SetOccurrences(int[] counts) => Occurrences = counts ?? Array.Empty<int>();

    public string Report()
    {
        lock (_lock)
        {
            long bytes = Bytes;
            return $"table: {_count - 1} combos, {_variants.Count} variants, {_blobLen} blob bytes, "
                 + $"{bytes / 1024} KB total";
        }
    }

    /// <summary>Every combination, in id order, as raw slot pairs — used by the on-disk snapshot.</summary>
    internal void Snapshot(out byte[] blob, out int blobLen, out int[] offset, out int count,
                           out List<(string Image, string Detail, BadgeTint Tint)> variants)
    {
        lock (_lock)
        {
            blob = _blob; blobLen = _blobLen; offset = _offset; count = _count;
            variants = new List<(string, string, BadgeTint)>(_variants);
        }
    }

    /// <summary>Rebuild from a snapshot (the on-disk file). The hash index is rebuilt so the pass can
    /// keep interning into a restored table.</summary>
    internal void Restore(byte[] blob, int blobLen, int[] offset, int count,
                          List<(string Image, string Detail, BadgeTint Tint)> variants)
    {
        lock (_lock)
        {
            _blob = blob; _blobLen = blobLen; _offset = offset; _count = count;
            _variants.Clear(); _variantIdx.Clear(); _tips.Clear(); _byHash.Clear();
            foreach (var v in variants)
            {
                _variants.Add(v);
                _variantIdx[(v.Image, v.Detail, (int)v.Tint)] = _variants.Count;
            }
            var scratch = new byte[512];
            for (int c = 1; c < _count; c++)
            {
                int p = _offset[c];
                int n = _blob[p];
                if (n * 2 > scratch.Length) scratch = new byte[n * 2];
                Array.Copy(_blob, p + 1, scratch, 0, n * 2);
                int h = Hash(scratch, n);
                if (!_byHash.TryGetValue(h, out var l)) _byHash[h] = l = new List<int>(1);
                l.Add(c);
            }
            LbLog.Info("badges", $"table restored: {_count - 1} combos, {_variants.Count} variants");
        }
    }
}
