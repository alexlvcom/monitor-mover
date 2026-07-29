using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorMover;

/// <summary>
/// One saved rule: "the app matching X belongs on monitor Y at this position/size/state".
/// A rule identifies an <em>executable</em>, not a single window: when applied it moves
/// every open window of that process, so a second Chrome or PhpStorm instance can never
/// be left behind on the wrong monitor. An optional title substring narrows a rule to
/// just the windows whose title contains it, for the rare case where one window of an
/// app really does belong somewhere else.
/// </summary>
public sealed class AppRule
{
    public string ProcessName { get; set; } = "";

    /// <summary>Optional: window title must contain this (case-insensitive) to match.</summary>
    public string? TitleContains { get; set; }

    /// <summary>Human label shown in the editor (usually the captured title).</summary>
    public string DisplayTitle { get; set; } = "";

    // Target monitor identity — several keys for robustness across sessions.
    public string MonitorDeviceName { get; set; } = "";
    public int MonitorIndex { get; set; }
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }

    /// <summary>Whether the target monitor was the primary at capture time
    /// (nullable so profiles saved before this field don't misreport). Primary
    /// state survives disconnect/reconnect far better than the device name.</summary>
    public bool? MonitorIsPrimary { get; set; }

    /// <summary>Top-left of the target monitor in virtual-desktop coordinates
    /// (nullable: profiles saved before these fields must not be read as 0,0,
    /// which is always the primary monitor). This is the desktop-layout position
    /// the user arranged in Display Settings, and it is what tells two monitors
    /// of the same resolution apart.</summary>
    public int? MonitorLeft { get; set; }
    public int? MonitorTop { get; set; }

    // Position relative to the target monitor's top-left, plus size and state.
    public int RelativeX { get; set; }
    public int RelativeY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public WinState State { get; set; }

    public bool Enabled { get; set; } = true;

    public bool Matches(WindowInfo w)
    {
        if (!string.Equals(w.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(TitleContains) &&
            w.Title.IndexOf(TitleContains, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    /// <summary>Record which monitor this rule targets, capturing every match key.</summary>
    public void SetTargetMonitor(MonitorInfo mon)
    {
        MonitorDeviceName = mon.DeviceName;
        MonitorIndex = mon.Index;
        MonitorWidth = mon.Bounds.Width;
        MonitorHeight = mon.Bounds.Height;
        MonitorIsPrimary = mon.IsPrimary;
        MonitorLeft = mon.Bounds.Left;
        MonitorTop = mon.Bounds.Top;
    }

    /// <summary>Resolve the monitor this rule targets in the current monitor set.</summary>
    /// <remarks>
    /// Device names (<c>\\.\DISPLAYn</c>) are reassigned by Windows when monitors are
    /// disconnected and reconnected, so trusting them first sends windows to the wrong
    /// screen after a re-dock. Instead every monitor is ranked on a series of keys,
    /// compared in order and each only breaking ties left by the one before it:
    /// <list type="number">
    ///   <item>resolution — the strongest identity signal;</item>
    ///   <item>desktop-layout position — the origin (0,0) is always the primary and
    ///         every other monitor keeps its arranged offset, so this is what tells
    ///         two monitors of the <em>same</em> resolution apart;</item>
    ///   <item>primary flag;</item>
    ///   <item>device name, then index — both reshuffled on re-dock, last resort.</item>
    /// </list>
    /// </remarks>
    public MonitorInfo? ResolveMonitor(List<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return null;

        MonitorInfo? best = null;
        (long Res, long Pos, int Primary, int Device, int Index) bestKey = default;
        foreach (var m in monitors)
        {
            var key = MatchKey(m);
            if (best == null || key.CompareTo(bestKey) < 0) { best = m; bestKey = key; }
        }
        return best;
    }

    /// <summary>Ranking keys for one candidate monitor; lower is a better match.</summary>
    private (long Res, long Pos, int Primary, int Device, int Index) MatchKey(MonitorInfo m)
    {
        long res = Math.Abs(m.Bounds.Width - MonitorWidth) +
                   Math.Abs(m.Bounds.Height - MonitorHeight);

        // Position is only a signal when the profile actually recorded it — older
        // profiles leave it null and must not all be treated as targeting 0,0.
        long pos = MonitorLeft.HasValue && MonitorTop.HasValue
            ? Math.Abs(m.Bounds.Left - MonitorLeft.Value) + Math.Abs(m.Bounds.Top - MonitorTop.Value)
            : 0;

        int primary = MonitorIsPrimary.HasValue && m.IsPrimary != MonitorIsPrimary.Value ? 1 : 0;
        int device = m.DeviceName == MonitorDeviceName ? 0 : 1;
        int index = m.Index == MonitorIndex ? 0 : 1;
        return (res, pos, primary, device, index);
    }
}

/// <summary>A named collection of rules, e.g. "Home" or "Office".</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";

    /// <summary>Snapshot of the monitor layout when captured (for reference/notes).</summary>
    public string MonitorSignature { get; set; } = "";

    public List<AppRule> Rules { get; set; } = new();

    public override string ToString() => Name;

    /// <summary>
    /// Enforce one rule per executable: since a rule now covers every window of its
    /// process, a second rule for the same process could never claim any window. Keeps
    /// the first rule for each process and drops the later duplicates. Rules narrowed
    /// by <see cref="AppRule.TitleContains"/> are deliberate per-window exceptions and
    /// are left alone. Returns how many rules were removed.
    /// </summary>
    /// <remarks>
    /// Applied when loading, so profiles captured by earlier versions — which stored one
    /// rule per window — collapse to per-executable rules without the user re-capturing.
    /// </remarks>
    public int CollapseToOneRulePerProcess()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<AppRule>(Rules.Count);
        foreach (var r in Rules)
        {
            if (!string.IsNullOrEmpty(r.TitleContains) || seen.Add(r.ProcessName))
                kept.Add(r);
        }
        int removed = Rules.Count - kept.Count;
        Rules = kept;
        return removed;
    }
}

/// <summary>Loads and saves all profiles to a single JSON file under %APPDATA%.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }
    public List<Profile> Profiles { get; private set; } = new();

    public ProfileStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MonitorMover");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "profiles.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Profiles = JsonSerializer.Deserialize<List<Profile>>(json, JsonOpts) ?? new();
                // Profiles saved before rules became per-executable may hold several
                // rules for one process; fold them down so applying is predictable.
                foreach (var p in Profiles) p.CollapseToOneRulePerProcess();
            }
        }
        catch
        {
            Profiles = new();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Profiles, JsonOpts);
        File.WriteAllText(FilePath, json);
    }

    public Profile? Find(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AddOrReplace(Profile profile)
    {
        var existing = Find(profile.Name);
        if (existing != null) Profiles.Remove(existing);
        Profiles.Add(profile);
        Save();
    }

    public void Remove(string name)
    {
        var p = Find(name);
        if (p != null) { Profiles.Remove(p); Save(); }
    }

    // ------------------------------------------------ capture & apply

    /// <summary>Build a profile from the current on-screen layout of all app windows.</summary>
    public static Profile CaptureCurrent(string name, List<WindowInfo> windows, List<MonitorInfo> monitors)
    {
        var profile = new Profile
        {
            Name = name,
            MonitorSignature = string.Join(" | ",
                monitors.Select(m => $"{m.DeviceName}={m.Bounds.Width}x{m.Bounds.Height}" +
                                     $"@{m.Bounds.Left},{m.Bounds.Top}{(m.IsPrimary ? "*" : "")}"))
        };

        // One rule per executable, not per window: the profile records where an app
        // belongs, and every instance of it follows that rule on apply.
        foreach (var group in windows.GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            // Use the restore rectangle so minimized/maximized windows capture the
            // position they will actually occupy once restored.
            var placed = group
                .Select(w => (Window: w, Rect: w.EffectiveBounds))
                .Select(x => (x.Window, x.Rect, Mon: WindowManager.MonitorContaining(x.Rect, monitors)))
                .ToList();

            // With several windows open the app's home is the monitor most of them sit
            // on; the first window there supplies the position, size and state.
            var rep = placed
                .GroupBy(p => p.Mon.DeviceName)
                .OrderByDescending(g => g.Count())
                .First()
                .First();

            var rule = new AppRule
            {
                ProcessName = rep.Window.ProcessName,
                TitleContains = null,
                DisplayTitle = rep.Window.Title,
                RelativeX = rep.Rect.Left - rep.Mon.Bounds.Left,
                RelativeY = rep.Rect.Top - rep.Mon.Bounds.Top,
                Width = rep.Rect.Width,
                Height = rep.Rect.Height,
                State = rep.Window.State,
                Enabled = true
            };
            rule.SetTargetMonitor(rep.Mon);
            profile.Rules.Add(rule);
        }
        return profile;
    }

    /// <summary>Offset applied to each extra instance of an app so windows sharing a
    /// rule cascade instead of hiding one another completely.</summary>
    private const int CascadeStep = 32;

    /// <summary>
    /// Apply a profile against the current windows/monitors. Every window of a matched
    /// executable is placed, not just the first one, so a second instance can't be left
    /// behind on the monitor it happened to open on. Returns a per-window log.
    /// </summary>
    public static List<string> Apply(Profile profile, List<WindowInfo> windows, List<MonitorInfo> monitors)
    {
        var log = new List<string>();
        var used = new HashSet<IntPtr>();

        // Title-narrowed rules run first: they are explicit exceptions, so they claim
        // their windows before the executable's general rule sweeps up the rest.
        var ordered = profile.Rules
            .Where(r => r.Enabled)
            .OrderBy(r => string.IsNullOrEmpty(r.TitleContains) ? 1 : 0)
            .ToList();

        foreach (var rule in ordered)
        {
            var matches = windows.Where(w => !used.Contains(w.Handle) && rule.Matches(w)).ToList();
            if (matches.Count == 0)
            {
                log.Add($"SKIP  {rule.ProcessName} \"{rule.DisplayTitle}\" — no matching window open");
                continue;
            }

            var mon = rule.ResolveMonitor(monitors);
            if (mon == null)
            {
                log.Add($"SKIP  {rule.ProcessName} — target monitor not found");
                continue;
            }

            int instance = 0;
            foreach (var match in matches)
            {
                try
                {
                    // Extra instances land on the same monitor in the same state; only
                    // free-floating ones are nudged so they stay individually reachable.
                    int nudge = rule.State == WinState.Normal ? instance * CascadeStep : 0;
                    WindowManager.ApplyPlacement(match.Handle, mon,
                        rule.RelativeX + nudge, rule.RelativeY + nudge, rule.Width, rule.Height, rule.State);
                    used.Add(match.Handle);
                    instance++;
                    log.Add($"OK    {rule.ProcessName} \"{match.Title}\" → monitor #{mon.Index + 1} ({rule.State})");
                }
                catch (Exception ex)
                {
                    log.Add($"FAIL  {rule.ProcessName} \"{match.Title}\" — {ex.Message}");
                }
            }
        }
        return log;
    }

    // ------------------------------------------------ layout comparison

    /// <summary>
    /// Compare two versions of a profile, one row per executable: which apps were added
    /// or dropped, and for the rest what moved (monitor, state, position, size). Rows for
    /// apps that did not move are included with <see cref="ChangeKind.Unchanged"/> so a
    /// caller can either show or just count them.
    /// </summary>
    public static List<LayoutChange> Diff(Profile before, Profile after, List<MonitorInfo> monitors)
    {
        var rows = new List<LayoutChange>();
        var oldRules = RulesByKey(before);
        var newRules = RulesByKey(after);

        foreach (var key in oldRules.Keys.Concat(newRules.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            oldRules.TryGetValue(key, out var o);
            newRules.TryGetValue(key, out var n);

            if (o == null && n != null)
            {
                rows.Add(new LayoutChange
                {
                    Kind = ChangeKind.Added,
                    App = key,
                    Monitor = $"#{MonitorNumber(n, monitors)}",
                    State = n.State.ToString(),
                    Position = $"{n.RelativeX},{n.RelativeY}",
                    Size = $"{n.Width}x{n.Height}",
                    Note = n.Enabled ? "" : "off"
                });
                continue;
            }
            if (n == null && o != null)
            {
                rows.Add(new LayoutChange
                {
                    Kind = ChangeKind.Removed,
                    App = key,
                    Monitor = $"#{MonitorNumber(o, monitors)}",
                    State = o.State.ToString(),
                    Position = $"{o.RelativeX},{o.RelativeY}",
                    Size = $"{o.Width}x{o.Height}",
                    Note = "no longer in profile"
                });
                continue;
            }
            if (o == null || n == null) continue;

            int oldMon = MonitorNumber(o, monitors), newMon = MonitorNumber(n, monitors);
            bool monChanged = oldMon != newMon;
            bool stateChanged = o.State != n.State;
            bool posChanged = o.RelativeX != n.RelativeX || o.RelativeY != n.RelativeY;
            bool sizeChanged = o.Width != n.Width || o.Height != n.Height;
            bool enabledChanged = o.Enabled != n.Enabled;

            rows.Add(new LayoutChange
            {
                Kind = monChanged || stateChanged || posChanged || sizeChanged || enabledChanged
                    ? ChangeKind.Changed : ChangeKind.Unchanged,
                App = key,
                Monitor = Pair($"#{oldMon}", $"#{newMon}"),
                State = Pair(o.State.ToString(), n.State.ToString()),
                Position = Pair($"{o.RelativeX},{o.RelativeY}", $"{n.RelativeX},{n.RelativeY}"),
                Size = Pair($"{o.Width}x{o.Height}", $"{n.Width}x{n.Height}"),
                Note = enabledChanged ? (n.Enabled ? "switched on" : "switched off")
                     : n.Enabled ? "" : "off",
                MonitorChanged = monChanged,
                StateChanged = stateChanged,
                PositionChanged = posChanged,
                SizeChanged = sizeChanged,
                NoteChanged = enabledChanged
            });
        }
        return rows;
    }

    /// <summary>"a" when both sides agree, otherwise "a → b".</summary>
    private static string Pair(string oldValue, string newValue) =>
        oldValue == newValue ? oldValue : $"{oldValue} → {newValue}";

    /// <summary>Rules of a profile keyed for comparison: the executable, plus the title
    /// filter when one narrows the rule (those are separate entries).</summary>
    private static Dictionary<string, AppRule> RulesByKey(Profile p)
    {
        var map = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in p.Rules)
        {
            string key = string.IsNullOrEmpty(r.TitleContains)
                ? r.ProcessName
                : $"{r.ProcessName} [{r.TitleContains}]";
            map[key] = r;
        }
        return map;
    }

    private static int MonitorNumber(AppRule r, List<MonitorInfo> monitors) =>
        (r.ResolveMonitor(monitors)?.Index ?? r.MonitorIndex) + 1;

    /// <summary>Independent copy, so a profile can be edited while its saved state is
    /// kept intact to compare against.</summary>
    public static Profile Clone(Profile p) =>
        JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p, JsonOpts), JsonOpts)!;
}

