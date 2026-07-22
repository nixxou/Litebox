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

    private static ComboBox? FindModelTypeCombo(Control root)
        => root.Controls.OfType<ComboBox>().FirstOrDefault(c => c.Items.Count == 5 && c.Items.Contains("Box"))
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
