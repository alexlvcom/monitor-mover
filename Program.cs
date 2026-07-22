using System.Windows.Forms;

namespace MonitorMover;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Headless CLI mode: apply a profile without showing the UI.
        //   MonitorMover.exe --apply "Office"
        //   MonitorMover.exe --list
        if (args.Length > 0)
        {
            if (RunCli(args)) return;
        }

        Application.Run(new MainForm());
    }

    /// <summary>Returns true if a CLI command was handled (so the GUI should not start).</summary>
    private static bool RunCli(string[] args)
    {
        var store = new ProfileStore();

        switch (args[0].ToLowerInvariant())
        {
            case "--list":
            case "-l":
                var names = store.Profiles.Count == 0
                    ? "(no profiles saved)"
                    : string.Join(Environment.NewLine, store.Profiles.Select(p => $"  {p.Name}  ({p.Rules.Count} rules)"));
                MessageBox.Show("Saved profiles:\n\n" + names, "MonitorMover", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;

            case "--apply":
            case "-a":
                if (args.Length < 2)
                {
                    MessageBox.Show("Usage: MonitorMover.exe --apply \"ProfileName\"", "MonitorMover",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return true;
                }
                var profile = store.Find(args[1]);
                if (profile == null)
                {
                    MessageBox.Show($"Profile \"{args[1]}\" not found.", "MonitorMover",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return true;
                }
                var windows = WindowManager.GetWindows();
                var monitors = WindowManager.GetMonitors();
                var log = ProfileStore.Apply(profile, windows, monitors);
                int ok = log.Count(l => l.StartsWith("OK"));
                // Silent success unless something was skipped/failed.
                if (ok < log.Count)
                    ResultViewer.Show($"Apply Profile — {profile.Name}", log);
                return true;

            case "--capture":
            case "-c":
                if (args.Length < 2)
                {
                    MessageBox.Show("Usage: MonitorMover.exe --capture \"ProfileName\"", "MonitorMover",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return true;
                }
                var capMon = WindowManager.GetMonitors();
                var capWin = WindowManager.GetWindows();
                var captured = ProfileStore.CaptureCurrent(args[1], capWin, capMon);
                store.AddOrReplace(captured);
                return true;

            case "--movetest":
            {
                // Diagnostic: move the first window matching arg[1] to the next monitor,
                // logging before/after. Runs in-process (consistent DPI coordinates).
                var mtMons = WindowManager.GetMonitors();
                var mtWins = WindowManager.GetWindows();
                var match = args.Length > 1
                    ? mtWins.FirstOrDefault(w => w.Title.Contains(args[1], StringComparison.OrdinalIgnoreCase))
                    : null;
                var mtSb = new System.Text.StringBuilder();
                if (match == null) { mtSb.AppendLine("no matching window"); }
                else
                {
                    var cur = WindowManager.MonitorForWindow(match, mtMons);
                    var next = mtMons[(cur.Index + 1) % mtMons.Count];
                    mtSb.AppendLine($"BEFORE: \"{match.Title}\" state={match.State} on monitor #{cur.Index + 1} ({cur.DeviceName})");
                    mtSb.AppendLine($"        bounds={match.Bounds}");
                    mtSb.AppendLine($"MOVE  -> next monitor #{next.Index + 1} ({next.DeviceName})");
                    WindowManager.MoveToMonitorKeepRelative(match, next, mtMons);
                    System.Threading.Thread.Sleep(500);
                    var after = WindowManager.GetWindows().FirstOrDefault(w => w.Handle == match.Handle);
                    if (after != null)
                    {
                        var am = WindowManager.MonitorForWindow(after, WindowManager.GetMonitors());
                        mtSb.AppendLine($"AFTER:  state={after.State} on monitor #{am.Index + 1} ({am.DeviceName})");
                        mtSb.AppendLine($"        bounds={after.Bounds}");
                        mtSb.AppendLine(am.DeviceName == next.DeviceName ? "RESULT: OK moved to next monitor" : "RESULT: FAIL still on original");
                    }
                }
                File.WriteAllText(args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "movetest.txt"), mtSb.ToString());
                return true;
            }

            case "--dump":
                // Diagnostic: write detected monitors + windows to a file.
                var mons = WindowManager.GetMonitors();
                var wins = WindowManager.GetWindows();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"MONITORS ({mons.Count}):");
                foreach (var m in mons)
                    sb.AppendLine($"  #{m.Index + 1} {m.ResolutionText} primary={m.IsPrimary} bounds={m.Bounds} dev={m.DeviceName}");
                sb.AppendLine($"\nWINDOWS ({wins.Count}):");
                foreach (var w in wins)
                    sb.AppendLine($"  [{w.ProcessName}] \"{w.Title}\" mon={w.MonitorDeviceName} live={w.LeftTop} eff={w.EffectiveBounds.Left},{w.EffectiveBounds.Top} {w.EffectiveBounds.Width}x{w.EffectiveBounds.Height} {w.State}");
                var outPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "monitormover-dump.txt");
                File.WriteAllText(outPath, sb.ToString());
                return true;

            default:
                return false; // fall through to GUI
        }
    }
}
