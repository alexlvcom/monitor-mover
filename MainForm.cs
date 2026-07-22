using System.Drawing;
using System.Windows.Forms;

namespace MonitorMover;

public sealed class MainForm : Form
{
    private readonly ProfileStore _store = new();

    private readonly ListView _monitorList = new();
    private readonly ListView _windowList = new();
    private readonly ComboBox _profileCombo = new();

    private SplitContainer _split = null!;
    private Label _windowsHeader = null!;
    private readonly TextBox _filterBox = new();

    private List<MonitorInfo> _monitors = new();
    private List<WindowInfo> _windows = new();

    /// <summary>Device name of the monitor whose windows are shown, or null for all.</summary>
    private string? _monitorFilter;

    /// <summary>Free-text substring to match against window title / process name.</summary>
    private string _textFilter = "";

    public MainForm()
    {
        Text = $"MonitorMover  v{Changelog.MarketingVersion}";
        Width = 1200;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;

        // Deterministic layout via a TableLayoutPanel — rows never overlap, unlike
        // ambiguous Dock z-ordering.
        var menu = BuildMenu();
        var profileBar = BuildProfileBar();
        var split = BuildLists();
        var status = BuildStatusBar();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // menu
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));    // profile bar
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // split (monitors/windows)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // status bar

        root.Controls.Add(menu, 0, 0);
        root.Controls.Add(profileBar, 0, 1);
        root.Controls.Add(split, 0, 2);
        root.Controls.Add(status, 0, 3);

        Controls.Add(root);
        MainMenuStrip = menu;

        Load += (_, _) =>
        {
            RefreshAll();
            SizeMonitorPane();
        };
    }

    /// <summary>
    /// Make the monitors pane just tall enough for its header + the monitors present
    /// (plus a small buffer), so the window list gets the rest of the height. Capped
    /// at ~28% so many monitors can't dominate. The user can still drag the splitter.
    /// </summary>
    private void SizeMonitorPane()
    {
        int rowH = _monitorList.Items.Count > 0 ? _monitorList.Items[0].Bounds.Height : 20;
        int want = 22 /*section header*/ + 26 /*column header*/
                   + rowH * (Math.Max(2, _monitors.Count) + 1) /*+1 spare row*/ + 8;

        int cap = (int)(_split.Height * 0.28);                 // never dominate
        int hardMax = _split.Height - _split.Panel2MinSize - 10;
        int target = Math.Min(Math.Min(want, cap), hardMax);
        target = Math.Max(_split.Panel1MinSize + 1, target);

        if (target > _split.Panel1MinSize && target < hardMax)
            _split.SplitterDistance = target;
    }

    // ------------------------------------------------------------- UI build

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Fill };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Refresh", null, (_, _) => RefreshAll()) { ShortcutKeys = Keys.F5 });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Open Profiles &Folder", null, (_, _) => OpenProfilesFolder());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var window = new ToolStripMenuItem("&Window");
        var toNext = new ToolStripMenuItem("Move To &Next Monitor", null, (_, _) => MoveSelectedToNext()) { ShortcutKeys = Keys.F8 };
        var toPrimary = new ToolStripMenuItem("Move To &Primary Monitor", null, (_, _) => MoveSelectedToPrimary()) { ShortcutKeys = Keys.F7 };
        window.DropDownItems.Add(toNext);
        window.DropDownItems.Add(toPrimary);

        var profiles = new ToolStripMenuItem("&Profiles");
        profiles.DropDownItems.Add("&Save Current Layout as Profile…", null, (_, _) => SaveCurrentLayout());
        profiles.DropDownItems.Add("Add &Selected Windows to Profile…", null, (_, _) => AddSelectedToProfile());
        profiles.DropDownItems.Add(new ToolStripSeparator());
        profiles.DropDownItems.Add("&Edit Selected Profile…", null, (_, _) => EditSelectedProfile());
        profiles.DropDownItems.Add("&Apply Selected Profile", null, (_, _) => ApplySelectedProfile());
        profiles.DropDownItems.Add("&Delete Selected Profile", null, (_, _) => DeleteSelectedProfile());

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => ShowAbout());

        menu.Items.AddRange(new ToolStripItem[] { file, window, profiles, help });
        return menu;
    }

    private Panel BuildProfileBar()
    {
        // FlowLayoutPanel + AutoSize controls so captions never clip at any DPI/scale.
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(6, 4, 6, 4)
        };

        var lbl = new Label { Text = "Profile:", AutoSize = true, Margin = new Padding(2, 9, 4, 0) };
        _profileCombo.Width = 220;
        _profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _profileCombo.Margin = new Padding(0, 5, 10, 0);

        Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(3, 4, 3, 4),
                Padding = new Padding(8, 3, 8, 3)
            };
            b.Click += onClick;
            return b;
        }

        var apply = MakeButton("Apply", (_, _) => ApplySelectedProfile());
        var save = MakeButton("Save Current Layout…", (_, _) => SaveCurrentLayout());
        var edit = MakeButton("Edit…", (_, _) => EditSelectedProfile());
        var refresh = MakeButton("Refresh (F5)", (_, _) => RefreshAll());

        bar.Controls.AddRange(new Control[] { lbl, _profileCombo, apply, save, edit, refresh });
        return bar;
    }

    private SplitContainer BuildLists()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 120,
            Panel1MinSize = 60,
            Panel2MinSize = 120
        };

        // --- Monitors (top) ---
        _monitorList.View = View.Details;
        _monitorList.FullRowSelect = true;
        _monitorList.GridLines = true;
        _monitorList.Dock = DockStyle.Fill;
        _monitorList.Columns.Add("Monitor", 70);
        _monitorList.Columns.Add("Resolution", 110);
        _monitorList.Columns.Add("Left-Top", 90);
        _monitorList.Columns.Add("Right-Bottom", 100);
        _monitorList.Columns.Add("Primary", 60);
        _monitorList.Columns.Add("Work Area", 160);
        _monitorList.Columns.Add("Colors", 60);
        _monitorList.Columns.Add("Device Name", 160);
        _monitorList.ContextMenuStrip = BuildMonitorContextMenu();
        // Click a monitor -> show only its windows below.
        _monitorList.SelectedIndexChanged += (_, _) =>
        {
            if (_monitorList.SelectedItems.Count > 0 &&
                _monitorList.SelectedItems[0].Tag is MonitorInfo m)
            {
                _monitorFilter = m.DeviceName;
                PopulateWindows();
            }
        };

        // --- Windows (bottom) ---
        _windowList.View = View.Details;
        _windowList.FullRowSelect = true;
        _windowList.GridLines = true;
        _windowList.Dock = DockStyle.Fill;
        _windowList.MultiSelect = true;
        _windowList.Columns.Add("Title", 260);
        _windowList.Columns.Add("Monitor", 70);
        _windowList.Columns.Add("Left-Top", 90);
        _windowList.Columns.Add("Size", 100);
        _windowList.Columns.Add("State", 80);
        _windowList.Columns.Add("Class", 150);
        _windowList.Columns.Add("Process", 140);
        _windowList.Columns.Add("Path", 300);
        _windowList.ContextMenuStrip = BuildWindowContextMenu();
        // Type-to-filter: when the list has focus, typing narrows the list instead of
        // triggering the ListView's built-in jump-to-item search.
        _windowList.KeyPress += WindowListKeyPress;

        var monHeader = new Label
        {
            Text = "Monitors  (click one to filter the window list below)",
            Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0)
        };
        _windowsHeader = new Label
        {
            Text = "Windows — all monitors",
            Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0)
        };

        split.Panel1.Controls.Add(WrapWithHeader(_monitorList, monHeader));
        split.Panel2.Controls.Add(BuildWindowsPanel());

        _split = split;
        return split;
    }

    /// <summary>Windows panel: a filter row (label + text box + status) on top of the list.</summary>
    private TableLayoutPanel BuildWindowsPanel()
    {
        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var lbl = new Label
        {
            Text = "Filter apps:", AutoSize = true, Anchor = AnchorStyles.Left,
            Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 4, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _filterBox.Dock = DockStyle.Fill;
        _filterBox.Margin = new Padding(0, 3, 0, 3);
        _filterBox.PlaceholderText = "type part of a window/app name…";
        _filterBox.TextChanged += (_, _) =>
        {
            _textFilter = _filterBox.Text;
            PopulateWindows();
        };
        // Esc in the box clears the filter.
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _filterBox.Clear(); e.Handled = e.SuppressKeyPress = true; }
        };

        _windowsHeader.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        bar.Controls.Add(lbl, 0, 0);
        bar.Controls.Add(_filterBox, 1, 0);
        bar.Controls.Add(_windowsHeader, 2, 0);

        tlp.Controls.Add(bar, 0, 0);
        tlp.Controls.Add(_windowList, 0, 1);
        return tlp;
    }

    /// <summary>Route printable keys typed over the list into the filter box.</summary>
    private void WindowListKeyPress(object? sender, KeyPressEventArgs e)
    {
        switch (e.KeyChar)
        {
            case (char)27: // Esc -> clear
                _filterBox.Clear();
                break;
            case (char)8:  // Backspace -> remove last char
                if (_filterBox.TextLength > 0)
                    _filterBox.Text = _filterBox.Text[..^1];
                break;
            default:
                if (char.IsControl(e.KeyChar)) return; // let arrows/enter through
                _filterBox.Text += e.KeyChar;
                break;
        }
        _filterBox.SelectionStart = _filterBox.TextLength;
        e.Handled = true; // suppress ListView's jump-to-item search
    }

    /// <summary>Header label on top of a list, stacked with a TableLayoutPanel (no overlap).</summary>
    private static TableLayoutPanel WrapWithHeader(Control list, Control header)
    {
        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tlp.Controls.Add(header, 0, 0);
        tlp.Controls.Add(list, 0, 1);
        return tlp;
    }

    private ContextMenuStrip BuildMonitorContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Windows On This Monitor", null, (_, _) =>
        {
            if (_monitorList.SelectedItems.Count > 0 &&
                _monitorList.SelectedItems[0].Tag is MonitorInfo m)
            {
                _monitorFilter = m.DeviceName;
                PopulateWindows();
            }
        });
        menu.Items.Add("Show Windows On All Monitors", null, (_, _) =>
        {
            _monitorFilter = null;
            _monitorList.SelectedItems.Clear();
            PopulateWindows();
        });
        return menu;
    }

    private ContextMenuStrip BuildWindowContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Move To Next Monitor (F8)", null, (_, _) => MoveSelectedToNext());
        menu.Items.Add("Move To Primary Monitor (F7)", null, (_, _) => MoveSelectedToPrimary());
        var toMon = new ToolStripMenuItem("Move To Monitor");
        menu.Items.Add(toMon);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Add Selected to Profile…", null, (_, _) => AddSelectedToProfile());
        menu.Items.Add("Bring To Front", null, (_, _) => BringSelectedToFront());

        // Populate the "Move To Monitor" submenu on open.
        menu.Opening += (_, _) =>
        {
            toMon.DropDownItems.Clear();
            foreach (var m in _monitors)
            {
                var captured = m;
                toMon.DropDownItems.Add($"Monitor #{m.Index + 1} ({m.ResolutionText})", null,
                    (_, _) => MoveSelectedToMonitor(captured));
            }
        };
        return menu;
    }

    private StatusStrip BuildStatusBar()
    {
        var strip = new StatusStrip { Dock = DockStyle.Fill };
        var item = new ToolStripStatusLabel { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        strip.Items.Add(item);
        _statusStrip = item;
        return strip;
    }

    private ToolStripStatusLabel _statusStrip = null!;

    private void SetStatus(string text) => _statusStrip.Text = text;

    // ------------------------------------------------------------- Data refresh

    private void RefreshAll()
    {
        _monitors = WindowManager.GetMonitors();
        _windows = WindowManager.GetWindows();
        PopulateMonitors();
        PopulateWindows();
        PopulateProfiles();
        SetStatus($"{_monitors.Count} monitor(s), {_windows.Count} window(s)  —  profiles: {_store.FilePath}");
    }

    private void PopulateMonitors()
    {
        _monitorList.BeginUpdate();
        _monitorList.Items.Clear();
        foreach (var m in _monitors)
        {
            var it = new ListViewItem($"#{m.Index + 1}");
            it.SubItems.Add(m.ResolutionText);
            it.SubItems.Add($"{m.Bounds.Left}, {m.Bounds.Top}");
            it.SubItems.Add($"{m.Bounds.Right}, {m.Bounds.Bottom}");
            it.SubItems.Add(m.IsPrimary ? "Yes" : "No");
            it.SubItems.Add($"{m.WorkingArea.Width}x{m.WorkingArea.Height} @ {m.WorkingArea.Left},{m.WorkingArea.Top}");
            it.SubItems.Add(m.BitsPerPixel > 0 ? m.BitsPerPixel.ToString() : "");
            it.SubItems.Add(m.DeviceName);
            it.Tag = m;
            if (m.DeviceName == _monitorFilter) it.Selected = true;
            _monitorList.Items.Add(it);
        }
        _monitorList.EndUpdate();
    }

    private void PopulateWindows()
    {
        // Drop a stale filter if that monitor is gone (e.g. after a layout change).
        if (_monitorFilter != null && _monitors.All(m => m.DeviceName != _monitorFilter))
            _monitorFilter = null;

        // Apply the monitor filter and the free-text filter (matched against title
        // or process name), combined with AND.
        string text = _textFilter?.Trim() ?? "";
        var shown = _windows.Where(w =>
            (_monitorFilter == null || w.MonitorDeviceName == _monitorFilter) &&
            (text.Length == 0 ||
             w.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             w.ProcessName.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Header text reflects the current filters.
        string scope = _monitorFilter == null
            ? "all monitors"
            : _monitors.FirstOrDefault(m => m.DeviceName == _monitorFilter) is { } fm
                ? $"monitor #{fm.Index + 1} ({fm.ResolutionText})"
                : "monitor";
        string filterNote = text.Length > 0 ? $"  filter \"{text}\"" : "";
        int total = _monitorFilter == null
            ? _windows.Count
            : _windows.Count(w => w.MonitorDeviceName == _monitorFilter);
        _windowsHeader.Text = $" Windows — {scope}{filterNote}  ({shown.Count}/{total})";

        _windowList.BeginUpdate();
        _windowList.Items.Clear();
        foreach (var w in shown)
        {
            var it = new ListViewItem(w.Title);
            var mon = _monitors.FirstOrDefault(m => m.DeviceName == w.MonitorDeviceName);
            it.SubItems.Add(mon != null ? $"#{mon.Index + 1}" : "?");
            it.SubItems.Add(w.LeftTop);
            it.SubItems.Add(w.SizeText);
            it.SubItems.Add(w.State.ToString());
            it.SubItems.Add(w.ClassName);
            it.SubItems.Add(w.ProcessName);
            it.SubItems.Add(w.ProcessPath);
            it.Tag = w;
            _windowList.Items.Add(it);
        }
        _windowList.EndUpdate();
    }

    private void PopulateProfiles()
    {
        string? current = _profileCombo.SelectedItem as string;
        _profileCombo.Items.Clear();
        foreach (var p in _store.Profiles.OrderBy(p => p.Name))
            _profileCombo.Items.Add(p.Name);
        if (current != null && _profileCombo.Items.Contains(current))
            _profileCombo.SelectedItem = current;
        else if (_profileCombo.Items.Count > 0)
            _profileCombo.SelectedIndex = 0;
    }

    // ------------------------------------------------------------- Selection helpers

    private List<WindowInfo> SelectedWindows() =>
        _windowList.SelectedItems.Cast<ListViewItem>()
            .Select(i => (WindowInfo)i.Tag!).ToList();

    private Profile? SelectedProfile() =>
        _profileCombo.SelectedItem is string name ? _store.Find(name) : null;

    // ------------------------------------------------------------- Move actions

    private void MoveSelectedToNext()
    {
        var sel = SelectedWindows();
        if (sel.Count == 0 || _monitors.Count < 2) { SetStatus("Select a window; need ≥2 monitors."); return; }

        foreach (var w in sel)
        {
            var cur = WindowManager.MonitorForWindow(w, _monitors);
            var next = _monitors[(cur.Index + 1) % _monitors.Count];
            WindowManager.MoveToMonitorKeepRelative(w, next, _monitors);
        }
        SetStatus($"Moved {sel.Count} window(s) to next monitor.");
        RefreshAll();
    }

    private void MoveSelectedToPrimary()
    {
        var sel = SelectedWindows();
        var primary = WindowManager.PrimaryMonitor(_monitors);
        if (sel.Count == 0 || primary == null) { SetStatus("Select a window first."); return; }

        foreach (var w in sel)
            WindowManager.MoveToMonitorKeepRelative(w, primary, _monitors);
        SetStatus($"Moved {sel.Count} window(s) to primary monitor.");
        RefreshAll();
    }

    private void MoveSelectedToMonitor(MonitorInfo target)
    {
        var sel = SelectedWindows();
        if (sel.Count == 0) { SetStatus("Select a window first."); return; }
        foreach (var w in sel)
            WindowManager.MoveToMonitorKeepRelative(w, target, _monitors);
        SetStatus($"Moved {sel.Count} window(s) to monitor #{target.Index + 1}.");
        RefreshAll();
    }

    private void BringSelectedToFront()
    {
        foreach (var w in SelectedWindows())
        {
            NativeMethods.ShowWindow(w.Handle, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(w.Handle);
        }
    }

    // ------------------------------------------------------------- Profile actions

    private void SaveCurrentLayout()
    {
        string? name = Prompt.Text("Save Layout", "Name this profile (e.g. Home, Office):", "Office");
        if (name == null) return;

        if (_store.Find(name) != null &&
            MessageBox.Show($"Profile \"{name}\" exists. Overwrite?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var profile = ProfileStore.CaptureCurrent(name, _windows, _monitors);

        // Let the user immediately prune the captured list.
        using var editor = new ProfileEditorForm(profile, _monitors);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        _store.AddOrReplace(editor.Result);
        PopulateProfiles();
        _profileCombo.SelectedItem = editor.Result.Name;
        SetStatus($"Saved profile \"{editor.Result.Name}\" with {editor.Result.Rules.Count(r => r.Enabled)} enabled rule(s).");
    }

    private void AddSelectedToProfile()
    {
        var sel = SelectedWindows();
        if (sel.Count == 0) { SetStatus("Select one or more windows first."); return; }

        string? name = Prompt.Text("Add to Profile", "Add selected windows to which profile? (new or existing name):",
            SelectedProfile()?.Name ?? "Office");
        if (name == null) return;

        var profile = _store.Find(name) ?? new Profile { Name = name };
        var captured = ProfileStore.CaptureCurrent(name, sel, _monitors);

        // Merge: replace any rule with the same process+title, else add.
        foreach (var rule in captured.Rules)
        {
            profile.Rules.RemoveAll(r =>
                r.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                (r.DisplayTitle == rule.DisplayTitle));
            profile.Rules.Add(rule);
        }
        if (string.IsNullOrEmpty(profile.MonitorSignature))
            profile.MonitorSignature = captured.MonitorSignature;

        _store.AddOrReplace(profile);
        PopulateProfiles();
        _profileCombo.SelectedItem = profile.Name;
        SetStatus($"Added {sel.Count} window(s) to profile \"{profile.Name}\".");
    }

    private void EditSelectedProfile()
    {
        var profile = SelectedProfile();
        if (profile == null) { SetStatus("Select a profile first."); return; }

        using var editor = new ProfileEditorForm(profile, _monitors);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        _store.AddOrReplace(editor.Result);
        PopulateProfiles();
        _profileCombo.SelectedItem = editor.Result.Name;
        SetStatus($"Updated profile \"{editor.Result.Name}\".");
    }

    private void ApplySelectedProfile()
    {
        var profile = SelectedProfile();
        if (profile == null) { SetStatus("Select a profile first."); return; }

        // Use a fresh window snapshot so we target what is open right now.
        _windows = WindowManager.GetWindows();
        _monitors = WindowManager.GetMonitors();

        var log = ProfileStore.Apply(profile, _windows, _monitors);
        RefreshAll();

        int ok = log.Count(l => l.StartsWith("OK"));
        SetStatus($"Applied \"{profile.Name}\": {ok}/{log.Count} rule(s) placed.");
        ResultViewer.Show($"Apply Profile — {profile.Name}", log);
    }

    private void DeleteSelectedProfile()
    {
        var profile = SelectedProfile();
        if (profile == null) { SetStatus("Select a profile first."); return; }
        if (MessageBox.Show($"Delete profile \"{profile.Name}\"?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _store.Remove(profile.Name);
        PopulateProfiles();
        SetStatus($"Deleted profile \"{profile.Name}\".");
    }

    private void OpenProfilesFolder()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_store.FilePath}\"");
        }
        catch { /* ignore */ }
    }

    private void ShowAbout()
    {
        var text = Changelog.FormatFull() +
                   Environment.NewLine + Environment.NewLine +
                   "Profiles file:" + Environment.NewLine + _store.FilePath;
        ResultViewer.Show($"About MonitorMover — v{Changelog.MarketingVersion}", text.Split('\n').Select(l => l.TrimEnd('\r')));
    }
}
