// Offscreen render probe for the Edit Platform window — for autonomous visual iteration. Loads a platform from
// Platforms.xml, builds each editor section, and DrawToBitmap-s it to a PNG (no ShowDialog → a single lightweight
// offscreen window, well under the agent-shell desktop-heap handle cap). Flag: --edit-platform-render <platform>
// <outDir>. The PNGs are then read back to check the rendering against LaunchBox.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformRenderProbe
{
    public static void Render(string lbRoot, string platformName, string outDir)
    {
        try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); } catch { }
        try { MediaResolver.Init(lbRoot); } catch { }

        var (plats, _) = PlatformCatalog.Load(Path.Combine(lbRoot, "Data"), Path.Combine(lbRoot, "Images"));
        var plat = plats.FirstOrDefault(p => string.Equals(p.Name, platformName, StringComparison.OrdinalIgnoreCase)) ?? plats.FirstOrDefault();
        if (plat == null) { Console.WriteLine("[render] no platform found (" + platformName + ")"); return; }
        Console.WriteLine("[render] platform = " + plat.Name);
        Directory.CreateDirectory(outDir);

        var sections = EditPlatformWindow.BuildSectionsForRender(plat, lbRoot, 1f);
        foreach (var (title, ctrl) in sections)
        {
            // For the 3D tab, render one PNG per Model Type (verify the morph); otherwise a single render.
            var typeCombo = title.StartsWith("3D") ? FindModelTypeCombo(ctrl) : null;
            if (typeCombo != null && typeCombo.Items.Count > 0)
            {
                for (int ti = 0; ti < typeCombo.Items.Count; ti++)
                    Shot(ctrl, outDir, $"{title} - {typeCombo.Items[ti]}", () => typeCombo.SelectedIndex = ti);
                // Extra: Jewel Case + preset spine (reveals Spine Version) and + custom spine (reveals path row).
                Shot(ctrl, outDir, $"{title} - JewelCase Preset Spine", () =>
                {
                    int jc = typeCombo.Items.IndexOf("Jewel Case"); if (jc >= 0) typeCombo.SelectedIndex = jc;
                    var style = FindSpineStyleCombo(ctrl);
                    if (style != null) { int pi = style.Items.IndexOf("Sony Playstation Spine"); if (pi >= 0) style.SelectedIndex = pi; }
                });
                Shot(ctrl, outDir, $"{title} - JewelCase Custom Spine", () =>
                {
                    var style = FindSpineStyleCombo(ctrl);
                    if (style != null) { int ci = style.Items.IndexOf("Custom Solid Spine"); if (ci >= 0) style.SelectedIndex = ci; }
                });
            }
            else
                Shot(ctrl, outDir, title, null);
        }
        Console.WriteLine("[render] done");
    }

    /// <summary>Offscreen render of the Edit Platform Category sections (flag --edit-category-render).</summary>
    public static void RenderCategory(string lbRoot, string categoryName, string outDir)
    {
        try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); } catch { }
        try { MediaResolver.Init(lbRoot); } catch { }
        var (_, cats) = PlatformCatalog.Load(Path.Combine(lbRoot, "Data"), Path.Combine(lbRoot, "Images"));
        var cat = cats.FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase)) ?? cats.FirstOrDefault();
        if (cat == null) { Console.WriteLine("[render] no category found (" + categoryName + ")"); return; }
        Console.WriteLine("[render] category = " + cat.Name);
        Directory.CreateDirectory(outDir);
        foreach (var (title, ctrl) in EditCategoryWindow.BuildSectionsForRender(cat, 1f))
            Shot(ctrl, outDir, "Cat " + title, null);
        Console.WriteLine("[render] done");
    }

    /// <summary>Offscreen render of all Edit Playlist sections (flag --edit-playlist-render).</summary>
    public static void RenderPlaylist(string lbRoot, string playlistName, string outDir)
    {
        try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); } catch { }
        try { MediaResolver.Init(lbRoot); } catch { }
        var playlists = PlaylistCatalog.Load(Path.Combine(lbRoot, "Data"), Path.Combine(lbRoot, "Images"));
        var playlist = playlists.FirstOrDefault(p => string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase))
                       ?? playlists.FirstOrDefault();
        if (playlist == null) { Console.WriteLine("[render] no playlist found (" + playlistName + ")"); return; }
        Console.WriteLine("[render] playlist = " + playlist.Name);
        Directory.CreateDirectory(outDir);
        foreach (var (title, ctrl) in EditPlaylistWindow.BuildSectionsForRender(playlist, 1f))
            Shot(ctrl, outDir, title, null);

        // Multi-selection: the two sections that exist only for a selection. Rendered over the
        // first real playlists, which is the only way to see the merges actually land.
        var selection = playlists.Take(3).ToList();
        if (selection.Count >= 2)
        {
            Console.WriteLine("[render] multi = " + string.Join(", ", selection.Select(p => p.Name)));
            try { Shot(EditPlaylistPopulate.BuildAutoPopulateMulti(selection, false, 1f).panel, outDir, "Multi Auto-Populate", null); }
            catch (Exception ex) { Console.WriteLine("[render] Multi Auto-Populate: " + ex.Message); }
            // Games over MANUAL playlists when the library has two: that is the path where Delete
            // is live and the hidden-count label appears. Falls back to the same selection.
            var manual = playlists.Where(p => !p.AutoPopulateValue).Take(3).ToList();
            var forGames = manual.Count >= 2 ? manual : selection;
            Console.WriteLine("[render] multi games = " + string.Join(", ", forGames.Select(p => p.Name)));
            try { Shot(EditPlaylistPopulate.BuildGamesMulti(forGames, false, 1f).panel, outDir, "Multi Games", null); }
            catch (Exception ex) { Console.WriteLine("[render] Multi Games: " + ex.Message); }
        }
        Console.WriteLine("[render] done");
    }

    /// <summary>--model3d-live &lt;platform&gt; &lt;out.png&gt;: host the REAL 3D Model Settings panel (with the live
    /// FlowModel preview) in a visible offscreen form, pump the message loop, screenshot via CopyFromScreen
    /// (DrawToBitmap can't capture WPF/ElementHost). Toggles Override on so the preview shows the case.</summary>
    public static void RenderLive(string lbRoot, string platformName, string outDir)
    {
        // WPF (the hosted FlowModel) requires STA; the diag-flag handler thread is MTA → run on an STA thread.
        var t = new System.Threading.Thread(() => RenderLiveSta(lbRoot, platformName, outDir));
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start(); t.Join(System.TimeSpan.FromSeconds(30));
    }

    private static void RenderLiveSta(string lbRoot, string platformName, string outDir)
    {
        try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); } catch { }
        try { if (System.Windows.Application.Current == null) _ = new System.Windows.Application(); } catch { }
        try { MediaResolver.Init(lbRoot); } catch { }
        var (plats, _) = PlatformCatalog.Load(Path.Combine(lbRoot, "Data"), Path.Combine(lbRoot, "Images"));
        var plat = plats.FirstOrDefault(p => string.Equals(p.Name, platformName, StringComparison.OrdinalIgnoreCase)) ?? plats.FirstOrDefault();
        if (plat == null) { Console.WriteLine("[live] no platform"); return; }
        Directory.CreateDirectory(outDir);

        // LB_GAME_MODE=1 → build the EDIT GAME variant of the panel (BuildForGame) instead of the platform one,
        // with LB_SAMPLE_TITLE as the game (reproduces game-window-specific layout/preview differences).
        // The Guid here is a PLACEHOLDER: this probe runs before HostBoot installs the catalogue, so there is
        // no game to name — it only needs a key to store an override under. EditPlatformModel.PreviewIdOf
        // rejects it for art resolution (an id no game answers to resolves to nothing), leaving the preview
        // to match LB_SAMPLE_TITLE by filename, which is all this probe can do without a catalogue.
        var (panel, _) = Environment.GetEnvironmentVariable("LB_GAME_MODE") == "1"
            ? EditPlatformModel.BuildForGame(plat.Name ?? platformName, Guid.NewGuid().ToString(), false, 1f, null,
                                             Environment.GetEnvironmentVariable("LB_SAMPLE_TITLE") ?? "")
            : EditPlatformModel.Build(plat, false, 1f);
        // LB_FORM_W/LB_FORM_H: bigger window = bigger preview zones in the screenshot (the A/B
        // comparisons need more than ~300px per zone to judge materials).
        int fw = int.TryParse(Environment.GetEnvironmentVariable("LB_FORM_W"), out var fwv) ? fwv : 1000;
        int fh = int.TryParse(Environment.GetEnvironmentVariable("LB_FORM_H"), out var fhv) ? fhv : 700;
        var form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(80, 80), Size = new System.Drawing.Size(fw, fh), BackColor = LiteBoxTheme.Bg, ShowInTaskbar = false, TopMost = true };
        panel.Dock = DockStyle.Fill; form.Controls.Add(panel);
        form.Shown += async (_, _) =>
        {
            form.Activate(); form.BringToFront();
            // Turn Override on so a case renders (find the checkbox by text). LB_NO_OVERRIDE=1 keeps it off —
            // for comparing the no-override (LB native default) state against the override-on state.
            var chk = FindOverride(panel);
            if (chk != null && Environment.GetEnvironmentVariable("LB_NO_OVERRIDE") != "1") chk.Checked = true;
            // Optional: force a Model Type (env LB_MODELTYPE = Box/DVD Case/Jewel Case/...) to dump/compare it.
            var forceType = Environment.GetEnvironmentVariable("LB_MODELTYPE");
            if (!string.IsNullOrEmpty(forceType))
            {
                var mt = FindModelTypeCombo(panel);
                if (mt != null) { int ix = mt.Items.IndexOf(forceType); if (ix >= 0) mt.SelectedIndex = ix; }
            }
            // LB_CHECK="text|text…": tick checkboxes by (prefix of) their label AFTER the type force — to
            // drive per-type rows (e.g. "Use Plain Text Title") through the REAL control path.
            var forceChecks = Environment.GetEnvironmentVariable("LB_CHECK");
            if (!string.IsNullOrEmpty(forceChecks))
                foreach (var t in forceChecks.Split('|'))
                { var c = FindCheckBox(panel, t.Trim()); if (c != null) c.Checked = true; else Console.WriteLine($"[live] LB_CHECK: no checkbox '{t}'"); }
            for (int i = 0; i < 40; i++) { Application.DoEvents(); await System.Threading.Tasks.Task.Delay(60); }
            // Optionally drive the shared orbit (env LB_ORBIT_TEST=1) to verify both zones move in sync.
            // LB_ORBIT_YAW / LB_ORBIT_PITCH override the angles (degrees) to aim at a specific face.
            if (Environment.GetEnvironmentVariable("LB_ORBIT_TEST") == "1")
            {
                double yaw = double.TryParse(Environment.GetEnvironmentVariable("LB_ORBIT_YAW"), out var oy) ? oy : 55;
                double pitch = double.TryParse(Environment.GetEnvironmentVariable("LB_ORBIT_PITCH"), out var op) ? op : 18;
                try { EditPlatformModel.LastOrbit?.Orbit(yaw, pitch); EditPlatformModel.LastOrbit?.Zoom(120); } catch { }
                // The orbit only drives OUR zone; rotate the ORACLE zone by the same 7.5°-units so
                // an A/B screenshot compares the same face (FlowModel.RotateModel: left/up).
                try { EditPlatformModel.LastOracle?.Rotate(yaw, 0, pitch, 0); } catch { }
            }
            for (int i = 0; i < 10; i++) { Application.DoEvents(); await System.Threading.Tasks.Task.Delay(60); }
            Application.DoEvents();
            // LB_ORACLE_DUMP=1 → print the ORACLE zone's built geometry (quads/materials, ground truth).
            // Here and not in the headless probe because FlowModel only builds inside a real window.
            if (Environment.GetEnvironmentVariable("LB_ORACLE_DUMP") == "1")
            {
                for (int i = 0; i < 100 && EditPlatformModel.LastOracle?.BuiltGeometry() == null; i++)
                { Application.DoEvents(); await System.Threading.Tasks.Task.Delay(100); }
                Tools.JewelRenderProbe.DumpStructure(EditPlatformModel.LastOracle?.BuiltGeometry());
            }
            try { Console.WriteLine("[live] oracle bounds: " + EditPlatformModel.LastOracle?.ModelBounds()); } catch { }
            try
            {
                using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    // PW_RENDERFULLCONTENT (0x2) captures WPF/DirectX airspace of the HWND without foreground.
                    bool ok = PrintWindow(form.Handle, hdc, 0x2);
                    g.ReleaseHdc(hdc);
                    if (!ok) Console.WriteLine("[live] PrintWindow returned false");
                }
                bmp.Save(Path.Combine(outDir, "3D-live-" + San(plat.Name) + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("[live] wrote screenshot");
            }
            catch (Exception ex) { Console.WriteLine("[live] shot: " + ex.Message); }
            form.Close();
        };
        Application.Run(form);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private static CheckBox? FindOverride(Control root) => FindCheckBox(root, "Override");

    private static CheckBox? FindCheckBox(Control root, string textPrefix)
    {
        foreach (Control c in root.Controls)
        {
            if (c is CheckBox cb && cb.Text.StartsWith(textPrefix, StringComparison.OrdinalIgnoreCase)) return cb;
            var f = FindCheckBox(c, textPrefix); if (f != null) return f;
        }
        return null;
    }

    private static ComboBox? FindModelTypeCombo(Control root)
        => root.Controls.OfType<ComboBox>().FirstOrDefault(c => c.Items.Count >= 5 && c.Items.Contains("Box"))
           ?? root.Controls.Cast<Control>().Select(FindModelTypeCombo).FirstOrDefault(c => c != null);

    private static ComboBox? FindSpineStyleCombo(Control root)
        => root.Controls.OfType<ComboBox>().FirstOrDefault(c => c.Items.Contains("Sony Playstation Spine"))
           ?? root.Controls.Cast<Control>().Select(FindSpineStyleCombo).FirstOrDefault(c => c != null);

    private static Form? _host;
    private static void Shot(Control ctrl, string outDir, string name, Action? mutate)
    {
        const int W = 1150, H = 720;
        try
        {
            if (_host == null)
            {
                _host = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(W, H), BackColor = LiteBoxTheme.Bg, ShowInTaskbar = false };
                _host.Show();
            }
            if (ctrl.Parent != _host) { _host.Controls.Clear(); ctrl.Dock = DockStyle.Fill; _host.Controls.Add(ctrl); }
            mutate?.Invoke();
            Application.DoEvents(); _host.PerformLayout(); Application.DoEvents();
            using var bmp = new Bitmap(W, H);
            _host.DrawToBitmap(bmp, new Rectangle(0, 0, W, H));
            string outp = Path.Combine(outDir, San(name) + ".png");
            bmp.Save(outp, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("[render] wrote " + outp);
        }
        catch (Exception ex) { Console.WriteLine("[render] " + name + " FAILED: " + ex.Message); }
    }

    private static string San(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
