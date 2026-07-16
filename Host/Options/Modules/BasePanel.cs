// Base module config panel — LiteBox's native port of ExtendDB's "Database & scraping" tab.
//
// Four GroupBoxes, built through ModulePanelKit for the shared ExtendDB-style dark look:
//   • Extended database status — the merge/install state, the GitHub up-to-date vs update-available
//     check (Data.ExtDbDownloader.CheckAsync), the installed version + size (from the on-disk file),
//     an "Update from GitHub" button (Data.ExtDbDownloader.DownloadAndInstallAsync + a progress line)
//     and a "Refresh" button. Force re-merge / Undo last merge / Unmerge are shown DISABLED: LiteBox
//     uses the Extended DB read-only and ships no LB→ExtendDB merge engine (see the gap note).
//   • Metadata description sources — an ordered priority list ("Launchbox" pinned = cannot be removed,
//     but can be reordered) with Add / Remove / Up / Down; persisted to LiteBox.ini [Base] OverviewSources
//     as a comma list.
//   • Behavior — Auto-download at boot ([Base] AutoUpdateDb, read by HostBoot), Enable LB→ExtendDB merge,
//     Enable defaultOverview cache, and a Duplicate-handling combo — persisted to [Base] via GetSec/SetSec.
//   • Image mirror — the ExtendDB image-mirror base URL (BaseCredentials.RemoteImageBaseUrl on read,
//     [Base] RemoteImageBaseUrl on write).
//
// NOTE: the ScreenScraper account fields the plugin's tab carries are intentionally NOT here — LiteBox
// reads LaunchBox's own scraper credentials elsewhere and does not manage SS creds on this surface.
//
// ModulesOptions.ModuleConfigPanel dispatches here for LbModule.Base.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Options;

