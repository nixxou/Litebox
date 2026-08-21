// Edit Game → Media → Image Query. A hybrid port of ExtendDB's batch Image Query, working for 1..N selected
// games. An in-memory SQLite TEMP table `GameImagesTmp` is (re)populated PER GAME from that game's disk
// images; the user writes a SQL SELECT (with the {SCORE_REGION}/{SCORE_TYPE}/@limit macros), previews the
// matches, and Execute copies/moves the top matches into a destination image type.
//
// Columns:
//   • Always (from disk, no ExtendDB needed — this is the standalone fallback): GameId, GameTitle, Platform,
//     TrueFileName, _DiskPath, Type, Region, FileNum, HasGuid, FileSize, SizeX, SizeY, Ratio. These already
//     fully IDENTIFY every file (folder→Type, sub-folder→Region, name→FileNum/HasGuid).
//   • Enriched ONLY when ExtendDB is loaded (via ImageInfoBridge → ImageInfoAds :info ADS): DatabaseId,
//     CRC32, Origin, Duplicate, FileType, NativeRegion, OriginalUrl. NULL otherwise — queries that use them
//     simply match nothing standalone.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Media;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private SqliteConnection? _imgqConn;
    private readonly Dictionary<string, (int x, int y)> _imgqDimCache = new(StringComparer.OrdinalIgnoreCase);

    private const string ImgqCreate =
        "CREATE TABLE GameImagesTmp (GameId TEXT, GameTitle TEXT, Platform TEXT, TrueFileName TEXT, _DiskPath TEXT," +
        " Type TEXT, Region TEXT, FileNum INTEGER, HasGuid INTEGER, FileSize INTEGER, SizeX INTEGER, SizeY INTEGER," +
        " Ratio REAL, DatabaseId INTEGER, CRC32 INTEGER, Origin TEXT, Duplicate INTEGER, FileType TEXT," +
        " NativeRegion TEXT, OriginalUrl TEXT);";

    private const string ImgqDefaultQuery =
@"SELECT *,
  {SCORE_REGION} AS ScoreRegion,
  {SCORE_TYPE}   AS ScoreType
