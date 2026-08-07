// MameHiscorePlugin — turn MAME's own hiscore plugin ON, so MAME actually writes <mame>\hi\<rom>.hi.
// Without it there is no score file, ever: nothing to read, nothing to submit, and the failure is silent.
//
// WHY THIS IS OURS TO DO. LaunchBox doesn't. Measured on the shipped core:
//   • LaunchBox.dll's "MAME full set" wizard region carries the literals `arcade`, `hiscore`, `hiscore.dat`
//     and the emulator command line it writes — `-artwork_crop -skip_gameinfo -waitvsync -nofilter
//     -keyboardprovider dinput -rompath %romlocation%`. No `-plugin hiscore` anywhere in it.
//   • No `plugin.ini` string exists in the core at all; its six `-plugin` literals are LaunchBox's OWN
//     plugin system (`single-plugin-list`, `Update plugins done waiting`, …), unrelated to MAME.
//   • Unbroken.LaunchBox.dll's only message on the subject checks the DATA file, never the plugin:
//     "there should be a hiscore.dat file in MAME's Plugins\hiscore folder that seems to be missing!"
// So the wizard drops hiscore.dat and stops there — enabling the plugin is left to the user, who has no
// reason to suspect it. This is the MAME counterpart of FbneoHiscore.EnsureDeployed, which does the
// equivalent favour for the FBNeo core.
//
// HOW MAME IS CONFIGURED. `plugin.ini`, beside mame.exe, one "<name> <0|1>" per line — the very file MAME
// writes when you tick the box in Configure Options ▸ Plugins. Writing it IS ticking the box. We rewrite
// exactly one line and leave every other byte alone, so a user's other plugins keep their settings.
//
// mame.ini's `plugins` is the master switch: with it at 0 no plugin runs, hiscore included. We only touch
// it when the file exists AND holds an explicit 0 — an absent mame.ini already means the default (on), and
// adding the line would be noise.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Mame;

internal static class MameHiscorePlugin
{
    private static readonly string[] PluginIniHeader = { "#", "# PLUGINS OPTIONS", "#" };

    private static void Log(string s) => MameHighScoreSubmit.Log(s, "mame-plugin");

    /// <summary>Enable the hiscore plugin on every MAME emulator in the library. Called when the MAME
    /// upload option is switched on — the moment the user asks for scores to leave the machine is the
    /// moment the machine had better be producing them.</summary>
    public static void EnsureEnabledForAllMame()
    {
        foreach (var e in Safe(() => PluginHelper.DataManager?.GetAllEmulators()) ?? Array.Empty<IEmulator>())
            if (MameLeaderboards.IsMameEmulator(e)) EnsureEnabled(e);
    }

    /// <summary>Make this MAME's plugin.ini enable the hiscore plugin. Returns true when it ends up enabled
    /// (already was, or we set it). Never throws.</summary>
    public static bool EnsureEnabled(IEmulator? mame)
    {
        try
        {
            string dir = EmuDir(mame);
            string who = Safe(() => mame?.Title) ?? "MAME";
            if (dir.Length == 0) { Log($"'{who}': emulator directory unknown → cannot enable the hiscore plugin."); return false; }

            // No plugins\hiscore folder = this MAME build doesn't carry the plugin. Writing plugin.ini would
            // change nothing and would hide the real cause behind an "all good" line in the log.
            string pluginDir = Path.Combine(dir, "plugins", "hiscore");
            if (!Directory.Exists(pluginDir))
            {
                Log($"'{who}': no plugins\\hiscore folder at {pluginDir} → this MAME has no hiscore plugin to enable.");
                return false;
            }
            // hiscore.dat is what tells the plugin WHERE each game keeps its score. Present but unenabled is
            // the normal LaunchBox state; enabled but absent scores nothing — worth saying either way.
            if (!File.Exists(Path.Combine(pluginDir, "hiscore.dat")))
                Log($"'{who}': plugins\\hiscore\\hiscore.dat is missing — the plugin will run but score no game.");

            string what = SetOption(Path.Combine(dir, "plugin.ini"), "hiscore", "1", PluginIniHeader, addIfMissing: true);
            if (what.Length == 0) { Log($"'{who}': could not write {Path.Combine(dir, "plugin.ini")}."); return false; }
            if (what != "already")
                Log($"'{who}': hiscore plugin enabled in plugin.ini ({what}) — effective from the next MAME start.");

            string mameIni = Path.Combine(dir, "mame.ini");
            if (File.Exists(mameIni) && SetOption(mameIni, "plugins", "1", null, addIfMissing: false) == "set")
                Log($"'{who}': mame.ini had 'plugins 0' → set to 1 (no plugin would have run at all).");

            return true;
        }
        catch (Exception ex) { Log("enable failed: " + ex.Message); return false; }
    }