/// <summary>How an app's rule differs between two versions of a profile.</summary>
public enum ChangeKind { Added, Removed, Changed, Unchanged }

/// <summary>
/// One row of a layout comparison. Each value column reads either as a single value (that
/// field is the same in both versions) or as "old → new"; the <c>*Changed</c> flags say
/// which ones actually moved, so a view can highlight exactly those cells.
/// </summary>
public sealed class LayoutChange
{
    public ChangeKind Kind { get; init; }

    /// <summary>Executable name, with the title filter appended for narrowed rules.</summary>
    public string App { get; init; } = "";

    public string Monitor { get; init; } = "";
    public string State { get; init; } = "";
    public string Position { get; init; } = "";
    public string Size { get; init; } = "";
    public string Note { get; init; } = "";

    public bool MonitorChanged { get; init; }
    public bool StateChanged { get; init; }
    public bool PositionChanged { get; init; }
    public bool SizeChanged { get; init; }
    public bool NoteChanged { get; init; }

    public bool IsChange => Kind != ChangeKind.Unchanged;

    /// <summary>Column caption; the symbol keeps the rows readable without relying on colour.</summary>
    public string KindText => Kind switch
    {
        ChangeKind.Added => "+  added",
        ChangeKind.Removed => "−  removed",
        ChangeKind.Changed => "~  changed",
        _ => "unchanged"
    };
}
