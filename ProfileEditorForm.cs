using System.Drawing;
using System.Windows.Forms;

namespace MonitorMover;

/// <summary>
/// Lets the user prune and tweak the rules of a captured profile: toggle rows on/off,
/// refine the title match, and pick the target monitor / state.
/// </summary>
/// <remarks>
/// When a <c>baseline</c> (the profile's currently saved state) is supplied, the lower
/// pane shows a live comparison of the edits against it and <b>Save</b> stays disabled
/// until something actually differs — so re-capturing a layout is never a blind
/// overwrite, and a no-op save is impossible.
/// </remarks>
public sealed class ProfileEditorForm : Form
{
    private readonly Profile _profile;
    private readonly Profile? _baseline;
    private readonly ISet<string>? _runningProcesses;
    private readonly List<MonitorInfo> _monitors;
    private readonly DataGridView _grid = new();
    private readonly TextBox _nameBox = new();

    private readonly DataGridView _diffGrid = new();
    private readonly Label _diffHeader = new();
    private readonly Label _diffEmptyNote = new();
    private readonly CheckBox _showUnchanged = new();
    private SplitContainer? _split;
    private Button _save = null!;

    public Profile Result => _profile;

    /// <summary>Number of differences between the saved profile and the edited one
    /// (0 when there is no baseline to compare against).</summary>
    public int ChangeCount { get; private set; }

