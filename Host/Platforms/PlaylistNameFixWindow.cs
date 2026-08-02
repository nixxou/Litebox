// Le modal qui débloque un collage de playlists : une ligne par copie dont le nom automatique ne
// convient pas (déjà pris, ou substitution sans effet parce qu'on colle sur la plateforme d'origine).
//
// Le champ pré-rempli est le nom CALCULÉ, même invalide, immédiatement bordé de rouge : on montre ce
// qui a échoué plutôt qu'une suggestion inventée. La validation est globale à chaque frappe — corriger
// la ligne 1 peut valider la ligne 3 — et OK reste désactivé tant qu'une ligne est rouge.
//
// Deux contraintes, pas une : le NOM (l'identité de la playlist chez LaunchBox) et la CLÉ D'IMAGES
// (Images\Playlists\<nom sanitizé>). Deux noms distincts peuvent produire le même dossier d'images, et
// comme le collage y COPIE des fichiers, laisser passer écraserait les images d'une autre playlist.
// Le Nested Name, lui, n'a aucune contrainte : LaunchBox accepte les doublons et le vide.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Platforms;

/// <summary>Une ligne du modal : ce qu'on propose en entrée, ce que l'utilisateur retient en sortie.</summary>
internal sealed class PlaylistNameFix
{
    public string SourceName = "";     // affiché, jamais modifiable
    public string Name = "";           // proposé puis saisi
    public string NestedName = "";
    public bool SameAsSource;          // collé sur sa propre plateforme → message dédié
}

internal sealed class PlaylistNameFixWindow : LiteBoxForm
{
    private static readonly Color Good = Color.FromArgb(120, 200, 120);
    private static readonly Color Bad = Color.FromArgb(200, 90, 90);

    private sealed class Row
    {
        public PlaylistNameFix Fix = null!;
        public TextBox Name = null!, Nested = null!;
        public Panel NameBorder = null!;
        public Label Error = null!;
    }

    private readonly List<Row> _rows = new();
    private readonly Button _ok;
    private readonly HashSet<string> _takenNames, _takenImages;

    private PlaylistNameFixWindow(List<PlaylistNameFix> fixes, HashSet<string> takenNames, HashSet<string> takenImages)
    {
        _takenNames = takenNames;
        _takenImages = takenImages;

        Text = "Paste playlists — choose names";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(S(620), S(Math.Min(560, 150 + fixes.Count * 104)));

        var header = new Label
        {
            Dock = DockStyle.Top, Height = S(46), Padding = new Padding(S(12), S(10), S(12), 0),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            Text = fixes.Count == 1
                ? "This copy needs a name of its own before it can be created."
                : $"{fixes.Count} copies need a name of their own before they can be created.",
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(48), BackColor = LiteBoxTheme.Bg };
        _ok = Btn("OK", LiteBoxTheme.Ok);
        _ok.Location = new Point(ClientSize.Width - S(200), S(9));
        _ok.Click += (_, _) => { Commit(); DialogResult = DialogResult.OK; Close(); };
        var cancel = Btn("Cancel", LiteBoxTheme.CancelBtn);
        cancel.Location = new Point(ClientSize.Width - S(100), S(9));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(_ok); footer.Controls.Add(cancel);
        AcceptButton = _ok; CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = LiteBoxTheme.Bg, Padding = new Padding(S(12), S(4), S(12), S(4)) };

        int y = 0;
        foreach (var fix in fixes)
        {
            var row = new Row { Fix = fix };

            var title = new Label
            {
                Text = fix.SourceName, AutoSize = false, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoEllipsis = true,
                Bounds = new Rectangle(0, y, S(580), S(20)),
            };
            body.Controls.Add(title); y += S(22);

            body.Controls.Add(new Label
            {
                Text = "Unique Name:", AutoSize = false, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
                TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(0, y, S(90), S(24)),
            });
            // Une TextBox WinForms n'a pas de couleur de bordure : le Panel qui l'enveloppe EST la bordure.
            row.NameBorder = new Panel { Bounds = new Rectangle(S(92), y, S(470), S(24)), Padding = new Padding(1), BackColor = LiteBoxTheme.Panel2 };
            row.Name = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Text = fix.Name, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg };
            row.NameBorder.Controls.Add(row.Name);
            body.Controls.Add(row.NameBorder); y += S(26);

