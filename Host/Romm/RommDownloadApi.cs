// GET/HEAD /api/roms/{id}/content/{file_name}?file_ids=&hidden_folder= — the download.
//
// What gets served, by shape:
//   • plain single-file game            → the ROM streamed from disk, Range honoured.
//   • archive, no file_ids             → the archive AS-IS (clients and EmulatorJS unpack themselves;
//                                         zero host CPU, and the whole file resumes cleanly).
//   • file_ids naming an archive entry → that entry, extracted through RomExtractor's regular LRU cache
//                                         (a resumed or repeated download re-extracts nothing). When the
//                                         profile extracts companions (.cue → .bins), the whole extracted
//                                         directory ships as a zip — the file alone would be broken.
//   • several file_ids / multi-disc    → a STORE zip streamed on the fly (ZipArchive over the chunked
//                                         body — ROMs are already compressed, deflate would only burn CPU)
//                                         plus a generated .m3u, honouring muOS's hidden_folder layout.
//
// The {file_name} path segment is what the CLIENT wants the download called; the ids in the query are
// what selects content — same contract as upstream. One fallback is layered on top: with no file_ids,
// a name matching exactly one archive entry serves THAT entry, because clients exist that address by
// name and never send ids.
//
// Taking an archive entry binds nothing: every version stays available to every client, told apart by
// a paired token. From then on the rom advertises only that entry and only its saves.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Rom;
using LbApiHost.Host.Web;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

internal static class RommDownloadApi
{
    /// <summary>One concrete thing a download can contain, already resolved to disk.</summary>
    private sealed class Piece
    {
        public string Name = "";          // name inside the zip / of the download
        public string AbsPath = "";       // resolved file on disk
        public long Size;
    }

    public static HttpResponse Content(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.RomsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        // Le rom_id NOMME le fichier : la ligne porte le chemin et, pour une ROM extraite, son chemin
        // dans l'archive. Rien à décider ici, rien à figer — l'index a tranché avant que ce client ne
        // voie la ligne. Le nom demandé dans l'URL n'est plus qu'un indice de repli.
        int romId = ctx.GetRouteInt("id", -1);
        var named = RommIndexer.RowOf(romId);
        var game = named == null ? null : SafeGameOf(named.GuidLb);
        if (game == null) return RommApi.Error(404, "Rom not found");
        if (!named!.Emulated)
            return RommApi.Error(422, "This game is not distributed as a file");
        if (st != null && (st.IsHidden(RommLibrary.PlatformOf(game)) || !st.IsRatingAllowed(RommLibrary.EsrbOf(game))))
            return RommApi.Error(404, "Rom not found");

        var requestedName = ctx.GetRoute("file_name") ?? "";
        bool hiddenFolder = ctx.Request!.GetQueryBool("hidden_folder");
        var rawIds = ctx.Request.GetQuery("file_ids") ?? "";
        var fileIds = rawIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(s => int.TryParse(s, out var v) ? v : -1)
                            .Where(v => v > 0)
                            .ToList();

        try
        {
            if (fileIds.Count > 0)
                return ServeSelected(ctx, game, fileIds, requestedName, hiddenFolder, identity);

            // WHICH FILE this rom_id means. Either it names one — this client is locked, or it asked for
            // a rom that names a file outright — or it is the game's default slot, which names none and
            // resolves now to what the desktop picker would select.
            //
            // Only a game that HAS a choice is resolved and frozen. One with a single playable file has
            // nothing to pin: a lock row for it would be an entry in the store saying what the absence of
            // an entry already says.
            if (named.RomPath.Length > 0)
                return ServeArchiveEntry(ctx, game, named.AppId.Length == 0 ? null : named.AppId,
                                         named.RomPath,
                                         // Le nom vient de la LIGNE, jamais de l'URL : un client rejoue le nom qu'un
                                         // listing lui a donne, et le sien peut etre perime. Mesure : 2 Mo de .smc
                                         // enregistres sous "Yoshi's Island.7z" parce qu'on lui renvoyait son nom.
                                         Path.GetFileName(named.RomPath.Replace('/', '\\')),
                                         hiddenFolder, identity);

            if (named.FilePath.Length > 0)
            {
                var abs = RomPaths.ResolveAbsolute(named.FilePath);
                if (abs == null || !File.Exists(abs)) return RommApi.Error(404, "ROM file missing on disk");
                return FileDownload(ctx, abs, Path.GetFileName(abs));
            }

            return ServeDefault(ctx, game, requestedName, hiddenFolder, identity);
        }
        catch (Exception ex)
        {
            LbLog.Warn("romm", "download failed: " + ex.Message);
            return RommApi.Error(500, "Download failed: " + ex.Message);
        }
    }

