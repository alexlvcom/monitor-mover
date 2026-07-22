using System.Reflection;

namespace MonitorMover;

/// <summary>
/// App version info and human-readable change history shown in Help → About.
/// Add a new entry at the top of <see cref="Entries"/> whenever behaviour changes,
/// and bump &lt;VersionPrefix&gt; in the .csproj to match.
/// </summary>
public static class Changelog
{
    /// <summary>Newest first. Each entry: version, date, and bullet notes.</summary>
    public static readonly (string Version, string Date, string[] Notes)[] Entries =
    {
        ("1.1.4", "2026-07-22", new[]
        {
            "Fixed: profile-bar button captions were clipped at the bottom on scaled",
            "     displays; the toolbar now auto-sizes its controls at any DPI.",
        }),
        ("1.1.3", "2026-07-22", new[]
        {
            "Layout: the monitors pane now defaults to ~14% of the height so the window",
            "     list gets most of the space; drag the splitter to adjust as before.",
        }),
        ("1.1.2", "2026-07-22", new[]
        {
            "New: filter the window list by typing — click any window, then start typing",
            "     and the list narrows to windows whose title/process contains the text.",
            "New: a 'Filter apps' text box above the window list does the same.",
            "     Esc or clearing the box removes the filter. Combines with the monitor filter.",
            "Window header shows matched/total counts.",
        }),
        ("1.1.1", "2026-07-22", new[]
        {
            "Fixed: 'Move To Next Monitor' / 'Move To Primary Monitor' now work for",
            "     maximized (and minimized) windows — they previously spilled off the",
            "     target monitor instead of maximizing on it.",
            "Moves now use SetWindowPlacement, reliable across mixed resolution/DPI.",
            "Applying a profile restores maximized windows onto the correct monitor too.",
        }),
        ("1.1.0", "2026-07-22", new[]
        {
            "Fixed: top pane now shows ALL monitors (menu/toolbar were overlapping the first row).",
            "New: click a monitor in the top pane to filter the window list to that monitor;",
            "     right-click a monitor for 'Show Windows On This Monitor' / 'Show All Monitors'.",
            "New: Help → About now shows the build version and this change history.",
            "Window count is shown in the Windows section header.",
        }),
        ("1.0.0", "2026-07-22", new[]
        {
            "Initial release.",
            "Detect monitors and list all application windows.",
            "Move a window to the next / primary / a specific monitor (F8 / F7 / right-click).",
            "Save the current window layout as a named profile (e.g. Home, Office).",
            "Apply a profile in one click to restore every app to its monitor/position/state.",
            "Command line: --list, --capture, --apply, --dump.",
        }),
    };

    /// <summary>Marketing version, e.g. "1.1.0" (from the newest changelog entry).</summary>
    public static string MarketingVersion => Entries[0].Version;

    /// <summary>Full build version including the auto-incrementing build/revision.</summary>
    public static string BuildVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v?.ToString() ?? MarketingVersion;
        }
    }

    /// <summary>Timestamp this executable was built (from the PE linker header).</summary>
    public static DateTime BuildDate
    {
        get
        {
            try
            {
                // Works for single-file publish (Assembly.Location is empty there).
                string path = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.GetLastWriteTime(path);
            }
            catch { /* ignore */ }
            return DateTime.MinValue;
        }
    }

    public static string FormatFull()
    {
        var lines = new List<string>
        {
            $"MonitorMover  v{MarketingVersion}",
            $"Build {BuildVersion}" +
                (BuildDate == DateTime.MinValue ? "" : $"   (built {BuildDate:yyyy-MM-dd HH:mm})"),
            "",
            "Detect monitors, move application windows between them, and save/apply",
            "layout profiles (e.g. Home vs Office) so your windows snap back to the",
            "right monitors in one click.",
            "",
            "What's changed",
            "──────────────",
        };

        foreach (var (version, date, notes) in Entries)
        {
            lines.Add("");
            lines.Add($"v{version}  ({date})");
            foreach (var n in notes)
                lines.Add("  • " + n);
        }
        return string.Join(Environment.NewLine, lines);
    }
}
