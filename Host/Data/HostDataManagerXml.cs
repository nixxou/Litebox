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

    /// <summary>LaunchBox's global settings (LB\Data\Settings.xml), lazily loaded.</summary>
    public LbSettingsStore LbSettings => _lbSettings ??= new LbSettingsStore(_dataDir, _store);
    private LbSettingsStore _lbSettings;
    private readonly string _dataDir;

    public HostDataManagerXml(GameStore store, string dataDir, string imagesRoot)
    {
        _store = store;
        _imagesRoot = imagesRoot;
        _dataDir = dataDir;

        // Game wrappers (thin) built once.
        _allGames = new List<IGame>(store.Count);
        for (int i = 0; i < store.Count; i++) _allGames.Add(new HostGame(store, i));

        // Platforms + categories from Platforms.xml (attach the store so setters write back).
        var (platforms, categories) = PlatformCatalog.Load(dataDir, imagesRoot);
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
        foreach (var e in emus) e.Attach(_store);
        _emulators = emus.Cast<IEmulator>().ToList();
        _emulatorById = emus.ToDictionary(e => e.Id, e => (IEmulator)e, StringComparer.OrdinalIgnoreCase);

        // Playlists: manual ones resolve via GetGameById; auto-populate ones
        // evaluate their filters over the full game list.
        var playlists = PlaylistCatalog.Load(dataDir, imagesRoot);
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

        var hasParent = new HashSet<object>();
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
                }
            }
            catch (Exception ex) { Console.WriteLine("[HostDataManagerXml] Parents.xml: " + ex.Message); }
        }
        foreach (var c in categories) c.SortChildren();

        // Roots = every category/platform/playlist that is not a child of a category.
        var roots = new List<object>();
        foreach (var c in categories) if (!hasParent.Contains(c)) roots.Add(c);
        foreach (var p in byName.Values) if (!hasParent.Contains(p)) roots.Add(p);
        foreach (var pl in playlists) if (!hasParent.Contains(pl)) roots.Add(pl);
        _roots = roots.OrderBy(HostPlatformCategory.NodeName, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine($"[HostDataManagerXml] playlists={_playlists.Count} roots={_roots.Count}");
    }

    public override IGame[] GetAllGames() => _allGames.ToArray();
    public override IGame GetGameById(string id)
        => (Guid.TryParse(id, out var g) && _store.ById.TryGetValue(g, out var i)) ? _allGames[i] : null;

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

    public override IPlaylist AddNewPlaylist(string name)
    {
        string id = Guid.NewGuid().ToString();
        var pl = new HostPlaylist { PlaylistIdValue = id, NameValue = name, FileValue = _store?.PlaylistFileFor(name), ImagesRootValue = _imagesRoot };
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
    }

    public override void ForceReload() => Console.WriteLine("[HostDataManagerXml] ForceReload — no-op (v1)");
}
