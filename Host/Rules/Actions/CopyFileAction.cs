// CopyFile — stage slow files onto fast storage right before the launch: every file argument whose
// path CONTAINS the source-directory text (case-sensitive substring, the original's Contains) is
// copied into the target directory and the argument rewritten to the copy. The classic use is a
// network share → local SSD. BigBoxProfile's semantics, split over our two channels the way BBP
// itself split them: the COPY happens in ExecuteBefore (real channel only, with a cheap idempotence
// the original lacked — same size + same write time = reuse, log "cache hit"), the REWRITE happens
// in Apply using what ExecuteBefore recorded; the EXAMPLE channel rewrites to the would-be targets
// and copies nothing. Delete-on-exit rides the pipeline's after-launch batch (BBP's ExecuteAfter),
// run by RunProcess once the emulator exits.
//
// M3u arguments (BBP's UseM3UContent=true) follow our established m3u treatment: the ENTRY files
// matching the source dir are copied, and a temp m3u KEEPING the original file name (hash in the
// directory — the rom-token search still sees "same name, another path") is written with every
// entry absolutized and the copied ones pointing at their copies. The original m3u is never touched.
//
// RAM disk (BBP's useRamDisk, restyled to OUR infrastructure): instead of the target directory,
// the copies can land on a per-launch ImDisk drive mounted through the ROM extractor's
// ArchiveRamDisk — same driver, same elevated-task path, same guards (size cap + free RAM), and
// the SAME exit cleanup: the mount is registered in ArchiveRamDisk's active table, which
// RomExtractor.OnGameExitCleanup already UnmountAll()s when the game exits — no second lifecycle.
// One drive sized to the SUM of the launch's copies (BBP mounted one PER FILE); delete-on-exit is
// moot there (the unmount destroys the drive). Every failure falls back to the target directory.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class CopyFileAction : IRuleAction
{
    private const string Tag = "rules";

    public string Type => LaunchRule.TypeCopyFile;
    public string AddLabel => "Add: Copy file…";
    public string DialogTitle => "Copy file rule";

    public bool IsConfigured(LaunchRule r) => r.CopySourceDir.Length > 0 && r.CopyTargetDir.Length > 0;

    public string Describe(LaunchRule r)
        => $"Copy args under {r.CopySourceDir} → {r.CopyTargetDir}"
         + (r.CopyDeleteOnExit ? " (deleted on exit)" : "");

    /// <summary>ExecuteBefore→Apply handoff for THIS walk: original path → copy path. The pipeline
    /// runs a rule's ExecuteBefore then its Apply back-to-back on one thread; Apply consumes.</summary>
    [ThreadStatic] private static Dictionary<string, string>? _pending;

    public void ExecuteBefore(LaunchRule r, RuleCmd cmd)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deleteOnExit = new List<string>();
        try
        {
            var parts = RuleArgs.Split(cmd.Args);
            // Destination: the RAM drive when asked for and mountable, the target dir otherwise.
            // On RAM, delete-on-exit is pointless — the exit unmount destroys the whole drive.
            string destDir = r.CopyUseRamDisk ? (TryMountRamDisk(r, parts) ?? r.CopyTargetDir) : r.CopyTargetDir;
            bool onRam = destDir != r.CopyTargetDir;
            foreach (var part in parts)
            {
                try
                {
                    if (!part.Contains(r.CopySourceDir) || !File.Exists(part)) continue;
                    if (part.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                    {
                        string? temp = StageM3u(r, part, destDir, onRam, map, deleteOnExit);
                        if (temp != null) map[part] = temp;
                    }
                    else
                    {
                        string target = CopyOne(part, destDir);
                        map[part] = target;
                        if (r.CopyDeleteOnExit && !onRam) deleteOnExit.Add(target);
                    }
                }
                catch (Exception ex) { LbLog.Warn(Tag, $"CopyFile: \"{part}\" failed ({ex.Message}) — argument left as-is"); }
            }
        }
        finally
        {
            _pending = map.Count > 0 ? map : null;
            if (deleteOnExit.Count > 0)
                RulePipeline.RegisterAfterLaunch(() =>
                {
                    foreach (var f in deleteOnExit)
                        try { if (File.Exists(f)) { File.Delete(f); LbLog.Info(Tag, $"CopyFile: deleted on exit \"{f}\""); } }
                        catch (Exception ex) { LbLog.Warn(Tag, $"CopyFile: delete-on-exit \"{f}\" failed ({ex.Message})"); }
                });
        }
    }

    /// <summary>Mounts one RAM drive sized to the SUM of this launch's matching files (+50 MB, the
    /// extractor's margin) under the extractor's own guards: driver present, size under the rule's
    /// cap AND under free RAM. Registered in ArchiveRamDisk's active table, so the existing
    /// game-exit cleanup (RomExtractor.OnGameExitCleanup → UnmountAll) unmounts it — no second
    /// lifecycle. Null on any refusal or failure → the caller copies to the target directory.</summary>
    private static string? TryMountRamDisk(LaunchRule r, string[] parts)
    {
        try
        {
            if (!Rom.ArchiveRamDisk.IsDriverInstalled())
            {
                LbLog.Info(Tag, "CopyFile: ramdisk requested but the ImDisk driver is absent — target dir used");
                return null;
            }
            long totalBytes = 0;
            foreach (var part in parts)
            {
                if (!part.Contains(r.CopySourceDir) || !File.Exists(part)) continue;
                if (part.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                {
                    string dir = Path.GetDirectoryName(part) ?? "";
                    foreach (var line in File.ReadAllLines(part))
                    {
                        string e = line.Trim();
                        if (e.Length == 0 || e.StartsWith("#")) continue;
                        try
                        {
                            string abs = Path.IsPathRooted(e) ? e : Path.GetFullPath(Path.Combine(dir, e));
                            if (abs.Contains(r.CopySourceDir) && File.Exists(abs)) totalBytes += new FileInfo(abs).Length;
                        }
                        catch { }
                    }
                }
                else totalBytes += new FileInfo(part).Length;
            }
            if (totalBytes == 0) return null;
            int needMb = (int)(totalBytes / (1024 * 1024)) + 50;
            int freeMb = Rom.ArchiveRamDisk.GetFreeRamMb();
            if (needMb > r.CopyRamDiskMaxMb || needMb >= freeMb)
            {
                LbLog.Info(Tag, $"CopyFile: ramdisk skipped (need {needMb}MB, max {r.CopyRamDiskMaxMb}MB, free {freeMb}MB)");
                return null;
            }
            string? root = Rom.ArchiveRamDisk.Mount(needMb);
            if (string.IsNullOrEmpty(root)) return null;
            Rom.ArchiveRamDisk.Register("launch-rules-copyfile", root!);
            LbLog.Info(Tag, $"CopyFile: ramdisk {root} ({needMb}MB) for this launch");
            return root;
        }
        catch (Exception ex) { LbLog.Warn(Tag, $"CopyFile: ramdisk mount failed ({ex.Message}) — target dir used"); return null; }
    }

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd)
    {
        var map = _pending;
        _pending = null;
        if (map == null || map.Count == 0) return cmd;
        var parts = RuleArgs.Split(cmd.Args);
        bool changed = false;
        for (int i = 0; i < parts.Length; i++)
            if (map.TryGetValue(parts[i], out var copy)) { parts[i] = copy; changed = true; }
        return changed ? cmd with { Args = RuleArgs.Join(parts) } : cmd;
    }

    /// <summary>Example channel: the would-be rewrite, nothing copied (the original's ModifyExemple
    /// showed targetDir\name too). An m3u argument shows the same simple form.</summary>
    public RuleCmd ApplyExample(LaunchRule r, RuleCmd cmd)
    {
        var parts = RuleArgs.Split(cmd.Args);
        bool changed = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!parts[i].Contains(r.CopySourceDir) || !File.Exists(parts[i])) continue;
            parts[i] = Path.Combine(r.CopyTargetDir, Path.GetFileName(parts[i]));
            changed = true;
        }
        return changed ? cmd with { Args = RuleArgs.Join(parts) } : cmd;
    }

    /// <summary>Files at least this big copy through the TopMost progress window (BBP's
    /// CopyFile_Task); smaller ones are near-instant and skip the flash.</summary>
    private const long ProgressWindowBytes = 32L * 1024 * 1024;

    /// <summary>One file into the target dir. Reuses an identical existing copy (size + write time)
    /// — the network-share case pays the copy once, not per launch.</summary>
    private static string CopyOne(string source, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, Path.GetFileName(source));
        var src = new FileInfo(source);
        var dst = new FileInfo(target);
        if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc)
        {
            LbLog.Info(Tag, $"CopyFile: cache hit \"{target}\"");
            return target;
        }
        var sw = Stopwatch.StartNew();
        if (src.Length >= ProgressWindowBytes)
        {
            // The visible copy — the progress window pumps on this (launch) thread, exactly how the
            // original ran its CopyFile_Task from ExecuteBefore. A failure inside deleted the
            // half-copy; rethrow so the per-argument catch leaves the argument untouched.
            using var dlg = new CopyProgressDialog(source, target);
            dlg.ShowDialog();
            if (dlg.Error != null) throw dlg.Error;
        }
        else File.Copy(source, target, overwrite: true);
        LbLog.Info(Tag, $"CopyFile: \"{source}\" → \"{target}\" ({src.Length / 1048576.0:0.#} MB in {sw.ElapsedMilliseconds} ms)");
        return target;
    }

    /// <summary>The m3u treatment: copy the matching ENTRY files, then write a temp m3u keeping the
    /// original file name (hash in the directory) with entries absolutized and copied ones pointing
    /// at their copies. Null = no entry matched, the m3u argument stays as-is.</summary>
    private static string? StageM3u(LaunchRule r, string m3uPath, string destDir, bool onRam,
        Dictionary<string, string> map, List<string> deleteOnExit)
    {
        string dir = Path.GetDirectoryName(m3uPath) ?? "";
        var lines = File.ReadAllLines(m3uPath);
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string abs;
            try { abs = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line)); }
            catch { continue; }
            if (abs.Contains(r.CopySourceDir) && File.Exists(abs))
            {
                string target = CopyOne(abs, destDir);
                map[abs] = target;
                if (r.CopyDeleteOnExit && !onRam) deleteOnExit.Add(target);
                lines[i] = target;
                changed = true;
            }
            else lines[i] = abs;   // absolutized — a relative entry would break from the temp copy's dir
        }
        if (!changed) return null;

        string outDir = Path.Combine(Path.GetTempPath(), "litebox-rules-m3u",
            ((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(m3uPath)).ToString());
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, Path.GetFileName(m3uPath));
        File.WriteAllLines(outPath, lines);
        return outPath;
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(576) };
        int y = 0;

        Label Cap(string t, int lines = 1)
        {
            var l = new Label
            {
                Text = t, AutoSize = false, Location = new Point(0, y + S(2)),
                Size = new Size(S(574), S(2 + 18 * lines)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            body.Controls.Add(l);
            y += S(6 + 18 * lines);
            return l;
        }
        (TextBox Box, Button Browse) DirField(string value)
        {
            var t = new TextBox
            {
                Text = value, Location = new Point(0, y), Width = S(486),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            var b = new Button
            {
                Text = "Browse…", Location = new Point(S(492), y - S(1)), Size = new Size(S(82), S(25)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            b.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog();
                if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) t.Text = dlg.SelectedPath;
            };
            body.Controls.Add(t); body.Controls.Add(b);
            y += S(30);
            return (t, b);
        }

        Cap("File arguments whose path contains the source directory are copied into the target directory"
            + " before launch and the argument points at the copy (network share → local disk). An .m3u"
            + " argument gets its matching ENTRY files copied and a rewritten temp copy; the original m3u"
            + " is never touched. Identical existing copies are reused.", 4);

        Cap("Source directory (substring the argument must contain):");
        var (src, _) = DirField(r.CopySourceDir);
        Cap("Target directory (the copies land here):");
        var (dst, _) = DirField(r.CopyTargetDir);

        var del = new CheckBox
        {
            Text = "Delete the copies after the game exits", Checked = r.CopyDeleteOnExit, AutoSize = true,
            Location = new Point(0, y), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(del);
        y += S(26);

        // ── RAM disk (the ROM extractor's ImDisk infrastructure, shared) ──
        var ram = new CheckBox
        {
            Text = "Copy onto a per-launch RAM disk instead (unmounted at game exit; falls back to the target dir)",
            Checked = r.CopyUseRamDisk, AutoSize = true,
            Location = new Point(0, y), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(ram);
        y += S(24);
        body.Controls.Add(new Label
        {
            Text = "Max size (MB):", AutoSize = true, Location = new Point(S(18), y + S(3)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        });
        var ramMax = new NumericUpDown
        {
            Minimum = 1, Maximum = 1000000, Value = Math.Min(1000000, Math.Max(1, r.CopyRamDiskMaxMb)),
            Location = new Point(S(110), y), Width = S(72),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(ramMax);
        bool ramReady = false;
        try { ramReady = Rom.ArchiveRamDisk.IsReady(); } catch { }
        body.Controls.Add(new Label
        {
            Text = ramReady
                ? "ImDisk ready (same setup as the ROM extractor)."
                : "ImDisk not ready — install the driver / elevated task from the ROM extractor options.",
            AutoSize = true, Location = new Point(S(196), y + S(3)),
            ForeColor = ramReady ? Color.FromArgb(120, 190, 120) : Color.FromArgb(220, 160, 90),
            BackColor = LiteBoxTheme.Bg,
        });
        y += S(30);

        // ── sandbox: pedagogical, so it rewrites on the substring match ALONE — the real launch
        // (and the page preview) additionally require the file to EXIST; a matching-but-missing
        // path is flagged instead of silently doing nothing with the demo line. Nothing is copied.
        Cap("Sandbox — test line (matching args get rewritten here even if the file doesn't exist):");
        var testLine = new TextBox
        {
            Text = @"emulator.exe ""\\NAS\roms\game.iso""", Location = new Point(0, y), Width = S(574),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(testLine);
        y += S(30);
        Cap("Result:");
        var result = new TextBox
        {
            Location = new Point(0, y), Width = S(574), Multiline = true, Height = S(46), ReadOnly = true,
            BackColor = LiteBoxTheme.Bg, ForeColor = LiteBoxTheme.SubFg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(result);
        y += S(52);

        void Recalc()
        {
            try
            {
                var all = RuleArgs.SplitFull(testLine.Text);
                if (all.Length == 0 || src.Text.Length == 0) { result.Text = ""; return; }
                var parts = all.Skip(1).ToArray();
                var missing = new List<string>();
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!parts[i].Contains(src.Text)) continue;
                    if (!File.Exists(parts[i])) missing.Add(parts[i]);
                    parts[i] = Path.Combine(dst.Text, Path.GetFileName(parts[i]));
                }
                result.Text = RuleArgs.Join(new[] { all[0] }.Concat(parts))
                    + (missing.Count > 0
                        ? "\r\n(note: " + string.Join(", ", missing.Select(Path.GetFileName))
                          + " doesn't exist here — at a real launch that argument would stay untouched)"
                        : "");
            }
            catch (Exception ex) { result.Text = "(invalid: " + ex.Message + ")"; }
        }
        src.TextChanged += (_, _) => Recalc();
        dst.TextChanged += (_, _) => Recalc();
        testLine.TextChanged += (_, _) => Recalc();
        Recalc();

        body.Height = y;
        return (body, y, () =>
        {
            r.CopySourceDir = src.Text.Trim();
            r.CopyTargetDir = dst.Text.Trim();
            r.CopyDeleteOnExit = del.Checked;
            r.CopyUseRamDisk = ram.Checked;
            r.CopyRamDiskMaxMb = (int)ramMax.Value;
        });
    }
}
