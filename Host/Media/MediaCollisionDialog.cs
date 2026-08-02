// Le dialogue de collision de titres — la spec du renommage, partie interactive.
//
// Quand la destination d'un renommage est deja occupee par un autre jeu, la decision revient a
// l'UTILISATEUR, pas au code : fusionner les collections, les separer en forme GUID, effacer nos
// fichiers, renommer sans toucher aux medias — ou abandonner le renommage entier. Le nom du rival
// s'affiche en ROUGE quand les deux DatabaseID sont presents et differents : la seule situation ou
// la difference est PROUVEE. Presents et egaux, ou absents d'un cote, le nom reste normal — on ne
// crie pas sur la foi d'un champ vide.
//
// La preselection suit ce que le mode automatique aurait fait : Fusionner quand rien ne prouve que
// les jeux different, Separer quand c'est prouve. OK sans reflechir = l'ancien comportement.
//
// Ce dialogue n'est PAS appele par GameMediaSync : la mecanique des medias doit rester utilisable
// sans interface (sonde, batch). C'est la fenetre d'edition qui interroge AVANT de poser le titre,
// et passe le choix en parametre — c'est aussi ce qui rend "Annuler le renommage" possible, un
// titre deja pose ne pouvant plus etre repris d'ici.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Media;

internal static class MediaCollisionDialog
{
    /// <summary>Auto quand il n'y a rien a demander — pas de rival, pas d'acces media, ou meme nom
    /// de fichier vise. Sinon la reponse de l'utilisateur, CancelRename compris.</summary>
    public static CollisionChoice AskIfNeeded(IWin32Window? owner, IGame game, string oldTitle, string newTitle)
    {
        try
        {
            if (game == null || !GameMediaSync.CanTouchMedia) return CollisionChoice.Auto;
            if (GameMediaRenamer.SameTargetName(oldTitle, newTitle)) return CollisionChoice.Auto;
            string platform = game.Platform ?? "";
            if (platform.Length == 0) return CollisionChoice.Auto;
            var rival = GameMediaSync.FindRival(game, platform, newTitle);
            if (rival == null) return CollisionChoice.Auto;

            int? mine = null, theirs = null;
            try { mine = game.LaunchBoxDbId; } catch { }
            try { theirs = rival.LaunchBoxDbId; } catch { }
            bool provablyDifferent = mine.HasValue && theirs.HasValue && mine.Value != theirs.Value;
            string rivalTitle = "";
            try { rivalTitle = rival.Title ?? ""; } catch { }

            return Show(owner, newTitle, rivalTitle, theirs, provablyDifferent);
        }
        catch { return CollisionChoice.Auto; }
    }

    private static CollisionChoice Show(IWin32Window? owner, string newTitle, string rivalTitle,
                                        int? rivalDb, bool provablyDifferent)
    {
        using var form = new Form
        {
            Text = "Title collision",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = LiteBoxTheme.Bg, ForeColor = LiteBoxTheme.Fg,
        };
        float s = LiteBoxTheme.DpiScale(form);
        int S(int px) => (int)Math.Round(px * s);
        form.ClientSize = new Size(S(500), S(316));

        form.Controls.Add(new Label
        {
            Text = $"Another game already answers to « {newTitle} » :",
            Location = new Point(S(14), S(12)), Size = new Size(S(472), S(20)),
            ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        });
        form.Controls.Add(new Label
        {
            // Rouge = les DatabaseID sont presents ET differents : deux jeux distincts, prouve.
            // Sinon couleur normale — deux fiches du meme jeu, ou pas de preuve du tout.
            Text = rivalTitle + (rivalDb.HasValue ? $"   (DatabaseID {rivalDb.Value})" : "   (no DatabaseID)"),
            Location = new Point(S(28), S(34)), Size = new Size(S(458), S(22)),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = provablyDifferent ? Color.FromArgb(235, 95, 80) : LiteBoxTheme.Fg,
            BackColor = LiteBoxTheme.Bg,
        });
        form.Controls.Add(new Label
        {
            Text = "What should happen to this game's media files?",
            Location = new Point(S(14), S(62)), Size = new Size(S(472), S(20)),
            ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        });

        RadioButton Radio(string text, string sub, int y, bool check)
        {
            var r = new RadioButton
            {
                Text = text, Checked = check, AutoSize = false,
                Location = new Point(S(20), S(y)), Size = new Size(S(466), S(20)),
                ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            };
            form.Controls.Add(r);
            form.Controls.Add(new Label
            {
                Text = sub, Location = new Point(S(38), S(y + 19)), Size = new Size(S(448), S(16)),
                Font = new Font("Segoe UI", 7.5f), ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            });
            return r;
        }

        // La preselection reproduit le mode automatique : OK sans lire = l'ancien comportement.
        var rMerge = Radio("Merge the collections",
            "byte-identical duplicates and near-identical pictures are skipped, the rest joins after the rival's numbering",
            88, !provablyDifferent);
        var rSplit = Radio("Keep them separate (GUID names)",
            "both games keep their own files, permanently distinguishable — the rival's files are converted too",
            130, provablyDifferent);
        var rDelete = Radio("Delete this game's media files",
            "the rename happens, this game's media are removed (files another game references by path are spared)",
            172, false);
        var rLeave = Radio("Rename only — leave all media untouched",
            "what LaunchBox itself would do; the files stay under the old title", 214, false);

        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Location = new Point(S(300), S(272)), Size = new Size(S(88), S(30)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        var cancel = new Button
        {
            Text = "Cancel rename", DialogResult = DialogResult.Cancel,
            Location = new Point(S(394), S(272)), Size = new Size(S(96), S(30)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        form.Controls.Add(ok); form.Controls.Add(cancel);
        form.AcceptButton = ok; form.CancelButton = cancel;

        if (form.ShowDialog(owner) != DialogResult.OK) return CollisionChoice.CancelRename;
        if (rMerge.Checked) return CollisionChoice.Merge;
        if (rSplit.Checked) return CollisionChoice.SplitGuid;
        if (rDelete.Checked) return CollisionChoice.DeleteOurs;
        if (rLeave.Checked) return CollisionChoice.LeaveMedia;
        return CollisionChoice.Auto;
    }
}
