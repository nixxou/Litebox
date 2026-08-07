// Real DataManager backed by the LaunchBox XMLs: compact GameStore for games,
// PlatformCatalog (metadata + custom media folders) for platforms/categories,
// EmulatorCatalog for emulators. Games are linked into their platform.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;

namespace LbApiHost.Host.Data;

internal sealed class HostDataManagerXml : DummyDataManager
{
    private readonly GameStore _store;
    /// <summary>The backing store, for the few callers that need what the SDK does not expose —
    /// per-game sub-entities such as &lt;GameSave&gt;.</summary>
    internal GameStore Store => _store;
    private readonly string _imagesRoot;
    private readonly List<IGame> _allGames;   // index-aligned with store.Rows; AddNewGame appends
    private readonly List<IPlatform> _platforms;
    private readonly Dictionary<string, IPlatform> _platformByName;
    private readonly List<IPlatformCategory> _categories;
    private readonly Dictionary<string, IPlatformCategory> _categoryByName;
    private readonly List<IEmulator> _emulators;
    private readonly Dictionary<string, IEmulator> _emulatorById;
    private readonly List<IPlaylist> _playlists;
    private readonly Dictionary<string, IPlaylist> _playlistById;
    private readonly List<object> _roots;   // tree roots (categories/platforms/playlists) from Parents.xml

    /// <summary>Host-side tree roots for the GUI (objects: HostPlatform / HostPlatformCategory / HostPlaylist).</summary>
    public IReadOnlyList<object> RootNodes => _roots;

    /// <summary>Read-only mode passthrough to the store (GUI option). True = never write to disk.</summary>
    public bool ReadOnly { get => _store?.ReadOnly ?? true; set { if (_store != null) _store.ReadOnly = value; } }

    /// <summary>Opportunistic write-back: flush the pending op-log to the XMLs NOW
    /// when it is safe (not read-only, LaunchBox/BigBox not running). Used by the
    /// editors so a change is on disk when their window closes instead of waiting
    /// for LiteBox to exit. No-op otherwise (the log keeps the ops).</summary>
    public void FlushIfSafe() { try { _store?.FlushJournalIfSafe(); } catch { } }

    /// <summary>Records a whole-collection replace of the game-controller CATALOG
    /// (Data\GameControllers.xml) — applied by the GameController branch of the op flush.</summary>
    public void ReplaceGameControllerCatalog(string json)
    { try { _store?.RecordEntityReplace("GameController", "GameControllers", json); } catch { } }

    /// <summary>Scoped variant for the emulator editors: flush ONLY the ops targeting
    /// Emulators.xml, leaving game/playlist ops pending until close. LB plugins read
    /// the XMLs directly (no settings API), so this keeps them on fresh emulator data.</summary>
    public void FlushEmulatorsIfSafe() { try { _store?.FlushEmulatorJournalIfSafe(); } catch { } }

    /// <summary>Scoped variant for the global options window: flush ONLY the
    /// "Settings" ops (Settings.xml).</summary>
    public void FlushLbSettingsIfSafe() { try { _store?.FlushLbSettingsJournalIfSafe(); } catch { } }

    /// <summary>Reconcile GOG/Steam games' Installed flag (and the GOG ApplicationPath)
    /// against the clients' local state — LiteBox runs without LaunchBox.exe, so nothing
    /// else flips these when a store game is (un)installed. Reads Galaxy's DB / Steam's
    /// appmanifest and writes back via the op-log. Fail-soft.</summary>
    public int SyncStoreInstallStates(bool quiet = false) { try { return StoreInstallStateSync.Sync(_store, quiet); } catch { return 0; } }

    /// <summary>LiteBox's own last (emulatorId, additionalAppId) for a game — the fallback the launch
    /// buttons use for their initial selection when ExtendDB isn't loaded. Null if none recorded.</summary>
    public (string emulatorId, string additionalAppId)? GetLastLaunch(string gameId)
    { try { return _store?.GetLastLaunch(gameId); } catch { return null; } }