    public ProfileEditorForm(Profile profile, List<MonitorInfo> monitors, Profile? baseline = null,
        ISet<string>? runningProcesses = null)
    {
        _profile = profile;
        _baseline = baseline;
        _runningProcesses = runningProcesses;
        _monitors = monitors;

        Text = "Edit Profile";
        AppIcon.Apply(this);
        Width = 980;
        Height = baseline == null ? 560 : 760;
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

        // With a baseline the rules and the comparison share the space through a draggable
        // splitter; without one the rules grid simply fills the dialog.
        var diffPanel = BuildDiffPanel();
        Control center = _grid;
        if (diffPanel != null)
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 120,
                Panel2MinSize = 130
            };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(diffPanel);
            _split = split;
            center = split;
        }

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

        _save = MakeButton("Save", DialogResult.OK);
        var cancel = MakeButton("Cancel", DialogResult.Cancel);
        var del = MakeButton("Remove Selected Rows");
        del.Margin = new Padding(0);
        del.Click += (_, _) => RemoveSelected();
        bottom.Controls.Add(del, 0, 0);
        bottom.Controls.Add(_save, 2, 0);
        bottom.Controls.Add(cancel, 3, 0);

        AcceptButton = _save;
        CancelButton = cancel;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 0, 8, 6),
            Text = "One row per application: every open window of that .exe is sent to its target " +
                   "monitor. Fill in \"Title Contains\" only to give one particular window a target of its own."
        };

        // Docking is applied outermost-last, so add from the inside out: the rules/diff
        // area takes what is left, then the header rows, then the button row.
        Controls.Add(center);
        Controls.Add(hint);
        Controls.Add(top);
        Controls.Add(bottom);

        _save.Click += (_, _) => CommitToProfile();

        LoadRows();

        // Any edit can change the diff, so recompute as the user works. Checkbox and
        // combo cells only raise CellValueChanged once the edit is pushed out of the cell.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, _) => RefreshDiff();
        _nameBox.TextChanged += (_, _) => RefreshDiff();
        _showUnchanged.CheckedChanged += (_, _) => RefreshDiff();

        RefreshDiff();
        // The splitter position can only be set once the container has a real height.
        Load += (_, _) => PlaceSplitter();
        Shown += (_, _) =>
        {
            _nameBox.SelectionStart = 0;
            _nameBox.SelectionLength = 0;
            _nameBox.ScrollToCaret();
        };
    }

    /// <summary>Give the comparison pane roughly 40% of the height, within its limits.</summary>
    private void PlaceSplitter()
    {
        if (_split == null) return;
        int height = _split.Height;
        int distance = height - (int)(height * 0.4) - _split.SplitterWidth;
        if (distance < _split.Panel1MinSize ||
            distance > height - _split.Panel2MinSize - _split.SplitterWidth) return;
        _split.SplitterDistance = distance;
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
        RefreshDiff();
    }

    /// <summary>Push the grid's cell values into the rule objects.</summary>
    private void SyncRowsToRules()
    {
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

    private void CommitToProfile()
    {
        _profile.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? _profile.Name : _nameBox.Text.Trim();
        SyncRowsToRules();

        // Clearing a title filter can leave two general rules for one .exe; only the
        // first would ever match, so drop the rest.
        _profile.CollapseToOneRulePerProcess();
    }

    // ------------------------------------------------------------ comparison pane

    private static readonly Color AddedBack = Color.FromArgb(233, 247, 234);
    private static readonly Color AddedFore = Color.FromArgb(22, 101, 43);
    private static readonly Color RemovedBack = Color.FromArgb(253, 234, 234);
    private static readonly Color RemovedFore = Color.FromArgb(160, 26, 26);
    private static readonly Color ChangedBack = Color.FromArgb(255, 248, 224);
    private static readonly Color ChangedFore = Color.FromArgb(140, 88, 0);
    private static readonly Color KeptBack = Color.FromArgb(236, 240, 245);
    private static readonly Color KeptFore = Color.FromArgb(70, 92, 115);
    private static readonly Color QuietFore = Color.FromArgb(130, 130, 130);

    // Banner palette: amber "there are changes", blue "nothing to save".
    private static readonly Color BannerChangedBack = Color.FromArgb(255, 243, 205);
    private static readonly Color BannerChangedFore = Color.FromArgb(124, 77, 0);
    private static readonly Color BannerCleanBack = Color.FromArgb(222, 236, 250);
    private static readonly Color BannerCleanFore = Color.FromArgb(13, 71, 133);

    private DataGridViewTextBoxColumn _dcKind = null!;
    private DataGridViewTextBoxColumn _dcApp = null!;
    private DataGridViewTextBoxColumn _dcMonitor = null!;
    private DataGridViewTextBoxColumn _dcState = null!;
    private DataGridViewTextBoxColumn _dcPos = null!;
    private DataGridViewTextBoxColumn _dcSize = null!;
    private DataGridViewTextBoxColumn _dcNote = null!;

    /// <summary>The "what changed" table, or null when there is no saved version to compare to.</summary>
    private Control? BuildDiffPanel()
    {
        if (_baseline == null) return null;

        // A banner, not a caption line: this is where the user reads whether saving will
        // do anything at all, so it has to be impossible to miss.
        _diffHeader.Dock = DockStyle.Top;
        _diffHeader.AutoSize = false;
        _diffHeader.Height = (int)(Font.Height * 2.4f);
        _diffHeader.TextAlign = ContentAlignment.MiddleLeft;
        _diffHeader.Padding = new Padding(10, 0, 10, 0);
        _diffHeader.Margin = new Padding(0, 0, 0, 6);
        _diffHeader.BorderStyle = BorderStyle.FixedSingle;
        _diffHeader.Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);

        // Shown instead of an empty table when there is nothing to report.
        _diffEmptyNote.Dock = DockStyle.Fill;
        _diffEmptyNote.AutoSize = false;
        _diffEmptyNote.TextAlign = ContentAlignment.MiddleCenter;
        _diffEmptyNote.ForeColor = QuietFore;
        _diffEmptyNote.Visible = false;

        _showUnchanged.Text = "Show unchanged apps";
        _showUnchanged.Dock = DockStyle.Bottom;
        _showUnchanged.AutoSize = true;

        _diffGrid.Dock = DockStyle.Fill;
        _diffGrid.ReadOnly = true;
        _diffGrid.AllowUserToAddRows = false;
        _diffGrid.AllowUserToDeleteRows = false;
        _diffGrid.AllowUserToResizeRows = false;
        _diffGrid.RowHeadersVisible = false;
        _diffGrid.EditMode = DataGridViewEditMode.EditProgrammatically;
        _diffGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        // Proportional widths so nothing is cut off at any dialog size.
        _diffGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _diffGrid.BackgroundColor = SystemColors.Window;
        _diffGrid.BorderStyle = BorderStyle.FixedSingle;
        _diffGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _diffGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _diffGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(210, 226, 242);
        _diffGrid.DefaultCellStyle.SelectionForeColor = Color.Black;

        _dcKind = new DataGridViewTextBoxColumn { HeaderText = "Change", FillWeight = 13, MinimumWidth = 80 };
        _dcApp = new DataGridViewTextBoxColumn { HeaderText = "Application", FillWeight = 24, MinimumWidth = 130 };
        _dcMonitor = new DataGridViewTextBoxColumn { HeaderText = "Monitor", FillWeight = 12, MinimumWidth = 80 };
        _dcState = new DataGridViewTextBoxColumn { HeaderText = "State", FillWeight = 19, MinimumWidth = 110 };
        _dcPos = new DataGridViewTextBoxColumn { HeaderText = "Position", FillWeight = 15, MinimumWidth = 90 };
        _dcSize = new DataGridViewTextBoxColumn { HeaderText = "Size", FillWeight = 15, MinimumWidth = 90 };
        _dcNote = new DataGridViewTextBoxColumn { HeaderText = "Note", FillWeight = 18, MinimumWidth = 110 };
        _diffGrid.Columns.AddRange(_dcKind, _dcApp, _dcMonitor, _dcState, _dcPos, _dcSize, _dcNote);

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 2)
        };
        panel.Controls.Add(_diffGrid);
        panel.Controls.Add(_diffEmptyNote);
        panel.Controls.Add(_showUnchanged);
        panel.Controls.Add(_diffHeader);
        return panel;
    }

    /// <summary>Recompute the comparison against the saved profile and gate Save on it.</summary>
    private void RefreshDiff()
    {
        if (_baseline == null) return;

        SyncRowsToRules();

        string newName = string.IsNullOrWhiteSpace(_nameBox.Text) ? _profile.Name : _nameBox.Text.Trim();
        bool renamed = !newName.Equals(_baseline.Name, StringComparison.Ordinal);

        var rows = new List<LayoutChange>();
        if (renamed)
        {
            rows.Add(new LayoutChange
            {
                Kind = ChangeKind.Changed,
                App = "(profile name)",
                Note = $"{_baseline.Name} → {newName}",
                NoteChanged = true
            });
        }
        rows.AddRange(ProfileStore.Diff(_baseline, _profile, _monitors, _runningProcesses));

        var changes = rows.Where(r => r.IsChange).ToList();
        var kept = rows.Where(r => r.Kind == ChangeKind.Kept).ToList();
        int unchanged = rows.Count - changes.Count - kept.Count;
        ChangeCount = changes.Count;

        // Changes first, then the closed apps whose rules are being preserved — those are
        // not changes, but the user needs to see the profile is not losing them.
        var shown = changes.Concat(kept);
        if (_showUnchanged.Checked)
            shown = shown.Concat(rows.Where(r => r.Kind == ChangeKind.Unchanged));

        _diffGrid.Rows.Clear();
        foreach (var c in shown) AddDiffRow(c);

        string keptText = kept.Count > 0 ? $"{kept.Count} app(s) not running — rules kept" : "";
        string unchangedText = unchanged > 0 ? $"{unchanged} app(s) unchanged" : "";
        string tail = string.Join("   ·   ", new[] { keptText, unchangedText }.Where(s => s.Length > 0));

        if (changes.Count == 0)
        {
            _diffHeader.BackColor = BannerCleanBack;
            _diffHeader.ForeColor = BannerCleanFore;
            _diffHeader.Text = $"✓   NO CHANGES TO SAVE — this layout already matches the saved " +
                               $"profile \"{_baseline.Name}\"" + (tail.Length > 0 ? $"      ({tail})" : "");
            _diffEmptyNote.Text = "Nothing differs from the saved profile, so Save is disabled.\r\n" +
                                  "Move a window and re-open this dialog, or tick \"Show unchanged apps\" " +
                                  "to review what the profile currently holds.";
        }
        else
        {
            _diffHeader.BackColor = BannerChangedBack;
            _diffHeader.ForeColor = BannerChangedFore;
            _diffHeader.Text = $"{changes.Count} CHANGE(S) TO SAVE  vs the saved profile \"{_baseline.Name}\"" +
                               (tail.Length > 0 ? $"      ({tail})" : "");
            _diffEmptyNote.Text = "";
        }

        // Show the placeholder instead of an empty table (only one of the two is visible,
        // so the visible one always gets the whole area).
        bool haveRows = _diffGrid.Rows.Count > 0;
        _diffEmptyNote.Visible = !haveRows;
        _diffGrid.Visible = haveRows;

        // Nothing to write — don't let the user overwrite the profile with itself.
        _save.Enabled = changes.Count > 0;
        _save.Text = changes.Count > 0 ? "Save" : "Nothing to Save";
    }

    private void AddDiffRow(LayoutChange c)
    {
        int idx = _diffGrid.Rows.Add(c.KindText, c.App, c.Monitor, c.State, c.Position, c.Size, c.Note);
        var row = _diffGrid.Rows[idx];

        (Color back, Color fore) = c.Kind switch
        {
            ChangeKind.Added => (AddedBack, AddedFore),
            ChangeKind.Removed => (RemovedBack, RemovedFore),
            ChangeKind.Changed => (ChangedBack, ChangedFore),
            ChangeKind.Kept => (KeptBack, KeptFore),
            _ => (SystemColors.Window, QuietFore)
        };
        row.DefaultCellStyle.BackColor = back;
        row.DefaultCellStyle.ForeColor = fore;

        row.Cells[_dcKind.Index].Style.Font = new Font(_diffGrid.Font, FontStyle.Bold);
        row.Cells[_dcApp.Index].Style.Font = new Font(_diffGrid.Font, FontStyle.Bold);

        // In a changed row only some fields moved: embolden those, mute the rest so the
        // eye lands on what actually differs.
        if (c.Kind == ChangeKind.Changed)
        {
            Highlight(row, _dcMonitor.Index, c.MonitorChanged, fore);
            Highlight(row, _dcState.Index, c.StateChanged, fore);
            Highlight(row, _dcPos.Index, c.PositionChanged, fore);
            Highlight(row, _dcSize.Index, c.SizeChanged, fore);
            Highlight(row, _dcNote.Index, c.NoteChanged, fore);
        }
    }

    private void Highlight(DataGridViewRow row, int column, bool changed, Color fore)
    {
        var cell = row.Cells[column];
        if (changed)
        {
            cell.Style.Font = new Font(_diffGrid.Font, FontStyle.Bold);
            cell.Style.ForeColor = fore;
        }
        else
        {
            cell.Style.ForeColor = QuietFore;
        }
    }
}