FROM GameImagesTmp
ORDER BY (ScoreRegion * 1000 + ScoreType) ASC
LIMIT @limit";

    private struct ImgqMatch { public string DiskPath, Type, Region; }

    private SqliteConnection ImgqConn()
    {
        if (_imgqConn == null)
        {
            _imgqConn = new SqliteConnection("Data Source=:memory:");
            _imgqConn.Open();
        }
        return _imgqConn;
    }

    // ── Page ──────────────────────────────────────────────────────────────────
    private Control BuildImageQueryPage()
    {
        var page = new Panel { BackColor = Bg, AutoScroll = true, Dock = DockStyle.Fill };
        var inner = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Location = new Point(S(16), S(10)), Width = S(760), BackColor = Bg };
        page.Controls.Add(inner);

        var lblFont = new Font("Segoe UI", 9f);
        var headerFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        int y = 0;
        Label L(string t, Font f, Color c, int lx, int ly) { var l = new Label { Text = t, Font = f, ForeColor = c, BackColor = Bg, AutoSize = true, Location = new Point(S(lx), S(ly)) }; inner.Controls.Add(l); return l; }
        ComboBox Cbo(int lx, int ly, int w) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Font = lblFont, Width = S(w), Location = new Point(S(lx), S(ly)) }; inner.Controls.Add(c); return c; }

        L("Image Query — batch image operations", new Font("Segoe UI", 13f, FontStyle.Bold), Accent, 0, y); y += 30;
        L($"Applies to {_editGames.Count} game{(_editGames.Count > 1 ? "s" : "")}." + (ImageInfoBridge.Available ? "  (ExtendDB metadata columns available)" : "  (disk columns only — ExtendDB not loaded)"),
            new Font("Segoe UI", 8.5f, FontStyle.Italic), SubFg, 0, y); y += 26;

        // Advanced-users warning banner + Help button.
        var warn = new Panel { BackColor = Color.FromArgb(74, 30, 32), Location = new Point(0, S(y)), Size = new Size(S(740), S(28)), BorderStyle = BorderStyle.FixedSingle };
        var warnLbl = new Label { Text = "⚠  Advanced users only — batch SQL that copies/moves image files. Read Help first.", ForeColor = Color.FromArgb(255, 175, 175), BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(8), 0, 0, 0) };
        var btnHelp = new Button { Text = "❓  Help", ForeColor = Color.White, BackColor = Color.FromArgb(120, 50, 52), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Dock = DockStyle.Right, Width = S(84), Cursor = Cursors.Hand };
        btnHelp.FlatAppearance.BorderSize = 0;
        btnHelp.Click += (_, _) => ImgqShowHelp();
        warn.Controls.Add(warnLbl);      // Fill first …
        warn.Controls.Add(btnHelp);      // … Right after so it claims the right edge
        inner.Controls.Add(warn); y += 36;

        L("Source (filter images from):", headerFont, Fg, 0, y); y += 20;
        var cmbSource = Cbo(16, y, 350);
        cmbSource.Items.Add("(all images on disk)");
        foreach (var kvp in LbApiHost.Host.Gc.SettingsWatcher.GetImageRegroupementPriorities())
        {
            cmbSource.Items.Add($"[Slot] {kvp.Key}");
            foreach (var t in kvp.Value) cmbSource.Items.Add($"  {t}");
        }
        cmbSource.SelectedIndex = 0; y += 30;

        L("Destination image type:", headerFont, Fg, 0, y); y += 20;
        var cmbDest = Cbo(16, y, 350);
        foreach (var t in MediaResolver.ImageTypeNames()) cmbDest.Items.Add(t);
        if (cmbDest.Items.Count > 0) cmbDest.SelectedIndex = 0; y += 34;

        L("Mode:", headerFont, Fg, 0, y);
        var cmbMode = Cbo(50, y, 80); cmbMode.Items.AddRange(new object[] { "Copy", "Move" }); cmbMode.SelectedIndex = 0;
        L("Limit per game:", lblFont, Fg, 150, y + 2);
        var nudLimit = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 1, BackColor = Field, ForeColor = Fg, Font = lblFont, Width = S(55), Location = new Point(S(260), S(y)) };
        inner.Controls.Add(nudLimit); y += 30;

        var chkGuid = new CheckBox { Text = "Use GUID naming", ForeColor = Fg, BackColor = Bg, Font = lblFont, AutoSize = true, Location = new Point(0, S(y)), Checked = true };
        var chkReplace = new CheckBox { Text = "Replace #01 (overwrite)", ForeColor = Fg, BackColor = Bg, Font = lblFont, AutoSize = true, Location = new Point(S(160), S(y)) };
        inner.Controls.Add(chkGuid); inner.Controls.Add(chkReplace);
        CheckBox? chkLock = null;
        if (ImageLockBridge.Available)
        {
            chkLock = new CheckBox { Text = "Lock destination", ForeColor = Fg, BackColor = Bg, Font = lblFont, AutoSize = true, Location = new Point(S(360), S(y)), Checked = true };
            inner.Controls.Add(chkLock);
        }
        y += 28;

        L("SQL query  ({SCORE_REGION}, {SCORE_TYPE}, @limit):", headerFont, Fg, 0, y); y += 20;
        var txtSql = new TextBox
        {
            // Normalise to CRLF — a multiline TextBox only renders \r\n as line breaks (the verbatim const is LF).
            Text = ImgqDefaultQuery.Replace("\r\n", "\n").Replace("\n", "\r\n"), Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            BackColor = Color.FromArgb(32, 32, 40), ForeColor = Color.FromArgb(180, 220, 180),
            Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(S(740), S(150)), Location = new Point(0, S(y)), AcceptsReturn = true, AcceptsTab = true,
        };
        inner.Controls.Add(txtSql); y += 156;

        var lblValid = new Label { Text = "", Font = new Font("Segoe UI", 11f), AutoSize = true, BackColor = Bg, Location = new Point(0, S(y + 1)) };
        inner.Controls.Add(lblValid); y += 26;

        // Preview game picker (multi only) + preview button.
        ComboBox? cmbGame = null;
        if (_editGames.Count > 1)
        {
            L("Preview game:", lblFont, Fg, 0, y + 4);
            cmbGame = Cbo(96, y, 300);
            foreach (var g in _editGames) cmbGame.Items.Add(Safe(() => g.Title) ?? "(untitled)");
            cmbGame.SelectedIndex = 0;
        }
        var btnPreview = DlgBtn("Preview", Color.FromArgb(40, 70, 90)); btnPreview.AutoSize = false; btnPreview.SetBounds(S(_editGames.Count > 1 ? 410 : 0), S(y), S(100), S(26));
        inner.Controls.Add(btnPreview);
        var lblInfo = new Label { Text = "", ForeColor = SubFg, BackColor = Bg, Font = lblFont, AutoSize = true, Location = new Point(S(_editGames.Count > 1 ? 520 : 110), S(y + 4)) };
        inner.Controls.Add(lblInfo); y += 32;

        var previewBar = new FlowLayoutPanel { Location = new Point(0, S(y)), Size = new Size(S(740), S(120)), BackColor = Color.FromArgb(28, 28, 36), WrapContents = false, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle };
        inner.Controls.Add(previewBar); y += 128;

        var btnExec = new Button
        {
            Text = $"Execute on {_editGames.Count} game{(_editGames.Count > 1 ? "s" : "")}", ForeColor = Accent,
            BackColor = Color.FromArgb(30, 50, 60), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Size = new Size(S(300), S(36)), Location = new Point(0, S(y)), Cursor = Cursors.Hand, Enabled = !_readOnly,
        };
        btnExec.FlatAppearance.BorderColor = Accent;
        inner.Controls.Add(btnExec);
        var lblProgress = new Label { Text = "", ForeColor = SubFg, BackColor = Bg, Font = lblFont, AutoSize = true, Location = new Point(S(312), S(y + 8)) };
        inner.Controls.Add(lblProgress);

        // ── Behaviour ──
        int SlotIndexOfLimit() => (int)nudLimit.Value;
        string Resolve(string sql) => ImgqResolveMacros(sql, cmbSource.SelectedItem?.ToString() ?? "", SlotIndexOfLimit());

        void Validate()
        {
            try
            {
                var conn = ImgqConn();
                ImgqPopulate(conn, _editGames[0], cmbSource.SelectedItem?.ToString() ?? "");   // schema exists (rows maybe 0)
                using var cmd = conn.CreateCommand();
                cmd.CommandText = Resolve(txtSql.Text.Trim());
                using var r = cmd.ExecuteReader();
                lblValid.Text = "✅ valid"; lblValid.ForeColor = Color.FromArgb(80, 200, 80);
            }
            catch (Exception ex) { lblValid.Text = "❌ " + ex.Message; lblValid.ForeColor = Color.FromArgb(210, 90, 80); }
        }
        txtSql.TextChanged += (_, _) => Validate();
        cmbSource.SelectedIndexChanged += (_, _) => Validate();
        Validate();

        btnPreview.Click += (_, _) =>
        {
            foreach (Control c in previewBar.Controls) if (c is PictureBox p) { var im = p.Image; p.Image = null; try { im?.Dispose(); } catch { } }   // detach BEFORE dispose (else PictureBox.Animate hits a dead image → GDI+ crash)
            previewBar.Controls.Clear();
            var g = _editGames[cmbGame?.SelectedIndex ?? 0];
            List<ImgqMatch> matches;
            try { matches = ImgqRun(g, cmbSource.SelectedItem?.ToString() ?? "", Resolve(txtSql.Text.Trim())); }
            catch (Exception ex) { lblInfo.Text = "error: " + ex.Message; return; }
            lblInfo.Text = $"{matches.Count} match(es)";
            int shown = 0;
            foreach (var m in matches)
            {
                if (shown >= 8 || !File.Exists(m.DiskPath)) continue;
                var pb = new PictureBox { Size = new Size(S(96), S(100)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(32, 32, 42), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(S(4)) };
                try { using var ms = new MemoryStream(File.ReadAllBytes(m.DiskPath)); using var tmp = Image.FromStream(ms); pb.Image = new Bitmap(tmp); } catch { }
                new ToolTip().SetToolTip(pb, $"{m.Type}\n{(string.IsNullOrEmpty(m.Region) ? "No Region" : m.Region)}\n{Path.GetFileName(m.DiskPath)}");
                previewBar.Controls.Add(pb); shown++;
            }
        };

        btnExec.Click += (_, _) =>
        {
            if (_readOnly || cmbDest.SelectedItem == null) return;
            foreach (Control c in previewBar.Controls) if (c is PictureBox p) { var im = p.Image; p.Image = null; try { im?.Dispose(); } catch { } }   // detach BEFORE dispose (else PictureBox.Animate hits a dead image → GDI+ crash)
            previewBar.Controls.Clear(); lblInfo.Text = "";
            string destType = cmbDest.SelectedItem.ToString()!;
            bool copy = cmbMode.SelectedIndex == 0;
            bool useGuid = chkGuid.Checked, replace = chkReplace.Checked, doLock = chkLock?.Checked == true;
            int done = 0, games = 0, fail = 0;
            string sqlResolved = Resolve(txtSql.Text.Trim());
            foreach (var g in _editGames)
            {
                games++;
                List<ImgqMatch> matches;
                try { matches = ImgqRun(g, cmbSource.SelectedItem?.ToString() ?? "", sqlResolved); } catch { fail++; continue; }
                foreach (var m in matches)
                    if (ImgqApply(g, m, destType, copy, useGuid, replace, doLock)) done++; else fail++;
            }
            lblProgress.Text = $"{done} image(s) written across {games} game(s)" + (fail > 0 ? $", {fail} failed/skipped" : "");
        };

        return page;
    }

    // ── Macros ────────────────────────────────────────────────────────────────
    private static string ImgqResolveMacros(string sql, string sourceSel, int limit)
    {
        string scoreRegion, scoreType;
        var regions = LbApiHost.Host.Gc.SettingsWatcher.GetRegionPriorities();
        if (regions.Count == 0) scoreRegion = "0";
        else { var sb = new System.Text.StringBuilder("CASE Region"); for (int i = 0; i < regions.Count; i++) sb.Append($" WHEN '{regions[i].Replace("'", "''")}' THEN {i + 1}"); sb.Append($" ELSE {regions.Count + 1} END"); scoreRegion = sb.ToString(); }

        List<string>? typeList = null;
        if (sourceSel.StartsWith("[Slot] ") && LbApiHost.Host.Gc.SettingsWatcher.GetImageRegroupementPriorities().TryGetValue(sourceSel.Substring(7), out var ts)) typeList = ts;
        typeList ??= LbApiHost.Host.Gc.SettingsWatcher.GetImageRegroupementPriorities().Values.FirstOrDefault();
        if (typeList == null || typeList.Count == 0) scoreType = "0";
        else { var sb = new System.Text.StringBuilder("CASE Type"); for (int i = 0; i < typeList.Count; i++) sb.Append($" WHEN '{typeList[i].Replace("'", "''")}' THEN {i + 1}"); sb.Append($" ELSE {typeList.Count + 1} END"); scoreType = sb.ToString(); }

        return sql.Replace("{SCORE_REGION}", scoreRegion).Replace("{SCORE_TYPE}", scoreType).Replace("@limit", limit.ToString());
    }

    // ── Populate + run ────────────────────────────────────────────────────────
    private void ImgqPopulate(SqliteConnection conn, IGame g, string sourceSel)
    {
        using (var drop = conn.CreateCommand()) { drop.CommandText = "DROP TABLE IF EXISTS GameImagesTmp;" + ImgqCreate; drop.ExecuteNonQuery(); }

        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        string idLower = idStr.ToLowerInvariant();
        string title = Safe(() => g.Title) ?? "";
        Guid.TryParse(idStr, out var id);

        // Source filter → the set of allowed types ("" = all).
        HashSet<string>? allow = null;
        if (sourceSel.StartsWith("[Slot] ") && LbApiHost.Host.Gc.SettingsWatcher.GetImageRegroupementPriorities().TryGetValue(sourceSel.Substring(7), out var slotTypes))
            allow = new HashSet<string>(slotTypes, StringComparer.OrdinalIgnoreCase);
        else if (sourceSel.StartsWith("  "))
            allow = new HashSet<string>(new[] { sourceSel.Trim() }, StringComparer.OrdinalIgnoreCase);

        List<(string path, string type, string region)> files;
        try { files = MediaResolver.AllImageFiles(plat, id, title); } catch { files = new(); }

        using var tx = conn.BeginTransaction();
        using var ins = conn.CreateCommand();
        ins.CommandText =
            "INSERT INTO GameImagesTmp VALUES ($gid,$gt,$pl,$fn,$dp,$ty,$rg,$num,$hg,$fs,$sx,$sy,$ra,$dbid,$crc,$or,$dup,$ft,$nr,$url)";
        foreach (var pn in new[] { "$gid", "$gt", "$pl", "$fn", "$dp", "$ty", "$rg", "$num", "$hg", "$fs", "$sx", "$sy", "$ra", "$dbid", "$crc", "$or", "$dup", "$ft", "$nr", "$url" })
            ins.Parameters.Add(new SqliteParameter(pn, DBNull.Value));

        foreach (var (path, type, region) in files)
        {
            if (allow != null && !allow.Contains(type)) continue;
            string name = Path.GetFileNameWithoutExtension(path);
            string nl = name.ToLowerInvariant();
            bool hasGuid = !string.IsNullOrEmpty(idLower) && nl.Contains($".{idLower}-");
            int numVal = 0; int dash = nl.LastIndexOf('-');
            if (dash >= 0 && dash < nl.Length - 1) int.TryParse(nl.Substring(dash + 1), out numVal);
            long fsize = 0; try { fsize = new FileInfo(path).Length; } catch { }
            var (sx, sy) = ImgqDims(path);
            double ratio = sy > 0 ? (double)sx / sy : 0;

            object dbId = DBNull.Value, crc = DBNull.Value, origin = DBNull.Value, dup = DBNull.Value, ft = DBNull.Value, nr = DBNull.Value, url = DBNull.Value;
            var info = ImageInfoBridge.Read(path);
            if (info.HasValue)
            {
                var v = info.Value;
                dbId = v.DatabaseId; crc = v.Crc32; origin = v.Origin; dup = v.Duplicate; ft = v.FileType; nr = v.NativeRegion; url = v.OriginalUrl;
                if (sx == 0 && v.SizeX > 0) { sx = v.SizeX; sy = v.SizeY; ratio = sy > 0 ? (double)sx / sy : 0; }
                if (fsize == 0 && v.FileSize > 0) fsize = v.FileSize;
            }

            ins.Parameters["$gid"].Value = idStr; ins.Parameters["$gt"].Value = title; ins.Parameters["$pl"].Value = plat;
            ins.Parameters["$fn"].Value = Path.GetFileName(path); ins.Parameters["$dp"].Value = path;
            ins.Parameters["$ty"].Value = type; ins.Parameters["$rg"].Value = region ?? "";
            ins.Parameters["$num"].Value = numVal; ins.Parameters["$hg"].Value = hasGuid ? 1 : 0; ins.Parameters["$fs"].Value = fsize;
            ins.Parameters["$sx"].Value = sx; ins.Parameters["$sy"].Value = sy; ins.Parameters["$ra"].Value = ratio;
            ins.Parameters["$dbid"].Value = dbId; ins.Parameters["$crc"].Value = crc; ins.Parameters["$or"].Value = origin;
            ins.Parameters["$dup"].Value = dup; ins.Parameters["$ft"].Value = ft; ins.Parameters["$nr"].Value = nr; ins.Parameters["$url"].Value = url;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private List<ImgqMatch> ImgqRun(IGame g, string sourceSel, string sqlResolved)
    {
        var conn = ImgqConn();
        ImgqPopulate(conn, g, sourceSel);
        var result = new List<ImgqMatch>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlResolved;
        using var r = cmd.ExecuteReader();
        int iPath = -1, iType = -1, iReg = -1;
        for (int i = 0; i < r.FieldCount; i++)
        {
            string nm = r.GetName(i);
            if (nm.Equals("_DiskPath", StringComparison.OrdinalIgnoreCase)) iPath = i;
            else if (nm.Equals("Type", StringComparison.OrdinalIgnoreCase)) iType = i;
            else if (nm.Equals("Region", StringComparison.OrdinalIgnoreCase)) iReg = i;
        }
        if (iPath < 0) return result;   // the query must keep _DiskPath (SELECT * does)
        while (r.Read())
        {
            string p = r.IsDBNull(iPath) ? "" : r.GetString(iPath);
            if (string.IsNullOrEmpty(p)) continue;
            result.Add(new ImgqMatch { DiskPath = p, Type = iType >= 0 && !r.IsDBNull(iType) ? r.GetString(iType) : "", Region = iReg >= 0 && !r.IsDBNull(iReg) ? r.GetString(iReg) : "" });
        }
        return result;
    }

    private bool ImgqApply(IGame g, ImgqMatch m, string destType, bool copy, bool useGuid, bool replace, bool doLock)
    {
        try
        {
            if (!File.Exists(m.DiskPath)) return false;
            string plat = Safe(() => g.Platform) ?? "";
            string idStr = Safe(() => g.Id) ?? "";
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string? baseFolder = MediaResolver.TypeFolder(plat, destType);
            if (string.IsNullOrEmpty(baseFolder)) return false;
            Directory.CreateDirectory(baseFolder);
            string prefix = useGuid ? $"{sani}.{idStr}" : sani;
            string ext = Path.GetExtension(m.DiskPath);
            int num = replace ? 1 : ImgMaxNum(baseFolder, prefix) + 1;
            string target = Path.Combine(baseFolder, $"{prefix}-{num:D2}{ext}");
            if (replace && File.Exists(target) && ImageLockBridge.Available) ImageLockBridge.Unlock(target);   // ExtendDB auto-unlocks on overwrite
            if (copy) File.Copy(m.DiskPath, target, overwrite: replace);
            else File.Move(m.DiskPath, target, overwrite: replace);
            if (doLock && ImageLockBridge.Available) ImageLockBridge.Lock(target);
            // Both ends: the source can be another game's file — another PLATFORM's, even — and a move
            // takes it away from whoever had it. ImgCacheTouch resolves each path on its own.
            ImgCacheTouch(plat, target, copy ? null : m.DiskPath);
            return true;
        }
        catch { return false; }
    }

    private (int x, int y) ImgqDims(string path)
    {
        if (_imgqDimCache.TryGetValue(path, out var d)) return d;
        (int, int) r = (0, 0);
        try { using var ms = new MemoryStream(File.ReadAllBytes(path)); using var img = Image.FromStream(ms); r = (img.Width, img.Height); } catch { }
        _imgqDimCache[path] = r;
        return r;
    }

    // ── Help modal ──────────────────────────────────────────────────────────────
    private void ImgqShowHelp()
    {
        using var f = NewDialog("Image Query — Help", 760, 600);
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
            BackColor = Color.FromArgb(28, 28, 36), ForeColor = Color.FromArgb(212, 218, 228),
            Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(S(12), S(12)), Size = new Size(S(716), S(500)),
            Text = ImgqHelpText().Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        box.Select(0, 0);
        f.Controls.Add(box);
        var close = DlgBtn("Close", Color.FromArgb(45, 95, 60)); close.AutoSize = false; close.SetBounds(S(628), S(522), S(100), S(30));
        close.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        f.AcceptButton = close;
        f.Controls.Add(close);
        f.ShowDialog(this);
    }

    private static string ImgqHelpText() =>
@"IMAGE QUERY — batch image operations
====================================

WHAT IT DOES
For each selected game, the tool builds an in-memory table (GameImagesTmp) of
that game's images found on disk, runs YOUR SQL SELECT against it, then COPIES
or MOVES the top matches into the destination image type you choose. Use
Preview first — nothing is written until you press Execute.

THE FLOW
  1. Source        — restrict the input to one slot/type, or all images on disk.
  2. Write a SELECT — return the rows (files) you want, best first.
  3. Destination   — the image type the matches are written to.
  4. Mode / Limit / options — Copy or Move, how many per game, naming, etc.
  5. Preview, then Execute (runs on EVERY selected game).

COLUMNS YOU CAN QUERY (table GameImagesTmp)
  Always (read from disk, no ExtendDB needed):
    GameId, GameTitle, Platform, TrueFileName, _DiskPath,
    Type, Region, FileNum, HasGuid, FileSize, SizeX, SizeY, Ratio
  Only when ExtendDB is loaded (from each file's :info ADS):
    DatabaseId, CRC32, Origin, Duplicate, FileType, NativeRegion, OriginalUrl
    (NULL without ExtendDB — queries using them then match nothing.)

  IMPORTANT: keep _DiskPath in the result (SELECT * does). It is the file the
  tool actually copies/moves; a query that drops it matches nothing.

MACROS (expanded automatically before the query runs)
  {SCORE_REGION}  a number scoring each row by YOUR region-priority list
                  (1 = your #1 region, higher = lower priority). Lower is better.
  {SCORE_TYPE}    same idea, by the image-type order of the selected Source slot
                  (1 = first type in the slot).
  @limit          the ""Limit per game"" value.

SIMPLE EXAMPLES
  • Best one per region + type (the default):
      SELECT *, {SCORE_REGION} AS r, {SCORE_TYPE} AS t
      FROM GameImagesTmp
      ORDER BY r*1000 + t ASC
      LIMIT @limit

  • Highest-resolution image first:
      SELECT * FROM GameImagesTmp
      ORDER BY SizeX*SizeY DESC
      LIMIT @limit

  • Only images from one region:
      SELECT * FROM GameImagesTmp
      WHERE Region = 'Japan'
      LIMIT @limit

  • Widest images first (banners / marquees):
      SELECT * FROM GameImagesTmp
      ORDER BY Ratio DESC
      LIMIT @limit

  • ExtendDB only — prefer a source, then region:
      SELECT * FROM GameImagesTmp
      WHERE Origin = 'screenscraper'
      ORDER BY {SCORE_REGION} ASC
      LIMIT @limit

USE CASES
  • Across a whole multi-selection, promote the single best box art into
    ""Box - Front"" in one shot.
  • Move the largest screenshot into a chosen slot.
  • Consolidate art from a preferred source (Origin) or region.
  • Re-home / re-number images by copying with GUID naming.

SAFETY
  • Preview shows the matches without touching anything.
  • Move DELETES the source file; Copy leaves it.
  • ""Replace #01"" overwrites the destination's first image (and unlocks it if
    ExtendDB had locked it).
  • Execute runs on EVERY selected game — check the match count first.
";
}