    private static IGame? SafeGameOf(string guid)
    {
        try { return Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetGameById(guid); }
        catch { return null; }
    }

    // ── No file_ids: the game's default shape ────────────────────────────────

    private static HttpResponse ServeDefault(RouteContext ctx, IGame game, string requestedName,
                                             bool hiddenFolder, RommIdentity identity)
    {
        var mainAbs = RommLibrary.RomAbsPath(game);
        if (mainAbs == null || !File.Exists(mainAbs)) return RommApi.Error(404, "ROM file missing on disk");

        var discs = DiscApps(game);
        if (discs.Count > 0)
        {
            // Multi-disc: everything as one zip + a generated .m3u (skipped when the library already
            // ships one — then the m3u IS the main file and lists its own discs).
            var pieces = new List<Piece> { PieceOf(mainAbs) };
            pieces.AddRange(discs.Select(d => PieceOf(d.abs)));
            return ZipResponse(pieces, DownloadName(requestedName, game, ".zip"), hiddenFolder,
                               includeM3u: !mainAbs.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));
        }

        // Name-addressed entry: no file_ids, but the name the client asked for IS one of the archive's
        // entries. Upstream selects with ids only, and this stays faithful to that — it is a FALLBACK,
        // reached solely when the name matches exactly one entry, which the archive's own name never
        // does. Without it a client that picks a file by name (Freegosy does: it builds
        // /content/{file_name} and never sends file_ids) receives the whole archive instead, once per
        // file it selected — the same bytes, N times, and then it extracts all N entries locally.
        var named = MatchEntryByName(game, requestedName);
        if (named != null)
        {
            RommTrace.Note("entry by name: " + named);
            return ServeArchiveEntry(ctx, game, null, named, requestedName, hiddenFolder, identity);
        }

