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
        ("1.1.9", "2026-07-29", new[]
        {
            "Fixed: applying a profile moved only one window of an app, leaving other",
            "     instances behind on the wrong monitor. A rule now places EVERY open",
            "     window of its executable — all Chrome windows, all Evernote notes, all",
            "     PhpStorm projects go to the app's monitor.",
            "Profiles now store one rule per executable instead of one per window;",
            "     profiles saved earlier are folded down to that automatically on load.",
            "     Extra instances of a normal-state window are cascaded slightly so they",
            "     don't sit exactly on top of each other.",
            "A \"Title Contains\" filter still gives one particular window a target of its",
            "     own, and such rules are applied before the app's general rule.",
            "New: updating a profile (Save Current Layout, or Edit) now shows a colour-",
            "     coded comparison table in the dialog, above the Save button: green for",
            "     added apps, red for removed, amber for changed, with only the fields",
            "     that actually moved (monitor, state, position, size) highlighted.",
            "     It refreshes live as rows are edited or the profile is renamed, and",
            "     unchanged apps are collapsed into a count you can expand.",
            "New: while nothing differs from the saved profile the Save button is disabled",
            "     and reads \"Nothing to Save\", and a full-width banner spells it out — so a",
            "     profile can no longer be overwritten with an identical copy, and the empty",
            "     table area explains why instead of sitting blank.",
        }),
        ("1.1.8", "2026-07-28", new[]
        {
            "Fixed: Save Current Layout now updates the selected profile directly",
            "     instead of prompting for a new profile name such as \"Office\".",
            "New: use Profiles → Save Current Layout as New Profile when a separate",
            "     profile is wanted.",
            "Fixed: toolbar and profile-editor button captions no longer clip on",
            "     scaled displays.",
            "Fixed: the profile name field no longer overlaps its label or hides the",
            "     beginning of the name at higher DPI settings.",
            "New: a Delete button beside Edit makes removing the selected profile",
            "     available directly from the toolbar, with confirmation.",
        }),
        ("1.1.7", "2026-07-28", new[]
        {
            "Fixed: two monitors of the same resolution could not be told apart, so a",
            "     profile could restore windows to the wrong one of the pair. Rules now",
            "     also record the monitor's desktop-layout position (0,0 is always the",
            "     primary) and match on it right after resolution.",
            "Monitors are now numbered by layout — #1 is always the monitor at 0,0,",
            "     then left-to-right — instead of by the device order Windows reshuffles",
            "     on re-dock. The profile editor's monitor list shows the position too.",
        }),
        ("1.1.6", "2026-07-28", new[]
        {
            "New: the window list now shows each app's icon next to its title,",
            "     like MultiMonitorTool. Icons are read from the window (best for",
            "     packaged/UWP apps) with the executable's icon as a fallback.",
        }),
        ("1.1.5", "2026-07-28", new[]
        {
            "Fixed: applying a profile after unplugging and reconnecting monitors sent",
            "     most windows to the primary screen. Windows reassigns display device",
            "     names on re-dock, so rules are now matched to monitors by resolution",
            "     and primary state first, with device name/index only breaking ties.",
        }),
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
