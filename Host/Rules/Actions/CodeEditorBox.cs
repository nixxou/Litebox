// A small dark C# code editor for the script rule — RichTextBox + a line-based tokenizer good
// enough for scripts this size: // and /* */ comments, "…" / @"…" / $"…" strings, keywords,
// numbers. The whole text recolors on a short debounce (full pass — scripts are small), behind
// WM_SETREDRAW so nothing flickers, caret and scroll preserved. Also renders the Documentation
// tab: prose gray, ALL-CAPS headings in the accent color, indented lines treated as code and run
// through the same tokenizer — the doc teaches by looking like the editor.

#nullable enable

using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal static class CodeEditorBox
{
    private static readonly Color ColDefault = Color.FromArgb(220, 220, 220);
    private static readonly Color ColKeyword = Color.FromArgb(86, 156, 214);
    private static readonly Color ColString = Color.FromArgb(214, 157, 133);
    private static readonly Color ColComment = Color.FromArgb(87, 166, 74);
    private static readonly Color ColNumber = Color.FromArgb(181, 206, 168);
    private static readonly Color ColHeading = Color.FromArgb(120, 180, 250);
    private static readonly Color ColProse = Color.FromArgb(170, 170, 174);

    private static readonly string[] Keywords =
    {
        "if", "else", "var", "new", "return", "foreach", "for", "while", "do", "using",
        "true", "false", "null", "string", "int", "long", "bool", "double", "float", "char",
        "try", "catch", "finally", "throw", "out", "ref", "in", "is", "as", "switch", "case",
        "default", "break", "continue", "void", "class", "struct", "public", "private",
        "static", "dynamic", "object", "byte", "short", "uint", "ulong", "not", "and", "or",
    };

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_SETREDRAW = 0x0B;
    private const int EM_GETFIRSTVISIBLELINE = 0xCE;
    private const int EM_LINESCROLL = 0xB6;

    /// <summary>The editor: dark, Consolas, C# highlighting on a 400 ms debounce.</summary>
    public static RichTextBox CreateEditor(string code)
    {
        var box = new RichTextBox
        {
            Text = code.ReplaceLineEndings("\r\n"),
            Font = new Font("Consolas", 9f),
            BackColor = LiteBoxTheme.Panel2, ForeColor = ColDefault,
            BorderStyle = BorderStyle.FixedSingle,
            AcceptsTab = true, WordWrap = false, DetectUrls = false,
            Dock = DockStyle.Fill,
        };
        var timer = new System.Windows.Forms.Timer { Interval = 400 };
        bool coloring = false;
        timer.Tick += (_, _) => { timer.Stop(); Highlight(box, ref coloring); };
        box.TextChanged += (_, _) => { if (!coloring) { timer.Stop(); timer.Start(); } };
        box.Disposed += (_, _) => timer.Dispose();
        Highlight(box, ref coloring);
        return box;
    }

    /// <summary>Full-pass recolor, flicker-free, caret and scroll kept.</summary>
    private static void Highlight(RichTextBox box, ref bool coloring)
    {
        if (coloring || box.IsDisposed) return;
        coloring = true;
        int selStart = box.SelectionStart, selLen = box.SelectionLength;
        int firstLine = (int)SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        SendMessage(box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            box.SelectAll();
            box.SelectionColor = ColDefault;
            foreach (var (start, len, color) in Tokenize(box.Text))
            {
                box.Select(start, len);
                box.SelectionColor = color;
            }
        }
        finally
        {
            box.Select(selStart, selLen);
            int nowFirst = (int)SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            if (nowFirst != firstLine)
                SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(firstLine - nowFirst));
            SendMessage(box.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            box.Invalidate();
            coloring = false;
        }
    }

    /// <summary>The tokens worth coloring, over the WHOLE text (block comments span lines).</summary>
    private static System.Collections.Generic.IEnumerable<(int Start, int Len, Color Color)> Tokenize(string s)
    {
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                int end = s.IndexOf('\n', i); if (end < 0) end = n;
                yield return (i, end - i, ColComment); i = end;
            }
            else if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                yield return (i, end - i, ColComment); i = end;
            }
            else if (c == '"' || (c is '@' or '$' && i + 1 < n && (s[i + 1] == '"' || (s[i + 1] is '@' or '$' && i + 2 < n && s[i + 2] == '"'))))
            {
                int start = i;
                bool verbatim = false;
                while (i < n && s[i] != '"') { if (s[i] == '@') verbatim = true; i++; }
                i++;   // past the opening quote
                while (i < n)
                {
                    if (s[i] == '"')
                    {
                        if (verbatim && i + 1 < n && s[i + 1] == '"') { i += 2; continue; }   // "" escape
                        i++; break;
                    }
                    if (!verbatim && s[i] == '\\' && i + 1 < n) { i += 2; continue; }
                    if (s[i] == '\n' && !verbatim) break;   // unterminated — stop at EOL
                    i++;
                }
                yield return (start, i - start, ColString);
            }
            else if (char.IsDigit(c))
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '.' || s[i] == '_')) i++;
                yield return (start, i - start, ColNumber);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                if (Array.IndexOf(Keywords, s[start..i]) >= 0)
                    yield return (start, i - start, ColKeyword);
            }
            else i++;
        }
    }

    // ── the documentation renderer ────────────────────────────────────────────

    /// <summary>Read-only doc view: headings in accent, prose gray, indented lines highlighted as
    /// code with the editor's own palette.</summary>
    public static RichTextBox CreateDocView(string doc)
    {
        var box = new RichTextBox
        {
            Font = new Font("Consolas", 9f),
            BackColor = LiteBoxTheme.Bg, ForeColor = ColProse,
            BorderStyle = BorderStyle.FixedSingle, ReadOnly = true,
            WordWrap = false, DetectUrls = false, Dock = DockStyle.Fill,
        };
        var bold = new Font("Consolas", 9.5f, FontStyle.Bold);
        foreach (var raw in doc.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            bool heading = line.Length > 3 && line == line.ToUpperInvariant()
                           && line.Any(char.IsLetter) && !line.StartsWith(" ");
            bool rule = line.Length > 3 && (line.All(ch => ch is '=' or '-'));
            bool code = line.StartsWith("     ") || line.StartsWith("       ");
            if (rule) continue;   // the ===== underlines become spacing
            if (heading)
            {
                box.SelectionFont = bold;
                box.SelectionColor = ColHeading;
                box.AppendText(line + "\r\n");
                box.SelectionFont = box.Font;
                continue;
            }
            if (code)
            {
                int lineStart = box.TextLength;
                box.SelectionColor = ColDefault;
                box.AppendText(line + "\r\n");
                foreach (var (start, len, color) in Tokenize(line))
                {
                    box.Select(lineStart + start, len);
                    box.SelectionColor = color;
                }
                box.Select(box.TextLength, 0);
                continue;
            }
            box.SelectionColor = ColProse;
            box.AppendText(line + "\r\n");
        }
        box.Select(0, 0);
        return box;
    }
}
