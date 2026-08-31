// The assignment screen: which ROM of a game each client is served.
//
// Only games that offer a CHOICE appear — one with several versions, or one whose archive the extractor
// actually takes apart. A game with a single playable file has nothing to assign and would be 2900 rows
// of noise on this install.
//
// The list is built from CHEAP signals only. Deciding that an archive really holds more than one entry
// means opening it, and doing that for a platform's worth of games would freeze the dialog on the first
// paint. So the grid shows candidates — additional applications (free, it is LaunchBox data) and archives
// the extractor handles (a config lookup and an extension test) — and the archive is opened only when a
// row is actually selected. That is also why the platform filter is not a convenience: it is what keeps
// the working set small enough to be honest about.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Romm;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Options;

internal static class RommAssignPanel
{
    /// <summary>Builds the Assignment tab. <paramref name="reloadClients"/> is called after a change so
    /// the Clients grid's lock count stays true.</summary>
    public static Control Build(float dpiS, bool readOnly, Action? reloadClients = null)
    {
        int S(int v) => ModulePanelKit.Sc(dpiS, v);
        var root = ModulePanelKit.Root(dpiS);

        var intro = ModulePanelKit.Caption(
            "A game is served to clients as ONE file. Games with several — versions, or a ROM archive the "
          + "extractor takes apart — are listed here; every other game has nothing to choose. A client is "
          + "pinned to a file the first time it downloads one, and you can move it here.", dpiS, maxWidth: 720);
        intro.Location = new Point(S(4), S(4));
        root.Controls.Add(intro);

        // ── Filters ───────────────────────────────────────────────────────────
        var lblPlat = ModulePanelKit.Caption("Platform:", dpiS);
        lblPlat.Location = new Point(S(4), S(52));
        root.Controls.Add(lblPlat);

        var cboPlatform = new ComboBox
        {
            Location = new Point(S(74), S(48)), Width = S(240),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f),
        };
        root.Controls.Add(cboPlatform);

        var lblFind = ModulePanelKit.Caption("Search:", dpiS);
        lblFind.Location = new Point(S(330), S(52));
        root.Controls.Add(lblFind);

        var txtFind = ModulePanelKit.TextField(dpiS, width: 220);
        txtFind.Location = new Point(S(390), S(48));
        root.Controls.Add(txtFind);

        // ── The games ─────────────────────────────────────────────────────────
        var grid = ModulePanelKit.Grid(dpiS, readOnly: true);
        grid.Location = new Point(S(4), S(84));
        grid.Size = new Size(S(700), S(260));
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add("title", "Game");
        grid.Columns.Add("kind", "Choice");
        grid.Columns.Add("assigned", "Clients pinned");
        grid.Columns[0].Width = S(380);
        grid.Columns[1].Width = S(120);
        grid.Columns[2].Width = S(180);
        root.Controls.Add(grid);

        var shown = new List<IGame>();

        // ── What each client is on, for the selected game ─────────────────────
        var lblWho = ModulePanelKit.Caption("Clients and the file each one gets:", dpiS);
        lblWho.Location = new Point(S(4), S(354));
        root.Controls.Add(lblWho);

        var who = ModulePanelKit.Grid(dpiS, readOnly: readOnly);
        who.Location = new Point(S(4), S(378));
        who.Size = new Size(S(700), S(170));
        who.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        who.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Client", ReadOnly = true, Width = S(220) });
        var colFile = new DataGridViewComboBoxColumn { HeaderText = "File served", Width = S(460), FlatStyle = FlatStyle.Flat };
        who.Columns.Add(colFile);
        root.Controls.Add(who);

        const string FollowDefault = "(default — whatever the server would pick)";
        var whoTokens = new List<int>();
        var whoFiles = new List<RommFile>();
        IGame? current = null;

