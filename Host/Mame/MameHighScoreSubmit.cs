// MameHighScoreSubmit — the auto-submit path: at the end of a game, read the local high score and (if the
// user opted in) submit it to the LaunchBox Games Database community leaderboards, exactly as LaunchBox does.
//
// We can't drive the core's MameHighScores.Parse(Emulator, Game, App): it wants the core's CONCRETE
// Emulator/Game objects, and LiteBox runs on its own host objects. So we replicate the two moving parts:
//   1) Extract locally with the bundled hi2txt (ThirdParty\hi2txt) — LB's own tool, per-rom descriptors —
//      `hi2txt -r <hifile>` → the main table's rank-1 SCORE + NAME. hi2txt reads the REAL score file, so it
//      never fabricates a value: worst case it finds nothing and we submit nothing (no bad data to LB).
//   2) Submit via GamesDatabase.UploadMameHighScore (Mame.MameUpload) — the core's clean call.
//
// Gating (⚠️ uploading from a non-LB client is a gray zone — user opt-in only, own scores only):
//   • MAME games  → the LB-native "Upload…" toggle (MameOptions.UploadEnabled).
//   • FBNeo/RetroArch → the LiteBox-own toggle AND RetroAchievements HARDCORE mode on the emulator (a
//     legitimacy gate: no unfair advantages, mirroring how RA restricts hardcore unlocks).
//   • Always requires a signed-in LaunchBox account token (LbKeys.HasGamesDbToken).
//
// Runs on a background thread off the game-exit path; every step logs so a real run can be validated.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Mame;

