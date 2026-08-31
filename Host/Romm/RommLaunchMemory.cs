// Ce que chaque jeu a lancé la dernière fois — lu d'un bloc.
//
// The pass needs, per game, the version last launched and the ROM last extracted from its archive: that
// is what makes the computed default the same file the desktop picker would select. LaunchHistoryDb only
// offered per-game getters, and asking it 3057 times at boot is 3057 round trips for a table that fits
// in a breath.

#nullable enable

using System;
using System.Collections.Generic;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Romm;

internal sealed class RommLaunchMemory
{
    /// <summary>What one game last ran. Both halves may be null — a game never launched, or launched
    /// before the extractor recorded which entry it took.</summary>
    internal readonly struct Last
    {
        public readonly string? AppId;
        public readonly string? RomEntry;
        public Last(string? appId, string? romEntry) { AppId = appId; RomEntry = romEntry; }
    }

    private readonly Dictionary<string, Last> _byGame;

    private RommLaunchMemory(Dictionary<string, Last> byGame) => _byGame = byGame;

    /// <summary>Empty — for a pass running where no history is available.</summary>
    public static RommLaunchMemory Empty { get; } =
        new(new Dictionary<string, Last>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Reads the whole table once. One row per game ever launched, so this is small even on a
    /// large library — and it replaces one query per game.</summary>
    public static RommLaunchMemory Load()
    {
        var map = new Dictionary<string, Last>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (gameId, appId, rom) in LaunchHistoryDb.AllLaunches())
                if (!string.IsNullOrEmpty(gameId)) map[gameId] = new Last(NullIfEmpty(appId), NullIfEmpty(rom));
        }
        catch { }
        return new RommLaunchMemory(map);
    }

    public Last For(string gameId)
        => gameId.Length > 0 && _byGame.TryGetValue(gameId, out var v) ? v : default;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
