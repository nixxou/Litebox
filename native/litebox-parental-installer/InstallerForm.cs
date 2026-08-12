// The installer window: shows the detected LaunchBox root + install state, and Install / Uninstall / Choose.
// Auto-detects the root from the exe's own folder (drop it at the LaunchBox root and double-click); otherwise
// the user picks the ROOT LaunchBox.exe (not Core\LaunchBox.exe). Uninstall asks for the PIN when one is set.

using System.Drawing;
using System.Windows.Forms;

namespace LiteBoxParentalInstaller;

internal sealed class InstallerForm : Form
{
    private string? _root;
    private readonly Label _rootLabel = new() { AutoSize = false, Location = new Point(16, 44), Size = new Size(468, 20) };
    private readonly Label _stateLabel = new() { AutoSize = false, Location = new Point(16, 66), Size = new Size(468, 20) };
    private readonly Button _install = new() { Text = "Install / Update", Location = new Point(16, 100), Width = 150, Height = 32 };
    private readonly Button _uninstall = new() { Text = "Uninstall", Location = new Point(176, 100), Width = 150, Height = 32 };
    private readonly Button _choose = new() { Text = "Choose LaunchBox…", Location = new Point(336, 100), Width = 150, Height = 32 };

    public InstallerForm()
    {
        Text = "LiteBox Parental — Plugin Installer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(502, 152);
        Font = new Font("Segoe UI", 9f);

        Controls.Add(new Label
        {
            Text = "Install or remove the parental-control plugin for LaunchBox / BigBox.",
            AutoSize = false, Location = new Point(16, 14), Size = new Size(470, 20)
        });
        Controls.Add(_rootLabel);
        Controls.Add(_stateLabel);
        Controls.Add(_install);
        Controls.Add(_uninstall);
        Controls.Add(_choose);

        _install.Click += (_, __) => DoInstall();
        _uninstall.Click += (_, __) => DoUninstall();
        _choose.Click += (_, __) => ChooseRoot();

        // Auto-detect: the exe's own directory (dropped at the LaunchBox root), else its parent (dropped in Core).
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (InstallerCore.LooksLikeRoot(baseDir)) SetRoot(baseDir);
        else if (InstallerCore.LooksLikeRoot(Path.GetDirectoryName(baseDir))) SetRoot(Path.GetDirectoryName(baseDir)!);
        else Refresh_();
    }

    private void SetRoot(string root) { _root = root; Refresh_(); }

    private void Refresh_()
    {
        if (_root == null)
        {
            _rootLabel.Text = "LaunchBox: not detected — click \"Choose LaunchBox…\".";
            _stateLabel.Text = "";
            _install.Enabled = _uninstall.Enabled = false;
            return;
        }
        var l = InstallerCore.Resolve(_root);
        _rootLabel.Text = "LaunchBox: " + _root;
        bool installed = InstallerCore.IsInstalled(l);
        _stateLabel.Text = "Status: " + (installed ? "installed" : "not installed")
            + (InstallerCore.HasPin(l) ? "   •   PIN set" : "");
        _install.Enabled = true;
        _uninstall.Enabled = installed;
    }

    private void ChooseRoot()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select the LaunchBox.exe in the LaunchBox root folder (not the Core folder)",
            Filter = "LaunchBox.exe|LaunchBox.exe|BigBox.exe|BigBox.exe|Executable|*.exe",
            CheckFileExists = true
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var root = InstallerCore.RootFromExe(ofd.FileName);
        if (!InstallerCore.LooksLikeRoot(root))
        {
            MessageBox.Show(this,
                "That folder doesn't look like a LaunchBox install (no Core folder found next to the exe).\n\n" +
                "Pick the LaunchBox.exe in the LaunchBox root — the folder that contains Core and Plugins.",
                "LiteBox Parental", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SetRoot(root);
    }

    private void DoInstall()
    {
        if (_root == null) return;
        var l = InstallerCore.Resolve(_root);
        var (ok, msg) = InstallerCore.Install(l);
        Report(ok, msg);
        Refresh_();
    }

    private void DoUninstall()
    {
        if (_root == null) return;
        var l = InstallerCore.Resolve(_root);

        // Now that a folder is targeted, we KNOW whether LiteBox is here — offer the options (incl. removing the
        // shared .dat) with a LiteBox warning only when relevant.
        using var opts = new UninstallOptionsDialog(_root, InstallerCore.LiteBoxInstalled(l));
        if (opts.ShowDialog(this) != DialogResult.OK) return;
        bool removeDat = opts.RemoveDat;

        if (InstallerCore.HasPin(l))
        {
            using var pin = new PinPrompt();
            if (pin.ShowDialog(this) != DialogResult.OK) return;
            if (!InstallerCore.VerifyPin(l, pin.Pin))
            {
                MessageBox.Show(this, "Wrong PIN — uninstall cancelled.",
                    "LiteBox Parental", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var (ok, msg) = InstallerCore.Uninstall(l, removeDat);
        Report(ok, msg);
        Refresh_();
    }

    private void Report(bool ok, string msg) =>
        MessageBox.Show(this, msg, "LiteBox Parental",
            MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
}

/// <summary>Masked-digit PIN prompt for uninstall. Enter submits, Esc cancels.</summary>
internal sealed class PinPrompt : Form
{
    private readonly TextBox _box = new() { UseSystemPasswordChar = true, MaxLength = 8, Location = new Point(96, 15), Width = 150 };
    public string Pin => _box.Text.Trim();

    public PinPrompt()
    {
        Text = "Enter parental PIN";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(266, 96);
        Font = new Font("Segoe UI", 9f);

        _box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        var lbl = new Label { Text = "PIN:", AutoSize = true, Location = new Point(16, 18) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(96, 54), Width = 70 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(176, 54), Width = 70 };
        Controls.AddRange(new Control[] { lbl, _box, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
    }
}

/// <summary>Shown AFTER the user clicks Uninstall (so the folder — and thus whether LiteBox is present — is known).
/// Offers the "also remove the shared .dat" choice, with a red LiteBox warning only when LiteBox is installed.</summary>
internal sealed class UninstallOptionsDialog : Form
{
    private readonly CheckBox _dat = new()
    {
        Text = "Also remove the parental configuration (litebox-parental.dat)",
        AutoSize = true, Location = new Point(16, 54),
    };
    public bool RemoveDat => _dat.Checked;

    public UninstallOptionsDialog(string root, bool liteBoxPresent)
    {
        Text = "Uninstall parental plugin";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);

        const int w = 476;
        Controls.Add(new Label
        {
            Text = "Remove the parental plugin from:\n" + root,
            AutoSize = false, Location = new Point(16, 12), Size = new Size(w - 32, 36),
        });
        Controls.Add(_dat);

        int y = 92;
        if (liteBoxPresent)
        {
            Controls.Add(new Label
            {
                Text = "⚠ LiteBox is installed here and shares this configuration —\n" +
                       "removing it will also erase LiteBox's parental settings.",
                AutoSize = false, Location = new Point(34, 78), Size = new Size(w - 48, 36),
                ForeColor = Color.Firebrick, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            });
            y = 122;
        }

        var ok = new Button { Text = "Uninstall", DialogResult = DialogResult.OK, Location = new Point(w - 188, y), Width = 84 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(w - 96, y), Width = 84 };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        ClientSize = new Size(w, y + 40);
    }
}
