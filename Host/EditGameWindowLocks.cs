// Edit Game window — per-field LOCK toggles (native LiteBox port of ExtendDB's locked-fields feature).
//
// This partial adds a small padlock button next to each Metadata field and the Notes box. Clicking it
// flags that field as "locked" for the edited game(s): the value the user wants is remembered in the
// native LockStore (Core\litebox\metadata-locks.json) so a future metadata refresh can preserve it.
// The lock is purely additive — the field stays fully editable; only its persisted "keep this value"
// intent changes. No module gate: this is built into the editor and works by default.
//
//   • 🔓 faint  = unlocked;  🔒 gold = locked.
//   • Single game: toggles that game's lock.
//   • Multi-select: toggles the lock for EVERY selected game at once; the indicator shows locked only
//     when the field is locked on ALL of them. When a multi-value field still shows the
//     "‹multiple values›" placeholder, each game's OWN current value is captured (not the placeholder).
//   • On OK, the stored value of every still-locked field is refreshed to the just-saved value.
//
// GAP: LiteBox has no native metadata-scrape/refresh pipeline (the "Search for Metadata" button is
// inert — scraping is ExtendDB's job), so nothing in-host currently re-applies these locked values.
// The store persists them and exposes LockStore.GetLockedFields(...) for a future refresh hook.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Editor;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    // Padlock glyphs (surrogate pairs so the source encoding is irrelevant): LOCK / OPEN LOCK.
    private const string GlyphLocked = "🔒";
    private const string GlyphUnlocked = "🔓";
    private static readonly Color LockedColor = Color.FromArgb(0xF6, 0xC3, 0x44);   // gold, matches the user star colour

    // control → (field key, its padlock button). Populated once by SetupLocks.
    private readonly Dictionary<Control, (string key, Button btn)> _locks = new();
    private bool _locksReady;

    // ── Wiring (called from the ctor, after the Metadata + Notes pages are built) ──────────────
    private void SetupLocks()
    {
        if (_locksReady) return;
        _locksReady = true;

        // Metadata page — one padlock per lockable field, in the small gutter to the field's right.
        // Date fields hold a ▦ picker button after the text box, so they anchor off the column, not the box.
        AttachLock(_title,       "title",        _title.Right + S(2));
        AttachLock(_releaseDate, "release_date", LFx + FW + S(2));
        AttachLock(_rating,      "rating",       _rating.Right + S(2));
        AttachLock(_releaseType, "release_type", _releaseType.Right + S(2));
        AttachLock(_maxPlayers,  "max_players",  _maxPlayers.Right + S(2));
        AttachLock(_genre,       "genre",        _genre.Right + S(2));
        AttachLock(_platform,    "platform",     _platform.Right + S(2));
        AttachLock(_developer,   "developer",    _developer.Right + S(2));
        AttachLock(_publisher,   "publisher",    _publisher.Right + S(2));
        AttachLock(_series,      "series",       _series.Right + S(2));
        AttachLock(_region,      "region",       _region.Right + S(2));
        AttachLock(_playMode,    "play_mode",    _playMode.Right + S(2));
        AttachLock(_version,     "version",      _version.Right + S(2));
        AttachLock(_status,      "status",       _status.Right + S(2));
        AttachLock(_source,      "source",       _source.Right + S(2));
        AttachLock(_lastPlayed,  "last_played",  LFx + FW + S(2));
        AttachLock(_progress,    "progress",     _progress.Right + S(2));
        AttachLock(_videoUrl,    "video_url",    _videoUrl.Right + S(2));
        AttachLock(_wikiUrl,     "wiki_url",     _wikiUrl.Right + S(2));
        AttachLock(_starBar,     "star_rating",  _starBar.Right + S(20));

        // Notes page — the box is Dock=Fill, so the padlock rides the panel's top-right, left of the ↺.
        AttachNotesLock();

        RefreshLocks();
    }

    private Button MakeLockButton()
    {
        var b = new Button
        {
            Size = new Size(S(16), S(16)), TabStop = false, Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = SubFg,
            Text = GlyphUnlocked, Font = new Font("Segoe UI Symbol", 8.25f),
            FlatAppearance = { BorderSize = 0 }, Enabled = !_readOnly,
        };
        return b;
    }

    private void AttachLock(Control field, string key, int lockLeft)
    {
        var parent = field.Parent;
        if (parent == null) return;
        var btn = MakeLockButton();
        int top = field.Top + Math.Max(0, (field.Height - btn.Height) / 2);
        btn.Location = new Point(lockLeft, top);
        btn.Click += (_, _) => ToggleLock(field);
        parent.Controls.Add(btn);
        btn.BringToFront();
        _locks[field] = (key, btn);
    }

    private void AttachNotesLock()
    {
        var parent = _notes.Parent as Panel;
        if (parent == null) return;
        var btn = MakeLockButton();
        parent.Controls.Add(btn);
        btn.BringToFront();
        _locks[_notes] = ("notes", btn);
        btn.Click += (_, _) => ToggleLock(_notes);

        void Place()
        {
            if (parent.ClientSize.Width > S(80))
                btn.Location = new Point(parent.ClientSize.Width - btn.Width - S(52), S(13));
        }
        parent.Resize += (_, _) => { Place(); btn.BringToFront(); };
        Place();
    }

    // ── Toggle / refresh ───────────────────────────────────────────────────────────────────────
    private void ToggleLock(Control field)
    {
        if (_readOnly || !_locks.TryGetValue(field, out var e)) return;
        string key = e.key;
        bool nowLocked = !LockedForAll(key);
        string ctlVal = LockValueOf(field);
        bool ctlIsPlaceholder = IsPlaceholder(field);

        foreach (var g in _editGames)
        {
            string? id = Safe(() => g.Id);
            if (string.IsNullOrEmpty(id)) continue;
            if (nowLocked)
            {
                // Multi-select over a still-differing field → keep each game's own value, not the placeholder.
                string val = (IsMulti && ctlIsPlaceholder) ? GameFieldValue(g, key) : ctlVal;
                LockStore.SetFieldLock(id, key, true, val);
            }
            else LockStore.SetFieldLock(id, key, false, null);
        }
        RefreshLockButton(field);
    }

    /// <summary>Repaint every padlock to match the current game(s)' lock state (called on load / navigate).</summary>
    private void RefreshLocks()
    {
        foreach (var f in _locks.Keys) RefreshLockButton(f);
    }

    private void RefreshLockButton(Control field)
    {
        if (!_locks.TryGetValue(field, out var e)) return;
        bool locked = LockedForAll(e.key);
        e.btn.Text = locked ? GlyphLocked : GlyphUnlocked;
        e.btn.ForeColor = locked ? LockedColor : SubFg;
        _tips.SetToolTip(e.btn, locked
            ? "Locked — this value is preserved across a metadata refresh (click to unlock)"
            : "Unlocked — click to lock this field's value against metadata refreshes");
    }

    // True only when the field is locked for every edited game (and there is at least one).
    private bool LockedForAll(string key)
    {
        bool any = false;
        foreach (var g in _editGames)
        {
            string? id = Safe(() => g.Id);
            if (string.IsNullOrEmpty(id)) continue;
            any = true;
            if (!LockStore.IsFieldLocked(id, key)) return false;
        }
        return any;
    }

    // ── Save: refresh the stored value of every still-locked field to the just-saved value ───────
    private void SaveLocks()
    {
        if (_readOnly) return;
        foreach (var kv in _locks)
        {
            Control field = kv.Key;
            string key = kv.Value.key;
            string ctlVal = LockValueOf(field);
            bool ctlIsPlaceholder = IsPlaceholder(field);
            foreach (var g in _editGames)
            {
                string? id = Safe(() => g.Id);
                if (string.IsNullOrEmpty(id)) continue;
                if (!LockStore.IsFieldLocked(id, key)) continue;
                string val = (IsMulti && ctlIsPlaceholder) ? GameFieldValue(g, key) : ctlVal;
                LockStore.SetFieldLock(id, key, true, val);
            }
        }
    }

    // ── Value extraction ─────────────────────────────────────────────────────────────────────
    // The current editor value of a lockable control, as the string LockStore should preserve.
    private string LockValueOf(Control c)
    {
        if (ReferenceEquals(c, _notes)) return _notes.Text.Replace("\r\n", "\n");
        if (ReferenceEquals(c, _starBar)) return _starBar.UserValue.ToString(CultureInfo.InvariantCulture);
        if (ReferenceEquals(c, _maxPlayers))
        {
            int v = _maxPlayers is NumericUpDown n ? (int)n.Value
                  : int.TryParse((_maxPlayers as TextBox)?.Text.Trim(), out var pv) ? pv : 0;
            return v <= 0 ? "" : v.ToString(CultureInfo.InvariantCulture);
        }
        return c switch
        {
            ComboBox cb => cb.Text.Trim(),
            TextBox t => t.Text.Trim(),
            _ => "",
        };
    }

    // A single game's own value for a field key (used when a multi-value field still shows the placeholder).
    private string GameFieldValue(IGame g, string key) => key switch
    {
        "title"        => Safe(() => g.Title) ?? "",
        "release_date" => FmtDate(Safe(() => (DateTime?)g.ReleaseDate)),
        "rating"       => Safe(() => g.Rating) ?? "",
        "release_type" => Safe(() => g.ReleaseType) ?? "",
        "max_players"  => Safe(() => { int v = g.MaxPlayers ?? 0; return v > 0 ? v.ToString(CultureInfo.InvariantCulture) : ""; }) ?? "",
        "genre"        => Safe(() => g.GenresString) ?? "",
        "platform"     => Safe(() => g.Platform) ?? "",
        "developer"    => Safe(() => g.Developer) ?? "",
        "publisher"    => Safe(() => g.Publisher) ?? "",
        "series"       => Safe(() => g.Series) ?? "",
        "region"       => Safe(() => g.Region) ?? "",
        "play_mode"    => Safe(() => g.PlayMode) ?? "",
        "version"      => Safe(() => g.Version) ?? "",
        "status"       => Safe(() => g.Status) ?? "",
        "source"       => Safe(() => g.Source) ?? "",
        "last_played"  => FmtDate(Safe(() => (DateTime?)g.LastPlayedDate)),
        "progress"     => Safe(() => g.Progress) ?? "",
        "video_url"    => Safe(() => g.VideoUrl) ?? "",
        "wiki_url"     => Safe(() => g.WikipediaUrl) ?? "",
        "star_rating"  => Safe(() => ((double)g.StarRatingFloat).ToString(CultureInfo.InvariantCulture)) ?? "",
        "notes"        => Safe(() => (g.Notes ?? "").Replace("\r\n", "\n")) ?? "",
        _ => "",
    };
}