        // Single path — archive or not, it ships as-is.
        RommTrace.Note(NoMatchReason(game, requestedName));
        return FileDownload(ctx, mainAbs, Path.GetFileName(mainAbs));
    }

    /// <summary>Why the name did not select an entry — the one line that answers "why did I get the
    /// whole archive". Only built when tracing is on.</summary>
    private static string NoMatchReason(IGame game, string requested)
    {
        if (!RommTrace.Enabled) return "";
        try
        {
            if (!RomExtractor.Available) return "archive as-is: the ROM extractor module is off";
            var abs = RommLibrary.RomAbsPath(game);
            if (abs == null) return "as-is: no ROM path";
            if (!RomExtractor.IsArchive(abs)) return "as-is: not a recognised archive";
            var entries = RomExtractor.ListEntriesDetailed(game, null, probeCache: false).Entries;
            if (entries.Count == 0) return "archive as-is: it lists no playable entry";
            int hits = entries.Count(e => string.Equals(e.FileName, requested, StringComparison.OrdinalIgnoreCase));
            if (hits > 1) return $"archive as-is: \"{requested}\" matches {hits} entries, refusing to guess";
            return $"archive as-is: \"{requested}\" is not one of the {entries.Count} entries "
                 + $"(first: \"{entries[0].FileName}\")";
        }
        catch (Exception ex) { return "as-is: " + ex.Message; }
    }

    /// <summary>Records which entry a client took, when the caller is a paired client.
    ///
    /// Only "Bearer rmm_…" carries a token id — the pairing path. A caller authenticating with the
    /// account password has no durable identity to bind to, so it silently stays on the unbound default
    /// (every entry advertised, no save served). That limit is stated in the options panel rather than
    /// left to be discovered.</summary>
    /// <summary>The in-archive path of the single entry whose file name is <paramref name="requested"/>,
    /// or null when the name matches nothing — or, deliberately, when it matches more than one: two
    /// entries can share a file name in different folders, and a download that quietly picks one of them
    /// is worse than one that ships the archive.</summary>
    private static string? MatchEntryByName(IGame game, string requested)
    {
        if (string.IsNullOrEmpty(requested) || !RomExtractor.Available) return null;
        try
        {
            var mainAbs = RommLibrary.RomAbsPath(game);
            if (mainAbs == null || !RomExtractor.IsArchive(mainAbs)) return null;

            var hits = RomExtractor.ListEntriesDetailed(game, null, probeCache: false).Entries
                .Where(e => string.Equals(e.FileName, requested, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToList();
            return hits.Count == 1 ? hits[0].PathInArchive : null;
        }
        catch { return null; }
    }

    /// <summary>Extracts one archive entry through the regular LRU cache and ships it — the file alone,
    /// or the whole extracted directory as a zip when the profile pulled companions alongside it (a .cue
    /// without its .bins is a broken download).</summary>
    private static HttpResponse ServeArchiveEntry(RouteContext ctx, IGame game, string? appId,
                                                  string pathInArchive, string requestedName,
                                                  bool hiddenFolder, RommIdentity identity)
    {
        var (file, dir) = RomExtractor.ExtractEntryForDownload(game, appId, pathInArchive);
        if (file == null) return RommApi.Error(503, "Extraction failed (is the ROM extractor module on?)");

        // Rien à figer ici : l'index a décidé quel fichier ce client reçoit avant même qu'il voie la
        // ligne, et le rom_id demandé le nomme. Un téléchargement ne change plus rien à l'attribution.

        if (dir != null)
        {
            var all = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(p => new Piece { Name = Path.GetRelativePath(dir, p).Replace('\\', '/'), AbsPath = p, Size = SafeSize(p) })
                .ToList();
            return ZipResponse(all, DownloadName(requestedName, game, ".zip"), hiddenFolder, includeM3u: false);
        }
        return FileDownload(ctx, file, Path.GetFileName(file));
    }

    // ── file_ids: specific pieces ────────────────────────────────────────────

    private static HttpResponse ServeSelected(RouteContext ctx, IGame game, List<int> fileIds,
                                              string requestedName, bool hiddenFolder, RommIdentity identity)
    {
        var gameId = RommLibrary.IdOf(game);
        var pieces = new List<Piece>();

        foreach (var fid in fileIds)
        {
            var key = RommIdMap.FileKeyOf(fid);
            if (key == null || !key.StartsWith(gameId + "|", StringComparison.OrdinalIgnoreCase))
                return RommApi.Error(404, $"File {fid} does not belong to this rom");
            var entryKey = key.Substring(gameId.Length + 1);

            if (entryKey == "main")
            {
                var abs = RommLibrary.RomAbsPath(game);
                if (abs == null || !File.Exists(abs)) return RommApi.Error(404, "ROM file missing on disk");
                pieces.Add(PieceOf(abs));
            }
            else if (entryKey.StartsWith("app:", StringComparison.Ordinal))
            {
                var appId = entryKey.Substring(4);
                var disc = DiscApps(game).FirstOrDefault(d => string.Equals(d.id, appId, StringComparison.OrdinalIgnoreCase));
                if (disc.abs == null || !File.Exists(disc.abs)) return RommApi.Error(404, "Disc file missing on disk");
                pieces.Add(PieceOf(disc.abs));
            }
            else if (entryKey.StartsWith("entry:", StringComparison.Ordinal))
            {
                // An in-archive version: extract through the LRU cache. The entry key may carry the app
                // scope ("entry:{appId}:{path}") for a disc that is itself an archive; the plain form is
                // the base archive.
                var rest = entryKey.Substring(6);
                string? appId = null;
                int sep = rest.IndexOf("::", StringComparison.Ordinal);
                if (sep > 0) { appId = rest.Substring(0, sep); rest = rest.Substring(sep + 2); }

                // One entry on its own goes through the shared path, which also ships its companions.
                if (fileIds.Count == 1)
                    return ServeArchiveEntry(ctx, game, appId, rest, requestedName, hiddenFolder, identity);

                var (file, _) = RomExtractor.ExtractEntryForDownload(game, appId, rest);
                if (file == null) return RommApi.Error(503, "Extraction failed (is the ROM extractor module on?)");
                pieces.Add(PieceOf(file));
            }
            else return RommApi.Error(404, $"Unknown file kind for id {fid}");
        }

        if (pieces.Count == 0) return RommApi.Error(404, "No files selected");
        if (pieces.Count == 1) return FileDownload(ctx, pieces[0].AbsPath, pieces[0].Name);
        return ZipResponse(pieces, DownloadName(requestedName, game, ".zip"), hiddenFolder, includeM3u: true);
    }

    // ── Response builders ─────────────────────────────────────────────────────

    private static HttpResponse FileDownload(RouteContext ctx, string absPath, string name)
    {
        var resp = HttpResponse.FromFile(absPath, "application/octet-stream", ctx.Request, name);
        resp.Headers["Cache-Control"] = "no-cache";
        return resp;
    }

    /// <summary>A STORE zip streamed straight onto the chunked body — nothing is buffered, so a 10 GB
    /// multi-disc set costs no RAM and starts instantly.</summary>
    private static HttpResponse ZipResponse(List<Piece> pieces, string zipName, bool hiddenFolder, bool includeM3u)
    {
        var resp = HttpResponse.FromChunked(stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            string prefix = hiddenFolder ? ".hidden/" : "";

            foreach (var p in pieces)
            {
                var entry = zip.CreateEntry(prefix + p.Name, CompressionLevel.NoCompression);
                using var es = entry.Open();
                using var fs = new FileStream(p.AbsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
                fs.CopyTo(es);
            }

            if (includeM3u)
            {
                // .cue files only when present (a raw .bin line would be an invalid m3u entry), upstream's
                // own rule. The m3u itself sits OUTSIDE the hidden folder — it is what the frontend lists.
                var cues = pieces.Where(p => p.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToList();
                var listed = cues.Count > 0 ? cues : pieces;
                var m3u = string.Join("\n", listed.Select(p => prefix + p.Name)) + "\n";
                var m3uEntry = zip.CreateEntry(Path.GetFileNameWithoutExtension(zipName) + ".m3u", CompressionLevel.NoCompression);
                using var ms = m3uEntry.Open();
                var bytes = Encoding.UTF8.GetBytes(m3u);
                ms.Write(bytes, 0, bytes.Length);
            }
        }, "application/zip");
        resp.Headers["Content-Disposition"] = HttpResponse.BuildDisposition(zipName);
        resp.Headers["Cache-Control"] = "no-cache";
        return resp;
    }

    // ── Pieces ────────────────────────────────────────────────────────────────

    private static Piece PieceOf(string absPath) => new()
    {
        Name = Path.GetFileName(absPath),
        AbsPath = absPath,
        Size = SafeSize(absPath),
    };

    private static long SafeSize(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }

    private static string DownloadName(string requested, IGame game, string ext)
    {
        var name = string.IsNullOrWhiteSpace(requested) ? RommLibrary.TitleOf(game) : requested;
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
        return name;
    }

    /// <summary>The game's launchable additional-app "discs" that point at their own file — the versions
    /// a multi-disc export ships. Documents and same-path alternates are not discs.</summary>
    internal static List<(string id, string abs)> DiscApps(IGame game)
    {
        var result = new List<(string, string)>();
        var mainAbs = RommLibrary.RomAbsPath(game);
        IAdditionalApplication[]? apps = null;
        try { apps = game.GetAllAdditionalApplications(); } catch { }
        if (apps == null) return result;

        foreach (var a in apps)
        {
            if (a == null) continue;
            if (a is Data.HostAdditionalApplication { IsNonLaunchable: true }) continue;
            string? id = null, path = null;
            try { id = a.Id; path = a.ApplicationPath; } catch { }
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(path)) continue;

            string abs;
            try
            {
                abs = Path.IsPathRooted(path)
                    ? path!
                    : Path.GetFullPath(Path.Combine(Media.MediaResolver.LbRoot ?? AppContext.BaseDirectory, path!));
            }
            catch { continue; }

            if (mainAbs != null && string.Equals(abs, mainAbs, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(abs)) continue;
            result.Add((id!, abs));
        }
        return result;
    }
}