internal static class BasePanel
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private const string Section = "Base";

    // The full set of pickable description sources (mirrors the plugin's dropdown). "Launchbox" is the
    // always-present, non-removable fallback and is never offered here (it is added to the list implicitly).
    private static readonly string[] AllSources =
    {
        "ScreenScraper-En", "ScreenScraper-Fr", "ScreenScraper-De",
        "ScreenScraper-Es", "ScreenScraper-It", "ScreenScraper-Pt",
        "Steam-En", "Steam-Fr", "Steam-De", "Steam-Es", "Steam-It", "Steam-Pt",
        "VNDB", "Igdb", "IgdbStoryline",
        "Ai-En", "Ai-Fr", "Ai-De", "Ai-Es", "Ai-It", "Ai-Pt",
    };

    private static readonly string[] DuplicateModes = { "Off", "Skip", "Merge" };

    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var Fg = ModulePanelKit.Fg;
        var Sub = ModulePanelKit.Sub;

        var root = ModulePanelKit.Root(dpiS);
        const int GroupW = 620;

        var cfg = LiteBoxConfig.LoadForExe();

        // ════════════════════════════════════════════════════════════════════
        // Group 1 — Extended database status
        // ════════════════════════════════════════════════════════════════════
        var gStatus = ModulePanelKit.Group("Extended database status", dpiS);
        gStatus.Location = new Point(S(4), S(6));
        gStatus.Size = new Size(S(GroupW), S(238));

        var lblState = new Label
        {
            AutoSize = true, ForeColor = Sub, BackColor = ModulePanelKit.Bg,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Location = new Point(S(14), S(28)), Text = "● Status loading…",
        };
        var lblGithub = new Label
        {
            AutoSize = true, ForeColor = Sub, BackColor = ModulePanelKit.Bg,
            Font = new Font("Segoe UI", 9f),
            Location = new Point(S(14), S(54)), Text = "Checking GitHub…",
        };
        var lblDetails = new Label
        {
            AutoSize = true, ForeColor = Sub, BackColor = ModulePanelKit.Bg,
            Font = new Font("Segoe UI", 9f),
            Location = new Point(S(14), S(78)), Text = "Version: -    Size: -",
        };
        gStatus.Controls.Add(lblState);
        gStatus.Controls.Add(lblGithub);
        gStatus.Controls.Add(lblDetails);

        var btnUpdate = ModulePanelKit.Button("Update from GitHub", dpiS, readOnly);
        btnUpdate.Location = new Point(S(14), S(104));
        var btnRefresh = ModulePanelKit.Button("Refresh", dpiS);
        btnRefresh.Location = new Point(S(184), S(104));
        var lblProgress = ModulePanelKit.Caption("", dpiS, GroupW - 40);
        lblProgress.Location = new Point(S(14), S(138));
        gStatus.Controls.Add(btnUpdate);
        gStatus.Controls.Add(btnRefresh);
        gStatus.Controls.Add(lblProgress);

        // Merge actions — native LB→ExtendDB merge engine (ExtDbMerger). Manual merge is intentional, so it
        // is gated only on !readOnly + own-DB present; [Base] EnableLbMerge gates only the boot auto-merge.
        var btnForce = ModulePanelKit.Button("Force re-merge", dpiS);
        btnForce.Location = new Point(S(14), S(166));
        var btnUndo = ModulePanelKit.Button("Undo last merge", dpiS);
        btnUndo.Location = new Point(S(154), S(166));
        var btnUnmerge = ModulePanelKit.Button("Unmerge", dpiS);
        btnUnmerge.Location = new Point(S(300), S(166));
        bool mergeEngine = File.Exists(ExtDbMerger.OwnExtendedDbPath);
        btnForce.Enabled = btnUndo.Enabled = btnUnmerge.Enabled = !readOnly && mergeEngine;
        gStatus.Controls.Add(btnForce);
        gStatus.Controls.Add(btnUndo);
        gStatus.Controls.Add(btnUnmerge);

        var lblMergeGap = ModulePanelKit.Caption(
            "Fold your LaunchBox metadata into LiteBox's own Extended DB copy. Undo/Unmerge revert to the "
            + "pristine downloaded database.", dpiS, GroupW - 40);
        lblMergeGap.Location = new Point(S(14), S(198));
        gStatus.Controls.Add(lblMergeGap);
        root.Controls.Add(gStatus);

        // ════════════════════════════════════════════════════════════════════
        // Group 2 — Metadata description sources
        // ════════════════════════════════════════════════════════════════════
        var gSources = ModulePanelKit.Group("Metadata description sources", dpiS);
        gSources.Location = new Point(S(4), S(256));
        gSources.Size = new Size(S(GroupW), S(256));

        var lblSrcHint = ModulePanelKit.Caption(
            "The first source in this list that has a description for a game wins. Use Up / Down to reorder. "
            + "\"Launchbox\" is the always-on fallback and cannot be removed.", dpiS, GroupW - 40);
        lblSrcHint.Location = new Point(S(14), S(24));
        gSources.Controls.Add(lblSrcHint);

        var cmbAdd = ModulePanelKit.Combo(dpiS, readOnly, width: 260);
        cmbAdd.Location = new Point(S(14), S(56));
        var btnAdd = ModulePanelKit.Button("Add", dpiS, readOnly);
        btnAdd.AutoSize = false; btnAdd.Size = new Size(S(64), S(24));
        btnAdd.Location = new Point(S(284), S(55));
        var btnRemove = ModulePanelKit.Button("Remove", dpiS, readOnly);
        btnRemove.AutoSize = false; btnRemove.Size = new Size(S(72), S(24));
        btnRemove.Location = new Point(S(356), S(55));
        gSources.Controls.Add(cmbAdd);
        gSources.Controls.Add(btnAdd);
        gSources.Controls.Add(btnRemove);

        var list = new ListBox
        {
            BackColor = ModulePanelKit.Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f), IntegralHeight = false,
            Location = new Point(S(14), S(88)), Size = new Size(S(460), S(150)),
        };
        gSources.Controls.Add(list);

        var btnUp = ModulePanelKit.Button("▲", dpiS, readOnly);
        btnUp.AutoSize = false; btnUp.Size = new Size(S(46), S(46));
        btnUp.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        btnUp.Location = new Point(S(484), S(98));
        var btnDown = ModulePanelKit.Button("▼", dpiS, readOnly);
        btnDown.AutoSize = false; btnDown.Size = new Size(S(46), S(46));
        btnDown.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        btnDown.Location = new Point(S(484), S(150));
        gSources.Controls.Add(btnUp);
        gSources.Controls.Add(btnDown);
        root.Controls.Add(gSources);

        // ════════════════════════════════════════════════════════════════════
        // Group 3 — Behavior
        // ════════════════════════════════════════════════════════════════════
        var gBehavior = ModulePanelKit.Group("Behavior", dpiS);
        gBehavior.Location = new Point(S(4), S(524));
        gBehavior.Size = new Size(S(GroupW), S(176));

        var chkAuto = ModulePanelKit.Check("Auto-download the Extended database at boot", dpiS,
            cfg.GetSecBool(Section, "AutoUpdateDb", true), readOnly);
        chkAuto.Location = new Point(S(14), S(28));
        var chkMerge = ModulePanelKit.Check("Enable LaunchBox → ExtendDB merge", dpiS,
            cfg.GetSecBool(Section, "EnableLbMerge", false), readOnly);
        chkMerge.Location = new Point(S(14), S(54));
        var chkOverview = ModulePanelKit.Check("Enable defaultOverview cache", dpiS,
            cfg.GetSecBool(Section, "EnableOverviewCache", true), readOnly);
        chkOverview.Location = new Point(S(14), S(80));
        gBehavior.Controls.Add(chkAuto);
        gBehavior.Controls.Add(chkMerge);
        gBehavior.Controls.Add(chkOverview);

        var lblDup = new Label
        {
            AutoSize = true, ForeColor = Fg, BackColor = ModulePanelKit.Bg,
            Font = new Font("Segoe UI", 9f),
            Location = new Point(S(14), S(112)), Text = "Duplicate handling:",
        };
        var cmbDup = ModulePanelKit.Combo(dpiS, readOnly, width: 180);
        cmbDup.Location = new Point(S(150), S(109));
        cmbDup.Items.AddRange(DuplicateModes);
        cmbDup.SelectedIndex = DuplicateIndex(cfg.GetSec(Section, "DuplicateHandling", "Off"));
        gBehavior.Controls.Add(lblDup);
        gBehavior.Controls.Add(cmbDup);

        var lblBehaviorNote = ModulePanelKit.Caption(
            "Merge and duplicate-handling are stored preferences; the LB→ExtendDB merge they drive is not "
            + "performed by LiteBox yet (read-only use).", dpiS, GroupW - 40);
        lblBehaviorNote.Location = new Point(S(14), S(140));
        gBehavior.Controls.Add(lblBehaviorNote);
        root.Controls.Add(gBehavior);

        // ════════════════════════════════════════════════════════════════════
        // Group 4 — Image mirror
        // ════════════════════════════════════════════════════════════════════
        var gMirror = ModulePanelKit.Group("Image mirror", dpiS);
        gMirror.Location = new Point(S(4), S(712));
        gMirror.Size = new Size(S(GroupW), S(100));

        var lblMirror = ModulePanelKit.Caption(
            "Base URL of the ExtendDB image mirror. Leave as the default unless you have a custom endpoint.",
            dpiS, GroupW - 40);
        lblMirror.Location = new Point(S(14), S(24));
        var mirror = ModulePanelKit.TextField(dpiS, readOnly, width: 520);
        mirror.Location = new Point(S(14), S(52));
        gMirror.Controls.Add(lblMirror);
        gMirror.Controls.Add(mirror);
        root.Controls.Add(gMirror);

        // ── Prefill ───────────────────────────────────────────────────────────
        try { mirror.Text = BaseCredentials.RemoteImageBaseUrl(); } catch { }

        foreach (var s in LoadSources(cfg)) list.Items.Add(s);

        // ── Priority-list behaviour ─────────────────────────────────────────────
        void RefreshAddDropdown()
        {
            var used = new HashSet<string>(list.Items.Cast<object>().Select(o => o.ToString()!), StringComparer.OrdinalIgnoreCase);
            cmbAdd.BeginUpdate();
            cmbAdd.Items.Clear();
            foreach (var s in AllSources) if (!used.Contains(s)) cmbAdd.Items.Add(s);
            if (cmbAdd.Items.Count > 0) cmbAdd.SelectedIndex = 0;
            cmbAdd.EndUpdate();
            btnAdd.Enabled = !readOnly && cmbAdd.Items.Count > 0;
        }

        void UpdateButtons()
        {
            int idx = list.SelectedIndex, count = list.Items.Count;
            if (idx < 0)
            {
                btnUp.Enabled = btnDown.Enabled = btnRemove.Enabled = false;
                return;
            }
            btnUp.Enabled = !readOnly && idx > 0;
            btnDown.Enabled = !readOnly && idx < count - 1;
            bool isLaunchbox = string.Equals(list.Items[idx].ToString(), "Launchbox", OIC);
            btnRemove.Enabled = !readOnly && !isLaunchbox;
        }

        list.SelectedIndexChanged += (_, _) => UpdateButtons();

        btnAdd.Click += (_, _) =>
        {
            if (readOnly) return;
            string? sel = cmbAdd.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(sel) || list.Items.Contains(sel)) return;
            list.Items.Add(sel);
            list.SelectedIndex = list.Items.Count - 1;
            RefreshAddDropdown();
            UpdateButtons();
        };
        btnRemove.Click += (_, _) =>
        {
            if (readOnly) return;
            int idx = list.SelectedIndex;
            if (idx < 0) return;
            if (string.Equals(list.Items[idx].ToString(), "Launchbox", OIC)) return;
            list.Items.RemoveAt(idx);
            if (list.Items.Count > 0) list.SelectedIndex = Math.Min(idx, list.Items.Count - 1);
            RefreshAddDropdown();
            UpdateButtons();
        };
        btnUp.Click += (_, _) =>
        {
            if (readOnly) return;
            int idx = list.SelectedIndex;
            if (idx <= 0) return;
            var item = list.Items[idx];
            list.Items.RemoveAt(idx);
            list.Items.Insert(idx - 1, item);
            list.SelectedIndex = idx - 1;
            UpdateButtons();
        };
        btnDown.Click += (_, _) =>
        {
            if (readOnly) return;
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= list.Items.Count - 1) return;
            var item = list.Items[idx];
            list.Items.RemoveAt(idx);
            list.Items.Insert(idx + 1, item);
            list.SelectedIndex = idx + 1;
            UpdateButtons();
        };

        RefreshAddDropdown();
        UpdateButtons();

        // ── Status refresh (marshalled to the UI thread) ────────────────────────
        void Ui(Action a)
        {
            try
            {
                if (gStatus.IsDisposed) return;
                if (gStatus.InvokeRequired) gStatus.BeginInvoke(a);
                else a();
            }
            catch { }
        }

        void ApplyLocalStatus()
        {
            try
            {
                string? path = File.Exists(ExtDbDownloader.TargetPath) ? ExtDbDownloader.TargetPath : MetadataDb.ExtendedDbPath;
                bool installed = path != null && File.Exists(path);
                if (installed)
                {
                    lblState.Text = "● Installed — read-only (never merged in LiteBox)";
                    lblState.ForeColor = Color.MediumSeaGreen;
                    long size = 0; try { size = new FileInfo(path!).Length; } catch { }
                    lblDetails.Text = $"File: {path}    Size: {FormatSize(size)}";
                }
                else
                {
                    lblState.Text = "● Not installed";
                    lblState.ForeColor = Color.IndianRed;
                    lblDetails.Text = "The extra metadata and non-LaunchBox medias need the Extended database.";
                }
            }
            catch { lblState.Text = "● Status unavailable"; lblState.ForeColor = Color.LightGray; }
        }

        async void StartRefresh()
        {
            ApplyLocalStatus();
            Ui(() => { lblGithub.Text = "Checking GitHub…"; lblGithub.ForeColor = Color.LightGray; });
            try
            {
                var res = await Task.Run(() => ExtDbDownloader.CheckAsync(CancellationToken.None)).ConfigureAwait(false);
                Ui(() =>
                {
                    if (res.UpdateAvailable)
                    {
                        lblGithub.Text = "● Update available on GitHub"
                            + (res.RemoteVersion != null ? $" ({FormatVersion(res.RemoteVersion)})" : "");
                        lblGithub.ForeColor = Color.DarkOrange;
                    }
                    else
                    {
                        lblGithub.Text = "✓ GitHub up to date";
                        lblGithub.ForeColor = Color.MediumSeaGreen;
                    }
                    if (res.LocalVersion != null)
                        lblDetails.Text = $"Version: {FormatVersion(res.LocalVersion)}    " + lblDetails.Text;
                });
            }
            catch (Exception ex)
            {
                Ui(() => { lblGithub.Text = "⚠ GitHub unreachable: " + ex.Message; lblGithub.ForeColor = Color.Goldenrod; });
            }
        }

        btnRefresh.Click += (_, _) => StartRefresh();
        btnUpdate.Click += async (_, _) =>
        {
            if (readOnly) return;
            btnUpdate.Enabled = false; btnRefresh.Enabled = false;
            var prog = new Progress<string>(m => Ui(() => { if (!lblProgress.IsDisposed) lblProgress.Text = m; }));
            try { await ExtDbDownloader.DownloadAndInstallAsync(prog, CancellationToken.None); }
            catch (Exception ex) { Ui(() => { if (!lblProgress.IsDisposed) lblProgress.Text = "Failed: " + ex.Message; }); }
            finally
            {
                Ui(() => { btnUpdate.Enabled = !readOnly; btnRefresh.Enabled = true; });
                StartRefresh();
            }
        };

        // ── Merge operations (Force re-merge / Undo / Unmerge → ExtDbMerger) ──────
        async void RunMergeOp(Func<IProgress<string>, ExtDbMerger.MergeResult> op)
        {
            if (readOnly) return;
            btnForce.Enabled = btnUndo.Enabled = btnUnmerge.Enabled = false;
            btnUpdate.Enabled = false; btnRefresh.Enabled = false;
            var prog = new Progress<string>(m => Ui(() => { if (!lblProgress.IsDisposed) lblProgress.Text = m; }));
            try
            {
                var res = await Task.Run(() => op(prog));
                Ui(() => { if (!lblProgress.IsDisposed) lblProgress.Text = res.Message; });
            }
            catch (Exception ex) { Ui(() => { if (!lblProgress.IsDisposed) lblProgress.Text = "Failed: " + ex.Message; }); }
            finally
            {
                Ui(() =>
                {
                    bool eng = File.Exists(ExtDbMerger.OwnExtendedDbPath);
                    btnForce.Enabled = btnUndo.Enabled = btnUnmerge.Enabled = !readOnly && eng;
                    btnUpdate.Enabled = !readOnly; btnRefresh.Enabled = true;
                });
                StartRefresh();
            }
        }
        btnForce.Click   += (_, _) => RunMergeOp(p => ExtDbMerger.RunMerge(p));
        btnUndo.Click    += (_, _) => RunMergeOp(p => ExtDbMerger.UndoLastMerge(p));
        btnUnmerge.Click += (_, _) => RunMergeOp(p => ExtDbMerger.Unmerge(p));

        // Kick off the first status read once the panel has a window handle to marshal onto.
        void OnHandle(object? _, EventArgs __) => StartRefresh();
        if (root.IsHandleCreated) StartRefresh();
        else root.HandleCreated += OnHandle;

        // ── Apply ───────────────────────────────────────────────────────────────
        void Apply()
        {
            if (readOnly) return;
            try
            {
                var c = LiteBoxConfig.LoadForExe();
                c.SetSec(Section, "RemoteImageBaseUrl", (mirror.Text ?? "").Trim());
                c.SetSec(Section, "OverviewSources", string.Join(",", list.Items.Cast<object>().Select(o => o.ToString())));
                c.SetSec(Section, "AutoUpdateDb", chkAuto.Checked ? "true" : "false");
                c.SetSec(Section, "EnableLbMerge", chkMerge.Checked ? "true" : "false");
                c.SetSec(Section, "EnableOverviewCache", chkOverview.Checked ? "true" : "false");
                c.SetSec(Section, "DuplicateHandling", DuplicateModes[Math.Max(0, cmbDup.SelectedIndex)]);
                c.Save();
            }
            catch { }
        }
        return (root, Apply);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Reads the persisted [Base] OverviewSources comma list, guaranteeing "Launchbox" is present
    /// (inserted at the front when absent) so the non-removable fallback always exists.</summary>
    private static List<string> LoadSources(LiteBoxConfig cfg)
    {
        var raw = cfg.GetSec(Section, "OverviewSources", "Launchbox") ?? "Launchbox";
        var items = raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (!items.Any(s => string.Equals(s, "Launchbox", OIC))) items.Insert(0, "Launchbox");
        return items;
    }

    private static int DuplicateIndex(string? value)
    {
        for (int i = 0; i < DuplicateModes.Length; i++)
            if (string.Equals(DuplicateModes[i], value, OIC)) return i;
        return 0;
    }

    /// <summary>The Extended-DB version is a compact UTC timestamp (yyyyMMddHHmmss); render it readably.</summary>
    private static string FormatVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "unknown";
        if (v.Length == 14 && DateTime.TryParseExact(v, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm");
        return v;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "-";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1L << 20) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1L << 30) return $"{bytes / (double)(1 << 20):F1} MB";
        return $"{bytes / (double)(1L << 30):F2} GB";
    }
}