internal static class MameHighScoreSubmit
{
    // Always-on trace to <LB>\Core\litebox\mame-submit.log (LiteBox's host console is invisible in normal GUI
    // runs, and litebox-debug.log only exists in debug mode — so this feature logs to its own file regardless,
    // like SaveManager/StoreTrace, to make "why wasn't my score posted?" answerable).
    private static void Log(string s)
    {
        Console.WriteLine("[mame-submit] " + s);
        try { File.AppendAllText(LiteBoxPaths.File("mame-submit.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {s}{Environment.NewLine}"); }
        catch { }
    }

    /// <summary>Raised (with the rom) after a score is submitted, so the HIGH SCORES tab can drop its stale
    /// cached board and re-fetch to show the new standing.</summary>
    public static event Action<string>? Submitted;

    // Pre-game snapshot: the rom's #1 score BEFORE this session. The .hi keeps the all-time best across
    // sessions AND lower ranks change too, so we submit at exit ONLY when the #1 actually improved — that is
    // exactly what the leaderboard shows (your best), and it avoids re-uploading the same record.
    private static readonly object _gate = new();
    private static string _baselineRom = "";
    private static long _baselineScore = -1;   // -1 = no baseline captured for this rom

    /// <summary>The leaderboard kind for this launch, IGNORING the upload toggles: "mame"/"fbneo"/"" (not a
    /// leaderboard emulator). Also yields the rom short name.</summary>
    private static string RawKind(IGame? game, IEmulator? emulator, out string rom)
    {
        rom = game == null ? "" : (MameLeaderboards.RomName(game) ?? "");
        if (rom.Length == 0) return "";
        if (MameLeaderboards.IsMameEmulator(emulator)) return "mame";
        if (MameLeaderboards.IsFbneoRetroArch(emulator)) return "fbneo";
        return "";
    }

    private static bool UploadEnabledFor(string kind)
        => kind == "mame" ? MameOptions.UploadEnabled : (kind == "fbneo" && MameOptions.UploadFbneoEnabled);

    /// <summary>At launch: record the rom's current #1 score as the baseline. Fire-and-forget; never throws.</summary>
    public static void OnGameLaunch(IGame? game, IEmulator? emulator, IAdditionalApplication? app)
        => Task.Run(() => { try { Snapshot(game, emulator); } catch (Exception ex) { Log("snapshot error: " + ex.Message); } });

    private static void Snapshot(IGame? game, IEmulator? emulator)
    {
        string kind = RawKind(game, emulator, out var rom);
        // Only snapshot when this kind's upload is enabled (else the baseline is irrelevant and we skip hi2txt).
        if (kind.Length == 0 || !UploadEnabledFor(kind)) { lock (_gate) { _baselineRom = ""; _baselineScore = -1; } return; }
        long pre = 0;
        var hi = LocateHiFile(emulator, rom, kind);
        if (hi != null) { var (s, _) = RunHi2txt(hi); if (s > 0) pre = s; }
        Log($"{kind} '{rom}': launch — pre-game best = {pre}");
        lock (_gate) { _baselineRom = rom; _baselineScore = pre; }
    }

    /// <summary>At exit: if the #1 score beat the launch baseline, submit it. Fire-and-forget; never throws.</summary>
    public static void OnGameExit(IGame? game, IEmulator? emulator, IAdditionalApplication? app)
        => Task.Run(() => { try { Run(game, emulator); } catch (Exception ex) { Log("error: " + ex.Message); } });

    private static void Run(IGame? game, IEmulator? emulator)
    {
        string kind = RawKind(game, emulator, out var rom);
        if (kind.Length == 0) return;   // not a MAME/FBNeo leaderboard game — stay silent

        if (!UploadEnabledFor(kind))
        {
            Log($"{kind} '{rom}': exit — upload option is OFF (enable it on the MAME Integrations tab) → skip.");
            return;
        }

        // FBNeo legitimacy gate: only in RetroAchievements hardcore mode (the emulator's EnableHardcoreAchievements).
        if (kind == "fbneo" && !Safe(() => emulator!.EnableHardcoreAchievements))
        {
            Log($"fbneo '{rom}': exit — RetroAchievements hardcore mode is OFF on the emulator → skip (legitimacy gate).");
            return;
        }

        if (!Data.LbKeys.HasGamesDbToken)
        {
            Log($"{kind} '{rom}': not signed in to LaunchBox (no token) → skip. Connect on the LaunchBox Integrations tab.");
            return;
        }

        string? hi = LocateHiFile(emulator, rom, kind);
        if (hi == null) { Log($"{kind} '{rom}': no high-score file found → nothing to submit."); return; }

        var (score, initials) = RunHi2txt(hi);
        if (score <= 0) { Log($"{kind} '{rom}': hi2txt returned no usable score (file '{hi}') → skip."); return; }

        // Submit only when the #1 beat the pre-game best. Unknown baseline (launch snapshot missed for this
        // rom) → treat as 0 so a genuine score isn't lost; the launch hook normally makes this exact.
        long baseline;
        lock (_gate) baseline = string.Equals(_baselineRom, rom, StringComparison.OrdinalIgnoreCase) ? _baselineScore : -1;
        long pre = baseline < 0 ? 0 : baseline;
        if (baseline < 0) Log($"{kind} '{rom}': no launch baseline — treating pre-game best as 0.");
        if (score <= pre)
        {
            Log($"{kind} '{rom}': score {score} did not beat the pre-game best {pre} → nothing to submit.");
            return;
        }

        Log($"{kind} '{rom}': new high score {score} (was {pre}) name=\"{initials}\" → submitting (from {hi})");
        var res = MameUpload.SendAsync(Data.LbKeys.GamesDbToken, rom, score, initials).GetAwaiter().GetResult();
        Log($"{kind} '{rom}': upload result = {res}");

        if (res != MameUploadResult.Failed)
        {
            // Update our new pre-game best so a second run in the same session doesn't re-submit the same score,
            // drop the read cache, and tell the UI to reload the board.
            lock (_gate) { _baselineRom = rom; _baselineScore = score; }
            MameLeaderboards.Invalidate(rom);
            try { Submitted?.Invoke(rom); } catch { }
        }
    }

    // ── locate the .hi score file the emulator just wrote ───────────────
    // The structure is known exactly, so we check precise paths (no recursion):
    //   • MAME     → <emuDir>\hi\<rom>.hi  (the hiscore plugin's fixed location).
    //   • FBNeo/RA → <emuDir>\saves\[<core folder>\]fbneo\<rom>.hi. The FBNeo libretro core always writes
    //     hiscores into a `fbneo` subfolder of RetroArch's save dir; RetroArch's "Sort saves into folders by
    //     core" option optionally inserts the core's display name ("FinalBurn Neo") above it. So there are two
    //     valid tails — sort-on and sort-off — and nothing else to guess.
    private static string? LocateHiFile(IEmulator? e, string rom, string kind)
    {
        string emuDir = "";
        try { var ap = Safe(() => e?.ApplicationPath); if (!string.IsNullOrWhiteSpace(ap)) emuDir = Path.GetDirectoryName(Path.GetFullPath(ap!)) ?? ""; }
        catch { }
        if (emuDir.Length == 0) { Log($"{kind} '{rom}': emulator directory unknown → cannot locate hi file."); return null; }

        string target = rom + ".hi";
        var paths = kind == "mame"
            ? new[] { Path.Combine(emuDir, "hi", target) }
            : new[] { Path.Combine(emuDir, "saves", "FinalBurn Neo", "fbneo", target),   // Sort saves by core: ON
                      Path.Combine(emuDir, "saves", "fbneo", target) };                  // Sort saves by core: OFF
        foreach (var c in paths)
            if (File.Exists(c)) { Log($"{kind} '{rom}': using hi file {c}"); return c; }

        Log($"{kind} '{rom}': no hi file at {string.Join(" ; ", paths)}");
        return null;
    }

    // ── run hi2txt and read the main table's rank-1 (SCORE, NAME) ────────
    private static (long score, string initials) RunHi2txt(string hiFile)
    {
        string root = MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string dir = Path.Combine(root, "ThirdParty", "hi2txt");
        string exe = Path.Combine(dir, "hi2txt.exe");
        if (!File.Exists(exe)) { Log("hi2txt.exe not found at " + exe); return (0, ""); }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = dir,                 // so hi2txt finds its bundled descriptor db
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-r");                 // main high-score entries (first table)
            psi.ArgumentList.Add(hiFile);

            using var p = Process.Start(psi);
            if (p == null) return (0, "");
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return (0, ""); }
            var parsed = ParseHi2txt(outp);
            // Surface hi2txt's own reason when nothing parsed (e.g. "No XML description found for ROM 'sf2ce'"
            // = the game isn't a hiscore-supported rom — the same reason LaunchBox couldn't score it either).
            if (parsed.score <= 0 && !string.IsNullOrWhiteSpace(err))
                Log("hi2txt: " + err.Replace("\r", "").Replace("\n", " ").Trim());
            return parsed;
        }
        catch (Exception ex) { Log("hi2txt run failed: " + ex.Message); return (0, ""); }
    }

    /// <summary>Parse hi2txt TXT output: find the first "RANK|SCORE|NAME|…" header, then its first data row;
    /// return that row's SCORE (numeric) and NAME. Column order can vary, so map by header name.</summary>
    private static (long score, string initials) ParseHi2txt(string txt)
    {
        if (string.IsNullOrWhiteSpace(txt)) return (0, "");
        var lines = txt.Replace("\r", "").Split('\n');
        int scoreCol = -1, nameCol = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.IndexOf('|') < 0) continue;
            var cells = line.Split('|');
            // header row?
            if (scoreCol < 0 && cells.Any(c => c.Trim().Equals("SCORE", StringComparison.OrdinalIgnoreCase)))
            {
                for (int c = 0; c < cells.Length; c++)
                {
                    var h = cells[c].Trim();
                    if (h.Equals("SCORE", StringComparison.OrdinalIgnoreCase) && scoreCol < 0) scoreCol = c;
                    if (h.Equals("NAME", StringComparison.OrdinalIgnoreCase) && nameCol < 0) nameCol = c;
                }
                continue;
            }
            // first data row after the header
            if (scoreCol >= 0 && cells.Length > scoreCol)
            {
                string sRaw = cells[scoreCol].Trim().Replace(",", "").Replace(".", "").Replace(" ", "");
                if (long.TryParse(sRaw, out var score) && score > 0)
                {
                    string name = nameCol >= 0 && cells.Length > nameCol ? cells[nameCol].Trim() : "";
                    return (score, name);
                }
                // a non-numeric first row → keep scanning within this table for the first numeric score
            }
        }
        return (0, "");
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