        // RE-ENTRANCY GUARD, and it is not belt-and-braces: filling the client grid ASSIGNS cell values,
        // which raises CellValueChanged, whose handler refills the game grid and re-selects a row, which
        // raises SelectionChanged, which fills the client grid again. Each turn is a stack frame deeper
        // and the process dies on the guard page — a StackOverflowException that .NET cannot even report.
        //
        // Leaving the tab is enough to start it: the grid commits the cell being edited on the way out.
        bool busy = false;

        // ── Loading ───────────────────────────────────────────────────────────

        void FillPlatforms()
        {
            cboPlatform.Items.Clear();
            cboPlatform.Items.Add("(all platforms)");
            try
            {
                foreach (var p in RommLibrary.Platforms(null, null, ignorePins: true).OrderBy(p => p.LbName, StringComparer.OrdinalIgnoreCase))
                    cboPlatform.Items.Add(p.LbName);
            }
            catch { }
            cboPlatform.SelectedIndex = 0;
        }

        void FillGames()
        {
            grid.Rows.Clear();
            // Clearing and refilling re-raises selection; the guard below keeps that from coming back here.
            shown.Clear();
            try
            {
                string? plat = cboPlatform.SelectedIndex > 0 ? cboPlatform.SelectedItem as string : null;
                var term = txtFind.Text.Trim();

                // No platform and no search would mean testing the whole library. Ask for one or the
                // other rather than pretending to answer.
                if (plat == null && term.Length < 2)
                {
                    grid.Rows.Add("Pick a platform, or type at least two letters to search.", "", "");
                    return;
                }

                IEnumerable<IGame> games = plat != null
                    ? RommLibrary.GamesOf(plat, null, ignorePins: true)
                    : RommLibrary.Query(null, term, "name", "asc", null, ignorePins: true);

                if (plat != null && term.Length > 0)
                    games = games.Where(g => RommLibrary.TitleOf(g).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

                foreach (var g in games)
                {
                    if (!RommFiles.MayHaveChoice(g)) continue;      // cheap test only — see the header
                    var guid = RommLibrary.IdOf(g);
                    var pins = RommRoms.PinsOf(guid);
                    int pinned = pins.Sum(l => l.ClientIds.Count);
                    shown.Add(g);
                    grid.Rows.Add(
                        RommLibrary.TitleOf(g),
                        RommFiles.ExtractorHandles(g) ? "archive" : "versions",
                        pinned == 0 ? "—" : pinned.ToString(CultureInfo.InvariantCulture));
                    if (shown.Count >= 500) break;                  // a dialog, not a catalogue browser
                }
                if (shown.Count == 0) grid.Rows.Add("No game here offers a choice of file.", "", "");
            }
            catch (Exception ex) { grid.Rows.Add("Could not list: " + ex.Message, "", ""); }
        }

        void FillWho()
        {
            who.Rows.Clear();
            whoTokens.Clear();
            whoFiles.Clear();
            current = null;

            int i = grid.CurrentRow?.Index ?? -1;
            if (i < 0 || i >= shown.Count) return;
            current = shown[i];

            // NOW the archive is opened — one game, because somebody asked about it.
            whoFiles.AddRange(RommFiles.Candidates(current));
            if (whoFiles.Count == 0) return;

            var guid = RommLibrary.IdOf(current);
            var pins = RommRoms.PinsOf(guid);
            var names = new List<string> { FollowDefault };
            names.AddRange(whoFiles.Select(f => f.Label));

            List<RommClientToken> tokens;
            try { tokens = RommAuth.ListTokens(); } catch { tokens = new List<RommClientToken>(); }
            foreach (var t in tokens)
            {
                int cidx = RommRoms.ClientIndexOf(t.Id);
                var onFile = pins.FirstOrDefault(l => cidx > 0 && l.ClientIds.Contains(cidx));
                var chosen = onFile == null ? FollowDefault
                    : (whoFiles.FirstOrDefault(f => SamePath(f, onFile))?.Label
                       ?? System.IO.Path.GetFileName((onFile.RomPath.Length > 0 ? onFile.RomPath : onFile.FilePath)
                                                     .Replace('/', '\\')));

                int row = who.Rows.Add();
                whoTokens.Add(t.Id);
                who.Rows[row].Cells[0].Value = t.Name;

                // Each row gets its OWN item list: two games never share candidates. Reaching the cell
                // before assigning Value is what detaches it from the column's shared list.
                if (who.Rows[row].Cells[1] is DataGridViewComboBoxCell cell)
                {
                    cell.Items.Clear();
                    foreach (var n in names) cell.Items.Add(n);
                    cell.Value = names.Contains(chosen) ? chosen : FollowDefault;
                }
            }
            if (tokens.Count == 0)
                lblWho.Text = "No client is paired yet — pair one in the Clients tab first.";
            else
                lblWho.Text = "Clients and the file each one gets:";
        }

        // ── Wiring ────────────────────────────────────────────────────────────

        void Guarded(Action a)
        {
            if (busy) return;
            busy = true;
            try { a(); } finally { busy = false; }
        }

        cboPlatform.SelectedIndexChanged += (_, _) => Guarded(() => { FillGames(); FillWho(); });
        txtFind.TextChanged += (_, _) => Guarded(() => { FillGames(); FillWho(); });
        grid.SelectionChanged += (_, _) => Guarded(FillWho);

        // Commit a combo choice without having to leave the cell first.
        who.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (who.IsCurrentCellDirty) who.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        who.DataError += (_, e) => { e.ThrowException = false; };

        who.CellValueChanged += (_, e) =>
        {
            if (busy || readOnly || e.RowIndex < 0 || e.ColumnIndex != 1) return;
            if (current == null || e.RowIndex >= whoTokens.Count) return;
            busy = true;
            try
            {
            var label = who.Rows[e.RowIndex].Cells[1].Value?.ToString() ?? FollowDefault;
            int tokenId = whoTokens[e.RowIndex];
            try
            {
                if (label == FollowDefault) RommIndexer.UnpinClient(current, tokenId);
                else
                {
                    var f = whoFiles.FirstOrDefault(x => x.Label == label);
                    if (f != null)
                    {
                        var (path, rom) = SplitKey(current, f.Key);
                        RommIndexer.PinClient(current, tokenId,
                            f.Key.StartsWith("app:", StringComparison.Ordinal) ? f.Key.Substring(4) : "",
                            path, rom, rom.Length > 0);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change the assignment: " + ex.Message, "RomM server",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            int keep = grid.CurrentRow?.Index ?? -1;
            FillGames();
            if (keep >= 0 && keep < grid.Rows.Count) grid.Rows[keep].Selected = true;
            reloadClients?.Invoke();
            }
            finally { busy = false; }
        };

        // Une clé de candidat ("main", "app:{id}", "entry:{chemin}") vers le couple (chemin, rom) que
        // porte une ligne de romm_games.
        (string, string) SplitKey(IGame g, string key)
        {
            if (key.StartsWith("entry:", StringComparison.Ordinal))
                return (RommLibrary.AppPathOf(g), key.Substring("entry:".Length));
            if (key.StartsWith("app:", StringComparison.Ordinal))
            {
                var id = key.Substring(4);
                try
                {
                    foreach (var a in g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                        if (a != null && string.Equals(a.Id, id, StringComparison.Ordinal))
                            return (a.ApplicationPath ?? "", "");
                }
                catch { }
                return ("", "");
            }
            return (RommLibrary.AppPathOf(g), "");
        }

        bool SamePath(RommFile f, RommPinnedRow row)
        {
            var (p, r) = SplitKey(current!, f.Key);
            return string.Equals(p, row.FilePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r, row.RomPath, StringComparison.OrdinalIgnoreCase);
        }

        Guarded(() => { FillPlatforms(); FillGames(); });
        return root;
    }
}
