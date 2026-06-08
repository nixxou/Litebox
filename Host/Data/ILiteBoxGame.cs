// Public escape-hatch interface so plugins can reach EVERY <Game> XML field, including the ~47
// LaunchBox writes but does NOT expose on the SDK's IGame (GogAppId, Origin*, Android*, Missing*,
// RetroAchievements*, the pause-screen AutoHotkey scripts, …). LaunchBox itself uses richer internal
// classes; LiteBox owns the data layer, so it doesn't bridle plugins to the SDK subset.
//
// How plugins use it:
//   • A LiteBox-native plugin: `if (game is ILiteBoxGame x) x.SetField("GogAppId", "123");`
//   • A cross-LaunchBox plugin (e.g. ExtendDB, which must also run under real LB and can't hard-
//     reference LiteBox) reflects the same public methods by name:
//       game.GetType().GetMethod("SetField", new[]{typeof(string),typeof(string)})?.Invoke(game, …)
//
// Writes go through the same op-log as the typed setters (persisted to the Platform XML, surgical,
// crash-safe). For fields IGame already exposes, prefer the typed properties — SetField/GetField
// also handle them, but GetField is primarily the reader for the non-IGame fields.

using System.Collections.Generic;

namespace LbApiHost;

public interface ILiteBoxGame
{
    /// <summary>Reads a raw XML field value by element name (e.g. "GogAppId"). For fields IGame
    /// exposes, prefer the typed property; this returns "" for an absent/unset field.</summary>
    string GetField(string xmlElementName);

    /// <summary>Writes a raw XML field by element name. Works for ANY field — those modelled by IGame
    /// (routed to the typed store) and those it doesn't expose (persisted verbatim). "" clears it.</summary>
    void SetField(string xmlElementName, string value);

    /// <summary>The non-IGame field names currently present for this game (the extras LB wrote).</summary>
    IReadOnlyCollection<string> ExtraFieldNames { get; }
}