            body.Controls.Add(new Label
            {
                Text = "Nested Name:", AutoSize = false, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
                TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(0, y, S(90), S(24)),
            });
            var nestedBorder = new Panel { Bounds = new Rectangle(S(92), y, S(470), S(24)), Padding = new Padding(1), BackColor = LiteBoxTheme.Panel2 };
            row.Nested = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Text = fix.NestedName, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg };
            nestedBorder.Controls.Add(row.Nested);
            body.Controls.Add(nestedBorder); y += S(26);

            row.Error = new Label
            {
                AutoSize = false, ForeColor = Bad, BackColor = LiteBoxTheme.Bg, AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(S(92), y, S(470), S(18)),
            };
            body.Controls.Add(row.Error); y += S(28);

            row.Name.TextChanged += (_, _) => Validate();
            _rows.Add(row);
        }

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
        Validate();
    }

    private Button Btn(string text, Color back) => new()
    {
        Text = text, Size = new Size(S(88), S(30)), FlatStyle = FlatStyle.Flat,
        BackColor = back, ForeColor = Color.White, Cursor = Cursors.Hand, FlatAppearance = { BorderSize = 0 },
    };

    /// <summary>Revalide TOUTES les lignes : une ligne peut devenir valide parce qu'une autre a changé.</summary>
    private void Validate()
    {
        var names = _rows.Select(r => r.Name.Text.Trim()).ToList();
        var keys = names.Select(n => n.Length == 0 ? "" : MediaResolver.Sanitize(n)).ToList();
        bool allOk = true;

        for (int i = 0; i < _rows.Count; i++)
        {
            string n = names[i], key = keys[i];
            string err = "";

            if (n.Length == 0)
                err = "a name is required";
            else if (_takenNames.Contains(n))
                err = _rows[i].Fix.SameAsSource && string.Equals(n, _rows[i].Fix.SourceName, StringComparison.OrdinalIgnoreCase)
                    ? "same platform as the original — give the copy its own name"
                    : $"a playlist named \"{n}\" already exists";
            else if (names.Where((_, j) => j != i).Any(o => string.Equals(o, n, StringComparison.OrdinalIgnoreCase)))
                err = "two rows use this name";
            else if (key.Length > 0 && _takenImages.Contains(key))
                err = "another playlist already uses the images folder this name resolves to";
            else if (key.Length > 0 && keys.Where((_, j) => j != i).Any(o => string.Equals(o, key, StringComparison.OrdinalIgnoreCase)))
                err = "two rows resolve to the same images folder";

            _rows[i].Error.Text = err;
            _rows[i].NameBorder.BackColor = err.Length == 0 ? Good : Bad;
            if (err.Length != 0) allOk = false;
        }
        _ok.Enabled = allOk;
    }

    private void Commit()
    {
        foreach (var r in _rows)
        {
            r.Fix.Name = r.Name.Text.Trim();
            r.Fix.NestedName = r.Nested.Text.Trim();
        }
    }

    /// <summary>Demande les noms manquants. False = annulé, l'appelant n'écrit rien.</summary>
    internal static bool Ask(IWin32Window? owner, List<PlaylistNameFix> fixes,
                             HashSet<string> takenNames, HashSet<string> takenImages)
    {
        if (fixes == null || fixes.Count == 0) return true;
        using var w = new PlaylistNameFixWindow(fixes, takenNames, takenImages);
        return w.ShowDialog(owner) == DialogResult.OK;
    }
}
