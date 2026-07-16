// ─────────────────────────────────────────────────────────────────────────────
// Parental control — web library-mutation write-guard.
// ─────────────────────────────────────────────────────────────────────────────
//
// Closes the runtime gap between the parental flags the options panel PERSISTS
// (Host/Options/Modules/ParentalPanel → Host/Parental/ParentalConfig) and what the
// web mutation handlers actually ENFORCE. Before this, a locked web client was
// simply refused every library write; the three persisted knobs below were dead:
//
//   • AllowLockedModifyRatings   — a locked client may still set a game's star rating.
//   • AllowLockedModifyFavorites — a locked client may still toggle favorite.
//   • BigBoxWriteMode (Block/Merge) — the write-guard policy. Block (default, safe)
//     never persists a RESTRICTED edit while locked. Merge (experimental) is the
//     plugin's "fold the filtered subset back into the live library" path; LiteBox
//     has no filtered-library rewrite to merge against, so Merge degrades to Block
//     here — restricted edits are still not persisted.
//
// A "restricted edit" = any locked-session mutation NOT explicitly permitted by one
// of the two allow-flags: hide / broken (which have no allow-flag) always, plus
// rating / favorite when their flag is off.
//
// The lock state passed in is the per-CLIENT web lock (Host/Web/WebParentalState
// cookie), NOT the host GUI runtime lock (ParentalFilter._locked) — the two surfaces
// stay independent, exactly as WebParentalState already reads/enforces on the read side.
//
// This type is pure/stateless and owns only READ access to ParentalConfig; the
// handlers (Host/Web/Theme/BigBoxMutationApi, and LaunchBoxMutationApi via it) call
// DenyReason() once per mutation.

#nullable enable

using System;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

internal static class ParentalWebWriteGuard
{
    // ── Read-only flag accessors (over Host/Parental/ParentalConfig) ─────────────

    /// <summary>A locked web client may still change a game's star rating.</summary>
    public static bool AllowLockedModifyRatings
    {
        get { try { return ParentalConfig.Instance.AllowLockedModifyRatings; } catch { return false; } }
    }

    /// <summary>A locked web client may still toggle a game's favorite state.</summary>
    public static bool AllowLockedModifyFavorites
    {
        get { try { return ParentalConfig.Instance.AllowLockedModifyFavorites; } catch { return false; } }
    }

    /// <summary>True when the write-guard policy is Block (the safe default): restricted edits are
    /// never persisted while locked. Merge → false, but degrades to Block behaviour here (see header).</summary>
    public static bool BlockWritesWhileLocked
    {
        get { try { return ParentalConfig.Instance.BigBoxWriteMode == ParentalWriteMode.Block; } catch { return true; } }
    }

    // ── Decision ─────────────────────────────────────────────────────────────────

    /// <summary>Decides whether a web library mutation of <paramref name="kind"/>
    /// ("rating" | "favorite" | "hide" | "broken" | …) must be REFUSED for a client whose
    /// per-client web lock state is <paramref name="isLocked"/>. Returns the deny reason string to
    /// surface in the JSON { ok:false, reason } response, or <c>null</c> when the mutation is allowed.
    ///
    ///   • Not locked                          → allowed (null).
    ///   • Locked + rating                     → allowed iff AllowLockedModifyRatings.
    ///   • Locked + favorite                   → allowed iff AllowLockedModifyFavorites.
    ///   • Locked + hide / broken / unknown    → refused (no allow-flag exists for these).
    ///
    /// BigBoxWriteMode is consulted for the audit trail: refusing a restricted edit while locked IS
    /// "honouring Block" (the on-disk library is never rewritten with an edit the parent didn't
    /// authorise); Merge has no LiteBox equivalent and degrades to the same refusal.</summary>
    public static string? DenyReason(bool isLocked, string? kind)
    {
        if (!isLocked) return null;   // unlocked client (or parental not configured) → no write-guard

        string k = (kind ?? "").Trim().ToLowerInvariant();
        bool allowed = k switch
        {
            "rating"   => AllowLockedModifyRatings,
            "favorite" => AllowLockedModifyFavorites,
            _          => false,      // hide / broken / anything else: no per-action allowance
        };

        if (allowed) return null;     // explicitly permitted while locked by the corresponding allow-flag

        // Restricted edit while locked → refuse. Never persists to the live library (Block honoured);
        // Merge would fold the edit back in the plugin, but LiteBox has no filtered-library rewrite to
        // merge against, so it degrades to Block (still refused). Log the effective mode for auditing.
        try { LbLog.Info("parental", $"web write-guard: refused '{k}' while locked (mode={(BlockWritesWhileLocked ? "Block" : "Merge->Block")})"); }
        catch { }
        return "parental_locked";
    }
}
