// Real DataManager backed by the LaunchBox XMLs: compact GameStore for games,
// PlatformCatalog (metadata + custom media folders) for platforms/categories,
// EmulatorCatalog for emulators. Games are linked into their platform.

using System;
using System.Collections.Generic;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;

namespace LbApiHost.Host.Data;

internal sealed class HostDataManagerXml : DummyDataManager
{
    private readonly GameStore _store;
    private readonly IGame[] _allGames;
    private readonly IPlatform[] _platforms;
    private readonly Dictionary<string, IPlatform> _platformByName;
    private readonly IPlatformCategory[] _categories;
    private readonly IEmulator[] _emulators;
    private readonly Dictionary<string, IEmulator> _emulatorById;
    private readonly IPlaylist[] _playlists;
    private readonly Dictionary<string, IPlaylist> _playlistById;

    public HostDataManagerXml(GameStore store, string dataDir, string imagesRoot)
    {
        _store = store;

        // Game wrappers (thin) built once.
        _allGames = new IGame[store.Count];
        for (int i = 0; i < store.Count; i++) _allGames[i] = new HostGame(store, i);

        // Platforms + categories from Platforms.xml.
        var (platforms, categories) = PlatformCatalog.Load(dataDir, imagesRoot);
        _categories = categories.Cast<IPlatformCategory>().ToArray();

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

        // Link games into each platform.
        foreach (var p in byName.Values)
        {
            var idxs = store.ByPlatform.TryGetValue(p.Name, out var list) ? list : null;
            p.SetGames(idxs != null ? idxs.Select(i => _allGames[i]).ToArray() : Array.Empty<IGame>());
        }

        _platforms = byName.Values.Cast<IPlatform>().ToArray();
        _platformByName = byName.ToDictionary(kv => kv.Key, kv => (IPlatform)kv.Value, StringComparer.OrdinalIgnoreCase);

        // Emulators.
        var emus = EmulatorCatalog.Load(dataDir);
        _emulators = emus.Cast<IEmulator>().ToArray();
        _emulatorById = emus.ToDictionary(e => e.Id, e => (IEmulator)e, StringComparer.OrdinalIgnoreCase);

        // Playlists: manual ones resolve via GetGameById; auto-populate ones
        // evaluate their filters over the full game list.
        var playlists = PlaylistCatalog.Load(dataDir);
        foreach (var pl in playlists) { pl.SetResolver(GetGameById); pl.SetAllGamesProvider(() => _allGames); }
        _playlists = playlists.Cast<IPlaylist>().ToArray();
        _playlistById = new Dictionary<string, IPlaylist>(StringComparer.OrdinalIgnoreCase);
        foreach (var pl in playlists)
            if (!string.IsNullOrEmpty(pl.PlaylistIdValue)) _playlistById[pl.PlaylistIdValue] = pl;

        Console.WriteLine($"[HostDataManagerXml] playlists={_playlists.Length}");
    }

    public override IGame[] GetAllGames() => _allGames;
    public override IGame GetGameById(string id)
        => (Guid.TryParse(id, out var g) && _store.ById.TryGetValue(g, out var i)) ? _allGames[i] : null;

    public override IPlatform[] GetAllPlatforms() => _platforms;
    public override IPlatform GetPlatformByName(string name)
        => (name != null && _platformByName.TryGetValue(name, out var p)) ? p : null;
    public override IPlatformCategory[] GetAllPlatformCategories() => _categories;
    public override IList<IPlatform> GetRootPlatformsCategoriesPlaylists() => _platforms.ToList();

    public override IEmulator[] GetAllEmulators() => _emulators;
    public override IEmulator GetEmulatorById(string id)
        => (id != null && _emulatorById.TryGetValue(id, out var e)) ? e : null;

    public override IPlaylist[] GetAllPlaylists() => _playlists;
    public override IPlaylist GetPlaylistById(string id)
        => (id != null && _playlistById.TryGetValue(id, out var pl)) ? pl : null;

    public override void Save(bool wait)
    {
        int n = _store.Flush();
        Console.WriteLine($"[HostDataManagerXml] Save(wait={wait}) — flushed {n} game(s) to XML");
    }

    public override void ForceReload() => Console.WriteLine("[HostDataManagerXml] ForceReload — no-op (v1)");
}