    /// <summary>LiteBox's last (emulatorId, additionalAppId, extractedRomPath) for a game — the launch
    /// buttons' initial selection fallback INCLUDING the last archive ROM, used when ExtendDB is absent
    /// but the native ROM module drives the ROM UI. Null if none recorded.</summary>
    public (string emulatorId, string additionalAppId, string extractedRomPath)? GetLastLaunchFull(string gameId)
    { try { return _store?.GetLastLaunchFull(gameId); } catch { return null; } }

    /// <summary>Cancels the game's LiteBox launch-history row — the launch buttons' reset-to-default
    /// button, so the next selection seeds pure defaults instead of the last launch.</summary>
    public void ClearLastLaunch(string gameId) { try { _store?.ClearLaunch(gameId); } catch { } }

    /// <summary>LaunchBox's global settings (LB\Data\Settings.xml), lazily loaded.</summary>
    public LbSettingsStore LbSettings => _lbSettings ??= new LbSettingsStore(_dataDir, _store);
    private LbSettingsStore _lbSettings;
    private readonly string _dataDir;

    public HostDataManagerXml(GameStore store, string dataDir, string imagesRoot)
    {
        _store = store;
        _imagesRoot = imagesRoot;
        _dataDir = dataDir;

        // The surviving journal (empty after a clean boot flush). The catalogues below load
        // PRISTINE XML; pending ops — edits made while LaunchBox was running, kept for a later
        // flush — are overlaid onto the loaded objects so the user's work shows immediately
        // instead of only after LB closes and a flush lands. (Game-level ops are handled by
        // GameStore.RecoverJournalOnLoad; this covers the entity catalogues.)
        var pending = store.PendingOps();

        // Game wrappers (thin) built once.
        _allGames = new List<IGame>(store.Count);
        for (int i = 0; i < store.Count; i++) _allGames.Add(new HostGame(store, i));

        // Platforms + categories from Platforms.xml (attach the store so setters write back).
        var (platforms, categories) = PlatformCatalog.Load(dataDir, imagesRoot);
        string lbRootForFolders = Path.GetDirectoryName(dataDir) ?? "";
        OverlayPlatformOps(pending, platforms, categories, imagesRoot, lbRootForFolders);
        foreach (var c in categories) c.Attach(_store);
        _categories = categories.Cast<IPlatformCategory>().ToList();
        _categoryByName = new Dictionary<string, IPlatformCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _categories) { var n = c?.Name; if (!string.IsNullOrEmpty(n)) _categoryByName[n] = c; }

