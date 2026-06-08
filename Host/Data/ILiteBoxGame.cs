// Public escape-hatch so plugins can reach EVERY XML field of an entity, including the ones
// LaunchBox writes but does NOT expose on its SDK interfaces (IGame/IEmulator/IPlatform/…). Examples:
// a game's GogAppId/Origin*/Android*/Missing*/RetroAchievements*; an emulator's UsePauseScreen /
// SuspendProcessOnPause / SkipVersionCheck / LoginToCheevoOnGameLaunch; etc. LaunchBox itself uses
// richer internal classes — LiteBox owns the data layer, so it doesn't bridle plugins to the subset.
//
// How plugins use it:
//   • A LiteBox-native plugin: `if (entity is ILiteBoxFields x) x.SetField("UsePauseScreen", "true");`
//   • A cross-LaunchBox plugin (e.g. ExtendDB, which must also run under real LB and can't hard-
//     reference LiteBox) reflects the same public methods by name:
//       entity.GetType().GetMethod("SetField", new[]{typeof(string),typeof(string)})?.Invoke(entity, …)
//
// Writes go through the same op-log as the typed setters (persisted to the XML, surgical, crash-safe).
// For fields the SDK interface already exposes, prefer the typed property; SetField also handles them.

using System.Collections.Generic;

namespace LbApiHost;

/// <summary>Generic full-field access implemented by every LiteBox host entity (game, emulator,
/// emulator-platform, platform, category, playlist).</summary>
public interface ILiteBoxFields
{
    /// <summary>Reads a raw XML field by element name. Returns "" for an absent field. For fields the
    /// SDK interface exposes, prefer the typed property.</summary>
    string GetField(string xmlElementName);

    /// <summary>Writes a raw XML field by element name — any field, whether the SDK interface exposes
    /// it or not. "" clears it. Persisted to the XML via the op-log.</summary>
    void SetField(string xmlElementName, string value);

    /// <summary>The field names NOT exposed by the SDK interface that are currently present on this
    /// entity (the extras LaunchBox wrote).</summary>
    IReadOnlyCollection<string> ExtraFieldNames { get; }
}

/// <summary>Marker for games (kept for game-typed casts); identical shape to <see cref="ILiteBoxFields"/>.</summary>
public interface ILiteBoxGame : ILiteBoxFields { }
