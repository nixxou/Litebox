// The library endpoints: platforms, the paginated roms list, and the single-rom detail.
//
//   GET /api/platforms                      → PlatformSchema[]
//   GET /api/platforms/identifiers          → int[]
//   GET /api/platforms/{id}                 → PlatformSchema
//   GET /api/roms?platform_ids=&search_term=&order_by=&order_dir=&limit=&offset=  → paginated SimpleRomSchema
//   GET /api/roms/identifiers               → int[] (the filtered id set)
//   GET /api/roms/{id}                      → DetailedRomSchema (asset lists arrive with S5)
//   GET /api/stats                          → library totals
//
// The rom DTO is RomM's SimpleRomSchema with every field present and honestly null/empty where LiteBox
// has nothing to say — a missing KEY breaks a client's parser, a null value does not. Covers ride the
// existing signed MediaProxy (registered on this router too), so path_cover_small is a same-origin URL
// the client just prefixes with its base URL, and the proxy's HMAC keeps it from reading arbitrary disk.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LbApiHost.Host.Web;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

internal static class RommLibraryApi
{
    // ── Platforms ─────────────────────────────────────────────────────────────

    public static HttpResponse Platforms(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.PlatformsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        return RommApi.Json(RommLibrary.Platforms(st, identity?.TokenId).Select(PlatformDto).ToArray());
    }

    public static HttpResponse PlatformIdentifiers(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.PlatformsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        return RommApi.Json(RommLibrary.Platforms(st, identity?.TokenId).Select(p => p.Id).ToArray());
    }

    public static HttpResponse PlatformById(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.PlatformsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        var p = RommLibrary.PlatformById(ctx.GetRouteInt("id", -1), st);
        return p == null ? RommApi.Error(404, "Platform not found") : RommApi.Json(PlatformDto(p));
    }

    /// <summary>Every slug RomM supports, as unmatched shells — the setup pages call this; answering the
    /// mapped set keeps it small and true.</summary>
    public static HttpResponse PlatformsSupported(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.PlatformsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        return RommApi.Json(RommLibrary.Platforms(st, identity?.TokenId).Select(PlatformDto).ToArray());
    }

    private static object PlatformDto(RommPlatform p) => new
    {
        id = p.Id,
        slug = p.Slug,
        fs_slug = p.Slug,
        // Probe: a client keying its cache on this count should refresh exactly once after it moves.
        rom_count = p.RomCount + RommConfig.DebugBumpRomCount,
        name = p.LbName,
        // The slug side of our map IS the IGDB-ish vocabulary, so advertising it as igdb_slug lets a
        // client that keys its emulator map off igdb_slug work unchanged.
        igdb_slug = p.Slug,
        moby_slug = (string?)null,
        hltb_slug = (string?)null,
        libretro_slug = (string?)null,
        custom_name = (string?)null,
        display_name = p.LbName,
        description = (string?)null,
        igdb_id = (int?)null,
        sgdb_id = (int?)null,
        moby_id = (int?)null,
        launchbox_id = (int?)null,
        ss_id = (int?)null,
        ra_id = (int?)null,
        hasheous_id = (int?)null,
        tgdb_id = (int?)null,
        flashpoint_id = (int?)null,
        category = (string?)null,
        generation = (int?)null,
        family_name = (string?)null,
        family_slug = (string?)null,
        url = (string?)null,
        url_logo = "",
        firmware = Array.Empty<object>(),
        firmware_count = 0,
        created_at = RommAuthApi.Iso(DateTime.UnixEpoch),
        updated_at = RommAuthApi.Iso(DateTime.UtcNow),
        fs_size_bytes = 0L,
        is_unidentified = false,
        is_identified = true,
        missing_from_fs = false,
    };

    // ── Roms ──────────────────────────────────────────────────────────────────

    public static HttpResponse Roms(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.RomsRead, out var identity);
        if (refused != null) return refused;

        var req = ctx.Request!;
        var st = RommLibrary.Parental(req);

