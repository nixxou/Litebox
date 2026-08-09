// The ini-backed 3D knobs, SNAPSHOTTED so the hot paths never touch the disk.
//
// Why this exists: LiteBoxConfig.LoadForExe() re-parses LiteBox.ini on EVERY call (it returns a new
// object), and these values are read from Model3dCache.Resolve — i.e. once per game during the key-index
// pass (4000+ games) and on every selection. Model3dBaker.TargetAspect() had exactly that problem
// already: one full ini parse per resolved game. Everything now goes through the snapshot below, which
// is built once and only rebuilt when the options window applies a change (Invalidate).
//
// Source: the HOST installs its live LiteBoxConfig instance (see MainWindow) so a snapshot rebuilt during
// Apply — before the file is written — already sees the new in-memory values. Without a host (headless
// probes, the bulk cache generator run standalone) it falls back to reading the ini once.

#nullable enable

using System;

namespace LbApiHost.Host.Model3d;

internal static class Model3dOptions
{
    /// <summary>The host's live config (set once at boot). Null → read the ini file on demand.</summary>
    public static Func<LiteBoxConfig>? Source;

    private sealed record Snap(bool NeedBack, bool NeedSpine, bool NeedBoth, bool AcceptFull, bool Use169,
                               bool AutoJewel);

    private static Snap? _snap;
    private static readonly object _lock = new();

    private static Snap S
    {
        get
        {
            var s = _snap;
            if (s != null) return s;
            lock (_lock)
            {
                if (_snap != null) return _snap;
                LiteBoxConfig cfg;
                try { cfg = Source?.Invoke() ?? LiteBoxConfig.LoadForExe(); }
                catch { return _snap = new Snap(false, false, false, false, true, true); }
                return _snap = new Snap(cfg.Model3dRequireBack, cfg.Model3dRequireSpine,
                                        cfg.Model3dRequireBothScans, cfg.Model3dAcceptFullScan,
                                        cfg.Use169ForMainScreenshot, cfg.Model3dAutoJewelCase);
            }
        }
    }

    /// <summary>Drop the snapshot — call when the options window changed any of these.</summary>
    public static void Invalidate() { lock (_lock) _snap = null; }

    // NOTE: these keys are seeded into the ini by Options.GlobalDefaults (one mechanism for every
    // LiteBox-own global), not from here.

    /// <summary>Is a model with these available sources worth showing (and baking)? <paramref name="full"/>
    /// is already gated by the game's own full-scan mode — Model3dCache only resolves that slot when the
    /// mode applies, so the per-platform/per-game scope is honoured without this rule knowing about it.</summary>
    public static bool Valid(bool front, bool back, bool spine, bool full)
    {
        var s = S;
        if (s.AcceptFull && full) return true;
        if (!front) return false;
        if (s.NeedBack && s.NeedSpine) return s.NeedBoth ? (back && spine) : (back || spine);
        if (s.NeedBack) return back;
        if (s.NeedSpine) return spine;
        return true;
    }

    /// <summary>The media box aspect the whole 3D pipeline targets (16:9 or the 2:3 poster ratio).</summary>
    public static double TargetAspect() => S.Use169 ? 16.0 / 9.0 : 2.0 / 3.0;

    /// <summary>May a long-box platform fall back to a plain jewel case when the artwork fits that shape
    /// better? See HomeModel3d.RefineCaseType — a deliberate divergence from LaunchBox, on by default.</summary>
    public static bool AutoJewelCase => S.AutoJewel;
}
