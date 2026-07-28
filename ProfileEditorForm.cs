using System.Windows.Forms;

namespace MonitorMover;

/// <summary>
/// Lets the user prune and tweak the rules of a captured profile: toggle rows on/off,
/// refine the title match, and pick the target monitor / state.
/// </summary>
public sealed class ProfileEditorForm : Form
{
    private readonly Profile _profile;
    private readonly List<MonitorInfo> _monitors;
    private readonly DataGridView _grid = new();
    private readonly TextBox _nameBox = new();

    public Profile Result => _profile;

    public ProfileEditorForm(Profile profile, List<MonitorInfo> monitors)
    {
        _profile = profile;
        _monitors = monitors;

        Text = "Edit Profile";
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 6, 8, 6)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var lbl = new Label
        {
            Text = "Profile name:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 6, 4)
        };
        _nameBox.Text = profile.Name;
        _nameBox.Dock = DockStyle.Fill;
        _nameBox.Margin = new Padding(0);
        top.Controls.Add(lbl, 0, 0);
        top.Controls.Add(_nameBox, 1, 0);

        BuildGrid();

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(8)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Button MakeButton(string text, DialogResult result = DialogResult.None) => new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(4, 0, 0, 0),
            Anchor = AnchorStyles.None
        };

        var ok = MakeButton("Save", DialogResult.OK);
        var cancel = MakeButton("Cancel", DialogResult.Cancel);
        var del = MakeButton("Remove Selected Rows");
        del.Margin = new Padding(0);
        del.Click += (_, _) => RemoveSelected();
        bottom.Controls.Add(del, 0, 0);
        bottom.Controls.Add(ok, 2, 0);
        bottom.Controls.Add(cancel, 3, 0);

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);

        ok.Click += (_, _) => CommitToProfile();

        LoadRows();
        Shown += (_, _) =>
        {
            _nameBox.SelectionStart = 0;
            _nameBox.SelectionLength = 0;
            _nameBox.ScrollToCaret();
        };
    }

    private DataGridViewCheckBoxColumn _colEnabled = null!;
    private DataGridViewTextBoxColumn _colProcess = null!;
    private DataGridViewTextBoxColumn _colTitle = null!;
    private DataGridViewTextBoxColumn _colMatch = null!;
    private DataGridViewComboBoxColumn _colMonitor = null!;
    private DataGridViewComboBoxColumn _colState = null!;
    private DataGridViewTextBoxColumn _colPos = null!;

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        _colEnabled = new DataGridViewCheckBoxColumn { HeaderText = "On", Width = 40 };
        _colProcess = new DataGridViewTextBoxColumn { HeaderText = "Process", Width = 150, ReadOnly = true };
        _colTitle = new DataGridViewTextBoxColumn { HeaderText = "Captured Title", Width = 250, ReadOnly = true };
        _colMatch = new DataGridViewTextBoxColumn { HeaderText = "Title Contains (optional)", Width = 160 };
        _colMonitor = new DataGridViewComboBoxColumn { HeaderText = "Target Monitor", Width = 120 };
        _colState = new DataGridViewComboBoxColumn { HeaderText = "State", Width = 90 };
        _colPos = new DataGridViewTextBoxColumn { HeaderText = "Pos / Size", Width = 150, ReadOnly = true };

        foreach (var m in _monitors)
            _colMonitor.Items.Add(MonitorLabel(m));
        _colState.Items.AddRange("Normal", "Maximized", "Minimized");

        _grid.Columns.AddRange(_colEnabled, _colProcess, _colTitle, _colMatch, _colMonitor, _colState, _colPos);
    }

    /// <summary>Dropdown caption for a monitor. Includes the layout position so two
    /// monitors of the same resolution get distinct entries.</summary>
    private string MonitorLabel(MonitorInfo m) =>
        $"#{m.Index + 1} ({m.Bounds.Width}x{m.Bounds.Height} @ {m.Bounds.Left},{m.Bounds.Top})" +
        (m.IsPrimary ? " *" : "");

    private void LoadRows()
    {
        _grid.Rows.Clear();
        foreach (var rule in _profile.Rules)
        {
            int idx = _grid.Rows.Add();
            var row = _grid.Rows[idx];
            row.Cells[_colEnabled.Index].Value = rule.Enabled;
            row.Cells[_colProcess.Index].Value = rule.ProcessName;
            row.Cells[_colTitle.Index].Value = rule.DisplayTitle;
            row.Cells[_colMatch.Index].Value = rule.TitleContains ?? "";

            var mon = rule.ResolveMonitor(_monitors);
            row.Cells[_colMonitor.Index].Value = mon != null ? MonitorLabel(mon) : null;
            row.Cells[_colState.Index].Value = rule.State.ToString();
            row.Cells[_colPos.Index].Value = $"{rule.RelativeX},{rule.RelativeY}  {rule.Width}x{rule.Height}";
            row.Tag = rule;
        }
    }

    private void RemoveSelected()
    {
        var toRemove = _grid.SelectedRows.Cast<DataGridViewRow>().ToList();
        foreach (var row in toRemove)
        {
            if (row.Tag is AppRule rule) _profile.Rules.Remove(rule);
            _grid.Rows.Remove(row);
        }
    }

    private void CommitToProfile()
    {
        _profile.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? _profile.Name : _nameBox.Text.Trim();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not AppRule rule) continue;
            rule.Enabled = row.Cells[_colEnabled.Index].Value is true;
            rule.TitleContains = (row.Cells[_colMatch.Index].Value as string)?.Trim() is { Length: > 0 } tc ? tc : null;

            // Target monitor
            if (row.Cells[_colMonitor.Index].Value is string monLabel)
            {
                var mon = _monitors.FirstOrDefault(m => MonitorLabel(m) == monLabel);
                if (mon != null) rule.SetTargetMonitor(mon);
            }

            if (row.Cells[_colState.Index].Value is string st && Enum.TryParse<WinState>(st, out var ws))
                rule.State = ws;
        }
    }
}
