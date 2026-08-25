// --selftest-rules: the Prefix action's behaviour pinned case by case against BigBoxProfile's
// semantics (Prefix.Modify + EmulatorLauncher's final marker pass), so the next ported action starts
// from a harness instead of a hope. Pure pipeline tests — no DB, no UI: rules are built in memory and
// pushed through the same code the launch hook runs.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace LbApiHost.Host.Rules;

internal static class RuleSelfTest
{
    private static int _fail;

    public static int Run()
    {
        // ── the action itself ──
        // NOTE the round trips: parse + re-join re-quotes MINIMALLY (quotes only where whitespace or
        // quotes demand them) — BigBoxUtils.ArgsToCommandLine's exact behaviour, invisible to the
        // spawned process since Windows parses both spellings identically.
        Check("as ARGUMENT inserts one token first",
            Prefix("-fullscreen"), @"""C:\roms\game.zip"" -x", @"-fullscreen C:\roms\game.zip -x");

        Check("as ARGUMENT keeps quotes where they matter (spaced path)",
            Prefix("-fullscreen"), @"""C:\my roms\game.zip"" -x", @"-fullscreen ""C:\my roms\game.zip"" -x");

        Check("as ARGUMENT trims and quotes a spaced prefix",
            Prefix("  hello world  "), "-x", @"""hello world"" -x");

        Check("as CMDLINE prepends verbatim (several args in one prefix)",
            Prefix(@"-L ""cores\snes.dll"" ", asArg: false), @"""C:\roms\game.zip""", @"-L ""cores\snes.dll"" ""C:\roms\game.zip""");

        Check("empty prefix = not configured, untouched",
            Prefix(""), "-x", "-x");

        // ── the filter probe (haystack includes the exe) ──
        Check("filter matches in the ARGS",
            Prefix("-a", filter: "game.zip"), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("filter matches in the EXE path (BigBoxProfile matched on args[0] too)",
            Prefix("-a", filter: "retroarch"), "-x", "-a -x");

        Check("filter miss leaves args untouched",
            Prefix("-a", filter: "nothere"), "-x", "-x");

        Check("filter is case-insensitive",
            Prefix("-a", filter: "GAME.ZIP"), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("comma filter = ANY entry",
            Prefix("-a", filter: "nothere, game.zip", comma: true), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("comma filter + matchall requires EVERY entry",
            Prefix("-a", filter: "roms, nothere", comma: true, matchAll: true), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        Check("comma filter + matchall passes when all present",
            Prefix("-a", filter: "roms, game.zip", comma: true, matchAll: true), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        // ── the exclude probe ──
        Check("exclude blocks on match",
            Prefix("-a", exclude: "game.zip"), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        Check("comma exclude blocks on ANY entry",
            Prefix("-a", exclude: "nothere, game.zip", commaEx: true), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        Check("comma exclude + matchall blocks only when ALL present (the fixed intent)",
            Prefix("-a", exclude: "nothere, game.zip", commaEx: true, matchAllEx: true), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("comma exclude + matchall with all present blocks",
            Prefix("-a", exclude: "roms, game.zip", commaEx: true, matchAllEx: true), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        // ── the marker system (RemoveFilter final pass) ──
        Check("marker routes the rule, then is stripped",
            Prefix("-a", filter: "-marker", remove: true), @"""C:\roms\game.zip"" -marker", @"-a C:\roms\game.zip");

        Check("marker strip is whole-argument, not substring",
            Prefix("-a", filter: "-marker", remove: true), @"-markers ""C:\roms\game.zip"" -marker", @"-a -markers C:\roms\game.zip");

        // A marker that only SUPPRESSED a rule (probe did not fire) is still stripped — BigBoxProfile
        // collects the removal list over every CONFIGURED module, fired or not.
        Check("marker stripped even when its rule did not fire",
            new[] { P("-a", filter: "nothere, -marker", comma: true, matchAll: true, remove: true) },
            "-marker -x", "-x");

        // ── the pipeline ──
        Check("rules run in order, each seeing the previous result",
            new[] { P("-first"), P("-second") }, "-x", "-second -first -x");

        Check("disabled rule is skipped",
            new[] { Disabled(P("-a")), P("-b") }, "-x", "-b -x");

        // ── the preview channel (EmulatorConfig.CalculateExemple) ──
        CheckPreview("preview transforms the full line, exe kept in front",
            Prefix("-a"), @"sd.exe ""C:\MyRomDir\MyRom.bin""", @"sd.exe -a C:\MyRomDir\MyRom.bin");

        CheckPreview("preview runs the marker pass too",
            Prefix("-a", filter: "-marker", remove: true), "sd.exe rom.bin -marker", "sd.exe -a rom.bin");

        // The parse + re-join round trip dequotes even when no rule acts — visible in BigBoxProfile's
        // own screenshot (IN quoted, OUT bare, one NON-CONFIGURED rule in the list).
        CheckPreview("preview skips a non-configured rule (round-trip requote only)",
            Prefix(""), @"sd.exe ""C:\MyRomDir\MyRom.bin""", @"sd.exe C:\MyRomDir\MyRom.bin");

        // ── Suffix (same probes, opposite end; BigBoxProfile parity) ──
        Check("suffix as ARGUMENT appends one token last",
            Suffix("-fullscreen"), @"""C:\roms\game.zip"" -x", @"C:\roms\game.zip -x -fullscreen");

        Check("suffix as ARGUMENT trims and quotes a spaced payload",
            Suffix("  hello world  "), "-x", @"-x ""hello world""");

        Check("suffix as CMDLINE appends verbatim (leading space is the author's)",
            Suffix(" -L \"cores\\snes.dll\"", asArg: false), @"""C:\roms\game.zip""", @"""C:\roms\game.zip"" -L ""cores\snes.dll""");

        Check("empty suffix = not configured, untouched",
            Suffix(""), "-x", "-x");

        Check("suffix honours the shared probes",
            Suffix("-a", filter: "nothere"), "-x", "-x");

        Check("prefix and suffix compose in pipeline order",
            new[] { P("-first"), Sfx("-last") }, "-x", "-first -x -last");

        // ── ChangeExe (the reason RuleCmd carries the exe) ──
        CheckCmd("changeexe: a rooted path replaces the exe outright",
            new[] { Cx(@"D:\other\emu.exe") }, @"C:\emu\retroarch.exe", "-x",
            @"D:\other\emu.exe", "-x");

        CheckCmd("changeexe: a relative path resolves against the ORIGINAL exe's folder",
            new[] { Cx("retroarch_debug.exe") }, @"C:\emu\retroarch.exe", "-x",
            @"C:\emu\retroarch_debug.exe", "-x");

        CheckCmd("changeexe: the probe gates it like any rule",
            new[] { Cx(@"D:\other\emu.exe", filter: "nothere") }, @"C:\emu\retroarch.exe", "-x",
            @"C:\emu\retroarch.exe", "-x");

        // ── ChangeRomPath (real files: priorities and the bare-filename fallback) ──
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lbx-rules-selftest");
            string orig = System.IO.Path.Combine(root, "orig");
            string high = System.IO.Path.Combine(root, "high");
            string low = System.IO.Path.Combine(root, "low");
            try
            {
                System.IO.Directory.CreateDirectory(orig);
                System.IO.Directory.CreateDirectory(high);
                System.IO.Directory.CreateDirectory(low);
                System.IO.File.WriteAllText(System.IO.Path.Combine(orig, "both.zip"), "");
                System.IO.File.WriteAllText(System.IO.Path.Combine(high, "both.zip"), "");
                System.IO.File.WriteAllText(System.IO.Path.Combine(high, "flat.zip"), "");
                System.IO.File.WriteAllText(System.IO.Path.Combine(low, "lost.zip"), "");

                CheckCmd("changerompath: HIGH priority wins even when the original exists",
                    new[] { Crp(orig, high: high) }, "emu.exe", Q(orig, "both.zip"),
                    "emu.exe", Q(high, "both.zip"));

                CheckCmd("changerompath: bare-filename fallback when the remainder subdir is gone",
                    new[] { Crp(orig, high: high) }, "emu.exe", Q(orig, @"deep\flat.zip"),
                    "emu.exe", Q(high, "flat.zip"));

                CheckCmd("changerompath: LOW priority is skipped while the original exists",
                    new[] { Crp(orig, low: low) }, "emu.exe", Q(orig, "both.zip"),
                    "emu.exe", Q(orig, "both.zip"));

                CheckCmd("changerompath: LOW priority rescues a missing original",
                    new[] { Crp(orig, low: low) }, "emu.exe", Q(orig, "lost.zip"),
                    "emu.exe", Q(low, "lost.zip"));

                CheckCmd("changerompath: an argument without the sought path is untouched",
                    new[] { Crp(orig, high: high) }, "emu.exe", "-x",
                    "emu.exe", "-x");

                // ── the m3u content (the original's UseM3UContent contract) ──
                string m3u = System.IO.Path.Combine(orig, "multi.m3u");
                System.IO.File.WriteAllLines(m3u, new[]
                {
                    "both.zip",                                    // relative; relocates HIGH
                    System.IO.Path.Combine(orig, "lost.zip"),      // absolute; missing → LOW
                });

                var m3uRules = new[] { Crp(orig, high: high, low: low) };
                var got = RulePipeline.ApplyRules(m3uRules.ToList(), "emu.exe", Q(orig, "multi.m3u"));
                var outArg = RuleArgs.Split(got.Args).FirstOrDefault() ?? "";
                bool swapped = !string.Equals(outArg, System.IO.Path.Combine(orig, "multi.m3u"), StringComparison.OrdinalIgnoreCase)
                               && outArg.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                               && System.IO.File.Exists(outArg);
                var content = swapped ? System.IO.File.ReadAllLines(outArg) : Array.Empty<string>();
                Expect("changerompath: an m3u argument is swapped for a relocated TEMP copy",
                    swapped
                    && content.Length == 2
                    && string.Equals(content[0], System.IO.Path.Combine(high, "both.zip"), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(content[1], System.IO.Path.Combine(low, "lost.zip"), StringComparison.OrdinalIgnoreCase));
                Expect("changerompath: the original m3u file is never modified",
                    System.IO.File.ReadAllLines(m3u)[0] == "both.zip");

                string preview = RulePipeline.PreviewExample(m3uRules.ToList(), "emu.exe " + Q(orig, "multi.m3u"));
                Expect("changerompath: the EXAMPLE channel leaves the m3u alone (it writes nothing)",
                    preview.Contains("multi.m3u", StringComparison.OrdinalIgnoreCase)
                    && !preview.Contains("litebox-rules-m3u", StringComparison.OrdinalIgnoreCase));
                if (swapped) { try { System.IO.File.Delete(outArg); } catch { } }
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { }
            }
        }

        // ── Replace (line): literal / regex / house "\1" syntax ──
        Check("replace literal in the command line",
            Rp("-old", "-new", asArg: false), "-old -x", "-new -x");

        Check("replace literal per argument, case-insensitive by default",
            Rp("GAME", "demo"), @"""C:\roms\game.zip""", @"C:\roms\demo.zip");

        Check("replace case-sensitive misses the wrong case",
            Rp("GAME", "demo", caseSensitive: true), @"""C:\roms\game.zip""", @"C:\roms\game.zip");

        Check("replace regex with \\1 group splice",
            Rp(@"disc(\d)", @"d\1", regex: true), "-load disc2", "-load d2");

        Check("replace literal keeps $ in the replacement literal",
            Rp("x", "$1"), "-x", "-$1");

        Check("replace honours the shared probes",
            Rp("-old", "-new", filter: "nothere"), "-old", "-old");

        // ── the variables system (cmd / arg / file sources, iterative, fallback) ──
        {
            string V(params RuleVariable[] vs) => RuleVariables.Serialize(vs.ToList());

            Check("variable from the CMD source, group spliced, used in the replacement",
                Rp("-t", "{ROM}", vars: V(new RuleVariable { Name = "{ROM}", Source = "cmd", Pattern = @"(\w+)\.zip", Value = @"\1" })),
                @"""C:\roms\game.zip"" -t", @"C:\roms\game.zip game");

            Check("variable from the ARG source: the LAST matching argument wins",
                Rp("-t", "{N}", vars: V(new RuleVariable { Name = "{N}", Source = "arg", Pattern = @"^-n(\d)$", Value = @"\1" })),
                "-n1 -n2 -t", "-n1 -n2 2");

            Check("variable falls back when nothing matches",
                Rp("-t", "{X}", vars: V(new RuleVariable { Name = "{X}", Source = "cmd", Pattern = "nothere", Value = "hit", Fallback = "fb" })),
                "-t", "fb");

            Check("variables expand iteratively (a value may contain another token)",
                Rp("-t", "{A}", vars: V(
                    new RuleVariable { Name = "{A}", Source = "cmd", Pattern = ".", Value = "[{B}]" },
                    new RuleVariable { Name = "{B}", Source = "cmd", Pattern = ".", Value = "deep" })),
                "-t", "[deep]");

            string vroot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lbx-rules-selftest-vars");
            try
            {
                System.IO.Directory.CreateDirectory(vroot);
                string src = System.IO.Path.Combine(vroot, "settings.ini");
                System.IO.File.WriteAllText(src, "core=snes9x\nvideo=gl\n");
                Check("variable from a FILE source reads its content",
                    Rp("-t", "{CORE}", vars: V(new RuleVariable { Name = "{CORE}", Source = src, Pattern = @"core=(\w+)", Value = @"\1" })),
                    "-t", "snes9x");
            }
            finally { try { System.IO.Directory.Delete(vroot, recursive: true); } catch { } }
        }

        // ── Replace in file: ExecuteBefore on the REAL channel, never in the preview ──
        {
            string froot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lbx-rules-selftest-file");
            try
            {
                System.IO.Directory.CreateDirectory(froot);
                string cfg = System.IO.Path.Combine(froot, "emu.cfg");

                System.IO.File.WriteAllText(cfg, "fullscreen=0\n");
                RulePipeline.ApplyRules(new List<LaunchRule> { Rif(cfg, "fullscreen=0", "fullscreen=1") }, "emu.exe", "-x");
                Expect("replaceinfile: the real channel rewrites the file, the line is untouched",
                    System.IO.File.ReadAllText(cfg).Contains("fullscreen=1"));

                System.IO.File.WriteAllText(cfg, "fullscreen=0\n");
                RulePipeline.PreviewExample(new List<LaunchRule> { Rif(cfg, "fullscreen=0", "fullscreen=1") }, "emu.exe -x");
                Expect("replaceinfile: the preview NEVER touches the file",
                    System.IO.File.ReadAllText(cfg).Contains("fullscreen=0"));

                System.IO.File.WriteAllText(cfg, "fullscreen=0\n");
                RulePipeline.ApplyRules(new List<LaunchRule> { Rif(cfg, "fullscreen=0", "fullscreen=1", filter: "nothere") }, "emu.exe", "-x");
                Expect("replaceinfile: a refused probe leaves the file alone",
                    System.IO.File.ReadAllText(cfg).Contains("fullscreen=0"));
            }
            finally { try { System.IO.Directory.Delete(froot, recursive: true); } catch { } }
        }

        // ── Create file: variables in the path AND the content, directory created, preview inert ──
        {
            string croot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lbx-rules-selftest-create");
            try
            {
                string vars = RuleVariables.Serialize(new List<RuleVariable>
                {
                    new() { Name = "{ROM}", Source = "cmd", Pattern = @"(\w+)\.zip", Value = @"\1" },
                }.ToList());
                var rule = new LaunchRule
                {
                    Type = LaunchRule.TypeCreateFile,
                    TargetFile = System.IO.Path.Combine(croot, "sub", "{ROM}.cfg"),
                    FileContent = "name={ROM}\r\n",
                    VariablesData = vars,
                };
                RulePipeline.ApplyRules(new List<LaunchRule> { rule }, "emu.exe", @"""C:\roms\game.zip""");
                string expected = System.IO.Path.Combine(croot, "sub", "game.cfg");
                Expect("createfile: variables expand in path and content, missing directory created",
                    System.IO.File.Exists(expected)
                    && System.IO.File.ReadAllText(expected).Contains("name=game"));

                System.IO.Directory.Delete(croot, recursive: true);
                RulePipeline.PreviewExample(new List<LaunchRule> { rule }, @"emu.exe ""C:\roms\game.zip""");
                Expect("createfile: the preview writes nothing",
                    !System.IO.Directory.Exists(croot));

                rule.Filter = "nothere";
                RulePipeline.ApplyRules(new List<LaunchRule> { rule }, "emu.exe", @"""C:\roms\game.zip""");
                Expect("createfile: a refused probe writes nothing",
                    !System.IO.Directory.Exists(croot));
            }
            finally { try { System.IO.Directory.Delete(croot, recursive: true); } catch { } }
        }

        // ── HID device detector: matcher/quota/%NUM%/marker logic on INJECTED backend data ──
        {
            Hid.HidInfoCache.InjectForTest(
                hidSharp: "FakePad<>1118<>767<>\\\\?\\hid#fake1\r\nOtherThing<>1<>2<>\\\\?\\hid#fake2\r\n",
                sdl: "SDL0<>Fake Lightgun A<>ABC123<>SDL2.SDL+SDL_JoystickGUID<><>030000001234<>VendorID=0x1234<>ProductID=0x0001\r\n"
                   + "SDL1<>Fake Lightgun B<>DEF456<>SDL2.SDL+SDL_JoystickGUID<><>030000005678<>VendorID=0x5678<>ProductID=0x0002\r\n"
                   + "SDL2<>Fake Lightgun B<>DEF456<>SDL2.SDL+SDL_JoystickGUID<><>030000005678<>VendorID=0x5678<>ProductID=0x0002\r\n");

            var gunMatcher = new Hid.HidMatcher
            {
                RegexToMatch = @"SDL(\d+)<>Fake Lightgun", Suffix = @"\1",
                DeviceType = "lightgun", UseSdl = true, MaxMatch = 0,
            };
            Expect("hid matcher: \\1 splice over injected SDL lines, MaxMatch 0 = all",
                string.Join("|", gunMatcher.Match("") ?? Array.Empty<string>()) == "0|1|2");

            var uniqueMatcher = new Hid.HidMatcher
            {
                RegexToMatch = @"SDL\d+<>(Fake Lightgun \w)", Suffix = @"\1",
                DeviceType = "lightgun", UseSdl = true, MaxMatch = 0, UniqueMatch = true,
            };
            Expect("hid matcher: UniqueMatch collapses duplicate suffixes",
                string.Join("|", uniqueMatcher.Match("") ?? Array.Empty<string>()) == "Fake Lightgun A|Fake Lightgun B");

            Expect("hid matcher: no match returns null",
                new Hid.HidMatcher { RegexToMatch = "nothere", UseSdl = true }.Match("") == null);

            // End-to-end: two buckets, quotas, %NUM%, final order controller-first, marker strip.
            var settings = new Actions.HidDetectSettings
            {
                NumLightgun = 2, PrefixLightgun = "--lightgun%NUM%=", ForceRemoveLightgun = false,
                NumController = 4, PrefixController = "--pad%NUM%=", ForceRemoveController = true,
                Matchers = new List<Hid.HidMatcher>
                {
                    new() { RegexToMatch = @"SDL(\d+)<>Fake Lightgun", Suffix = @"\1", DeviceType = "lightgun", UseSdl = true, MaxMatch = 0 },
                    new() { RegexToMatch = @"(FakePad)<>1118", Suffix = @"\1", DeviceType = "controller", UseHidSharp = true },
                },
            };
            var hidRule = new LaunchRule { Type = LaunchRule.TypeHidDetect, HidData = settings.Serialize() };
            var seesMarker = Sfx("--saw-pad", asArg: true);
            seesMarker.Filter = "--pad1=fakepad";
            var (_, outArgs) = RulePipeline.ApplyRules(new List<LaunchRule> { hidRule, seesMarker }, "emu.exe", "\"C:\\roms\\game.zip\"");
            Expect("hid detect: lightgun quota 2 of 3 matches, %NUM% per bucket, marker arg visible downstream then stripped",
                outArgs == "C:\\roms\\game.zip --lightgun1=0 --lightgun2=1 --saw-pad");

            var previewOut = RulePipeline.PreviewExample(new List<LaunchRule> { hidRule, seesMarker },
                "emu.exe \"C:\\roms\\game.zip\"");
            Expect("hid detect: the example channel computes the same line",
                previewOut == "emu.exe C:\\roms\\game.zip --lightgun1=0 --lightgun2=1 --saw-pad");

            Expect("hid detect: no matchers = NOT CONFIGURED",
                !new LaunchRule { Type = LaunchRule.TypeHidDetect }.IsConfigured
                && hidRule.IsConfigured);
        }

        // ── Copy file: real copies, cache hit, delete-on-exit batch, m3u staging, inert preview ──
        {
            string Quo(string qp) => "\"" + qp + "\"";
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lbx-rules-selftest-copy");
            string srcDir = System.IO.Path.Combine(root, "src");
            string dstDir = System.IO.Path.Combine(root, "dst");
            try
            {
                System.IO.Directory.CreateDirectory(srcDir);
                string rom = System.IO.Path.Combine(srcDir, "game.iso");
                System.IO.File.WriteAllText(rom, "ROMDATA");
                var rule = new LaunchRule
                {
                    Type = LaunchRule.TypeCopyFile,
                    CopySourceDir = srcDir, CopyTargetDir = dstDir, CopyDeleteOnExit = true,
                };
                string copy = System.IO.Path.Combine(dstDir, "game.iso");

                var (_, outArgs) = RulePipeline.ApplyRules(new List<LaunchRule> { rule }, "emu.exe", Quo(rom));
                var batch = RulePipeline.TakeAfterLaunch();
                Expect("copyfile: the argument points at the copy and the copy exists",
                    outArgs == Quo(copy).Trim('"') || outArgs == Quo(copy));
                Expect("copyfile: the copy has the source's content, the original is intact",
                    System.IO.File.ReadAllText(copy) == "ROMDATA" && System.IO.File.Exists(rom));

                var again = RulePipeline.ApplyRules(new List<LaunchRule> { rule }, "emu.exe", Quo(rom));
                var batch2 = RulePipeline.TakeAfterLaunch();
                Expect("copyfile: an identical existing copy is reused", System.IO.File.Exists(copy));

                foreach (var w in batch) w();
                foreach (var w in batch2) w();
                Expect("copyfile: the after-launch batch deletes the copy on exit",
                    !System.IO.File.Exists(copy) && System.IO.File.Exists(rom));

                // m3u: the ENTRY is copied, a temp m3u keeps the original name, the original m3u is intact.
                string m3u = System.IO.Path.Combine(srcDir, "set.m3u");
                System.IO.File.WriteAllLines(m3u, new[] { "game.iso" });
                var ruleM3u = new LaunchRule
                {
                    Type = LaunchRule.TypeCopyFile,
                    CopySourceDir = srcDir, CopyTargetDir = dstDir,
                };
                var (_, m3uArgs) = RulePipeline.ApplyRules(new List<LaunchRule> { ruleM3u }, "emu.exe", Quo(m3u));
                RulePipeline.TakeAfterLaunch();
                string tempM3u = RuleArgs.Split(m3uArgs)[0];
                Expect("copyfile m3u: temp copy keeps the original file name, entry points at the copy",
                    System.IO.Path.GetFileName(tempM3u) == "set.m3u" && tempM3u != m3u
                    && System.IO.File.ReadAllLines(tempM3u)[0] == copy && System.IO.File.Exists(copy));
                Expect("copyfile m3u: the original m3u still holds its relative entry",
                    System.IO.File.ReadAllLines(m3u)[0] == "game.iso");

                System.IO.File.Delete(copy);
                var preview = RulePipeline.PreviewExample(new List<LaunchRule> { rule }, "emu.exe " + Quo(rom));
                Expect("copyfile: the preview rewrites to the would-be target but copies nothing",
                    preview.Contains("game.iso") && preview.Contains(dstDir) && !System.IO.File.Exists(copy));
            }
            finally { try { System.IO.Directory.Delete(root, recursive: true); } catch { } }
        }

        // ── Use file content: pointer files, rooted only when real, guards, exe out of reach ──
        {
            string Quo(string qp) => "\"" + qp + "\"";
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lbx-rules-selftest-ufc");
            try
            {
                System.IO.Directory.CreateDirectory(root);
                string target = System.IO.Path.Combine(root, "real-game.chd");
                System.IO.File.WriteAllText(target, "X");
                string pointerRel = System.IO.Path.Combine(root, "pointer-rel.txt");
                System.IO.File.WriteAllText(pointerRel, "real-game.chd\r\n");     // relative + trailing newline
                string pointerOpt = System.IO.Path.Combine(root, "pointer-opt.txt");
                System.IO.File.WriteAllText(pointerOpt, "--fullscreen\r\n");      // not a path

                var rule = new LaunchRule { Type = LaunchRule.TypeUseFileContent, UseFileDir = true };
                var (_, outArgs) = RulePipeline.ApplyRules(new List<LaunchRule> { rule },
                    "emu.exe", Quo(pointerRel) + " " + Quo(pointerOpt) + " --keep");
                var parts = RuleArgs.Split(outArgs);
                Expect("usefilecontent: relative pointer content rooted beside the file, trimmed",
                    parts[0] == target);
                Expect("usefilecontent: non-path content passes through raw (never path-ified)",
                    parts[1] == "--fullscreen");
                Expect("usefilecontent: a non-file argument is untouched", parts[2] == "--keep");

                string big = System.IO.Path.Combine(root, "big.bin");
                System.IO.File.WriteAllText(big, new string('A', 5000));
                var (_, bigArgs) = RulePipeline.ApplyRules(new List<LaunchRule> { rule }, "emu.exe", Quo(big));
                Expect("usefilecontent: a file over the pointer size cap is left alone",
                    RuleArgs.Split(bigArgs)[0] == big);
            }
            finally { try { System.IO.Directory.Delete(root, recursive: true); } catch { } }
        }

        // ── Monitor profile rule: pre-evaluation on the preview walk, last fired wins ──
        {
            Actions.MonitorProfileAction.TestBypassModuleGate = true;
            LaunchRule Mon(string assign, string filter = "", bool nvapi = false, string custom = "")
                => new()
                {
                    Type = LaunchRule.TypeMonitorProfile, MonitorAssignData = assign,
                    Filter = filter, MonitorNvapi = nvapi, MonitorCustomData = custom,
                };

            var (a1, _, _) = Actions.MonitorProfileAction.EvaluateRules(
                new List<LaunchRule> { Mon("none", filter: "--sinden") }, "emu.exe game.zip");
            Expect("monitor rule: a refused probe contributes nothing", !a1.IsSet);

            var (a2, _, nv2) = Actions.MonitorProfileAction.EvaluateRules(
                new List<LaunchRule> { Mon("none", filter: "--sinden", nvapi: true) }, "emu.exe game.zip --sinden");
            Expect("monitor rule: fired \"none\" answers explicitly, nvapi flag carried",
                a2.IsSet && a2.Kind == Monitors.AssignKind.None && nv2);

            // The branching bus works on the pre-evaluation too: an earlier rule injects the marker
            // the monitor rule keys on — the preview walk sees the TRANSFORMED line.
            var injected = Sfx("--lightgun", asArg: true);
            var (a3, _, _) = Actions.MonitorProfileAction.EvaluateRules(
                new List<LaunchRule> { injected, Mon("profile-A", filter: "--lightgun") }, "emu.exe game.zip");
            Expect("monitor rule: probes see the line as previous rules left it",
                a3.IsSet && a3.Kind == Monitors.AssignKind.Profile && a3.ProfileId == "profile-A");

            var (a4, c4, _) = Actions.MonitorProfileAction.EvaluateRules(
                new List<LaunchRule>
                {
                    Mon("profile-A"),
                    Mon("custom", custom: "{\"Name\":\"RuleCustom\"}"),
                }, "emu.exe game.zip");
            Expect("monitor rule: the LAST fired rule supersedes, custom settings deserialized",
                a4.Kind == Monitors.AssignKind.Custom && c4 != null && c4.Name == "RuleCustom");

            var disabledRule = Mon("profile-A");
            disabledRule.Enabled = false;
            var (a5, _, _) = Actions.MonitorProfileAction.EvaluateRules(
                new List<LaunchRule> { disabledRule }, "emu.exe game.zip");
            Expect("monitor rule: a disabled rule contributes nothing", !a5.IsSet);
            Actions.MonitorProfileAction.TestBypassModuleGate = false;
        }

        // ── the rom-token search (Mehdi's unification: one pipeline, then ask what became of
        //    the rom argument) ──
        {
            string rom = @"G:\Roms\SNES\game.zip";
            var c = RomTokenSearch.Classify(RuleArgs.Split(@"-L core.dll ""G:\Roms\SNES\game.zip"" -f"), rom, nameOnly: false);
            Expect("token: found untouched, at its argument index",
                c.State == RomTokenState.Found && c.ArgIndex == 2);

            c = RomTokenSearch.Classify(RuleArgs.Split(@"-L core.dll ""D:\Mirror\game.zip"" -f"), rom, nameOnly: false);
            Expect("token: same file name in another directory = relocated, path captured",
                c.State == RomTokenState.Relocated && c.Relocated == @"D:\Mirror\game.zip");

            c = RomTokenSearch.Classify(RuleArgs.Split(@"-L core.dll ""D:\Mirror\other.7z"" -f"), rom, nameOnly: false);
            Expect("token: a different name = missing (resolution will be skipped)",
                c.State == RomTokenState.Missing);

            c = RomTokenSearch.Classify(RuleArgs.Split("-flag game -x"), rom, nameOnly: true);
            Expect("token: name-only mode matches the bare name",
                c.State == RomTokenState.Found && c.ArgIndex == 1);

            c = RomTokenSearch.Classify(RuleArgs.Split("-flag renamed -x"), rom, nameOnly: true);
            Expect("token: name-only mode cannot trace a rename = missing",
                c.State == RomTokenState.Missing);

            // A marker argument must never be mistaken for a relocated rom (not path-shaped).
            c = RomTokenSearch.Classify(RuleArgs.Split("game.zip -x"), rom, nameOnly: false);
            Expect("token: a bare same-name argument without a path shape is not a relocation",
                c.State == RomTokenState.Missing);
        }

        // ── alias validation (databases hang on it) ──
        {
            static long? None(string _) => null;
            Expect("alias: size validated by stat = original identity",
                RomSourceDecision.ValidateAlias(@"G:\r\game.zip", @"D:\m\game.zip", p => 1234, None) == @"G:\r\game.zip");

            Expect("alias: original missing, size validated by the DATABASE record",
                RomSourceDecision.ValidateAlias(@"G:\r\game.zip", @"D:\m\game.zip",
                    p => p.StartsWith(@"D:\") ? 1234 : (long?)null, p => 1234) == @"G:\r\game.zip");

            Expect("alias: sizes differ = no alias",
                RomSourceDecision.ValidateAlias(@"G:\r\game.zip", @"D:\m\game.zip",
                    p => p.StartsWith(@"D:\") ? 1234 : 5678, None) == null);

            Expect("alias: nothing to validate against = no alias",
                RomSourceDecision.ValidateAlias(@"G:\r\game.zip", @"D:\m\game.zip", None, None) == null);
        }

        // ── the trace channel (groups + trace lot) ──
        {
            var rules = new List<LaunchRule>
            {
                P("-a", filter: "rom.bin"),          // fires
                P("-b", filter: "nothere"),          // refused
                Disabled(P("-c")),                   // skipped
            };
            RulePipeline.PreviewWithTrace(rules, "emu.exe rom.bin", out var tr);
            Expect("trace: fired / refused / skipped",
                tr.Count == 3
                && tr[0].State == RulePipeline.TraceState.Fired
                && tr[1].State == RulePipeline.TraceState.Refused
                && tr[2].State == RulePipeline.TraceState.Skipped
                && tr.All(t => !t.AnchorBroken));
        }
        {
            // An anchored condition whose evaluation FLIPS mid-pipeline: rule 0 is anchored on a
            // token that is absent (group entry records "refused"), rule 1 injects the token, rule 2
            // (same anchored signature) now passes — the commitment the checkbox makes is broken,
            // and the trace is what says so.
            var rules = new List<LaunchRule>
            {
                Anchored(P("-x", filter: "-tok")),
                P("-tok"),
                Anchored(P("-y", filter: "-tok")),
            };
            RulePipeline.PreviewWithTrace(rules, "emu.exe rom.bin", out var tr);
            Expect("trace: a mid-pipeline injection breaks the anchor",
                !tr[0].AnchorBroken && tr[2].AnchorBroken
                && tr[0].State == RulePipeline.TraceState.Refused
                && tr[2].State == RulePipeline.TraceState.Fired);
        }

        // ── canonical signatures (the derived group view's keys) ──
        Expect("signature: entry order and case do not matter",
            Sig("a, b", comma: true, matchAll: true)!.Equals(Sig("B,a", comma: true, matchAll: true)));
        Expect("signature: refinement is strict EVERY-superset",
            Sig("a,b,c", comma: true, matchAll: true)!.Refines(Sig("a, b", comma: true, matchAll: true)!)
            && !Sig("a,b", comma: true, matchAll: true)!.Refines(Sig("a,b,c", comma: true, matchAll: true)!));
        Expect("signature: ANY mode never nests",
            !Sig("a,b", comma: true, matchAll: false)!.Refines(Sig("a", comma: false, matchAll: false)!));
        Expect("signature: only anchored rules have one",
            ProbeSignature.Of(P("-a", filter: "x")) == null && Sig("x") != null);

        Console.WriteLine(_fail == 0 ? "ALL OK" : $"{_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    private static LaunchRule Sfx(string suffix, bool asArg = true, string filter = "")
        => new() { Type = LaunchRule.TypeSuffix, Suffix = suffix, AsArg = asArg, Filter = filter };

    private static LaunchRule[] Suffix(string suffix, bool asArg = true, string filter = "")
        => new[] { Sfx(suffix, asArg, filter) };

    private static LaunchRule[] Rp(string search, string replace, bool asArg = true, bool regex = false,
        bool caseSensitive = false, string filter = "", string vars = "")
        => new[] { new LaunchRule { Type = LaunchRule.TypeReplace, Search = search, ReplaceWith = replace,
                                    AsArg = asArg, UseRegex = regex, CaseSensitive = caseSensitive,
                                    Filter = filter, VariablesData = vars } };

    private static LaunchRule Rif(string file, string search, string replace, string filter = "")
        => new() { Type = LaunchRule.TypeReplaceInFile, TargetFile = file, Search = search, ReplaceWith = replace, Filter = filter };

    private static LaunchRule Cx(string newExe, string filter = "")
        => new() { Type = LaunchRule.TypeChangeExe, NewExe = newExe, Filter = filter };

    private static LaunchRule Crp(string find, string high = "", string low = "")
        => new() { Type = LaunchRule.TypeChangeRomPath, RomPathFind = find, RomPathHigh = high, RomPathLow = low };

    /// <summary>A path argument as the launch would carry it — quoted when spaces demand it, which
    /// %TEMP% often does; expectations must speak post-round-trip quoting like every other case.</summary>
    private static string Q(string dir, string file)
        => RuleArgs.Join(new[] { System.IO.Path.Combine(dir, file) });

    private static void CheckCmd(string what, LaunchRule[] rules, string exe, string args, string expExe, string expArgs)
    {
        var got = RulePipeline.ApplyRules(rules.ToList(), exe, args);
        if (got.Exe == expExe && got.Args == expArgs) { Console.WriteLine($"  ok    {what}"); return; }
        _fail++;
        Console.WriteLine($"  FAIL  {what}\n        expected : \"{expExe}\" {expArgs}\n        got      : \"{got.Exe}\" {got.Args}");
    }

    private static LaunchRule Anchored(LaunchRule r) { r.AsGroup = true; return r; }

    private static ProbeSignature? Sig(string filter, bool comma = false, bool matchAll = false)
        => ProbeSignature.Of(Anchored(P("-p", filter: filter, comma: comma, matchAll: matchAll)));

    private static void Expect(string what, bool ok)
    {
        if (ok) { Console.WriteLine($"  ok    {what}"); return; }
        _fail++;
        Console.WriteLine($"  FAIL  {what}");
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static LaunchRule P(string prefix, bool asArg = true, string filter = "", bool comma = false,
        bool matchAll = false, bool remove = false, string exclude = "", bool commaEx = false, bool matchAllEx = false)
        => new()
        {
            Type = LaunchRule.TypePrefix, Prefix = prefix, AsArg = asArg,
            Filter = filter, CommaFilter = comma, MatchAllFilter = matchAll, RemoveFilter = remove,
            Exclude = exclude, CommaExclude = commaEx, MatchAllExclude = matchAllEx,
        };

    private static LaunchRule[] Prefix(string prefix, bool asArg = true, string filter = "", bool comma = false,
        bool matchAll = false, bool remove = false, string exclude = "", bool commaEx = false, bool matchAllEx = false)
        => new[] { P(prefix, asArg, filter, comma, matchAll, remove, exclude, commaEx, matchAllEx) };

    private static LaunchRule Disabled(LaunchRule r) { r.Enabled = false; return r; }

    private static void CheckPreview(string what, LaunchRule[] rules, string fullLine, string expected)
    {
        string got = RulePipeline.PreviewExample(rules.ToList(), fullLine);
        if (got == expected) { Console.WriteLine($"  ok    {what}"); return; }
        _fail++;
        Console.WriteLine($"  FAIL  {what}\n        in       : {fullLine}\n        expected : {expected}\n        got      : {got}");
    }

    private static void Check(string what, LaunchRule[] rules, string args, string expected)
    {
        string got = RunPipeline(rules, @"C:\emu\retroarch\retroarch.exe", args);
        if (got == expected) { Console.WriteLine($"  ok    {what}"); return; }
        _fail++;
        Console.WriteLine($"  FAIL  {what}\n        args     : {args}\n        expected : {expected}\n        got      : {got}");
    }

    /// <summary>The pipeline body, minus the store: same probe + action + marker code as a launch.</summary>
    private static string RunPipeline(IEnumerable<LaunchRule> rules, string exe, string args)
        => RulePipeline.ApplyRules(rules.ToList(), exe, args).Args;
}