        int? platformId = null;
        var rawPlatform = req.GetQuery("platform_ids") ?? req.GetQuery("platform_id");
        if (!string.IsNullOrEmpty(rawPlatform))
        {
            // Repeating the parameter collapses to the last value in our query parser; one platform per
            // request is what every client sends.
            var first = rawPlatform!.Split(',')[0].Trim();
            if (int.TryParse(first, out var pid)) platformId = pid;
        }

        var games = RommLibrary.Query(
            platformId,
            req.GetQuery("search_term"),
            (req.GetQuery("order_by") ?? "name").ToLowerInvariant(),
            (req.GetQuery("order_dir") ?? "asc").ToLowerInvariant(),
            st, identity?.TokenId);

        int limit = Math.Clamp(req.GetQueryInt("limit", 50), 1, 10_000);
        int offset = Math.Max(0, req.GetQueryInt("offset", 0));

        // The binding narrows the list view too, or a bound client would see every entry here and only
        // its own on the detail page.
        // Les rom_id de la page, puis LEURS lignes en une seule requete — pas une par ligne.
        var slice = games.Skip(offset).Take(limit).ToList();
        var ids = slice.Select(g => RommRoms.RomIdFor(g, identity?.TokenId)).Where(v => v > 0).ToList();
        var rows = RommIndexer.RowsOf(ids);

        var page = slice.Select(g =>
        {
            long id = RommRoms.RomIdFor(g, identity?.TokenId);
            rows.TryGetValue(id, out var r);
            return RomDto(g, detailed: false, identity?.TokenId, r);
        }).ToArray();

        // The sidecar indexes back RomM's virtual scroll; each is gated by its query flag like upstream.
        var charIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        if (req.GetQueryBool("with_char_index", true))
        {
            for (int i = 0; i < games.Count; i++)
            {
                var name = RommLibrary.SortNameOf(games[i]);
                var c = name.Length > 0 && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : "#";
                if (!charIndex.ContainsKey(c)) charIndex[c] = i;
            }
        }

        var romIdIndex = req.GetQueryBool("with_rom_id_index", true)
            ? games.Select(g => (int)RommRoms.RomIdFor(g, identity?.TokenId)).ToArray()
            : Array.Empty<int>();

        object filterValues = new
        {
            genres = Array.Empty<string>(),
            franchises = Array.Empty<string>(),
            collections = Array.Empty<string>(),
            companies = Array.Empty<string>(),
            game_modes = Array.Empty<string>(),
            age_ratings = Array.Empty<string>(),
            player_counts = Array.Empty<string>(),
            regions = Array.Empty<string>(),
            languages = Array.Empty<string>(),
            tags = Array.Empty<string>(),
            platforms = games.Select(g => RommIdMap.PlatformId(RommLibrary.PlatformOf(g))).Distinct().ToArray(),
        };

