// The "Images" panel of the Edit Platform window — LaunchBox parity. Docked to the RIGHT of the window, always
// visible across sections. Per image type it shows the platform's OWN images (Images\Platforms\<name>\<type>\)
// AND media-pack fallbacks (Images\Media Packs\<category>\<pack>\<scrapeAs|name>) with the source labelled, e.g.
// "Clear Logo (Nostalgic Platform Clear Logos)". The image query lives on HostPlatform.GetImagesForType (reusable
// by the tree / web too), not here. Add copies a file into the platform's own type folder; Remove deletes a file
// but ONLY when it's an own image (media-pack files are shared assets, left untouched).

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformImages
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color Panel2 = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;

    // Display label → on-disk type folder. Order matches LaunchBox's dropdown.
    private static readonly (string label, string folder)[] PlatformTypes =
    {
        ("Banner", "Banner"),
        ("Clear Logo (Override)", "Clear Logo"),
        ("Default 3D Box", "Default 3D Box"),
        ("Default 3D Cart", "Default 3D Cart"),
        ("Default Box", "Default Box"),
        ("Default Cart", "Default Cart"),
        ("Device", "Device"),
        ("Fanart", "Fanart"),
        ("Steam Banner", "Steam Banner"),
    };
    // Category image types — LB's Edit Platform Category dropdown shows exactly these four.
    private static readonly (string label, string folder)[] CategoryTypes =
    {
        ("Banner", "Banner"),
        ("Clear Logo (Override)", "Clear Logo"),
        ("Device", "Device"),
        ("Fanart", "Fanart"),
    };

    public static Control Build(IPlatform plat, bool readOnly, float s)
    {
        var hp = plat as Data.HostPlatform;   // fully-qualified: a different HostPlatform lives in LbApiHost.Host
        string name = Safe(() => plat.Name) ?? "";
        return BuildCore(name, PlatformTypes, tf => hp?.GetImagesForType(tf) ?? new List<(string, string)>(), "Platforms", readOnly, s);
    }

    /// <summary>Images panel for a Platform Category — own images under Images\Platform Categories\&lt;name&gt;
    /// plus media-pack fallbacks, restricted to LB's four category image types.</summary>
    public static Control BuildForCategory(string name, string nestedName, bool readOnly, float s)
        => BuildForEntity("Platform Categories", name, nestedName, readOnly, s);

    /// <summary>Images panel for a Playlist — own images under Images\Playlists\&lt;name&gt;; same four types
    /// as categories (LB's Edit Playlist dropdown is identical).</summary>
    public static Control BuildForPlaylist(string name, string nestedName, bool readOnly, float s)
        => BuildForEntity("Playlists", name, nestedName, readOnly, s);

    /// <summary>Own images plus media-pack fallbacks. The NESTED name is offered as the pack key ahead of
    /// the unique one, because that is how packs file these: the entry for "Arcade 2-Player Games" ships as
    /// Playlists-Player Games.png. Searching only the unique name found nothing, which is why LaunchBox
    /// showed a logo here and we showed "(no image)".</summary>
    private static Control BuildForEntity(string entityFolder, string name, string nestedName, bool readOnly, float s)
        => BuildCore(name, CategoryTypes,
            tf => Media.MediaResolver.EntityTypeImages(Media.MediaResolver.ImagesRoot, entityFolder, name,
                                                       string.IsNullOrWhiteSpace(nestedName) ? name : nestedName, tf),
            entityFolder, readOnly, s);

    private static Control BuildCore(string name, (string label, string folder)[] Types,
                                     Func<string, List<(string path, string source)>> getImages,
                                     string entityFolder, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);

        var root = new Panel { BackColor = Bg, Padding = new Padding(S(8)) };

        var typeCombo = new ComboBox
        {
            Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Panel2, ForeColor = Fg,
        };
        foreach (var t in Types) typeCombo.Items.Add(t.label);
        typeCombo.SelectedIndex = 0;

        // toolbar
        var bar = new Panel { Dock = DockStyle.Top, Height = S(30), BackColor = Bg };
        Button Btn(string text, int x, int w) => new()
        {
            Text = text, Location = new Point(S(x), S(3)), Size = new Size(S(w), S(24)),
            FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 },
        };
        var prev = Btn("◄", 0, 30);
        var counter = new Label { Location = new Point(S(34), S(7)), Size = new Size(S(56), S(20)), ForeColor = SubFg, BackColor = Bg, TextAlign = ContentAlignment.MiddleCenter, Text = "0/0" };
        var next = Btn("►", 92, 30);
        var add = Btn("✚ Add", 130, 64); add.ForeColor = Color.FromArgb(150, 210, 150);
        var remove = Btn("✕ Remove", 200, 82); remove.ForeColor = Color.FromArgb(220, 130, 120);
        bar.Controls.AddRange(new Control[] { prev, counter, next, add, remove });

        // source label ("Clear Logo (Nostalgic Platform Clear Logos)")
        var sourceLbl = new Label { Dock = DockStyle.Top, Height = S(22), ForeColor = SubFg, BackColor = Bg, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(2), 0, 0, 0) };

        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Panel2 };
        var empty = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SubFg, BackColor = Panel2, Text = "(no image)", Visible = false };
        var picHost = new Panel { Dock = DockStyle.Fill, BackColor = Panel2 };
        picHost.Controls.Add(empty); picHost.Controls.Add(pic);

        // dock order: pic host fills, then (top→) sourceLbl, bar, typeCombo
        root.Controls.Add(picHost);
        root.Controls.Add(sourceLbl);
        root.Controls.Add(bar);
        root.Controls.Add(typeCombo);

        List<(string path, string source)> entries = new();
        int idx = 0;

        string TypeFolder() => Types[Math.Max(0, typeCombo.SelectedIndex)].folder;
        string OwnDir() => Path.Combine(Media.MediaResolver.LbRoot ?? "", "Images", entityFolder, Sanitize(name), TypeFolder());
        bool IsOwn(int i) => i >= 0 && i < entries.Count && string.Equals(entries[i].source, TypeFolder(), StringComparison.OrdinalIgnoreCase);

        void LoadList()
        {
            entries = getImages(TypeFolder()) ?? new List<(string, string)>();
            if (idx >= entries.Count) idx = Math.Max(0, entries.Count - 1);
            Show();
        }

        void Show()
        {
            var old = pic.Image; pic.Image = null; old?.Dispose();
            string typeLabel = Types[Math.Max(0, typeCombo.SelectedIndex)].label;
            if (entries.Count == 0)
            {
                empty.Visible = true; pic.Visible = false; counter.Text = "0/0";
                sourceLbl.Text = typeLabel;
            }
            else
            {
                var (path, source) = entries[idx];
                try { pic.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); pic.Visible = true; empty.Visible = false; }
                catch { pic.Visible = false; empty.Visible = true; empty.Text = "(cannot display)"; }
                counter.Text = $"{idx + 1}/{entries.Count}";
                // Own image → just the type; media-pack → "Type (Pack Name)".
                sourceLbl.Text = string.Equals(source, TypeFolder(), StringComparison.OrdinalIgnoreCase) ? typeLabel : $"{typeLabel} ({source})";
            }
            prev.Enabled = next.Enabled = entries.Count > 1;
            remove.Enabled = !readOnly && entries.Count > 0 && IsOwn(idx);
        }

        void DoAdd()
        {
            if (readOnly) return;
            using var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*", Multiselect = true };
            if (d.ShowDialog() != DialogResult.OK) return;
            string dir = OwnDir();
            try { Directory.CreateDirectory(dir); } catch { }
            foreach (var src in d.FileNames)
                try { File.Copy(src, UniqueDest(dir, name, Path.GetExtension(src)), false); } catch (Exception ex) { Console.WriteLine("[editplat-img] add failed: " + ex.Message); }
            idx = int.MaxValue; LoadList();
        }
        void DoRemove()
        {
            if (readOnly || entries.Count == 0 || !IsOwn(idx)) return;
            if (MessageBox.Show($"Delete this image?\n{Path.GetFileName(entries[idx].path)}", "Remove Image", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { File.Delete(entries[idx].path); } catch (Exception ex) { Console.WriteLine("[editplat-img] remove failed: " + ex.Message); }
            LoadList();
        }
        void DoRemoveAll()
        {
            if (readOnly) return;
            var own = entries.Where((_, i) => IsOwn(i)).Select(e => e.path).ToList();
            if (own.Count == 0) return;
            if (MessageBox.Show($"Delete ALL {own.Count} OWN image(s) of this type? (media-pack images are left untouched)", "Remove All Images", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (var f in own) { try { File.Delete(f); } catch { } }
            idx = 0; LoadList();
        }
        // Reassign the CURRENTLY selected (OWN) image's type = move its file into the target type's folder.
        void RetypeCurrent(int targetType)
        {
            if (readOnly || entries.Count == 0 || !IsOwn(idx)) return;
            string src = entries[idx].path;
            string destDir = Path.Combine(Media.MediaResolver.LbRoot ?? "", "Images", entityFolder, Sanitize(name), Types[targetType].folder);
            if (string.Equals(Path.GetDirectoryName(src), destDir, StringComparison.OrdinalIgnoreCase)) return;   // already that type
            try
            {
                Directory.CreateDirectory(destDir);
                File.Move(src, UniqueDest(destDir, name, Path.GetExtension(src)));
            }
            catch (Exception ex) { Console.WriteLine("[editplat-img] retype failed: " + ex.Message); return; }
            idx = 0; typeCombo.SelectedIndex = targetType;   // jump to the destination type (fires LoadList)
        }
        void DoView() { if (entries.Count > 0) try { Process.Start(new ProcessStartInfo(entries[idx].path) { UseShellExecute = true }); } catch { } }
        void DoSaveAs()
        {
            if (entries.Count == 0) return;
            using var d = new SaveFileDialog { FileName = Path.GetFileName(entries[idx].path), Filter = "Image|*" + Path.GetExtension(entries[idx].path) };
            if (d.ShowDialog() == DialogResult.OK) try { File.Copy(entries[idx].path, d.FileName, true); } catch { }
        }
        void DoShowInExplorer()
        {
            if (entries.Count == 0) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entries[idx].path}\"") { UseShellExecute = true }); } catch { }
        }
        void DoInfo()
        {
            if (entries.Count == 0) return;
            var (path, source) = entries[idx];
            string typeLabel = Types[Math.Max(0, typeCombo.SelectedIndex)].label;
            string dims = "?", size = "?";
            try { size = $"{new FileInfo(path).Length / 1024.0:0.#} KB"; } catch { }
            try { using var ms = new MemoryStream(File.ReadAllBytes(path)); using var bmp = Image.FromStream(ms); dims = $"{bmp.Width} × {bmp.Height}"; } catch { }
            bool own = string.Equals(source, TypeFolder(), StringComparison.OrdinalIgnoreCase);
            string text = $"Type:  {typeLabel}\nSource:  {(own ? "Platform (own)" : source)}\nDimensions:  {dims}\nSize:  {size}\n";
            // ADS metadata (:crc32 + :info) — ExtendDB-format provenance, same as the game images editor.
            string crc32 = Media.FileMetaStore.Read(path, Media.FileMetaStore.StreamCrc32);
            var info = Media.ImageInfoBridge.ReadAny(path);
            text += "\n── ADS metadata " + (Media.ImageInfoBridge.Available ? "(via ExtendDB reader)" : "(native)") + " ──\n";
            text += $"CRC32 (:crc32):  {(string.IsNullOrEmpty(crc32) ? "(none)" : crc32)}\n";
            if (info is Media.ImageInfo i)
                text += $"Origin:  {(string.IsNullOrEmpty(i.Origin) ? "(none)" : i.Origin)}\nDatabase Id:  {i.DatabaseId}\nCRC32 (:info):  {i.Crc32}\nDuplicate:  {i.Duplicate}\n" +
                        $"File type:  {(string.IsNullOrEmpty(i.FileType) ? "(none)" : i.FileType)}\nNative region:  {(string.IsNullOrEmpty(i.NativeRegion) ? "(none)" : i.NativeRegion)}\n" +
                        $"Stored dims:  {i.SizeX} × {i.SizeY}\nFile size:  {i.FileSize}\nSource URL:  {(string.IsNullOrEmpty(i.OriginalUrl) ? "(none)" : i.OriginalUrl)}\n";
            else text += "(:info):  (none)\n";
            text += $"\n{path}";
            MessageBox.Show(text, "Image info", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        prev.Click += (_, _) => { if (entries.Count > 0) { idx = (idx - 1 + entries.Count) % entries.Count; Show(); } };
        next.Click += (_, _) => { if (entries.Count > 0) { idx = (idx + 1) % entries.Count; Show(); } };
        add.Click += (_, _) => DoAdd();
        remove.Click += (_, _) => DoRemove();
        typeCombo.SelectedIndexChanged += (_, _) => { idx = 0; LoadList(); };

        void ShowMenu(Point at)
        {
            bool own = IsOwn(idx);
            var m = new ContextMenuStrip { BackColor = Panel2, ForeColor = Fg };

            // Read-only actions (always available when there's an image, incl. media-pack ones).
            if (entries.Count > 0)
            {
                m.Items.Add(new ToolStripMenuItem("View Image", null, (_, _) => DoView()));
                m.Items.Add(new ToolStripMenuItem("Show in Explorer", null, (_, _) => DoShowInExplorer()));
                m.Items.Add(new ToolStripMenuItem("Image Info…", null, (_, _) => DoInfo()));
                m.Items.Add(new ToolStripMenuItem("Save Image As…", null, (_, _) => DoSaveAs()));
            }

            // Editing actions only for OWN images (media-pack images are shared read-only assets — no retype /
            // remove / add-as-this on them). Add is always offered (it adds a new OWN image of the current type).
            if (!readOnly)
            {
                if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem("Add Image…", null, (_, _) => DoAdd()));
                if (own)
                {
                    // Image Type = REASSIGN the selected image's type (moves the file to that type's folder).
                    var typeItem = new ToolStripMenuItem("Image Type");
                    for (int i = 0; i < Types.Length; i++)
                    {
                        int ti = i;
                        typeItem.DropDownItems.Add(new ToolStripMenuItem(Types[i].label, null, (_, _) => RetypeCurrent(ti))
                        { Checked = string.Equals(Types[ti].folder, TypeFolder(), StringComparison.OrdinalIgnoreCase) });
                    }
                    m.Items.Add(typeItem);
                    m.Items.Add(new ToolStripMenuItem("Remove Image", null, (_, _) => DoRemove()));
                    m.Items.Add(new ToolStripMenuItem("Remove All Images", null, (_, _) => DoRemoveAll()));
                }
            }
            if (m.Items.Count > 0) m.Show(pic, at);
        }
        pic.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(e.Location); };
        empty.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(e.Location); };
        pic.MouseDoubleClick += (_, _) => DoView();

        LoadList();
        return root;
    }

    private static string UniqueDest(string dir, string name, string ext)
    {
        string san = Sanitize(name);
        string p = Path.Combine(dir, san + ext);
        if (!File.Exists(p)) return p;
        for (int i = 1; i < 1000; i++) { p = Path.Combine(dir, $"{san}-{i:00}{ext}"); if (!File.Exists(p)) return p; }
        return Path.Combine(dir, $"{san}-{Guid.NewGuid():N}{ext}");
    }

    private static string Sanitize(string sn)
    {
        if (string.IsNullOrEmpty(sn)) return sn;
        foreach (var c in Path.GetInvalidFileNameChars()) sn = sn.Replace(c, '_');
        return sn.Replace('\'', '_').Trim();
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
