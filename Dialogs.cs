using System.Windows.Forms;

namespace MonitorMover;

/// <summary>Minimal text-input prompt (WinForms has no built-in InputBox).</summary>
public static class Prompt
{
    public static string? Text(string caption, string message, string initial = "")
    {
        using var form = new Form
        {
            Text = caption,
            Width = 420,
            Height = 170,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var lbl = new Label { Left = 12, Top = 12, Width = 380, Text = message };
        var box = new TextBox { Left = 12, Top = 40, Width = 380, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 226, Top = 80, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 312, Top = 80, Width = 80 };
        form.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}

/// <summary>Read-only multiline result viewer for the "apply profile" log.</summary>
public static class ResultViewer
{
    public static void Show(string caption, IEnumerable<string> lines)
    {
        using var form = new Form
        {
            Text = caption,
            Width = 640,
            Height = 440,
            StartPosition = FormStartPosition.CenterParent
        };
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new System.Drawing.Font("Consolas", 9f),
            Text = string.Join(Environment.NewLine, lines)
        };
        var close = new Button { Text = "Close", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.OK };
        form.Controls.Add(box);
        form.Controls.Add(close);
        form.AcceptButton = close;
        form.ShowDialog();
    }
}