        return RommApi.Json(new
        {
            items = page,
            total = games.Count,
            limit,
            offset,
            char_index = charIndex,
            rom_id_index = romIdIndex,
            filter_values = filterValues,
        });
    }

    public static HttpResponse RomIdentifiers(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.RomsRead, out var identity);
        if (refused != null) return refused;

        var req = ctx.Request!;
        var st = RommLibrary.Parental(req);
        int? platformId = int.TryParse(req.GetQuery("platform_ids"), out var pid) ? pid : null;
        var games = RommLibrary.Query(platformId, req.GetQuery("search_term"), "name", "asc", st,
                                      identity?.TokenId);
        return RommApi.Json(games.Select(g => (int)RommRoms.RomIdFor(g, identity?.TokenId)).ToArray());
    }

    public static HttpResponse RomById(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.RomsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        var game = RommLibrary.GameByRomId(ctx.GetRouteInt("id", -1));
        if (game == null) return RommApi.Error(404, "Rom not found");
        if (st != null && (st.IsHidden(RommLibrary.PlatformOf(game)) || !st.IsRatingAllowed(RommLibrary.EsrbOf(game))))
            return RommApi.Error(404, "Rom not found");   // hidden ≠ forbidden: don't confirm it exists
        if (RommLibrary.HiddenOf(game) && !RommConfig.ExposeHiddenGames)
            return RommApi.Error(404, "Rom not found");

        // The detail view is the only one that opens an archive, so it is the only one a client's ROM
        // binding can narrow.
        // The row travels too: without it the embedded saves cover the whole game — with extraction
        // on, another entry's saves land in this ROM's detail, stamped with the wrong rom id.
        return RommApi.Json(RomDto(game, detailed: true, identity?.TokenId,
                                   RommIndexer.RowOf(ctx.GetRouteInt("id", -1))));
    }

    public static HttpResponse Stats(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.RomsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        var platforms = RommLibrary.Platforms(st, identity?.TokenId);
        long totalGames = platforms.Sum(p => (long)p.RomCount);
        return RommApi.Json(new
        {
            PLATFORMS = platforms.Count,
            ROMS = totalGames,
            SAVES = 0,
            STATES = 0,
            SCREENSHOTS = 0,
            TOTAL_FILESIZE_BYTES = 0L,
        });
    }

    // ── The rom DTO ───────────────────────────────────────────────────────────

    /// <param name="row">The romm_games row this client is served, when the caller already fetched it
    /// for the whole page. Null means "look it up" — right for a single game, wrong for a listing.</param>
    internal static object RomDto(IGame g, bool detailed, int? tokenId = null, RommGameRow? row = null)
    {
        var gameId = RommLibrary.IdOf(g);
        int romId = (int)RommRoms.RomIdFor(g, tokenId);
        var platformName = RommLibrary.PlatformOf(g);
        var slug = RommPlatformMap.SlugFor(platformName) ?? "unknown";
        int platformId = RommIdMap.PlatformId(platformName);

        var absPath = RommLibrary.RomAbsPath(g);
        var fsName = absPath != null ? Path.GetFileName(absPath) : (RommLibrary.TitleOf(g) + ".rom");
        long size = RommLibrary.SizeOf(g);
        var fsPath = "roms/" + slug;
        string fsNameNoExt, fsExt;
        bool missing = absPath == null || !SafeFileExists(absPath);

        // ONE rom, ONE file. The rom_id already names which — either this client's lock, or the game's
        // default slot, which resolves to what the desktop picker would select.
        //
        // NOTHING here may name the archive. A client builds its download URL from this field, and one
        // did: it asked for "Super Mario World 2 - Yoshi's Island.7z" by name. Resolving the real file
        // on a listing row would mean opening one archive per row, so a listing answers with the game's
        // TITLE — which names nothing that can be requested — and the game page tells the truth as soon
        // as a client actually looks at it.
        // WHICH file this client gets. Settled at pairing, so a listing only reads it — and reads the
        // NAME straight out of the stored key, without opening an archive. That is the whole reason the
        // decision was moved to pairing: a client caches this field on sight.
        // Le nom du fichier vient de la LIGNE, jamais d'une lecture d'archive. Une clé d'entrée porte
        // le chemin dans l'archive, donc son basename est la réponse — et c'est ce qui rend un listing à
        // la fois vrai et gratuit. Mesuré : un client construit son URL de téléchargement depuis ce
        // champ et le met en cache dès qu'il voit la ligne.
        // La ligne SERVIE — defaut compris. Lire seulement les epingles etait le defaut : tout le
        // monde est sur le defaut, donc le nom n'etait jamais remplace et le client telechargeait sous
        // le nom de l'archive. Mesure : "…/content/Super Mario World 2 - Yoshi's Island.7z".
        row ??= romId > 0 ? RommIndexer.RowOf(romId) : null;
        if (row != null && row.Emulated)
        {
            var served = row.RomPath.Length > 0 ? row.RomPath : row.FilePath;
            if (served.Length > 0)
            {
                fsName = Path.GetFileName(served.Replace('/', '\\').TrimEnd('\\'));
                if (row.RomPath.Length == 0) size = SafeSize(row.FilePath, size);
            }
        }

        RommFile? chosen = null;
        bool nameIsTitle = false;

        // A title is not a file name: splitting "Mr. Do!" on its dot would advertise an extension of
        // "Do!". Only a real file name has one.
        fsNameNoExt = nameIsTitle ? fsName : Path.GetFileNameWithoutExtension(fsName);
        fsExt = nameIsTitle ? "" : Path.GetExtension(fsName).TrimStart('.');

        var title = RommLibrary.TitleOf(g);
        var added = RommLibrary.AddedOf(g);
        var modified = RommLibrary.ModifiedOf(g);
        var lastPlayed = RommLibrary.LastPlayedOf(g);
        var release = RommLibrary.ReleaseOf(g);

        var cover = CoverUrls(g);
        var regions = SplitList(RommLibrary.RegionOf(g));
        var genres = SplitList(RommLibrary.GenresOf(g));

        var files = BuildFiles(g, gameId, romId, fsPath, added, modified, chosen);

        var dto = new Dictionary<string, object?>
        {
            ["id"] = romId,
            ["igdb_id"] = null,
            ["sgdb_id"] = null,
            ["moby_id"] = null,
            ["ss_id"] = null,
            ["ra_id"] = null,
            ["launchbox_id"] = LaunchBoxDbIdOf(g),
            ["hasheous_id"] = null,
            ["tgdb_id"] = null,
            ["flashpoint_id"] = null,
            ["hltb_id"] = null,
            ["gamelist_id"] = null,
            ["libretro_id"] = null,

            ["platform_id"] = platformId,
            ["platform_slug"] = slug,
            ["platform_fs_slug"] = slug,
            ["platform_custom_name"] = null,
            ["platform_display_name"] = platformName,

            // The client builds its download URL from file_name first, falling back to fs_name;
            // both now name a ROM, so the archive can no longer be requested by accident.
            ["file_name"] = chosen?.FileName,
            ["fs_name"] = fsName,
            ["fs_name_no_tags"] = StripTags(fsNameNoExt),
            ["fs_name_no_ext"] = fsNameNoExt,
            ["fs_extension"] = fsExt,
            ["fs_path"] = fsPath,
            ["fs_size_bytes"] = size,

            ["name"] = title,
            ["name_sort_key"] = RommLibrary.SortNameOf(g).ToLowerInvariant(),
            ["slug"] = Slugify(title),
            // Probe: the description becomes the moment this response was built, so how stale a client's
            // copy is can be read straight off the game page. See RommConfig.DebugStampSummary.
            ["summary"] = RommConfig.DebugStampSummary
                ? "SYNC " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : NullIfEmpty(RommLibrary.NotesOf(g)),

            ["alternative_names"] = Array.Empty<string>(),
            ["youtube_video_id"] = null,
            ["metadatum"] = new
            {
                rom_id = romId,
                genres,
                franchises = Array.Empty<string>(),
                collections = Array.Empty<string>(),
                companies = CompaniesOf(g),
                game_modes = SplitList(RommLibrary.PlayModeOf(g)),
                age_ratings = SplitList(RommLibrary.EsrbOf(g)),
                player_count = "",
                first_release_date = release == null
                    ? (long?)null
                    : new DateTimeOffset(release.Value.ToUniversalTime()).ToUnixTimeMilliseconds(),
                average_rating = RommLibrary.RatingOf(g) > 0 ? (double?)Math.Round(RommLibrary.RatingOf(g) * 20, 1) : null,
            },
            ["igdb_metadata"] = null,
            ["moby_metadata"] = null,
            ["ss_metadata"] = null,
            ["launchbox_metadata"] = null,
            ["hasheous_metadata"] = null,
            ["flashpoint_metadata"] = null,
            ["hltb_metadata"] = null,
            ["gamelist_metadata"] = null,
            ["manual_metadata"] = null,

            ["path_cover_small"] = cover.small,
            ["path_cover_large"] = cover.large,
            ["url_cover"] = "",

            ["has_manual"] = false,
            ["has_soundtrack"] = false,
            ["path_manual"] = null,
            ["url_manual"] = null,
            ["path_video"] = null,

            ["is_identifying"] = false,
            ["is_unidentified"] = false,
            ["is_identified"] = true,

            ["revision"] = NullIfEmpty(RommLibrary.VersionOf(g)),
            ["regions"] = regions,
            ["languages"] = Array.Empty<string>(),
            ["tags"] = Array.Empty<string>(),

            ["crc_hash"] = null,
            ["md5_hash"] = null,
            ["sha1_hash"] = null,
            ["ra_hash"] = null,

            ["has_simple_single_file"] = files.Count <= 1,
            ["has_nested_single_file"] = false,
            ["has_multiple_files"] = files.Count > 1,
            ["full_path"] = fsPath + "/" + fsName,
            ["created_at"] = RommAuthApi.Iso(added),
            ["updated_at"] = RommAuthApi.Iso(modified),
            ["missing_from_fs"] = missing,
            ["has_notes"] = false,

            ["rom_user"] = RommUserApi.RomUserDto(g),
            ["merged_screenshots"] = Array.Empty<string>(),
            ["merged_ra_metadata"] = null,

            ["files"] = files,
            ["sibling_roms"] = Array.Empty<object>(),
            ["screenshot_path"] = null,
        };

        if (detailed)
        {
            // One user ⇒ the "all users" views are the same list as the user's own. Row and token
            // travel: without the row this covered the whole game's saves, and without the token the
            // requester's own branch was filtered out — /api/saves answered differently, and of two
            // contradicting views this one was the wrong one.
            object[] saves, states;
            try { saves = RommAssetsApi.ListForGame(g, states: false, tokenId, row).Select(a => RommAssetsApi.AssetDto(a, "saves")).ToArray(); }
            catch { saves = Array.Empty<object>(); }
            try { states = RommAssetsApi.ListForGame(g, states: true, tokenId, row).Select(a => RommAssetsApi.AssetDto(a, "states")).ToArray(); }
            catch { states = Array.Empty<object>(); }
            dto["user_saves"] = saves;
            dto["user_states"] = states;
            dto["all_user_saves"] = saves;
            dto["all_user_states"] = states;
            dto["user_screenshots"] = Array.Empty<object>();
            dto["all_user_screenshots"] = Array.Empty<object>();
            dto["user_collections"] = Array.Empty<object>();
            dto["all_user_notes"] = Array.Empty<object>();
        }

        return dto;
    }

    // ── files[] ───────────────────────────────────────────────────────────────

    /// <summary>The game's downloadable pieces. The list view stays cheap (the main ROM + the disc
    /// additional-apps — no archive is ever opened); the DETAIL view expands an archive's playable
    /// entries through RomExtractor's memoised listing, each as its own file — RomM's "version" the
    /// user asked for, selected at download/play time by its file id.</summary>
    /// <summary>The archive's playable entries, or empty when the game is not an expandable archive.
    ///
    /// Called for the LIST view too, not just the detail — that asymmetry was the whole defect: versions
    /// showed up everywhere because they cost nothing to enumerate, archive entries only appeared on the
    /// detail page, so a client downloading from a list never knew they existed and asked for the archive.
    /// The listing is cache-first (ArchiveListingCache, persisted), so a warm archive is a keyed DB read;
    /// only one never listed pays for an analysis, and the page size bounds how many can do so at once.</summary>
    private static IReadOnlyList<Rom.RomEntryView> ArchiveEntries(IGame g, string? absPath)
    {
        try
        {
            if (absPath == null || !Rom.RomExtractor.Available || !Rom.RomExtractor.IsArchive(absPath))
                return Array.Empty<Rom.RomEntryView>();
            // No cache probe: this is a JSON projection, not the picker, and the probe is what made a
            // 50-game listing take two seconds.
            var listing = Rom.RomExtractor.ListEntriesDetailed(g, null, probeCache: false).Entries;
            return listing.Count > 1 ? listing : Array.Empty<Rom.RomEntryView>();
        }
        catch { return Array.Empty<Rom.RomEntryView>(); }
    }

    /// <summary>The entry that stands in for the game itself: the one this client is bound to, else the
    /// best-ranked. Never arbitrary — RomExtractor scores by the profile's tag weights and floats
    /// favourites and the last-played entry, so the head of the list is the one you would launch.</summary>
    /// <summary>The entry that stands for the game: the best-ranked one. SortForDisplay has already
    /// floated favourites and last-played, so the head is the one worth naming.</summary>
    private static Rom.RomEntryView? MainEntry(IReadOnlyList<Rom.RomEntryView> entries)
        => entries.Count == 0 ? null : entries[0];

    /// <summary>The rom's files — exactly ONE, because a rom_id names one file.
    ///
    /// has_multiple_files therefore falls to false through the client's own files.Count test, and every
    /// picker it drives disappears. That is the point of the whole model: a device is never shown a
    /// choice it could get wrong, and the choice itself lives in the assignment screen.</summary>
    private static List<object> BuildFiles(IGame g, string gameId, int romId, string fsPath,
                                           DateTime added, DateTime modified, RommFile? chosen)
    {
        var files = new List<object>();

        object FileDto(int id, string name, long bytes, string category) => new
        {
            id,
            rom_id = romId,
            file_name = name,
            file_path = fsPath,
            file_size_bytes = bytes,
            full_path = fsPath + "/" + name,
            is_top_level = true,
            created_at = RommAuthApi.Iso(added),
            updated_at = RommAuthApi.Iso(modified),
            last_modified = RommAuthApi.Iso(modified),
            crc_hash = (string?)null,
            md5_hash = (string?)null,
            sha1_hash = (string?)null,
            ra_hash = (string?)null,
            chd_sha1_hash = (string?)null,
            archive_members = (object?)null,
            category,
            track_meta = (object?)null,
        };

        // No chosen file means a listing row, which does not resolve one — the game's own ROM name is
        // the honest answer there, and the game page says what the file really is.
        if (chosen != null)
            files.Add(FileDto(RommIdMap.FileId(gameId, chosen.Key), chosen.FileName, Math.Max(0, chosen.Size), "game"));
        else
        {
            var mainAbs = RommLibrary.RomAbsPath(g);
            var name = mainAbs != null ? Path.GetFileName(mainAbs) : (RommLibrary.TitleOf(g) + ".rom");
            files.Add(FileDto(RommIdMap.FileId(gameId, RommFiles.MainKey), name, RommLibrary.SizeOf(g), "game"));
        }

        return files;
    }

    // ── Covers ────────────────────────────────────────────────────────────────

    /// <summary>Signed same-origin proxy URLs for the game's box front, or empty strings. Delegates to
    /// the theme surfaces' own resolution (GameCache first, LB image path second) so a client and the
    /// web themes can never disagree about whether a game has a cover. Clients treat "" as "no cover".</summary>
    private static (string small, string large) CoverUrls(IGame g)
    {
        try { return OwnedDataProvider.CoverPair(g); }
        catch { return ("", ""); }
    }

    // ── Small helpers ─────────────────────────────────────────────────────────

    /// <summary>The size of a recorded path, or the fallback when it cannot be read.</summary>
    private static long SafeSize(string rel, long fallback)
    {
        try
        {
            var abs = Rom.RomPaths.ResolveAbsolute(rel);
            return !string.IsNullOrEmpty(abs) && File.Exists(abs) ? new FileInfo(abs).Length : fallback;
        }
        catch { return fallback; }
    }

    private static bool SafeFileExists(string p) { try { return File.Exists(p); } catch { return false; } }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int? LaunchBoxDbIdOf(IGame g) { try { return g.LaunchBoxDbId; } catch { return null; } }

    private static string[] SplitList(string raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

    private static string[] CompaniesOf(IGame g)
    {
        var list = new List<string>();
        list.AddRange(SplitList(RommLibrary.DeveloperOf(g)));
        foreach (var p in SplitList(RommLibrary.PublisherOf(g)))
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        return list.ToArray();
    }

    /// <summary>"The Legend of Zelda" → "the-legend-of-zelda". Cosmetic — nothing routes on it.</summary>
    private static string Slugify(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool dash = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        return sb.ToString().TrimEnd('-');
    }

    /// <summary>Drops the trailing "(USA)"-style tag groups the way RomM's fs_name_no_tags does.</summary>
    private static string StripTags(string s)
    {
        var r = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        return r.Trim();
    }
}