    /// <summary>Where the hiscore plugin keeps the score files it writes. The formula is the plugin's own
    /// (plugins\hiscore\init.lua): <c>homepath:value():match('([^;]+)') .. '/hiscore'</c> — the FIRST path of
    /// the homepath core option, plus "hiscore". homepath defaults to "." = MAME's working directory, which
    /// LaunchBox sets to the emulator folder (its cfg\*.cfg land there, so this is measured, not assumed).
    ///
    /// Three same-named directories live near each other and only this one holds scores:
    ///   &lt;mame&gt;\hiscore          ← HERE, the .hi files
    ///   &lt;mame&gt;\plugins\hiscore  ← the plugin's code + hiscore.dat (which games are supported)
    ///   &lt;mame&gt;\hi               ← where older plugin builds wrote; kept as a read fallback only.</summary>
    public static string ScoreDir(string emuDir)
    {
        string home = emuDir;
        try
        {
            string ini = Path.Combine(emuDir, "mame.ini");
            if (File.Exists(ini))
                foreach (var raw in File.ReadAllLines(ini))
                {
                    string t = raw.TrimStart();
                    if (t.Length == 0 || t[0] == '#') continue;
                    int sp = t.IndexOfAny(new[] { ' ', '\t' });
                    if (sp < 0 || !t.Substring(0, sp).Equals("homepath", StringComparison.OrdinalIgnoreCase)) continue;
                    // homepath may list several paths; the plugin uses the first and so must we.
                    string v = t.Substring(sp).Trim().Trim('"').Split(';')[0].Trim();
                    if (v.Length == 0) break;
                    home = Path.IsPathRooted(v) ? v : Path.GetFullPath(Path.Combine(emuDir, v));
                    break;
                }
        }
        catch { }
        return Path.Combine(home, "hiscore");
    }

    /// <summary>Set "&lt;name&gt; &lt;value&gt;" in a MAME options file (one "name value" per line, '#' comments).
    /// Only that line is rewritten; every other line survives untouched. Returns what happened —
    /// "already" / "set" / "added" / "created", or "" when nothing could be done. Internal because it edits
    /// a file we don't own — --selftest-mame-plugin pins its behaviour (see Tools/MamePluginIniSelfTest).</summary>
    internal static string SetOption(string file, string name, string value, string[]? createHeader, bool addIfMissing)
    {
        // MAME pads the option name out to column 26 in the files it writes itself; match it so a file we
        // created and one MAME rewrote later look the same.
        string written = name.PadRight(26) + value;
        try
        {
            if (!File.Exists(file))
            {
                if (createHeader == null) return "";
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllLines(file, createHeader.Append(written));
                return "created";
            }

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimStart();
                if (t.Length == 0 || t[0] == '#') continue;
                int sp = t.IndexOfAny(new[] { ' ', '\t' });
                string key = sp < 0 ? t : t.Substring(0, sp);
                if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if ((sp < 0 ? "" : t.Substring(sp).Trim()) == value) return "already";
                lines[i] = written;
                File.WriteAllLines(file, lines);
                return "set";
            }

            if (!addIfMissing) return "already";   // absent = the default, which is what we wanted
            File.WriteAllLines(file, lines.Append(written));
            return "added";
        }
        catch (Exception ex) { Log($"write failed on {file}: {ex.Message}"); return ""; }
    }

    private static string EmuDir(IEmulator? e)
    {
        try
        {
            var ap = Safe(() => e?.ApplicationPath);
            if (string.IsNullOrWhiteSpace(ap)) return "";
            return Path.GetDirectoryName(Path.GetFullPath(ap!)) ?? "";
        }
        catch { return ""; }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
