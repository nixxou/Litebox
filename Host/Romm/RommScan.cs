// Le tableau des plateformes de la page module : ce que chacune exige avant d'etre servable.
//
// Deux traitements existent. "Direct" : le fichier de la ROM est servi tel quel, rien a preparer.
// "Archives" : l'extraction est active pour (emulateur, plateforme), donc un jeu n'est annoncable
// qu'une fois le CONTENU de son archive connu — la passe d'index ne lit jamais un fichier, elle ne
// fait que consulter le cache de listing que le picker du bureau remplit. Tant que le cache ignore
// une archive, le jeu reste muet ("archive contents unknown"), et c'est ce que la colonne compte.
//
// Le scan comble exactement ce manque : il appelle le MEME chemin que le picker
// (RomExtractor.ListEntriesDetailed), qui analyse l'archive, memoise le listing et reveille
// l'indexeur (Touch). Il n'ecrit que du cache — noms, chemins, tailles, signature courte — sous une
// cle chemin+taille re-inscriptible : une passe ulterieure (hachage RetroAchievements par exemple)
// rouvrira les archives pour son propre besoin et pourra reecrire ou enrichir sans conflit.
//
// Le sondage n'utilise PAS RommLibrary.GamesOf : celui-ci est barre par IncludedPlatforms, et le
// panneau sonde justement des plateformes pas encore incluses.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Rom;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

internal sealed class RommPlatformSurvey
{
    public string Platform = "";
    public bool Emulated;               // at least one emulated game
    public bool Archives;               // at least one candidate goes through extraction
    public int Unknown;                 // games silent because their archive was never listed
    public int Games;

    public string ModeWord => !Emulated ? "-" : Archives ? "Archives" : "Direct";
}

internal static class RommScan
{
    /// <summary>One platform, surveyed with the index pass's own eyes: same candidates, same
    /// "known?" test — so the Unknown column predicts exactly what the pass will refuse to
    /// advertise. Cache lookups only, no archive is opened here.</summary>
    public static RommPlatformSurvey Survey(string platformName)
    {
        var s = new RommPlatformSurvey { Platform = platformName };
        foreach (var game in GamesOf(platformName))
        {
            s.Games++;
            if (!RommFiles.IsEmulated(game)) continue;
            s.Emulated = true;
            List<RommCandidate> candidates;
            try { candidates = RommFiles.CandidatesOf(game); } catch { continue; }
            if (candidates.Any(c => c.IsExtract)) s.Archives = true;
            if (candidates.Count > 0 && !candidates.Any(c => !c.IsExtract || c.Known)) s.Unknown++;
        }
        return s;
    }

    // ── The scan ──────────────────────────────────────────────────────────────

    private static readonly object _gate = new();
    private static volatile bool _running, _stop;

    public static bool Running => _running;
    public static void Stop() => _stop = true;

    /// <summary>Analyses every unlisted archive of one platform. Long — a solid 7z pays a real
    /// decompression — so callers run it on a background thread; <paramref name="progress"/> gets
    /// (done, total) after each game. One scan at a time; returns the number of games whose
    /// archives were listed, or -1 when a scan was already running.</summary>
    public static int Scan(string platformName, Action<int, int>? progress = null)
    {
        lock (_gate)
        {
            if (_running) return -1;
            _running = true; _stop = false;
        }
        int done = 0;
        try
        {
            var pending = GamesOf(platformName).Where(g =>
            {
                if (!RommFiles.IsEmulated(g)) return false;
                List<RommCandidate> c;
                try { c = RommFiles.CandidatesOf(g); } catch { return false; }
                return c.Count > 0 && !c.Any(x => !x.IsExtract || x.Known);
            }).ToList();

            foreach (var game in pending)
            {
                if (_stop) break;
                try
                {
                    foreach (var c in RommFiles.CandidatesOf(game).Where(x => x.IsExtract && !x.Known))
                        // Analyse + memoise + RommIndexer.Touch — the picker's own path.
                        RomExtractor.ListEntriesDetailed(game, c.AppId.Length == 0 ? null : c.AppId);
                    done++;
                }
                catch (Exception ex) { LbLog.Warn("romm", "scan: " + ex.Message); }
                progress?.Invoke(done, pending.Count);
            }
            LbLog.Info("romm", $"scan \"{platformName}\": {done}/{pending.Count} archive(s) listed" + (_stop ? " (stopped)" : ""));
            return done;
        }
        finally { _running = false; }
    }

    private static List<IGame> GamesOf(string platformName)
    {
        try
        {
            foreach (var p in PluginHelper.DataManager.GetAllPlatforms() ?? Array.Empty<IPlatform>())
                if (string.Equals(p?.Name, platformName, StringComparison.OrdinalIgnoreCase))
                    return (p!.GetAllGames(true, true) ?? Enumerable.Empty<IGame>()).Where(g => g != null).ToList();
        }
        catch (Exception ex) { LbLog.Warn("romm", "scan survey: " + ex.Message); }
        return new List<IGame>();
    }
}