        var byName = new Dictionary<string, HostPlatform>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in platforms) byName[p.Name] = p;

        // Ensure every platform referenced by a game exists (some games may
        // reference a platform absent from Platforms.xml).
        foreach (var kv in store.ByPlatform)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (!byName.ContainsKey(kv.Key))
                byName[kv.Key] = new HostPlatform(kv.Key, null, imagesRoot);
        }

        // Link games into each platform + attach the store (write-back).
        foreach (var p in byName.Values)
        {
            var idxs = store.ByPlatform.TryGetValue(p.Name, out var list) ? list : null;
            p.SetGames(idxs != null ? idxs.Select(i => _allGames[i]).ToArray() : Array.Empty<IGame>());
            p.Attach(_store);
        }

        _platforms = byName.Values.Cast<IPlatform>().ToList();
        _platformByName = byName.ToDictionary(kv => kv.Key, kv => (IPlatform)kv.Value, StringComparer.OrdinalIgnoreCase);

        // Emulators (attach the store so setters / AddNew / TryRemove route through the op-log).
        var emus = EmulatorCatalog.Load(dataDir);
        OverlayEmulatorOps(pending, emus);
        foreach (var e in emus) e.Attach(_store);
        _emulators = emus.Cast<IEmulator>().ToList();
        _emulatorById = emus.ToDictionary(e => e.Id, e => (IEmulator)e, StringComparer.OrdinalIgnoreCase);

        // Playlists: manual ones resolve via GetGameById; auto-populate ones
        // evaluate their filters over the full game list.
        var playlists = PlaylistCatalog.Load(dataDir, imagesRoot);
        OverlayPlaylistOps(pending, playlists, imagesRoot);
        foreach (var pl in playlists) { pl.SetResolver(GetGameById); pl.SetAllGamesProvider(() => _allGames); pl.Attach(_store); }
        _playlists = playlists.Cast<IPlaylist>().ToList();
        _playlistById = new Dictionary<string, IPlaylist>(StringComparer.OrdinalIgnoreCase);
        foreach (var pl in playlists)
            if (!string.IsNullOrEmpty(pl.PlaylistIdValue)) _playlistById[pl.PlaylistIdValue] = pl;

        // ── Category tree from Parents.xml (the LaunchBox-native hierarchy) ──
        var catByName = new Dictionary<string, HostPlatformCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in categories) if (!string.IsNullOrEmpty(c.Name)) catByName[c.Name] = c;
        var plById = new Dictionary<string, HostPlaylist>(StringComparer.OrdinalIgnoreCase);
        foreach (var pl in playlists) if (!string.IsNullOrEmpty(pl.PlaylistIdValue)) plById[pl.PlaylistIdValue] = pl;

        object ResolveNode(string platName, string playId, string catName)
            => !string.IsNullOrEmpty(platName) ? (byName.TryGetValue(platName, out var p) ? (object)p : null)
             : !string.IsNullOrEmpty(playId) ? (plById.TryGetValue(playId, out var pl) ? (object)pl : null)
             : !string.IsNullOrEmpty(catName) ? (catByName.TryGetValue(catName, out var c) ? (object)c : null)
             : null;

        // LB parent rules: platform parents = Root/categories; category & playlist parents = Root/categories/
        // PLATFORMS (playlists are never parents). A row whose parent fields are ALL EMPTY is an EXPLICIT
        // "Root" membership — a node can be at Root AND under categories simultaneously (multi-parent).
        var hasParent = new HashSet<object>();
        var explicitRoot = new HashSet<object>();
        string parentsFile = Path.Combine(dataDir, "Parents.xml");
        if (File.Exists(parentsFile))
        {
            try
            {
                foreach (var pe in XDocument.Load(parentsFile).Root.Elements("Parent"))
                {
                    var node = ResolveNode((string)pe.Element("PlatformName"), (string)pe.Element("PlaylistId"), (string)pe.Element("PlatformCategoryName"));
                    if (node == null) continue;
                    var parent = ResolveNode((string)pe.Element("ParentPlatformName"), (string)pe.Element("ParentPlaylistId"), (string)pe.Element("ParentPlatformCategoryName"));
                    if (parent is HostPlatformCategory parentCat) { parentCat.AddChild(node); hasParent.Add(node); }
                    else if (parent is HostPlatform parentPlat) { parentPlat.AddTreeChild(node); hasParent.Add(node); }
                    else explicitRoot.Add(node);   // empty (or unresolvable) parent = Root membership
                }
            }
            catch (Exception ex) { Console.WriteLine("[HostDataManagerXml] Parents.xml: " + ex.Message); }
        }
        foreach (var c in categories) c.SortChildren();
        foreach (var p in byName.Values) p.SortTreeChildren();

        // Roots = explicit Root memberships + every node with no parent row at all.
        var roots = new List<object>();
        foreach (var c in categories) if (explicitRoot.Contains(c) || !hasParent.Contains(c)) roots.Add(c);
        foreach (var p in byName.Values) if (explicitRoot.Contains(p) || !hasParent.Contains(p)) roots.Add(p);
        foreach (var pl in playlists) if (explicitRoot.Contains(pl) || !hasParent.Contains(pl)) roots.Add(pl);
        _roots = roots.OrderBy(HostPlatformCategory.NodeName, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine($"[HostDataManagerXml] playlists={_playlists.Count} roots={_roots.Count}");
    }

    /// <summary>Re-read Parents.xml and rebuild the category tree + roots IN PLACE — called after the Edit
    /// Platform window closes (rename / category membership changes) so the source tree refreshes without a
    /// restart. Platform renames are re-keyed from the live objects (their Name setters already updated them).</summary>
    public void ReloadHierarchy()
    {
        // Les paniers par plateforme datent du CHARGEMENT : un jeu cree par un Expand n y entrait
        // jamais, un jeu supprime par un Combine n en sortait jamais — le nœud plateforme montrait
        // l ancien monde jusqu au redemarrage, alors que le nœud « All », qui filtre sur le store,
        // etait juste. Reconstruits ici, ou combine et expand passent deja tous les deux.
        var byPlat = new Dictionary<string, List<IGame>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _allGames)
        {
            string plat;
            try
            {
                if (!Guid.TryParse(g.Id, out var gid) || !_store.ById.ContainsKey(gid)) continue;
                plat = g.Platform ?? "";
            }
            catch { continue; }
            if (plat.Length == 0) continue;
            if (!byPlat.TryGetValue(plat, out var lst)) byPlat[plat] = lst = new List<IGame>();
            lst.Add(g);
        }
        foreach (var p in _platforms.OfType<HostPlatform>())
        {
            string pn; try { pn = p.Name ?? ""; } catch { pn = ""; }
            p.SetGames(byPlat.TryGetValue(pn, out var lst) ? lst.ToArray() : Array.Empty<IGame>());
        }

        _platformByName.Clear();
        foreach (var p in _platforms)
        { string n; try { n = p?.Name; } catch { n = null; } if (!string.IsNullOrEmpty(n)) _platformByName[n] = p; }
        _categoryByName.Clear();
        foreach (var c0 in _categories)
        { string n; try { n = c0?.Name; } catch { n = null; } if (!string.IsNullOrEmpty(n)) _categoryByName[n] = c0; }

        var catByName = new Dictionary<string, HostPlatformCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _categories.OfType<HostPlatformCategory>())
        { c.ClearChildren(); if (!string.IsNullOrEmpty(c.Name)) catByName[c.Name] = c; }
        foreach (var p in _platforms.OfType<HostPlatform>()) p.ClearTreeChildren();
        var plById = new Dictionary<string, HostPlaylist>(StringComparer.OrdinalIgnoreCase);
        foreach (var pl in _playlists.OfType<HostPlaylist>())
            if (!string.IsNullOrEmpty(pl.PlaylistIdValue)) plById[pl.PlaylistIdValue] = pl;

        object ResolveNode(string platName, string playId, string catName)
            => !string.IsNullOrEmpty(platName) ? (_platformByName.TryGetValue(platName, out var p) ? (object)p : null)
             : !string.IsNullOrEmpty(playId) ? (plById.TryGetValue(playId, out var pl) ? (object)pl : null)
             : !string.IsNullOrEmpty(catName) ? (catByName.TryGetValue(catName, out var c) ? (object)c : null)
             : null;

        var hasParent = new HashSet<object>();
        var explicitRoot = new HashSet<object>();
        string parentsFile = Path.Combine(_dataDir, "Parents.xml");
        if (File.Exists(parentsFile))
        {
            try
            {
                foreach (var pe in XDocument.Load(parentsFile).Root.Elements("Parent"))
                {
                    var node = ResolveNode((string)pe.Element("PlatformName"), (string)pe.Element("PlaylistId"), (string)pe.Element("PlatformCategoryName"));
                    if (node == null) continue;
                    var parent = ResolveNode((string)pe.Element("ParentPlatformName"), (string)pe.Element("ParentPlaylistId"), (string)pe.Element("ParentPlatformCategoryName"));
                    if (parent is HostPlatformCategory parentCat) { parentCat.AddChild(node); hasParent.Add(node); }
                    else if (parent is HostPlatform parentPlat) { parentPlat.AddTreeChild(node); hasParent.Add(node); }
                    else explicitRoot.Add(node);   // empty (or unresolvable) parent = Root membership
                }
            }
            catch (Exception ex) { Console.WriteLine("[HostDataManagerXml] Parents.xml reload: " + ex.Message); }
        }
        foreach (var c in _categories.OfType<HostPlatformCategory>()) c.SortChildren();
        foreach (var p in _platforms.OfType<HostPlatform>()) p.SortTreeChildren();

        var roots = new List<object>();
        foreach (var c in _categories) if (explicitRoot.Contains(c) || !hasParent.Contains(c)) roots.Add(c);
        foreach (var p in _platforms) if (explicitRoot.Contains(p) || !hasParent.Contains(p)) roots.Add(p);
        foreach (var pl in _playlists) if (explicitRoot.Contains(pl) || !hasParent.Contains(pl)) roots.Add(pl);
        _roots.Clear();
        _roots.AddRange(roots.OrderBy(HostPlatformCategory.NodeName, StringComparer.OrdinalIgnoreCase));
    }

    // Filter out wrappers whose store row was deleted (game deletion keeps the wrapper in _allGames for
    // index alignment; platform deletion drops whole platforms' rows) — no ghosts in "All Games".
    public override IGame[] GetAllGames()
        => _allGames.Where(g => { try { return Guid.TryParse(g.Id, out var gid) && _store.ById.ContainsKey(gid); } catch { return false; } }).ToArray();

    /// <summary>In-memory removal of a platform (its game rows, list + lookup entries). The XML surgery
    /// (Platforms.xml node, Parents.xml refs, the games file…) is the caller's job; follow with
    /// ReloadHierarchy so the tree drops the node.</summary>
    public void DeletePlatformInternal(IPlatform p)
    {
        if (p == null) return;
        string name; try { name = p.Name; } catch { name = null; }
        if (!string.IsNullOrEmpty(name)) { _store.DropPlatformRows(name); _platformByName.Remove(name); }
        _platforms.Remove(p);
    }

    public void DeleteCategoryInternal(IPlatformCategory c)
    {
        if (c == null) return;
        string name; try { name = c.Name; } catch { name = null; }
        if (!string.IsNullOrEmpty(name)) _categoryByName.Remove(name);
        _categories.Remove(c);
    }

    public void DeletePlaylistInternal(IPlaylist pl)
    {
        if (pl == null) return;
        string id = (pl as HostPlaylist)?.PlaylistIdValue;
        if (!string.IsNullOrEmpty(id)) _playlistById.Remove(id);
        _playlists.Remove(pl);
    }
    public override IGame GetGameById(string id)
        => (Guid.TryParse(id, out var g) && _store.ById.TryGetValue(g, out var i)) ? _allGames[i] : null;

    /// <summary>Pre-size the row buffer for a KNOWN batch of AddNewGame calls (Expand, an import):
    /// one exact reallocation instead of chunked growth. Purely an optimization hint — omitting it
    /// is always correct.</summary>
    internal void ReserveAdditionalGames(int n) { try { if (n > 0) _store?.Reserve(_store.Count + n); } catch { } }

    public override IGame AddNewGame(string title)
    {
        int idx = _store.AddGameRow(title ?? "", out _);   // grows the store + logs an "add" op
        var g = new HostGame(_store, idx);
        _allGames.Add(g);                                  // stays index-aligned (idx == old count)
        return g;
    }

    public override bool TryRemoveGame(IGame game)
    {
        // Removes from the store (logs a "delete" op); the wrapper stays in _allGames to keep index
        // alignment — GetGameById returns null for it immediately, the list refreshes on next load.
        return game != null && Guid.TryParse(game.Id, out var gid) && _store.DeleteGameRow(gid);
    }

    public override IPlatform[] GetAllPlatforms() => _platforms.ToArray();
    public override IPlatform GetPlatformByName(string name)
        => (name != null && _platformByName.TryGetValue(name, out var p)) ? p : null;
    public override IPlatformCategory[] GetAllPlatformCategories() => _categories.ToArray();
    public override IPlatformCategory GetPlatformCategoryByName(string name)
        => (name != null && _categoryByName.TryGetValue(name, out var c)) ? c : null;
    // The SDK tree is IList<IPlatform>, but categories/playlists aren't IPlatform here — so we wrap
    // them in IPlatform adapters that also implement IPlatformCategory/IPlaylist (see SdkTree), the
    // way real LaunchBox's nodes do. This is what plugin consumers (ExtendDB's LaunchBoxWeb/BigBoxWeb
    // tree) walk via `node is IPlatformCategory` + GetChildren(). The native GUI still uses RootNodes.
    public override IList<IPlatform> GetRootPlatformsCategoriesPlaylists() => SdkTree.WrapChildren(_roots);

    public override IPlatform AddNewPlatform(string name)
    {
        var p = new HostPlatform(name ?? "", null, _imagesRoot);
        p.Attach(_store);
        if (!string.IsNullOrEmpty(name)) _platformByName[name] = p;
        _platforms.Add(p);
        _store?.RecordEntityAdd("Platform", name ?? "");
        return p;
    }
    public override bool TryRemovePlatform(IPlatform platform)
    {
        if (platform == null || string.IsNullOrEmpty(platform.Name)) return false;
        _platformByName.Remove(platform.Name);
        if (platform is HostPlatform hp) _platforms.Remove(hp);
        _store?.RecordEntityDelete("Platform", platform.Name);
        return true;
    }

    public override IPlatformCategory AddNewPlatformCategory(string name)
    {
        var c = new HostPlatformCategory(name ?? "", _imagesRoot);
        c.Attach(_store);
        if (!string.IsNullOrEmpty(name)) _categoryByName[name] = c;
        _categories.Add(c);
        _store?.RecordEntityAdd("PlatformCategory", name ?? "");
        return c;
    }
    public override bool TryRemovePlatformCategory(IPlatformCategory platformCategory)
    {
        if (platformCategory == null || string.IsNullOrEmpty(platformCategory.Name)) return false;
        _categoryByName.Remove(platformCategory.Name);
        if (platformCategory is HostPlatformCategory hc) _categories.Remove(hc);
        _store?.RecordEntityDelete("PlatformCategory", platformCategory.Name);
        return true;
    }

    public override IEmulator[] GetAllEmulators() => _emulators.ToArray();
    public override IEmulator GetEmulatorById(string id)
        => (id != null && _emulatorById.TryGetValue(id, out var e)) ? e : null;

    public override IEmulator AddNewEmulator()
    {
        string id = Guid.NewGuid().ToString();
        var e = new HostEmulator(id, new Dictionary<string, string>(StringComparer.Ordinal) { ["ID"] = id }, new List<HostEmulatorPlatform>());
        e.Attach(_store);
        _emulators.Add(e);
        _emulatorById[id] = e;
        _store?.RecordEntityAdd("Emulator", id);
        return e;
    }

    public override bool TryRemoveEmulator(IEmulator emulator)
    {
        if (emulator == null || string.IsNullOrEmpty(emulator.Id)) return false;
        _emulatorById.Remove(emulator.Id);
        if (emulator is HostEmulator he) _emulators.Remove(he);
        _store?.RecordEntityDelete("Emulator", emulator.Id);
        return true;
    }

    public override IPlaylist[] GetAllPlaylists() => _playlists.ToArray();
    public override IPlaylist GetPlaylistById(string id)
        => (id != null && _playlistById.TryGetValue(id, out var pl)) ? pl : null;

    public override IPlaylist AddNewPlaylist(string name) => AddNewPlaylistAt(name, _store?.PlaylistFileFor(name));

    /// <summary>Idem, mais avec le fichier cible IMPOSÉ. <see cref="GameStore.PlaylistFileFor"/> dérive le
    /// chemin DU NOM, et deux playlists qui atterrissent dans le même fichier sont fatales — le chargement
    /// n'y lit que le PREMIER &lt;Playlist&gt;. Le copieur de playlists doit donc pouvoir esquiver une
    /// collision de fichier sans toucher au nom que l'utilisateur a choisi.</summary>
    internal HostPlaylist AddNewPlaylistAt(string name, string file)
    {
        string id = Guid.NewGuid().ToString();
        var pl = new HostPlaylist { PlaylistIdValue = id, NameValue = name, FileValue = file, ImagesRootValue = _imagesRoot };
        pl.SetResolver(GetGameById);
        pl.SetAllGamesProvider(() => _allGames);
        pl.Attach(_store);
        _playlists.Add(pl);
        _playlistById[id] = pl;
        _store?.RecordPlaylistAdd(id, pl.FileValue);
        if (!string.IsNullOrEmpty(name)) _store?.RecordPlaylistModify(id, pl.FileValue, "Name", name);
        return pl;
    }

    public override bool TryRemovePlaylist(IPlaylist playlist)
    {
        if (playlist == null || string.IsNullOrEmpty(playlist.PlaylistId)) return false;
        _playlistById.Remove(playlist.PlaylistId);
        _store?.RecordPlaylistDelete(playlist.PlaylistId, (playlist as HostPlaylist)?.FileValue);
        return true;
    }

    public override void Save(bool wait)
    {
        int n = _store.Flush();
        Console.WriteLine($"[HostDataManagerXml] Save(wait={wait}) — flushed {n} game(s) to XML");
        // The badge snapshot is only reusable while every platform XML is byte-for-byte what it was
        // when the pass ran — which the flush above may have just changed. So it is rewritten HERE,
        // after the flush, and never before it.
        try { Badges.BadgeEngine.SaveSnapshot(force: n > 0); } catch { }
    }

    public override void ForceReload() => Console.WriteLine("[HostDataManagerXml] ForceReload — no-op (v1)");

    // ── Boot overlay of PENDING journal ops onto the freshly-loaded catalogues ───────────────
    // Ops are applied in seq order (last-write-wins), through the Silent appliers so nothing is
    // re-recorded. This is what makes "create a playlist / emulator / platform while LaunchBox is
    // open, restart LiteBox" show the object instead of losing it until the eventual flush — the
    // exact bug PlaylistCopier documented as "the copy only appears after two restarts".

    private static void OverlayPlatformOps(List<Op> ops, List<HostPlatform> platforms, List<HostPlatformCategory> categories, string imagesRoot, string lbRoot)
    {
        int n = 0;
        foreach (var op in ops)
        {
            try
            {
                // Custom media folders: a pending row-set replaces what Platforms.xml still holds, so
                // MediaResolver answers from the edit rather than from the file it has not reached yet.
                if (op.OpType == GameStore.KeyedReplace && op.Entity == "PlatformFolder" && !string.IsNullOrEmpty(op.Id))
                {
                    var hp = platforms.FirstOrDefault(x => string.Equals(x.Name, op.Id, StringComparison.OrdinalIgnoreCase));
                    if (hp != null)
                    {
                        var rows = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(op.Value ?? "[]") ?? new();
                        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var r in rows)
                            if (r.TryGetValue("MediaType", out var mt) && r.TryGetValue("FolderPath", out var fp)) stored[mt] = fp;
                        hp.SetPlatformFolders(stored, lbRoot);
                        n++;
                    }
                }
                else if (op.Entity == "Platform" && !string.IsNullOrEmpty(op.Id))
                {
                    var p = platforms.FirstOrDefault(x => string.Equals(x.Name, op.Id, StringComparison.OrdinalIgnoreCase));
                    if (op.OpType == "add") { if (p == null) { platforms.Add(new HostPlatform(op.Id, null, imagesRoot)); n++; } }
                    else if (op.OpType == "modify" && p != null) { p.ApplyFieldSilent(op.Field, op.Value); n++; }
                    else if (op.OpType == "delete" && p != null) { platforms.Remove(p); n++; }
                }
                else if (op.Entity == "PlatformCategory" && !string.IsNullOrEmpty(op.Id))
                {
                    var c = categories.FirstOrDefault(x => string.Equals(x.Name, op.Id, StringComparison.OrdinalIgnoreCase));
                    if (op.OpType == "add") { if (c == null) { categories.Add(new HostPlatformCategory(op.Id, imagesRoot)); n++; } }
                    else if (op.OpType == "modify" && c != null) { c.ApplyFieldSilent(op.Field, op.Value); n++; }
                    else if (op.OpType == "delete" && c != null) { categories.Remove(c); n++; }
                }
            }
            catch (Exception ex) { Console.WriteLine("[overlay] platform op seq=" + op.Seq + ": " + ex.Message); }
        }
        if (n > 0) Console.WriteLine($"[overlay] platforms/categories: {n} pending op(s) re-applied");
    }

    private static void OverlayEmulatorOps(List<Op> ops, List<HostEmulator> emus)
    {
        int n = 0;
        foreach (var op in ops)
        {
            try
            {
                if (op.Entity == "Emulator" && !string.IsNullOrEmpty(op.Id))
                {
                    var e = emus.FirstOrDefault(x => string.Equals(x.Id, op.Id, StringComparison.OrdinalIgnoreCase));
                    if (op.OpType == "add")
                    { if (e == null) { emus.Add(new HostEmulator(op.Id, new Dictionary<string, string>(StringComparer.Ordinal) { ["ID"] = op.Id }, new List<HostEmulatorPlatform>())); n++; } }
                    else if (op.OpType == "modify" && e != null) { e.ApplyFieldSilent(op.Field, op.Value); n++; }
                    else if (op.OpType == "delete" && e != null) { emus.Remove(e); n++; }
                }
                else if (op.Entity == "EmulatorPlatform" && op.OpType == "replace" && !string.IsNullOrEmpty(op.ParentId))
                {
                    var e = emus.FirstOrDefault(x => string.Equals(x.Id, op.ParentId, StringComparison.OrdinalIgnoreCase));
                    if (e != null) { e.ReplacePlatformsSilent(op.Value); n++; }
                }
            }
            catch (Exception ex) { Console.WriteLine("[overlay] emulator op seq=" + op.Seq + ": " + ex.Message); }
        }
        if (n > 0) Console.WriteLine($"[overlay] emulators: {n} pending op(s) re-applied");
    }

    private static void OverlayPlaylistOps(List<Op> ops, List<HostPlaylist> playlists, string imagesRoot)
    {
        int n = 0;
        HostPlaylist Find(string id) => playlists.FirstOrDefault(x => string.Equals(x.PlaylistIdValue, id, StringComparison.OrdinalIgnoreCase));
        foreach (var op in ops)
        {
            try
            {
                if (op.Entity == "Playlist" && !string.IsNullOrEmpty(op.Id))
                {
                    var pl = Find(op.Id);
                    if (op.OpType == "add")
                    { if (pl == null) { playlists.Add(new HostPlaylist { PlaylistIdValue = op.Id, FileValue = op.ParentId, ImagesRootValue = imagesRoot }); n++; } }
                    else if (op.OpType == "modify" && pl != null) { pl.ApplyFieldSilent(op.Field, op.Value); n++; }
                    else if (op.OpType == "delete" && pl != null) { playlists.Remove(pl); n++; }
                }
                else if (op.Entity == "PlaylistGame" && op.OpType == "replace" && !string.IsNullOrEmpty(op.Id))
                { var pl = Find(op.Id); if (pl != null) { pl.ReplaceGamesSilent(op.Value); n++; } }
                else if (op.Entity == "PlaylistFilter" && op.OpType == "replace" && !string.IsNullOrEmpty(op.Id))
                { var pl = Find(op.Id); if (pl != null) { pl.ReplaceFiltersSilent(op.Value); n++; } }
            }
            catch (Exception ex) { Console.WriteLine("[overlay] playlist op seq=" + op.Seq + ": " + ex.Message); }
        }
        if (n > 0) Console.WriteLine($"[overlay] playlists: {n} pending op(s) re-applied");
    }
}
