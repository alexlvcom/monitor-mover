using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorMover;

/// <summary>
/// One saved rule: "the app matching X belongs on monitor Y at this position/size/state".
/// Matching is by process name; an optional title substring disambiguates multiple
/// windows of the same process.
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

        foreach (var w in windows)
        {
            // Use the restore rectangle so minimized/maximized windows capture the
            // position they will actually occupy once restored.
            var rect = w.EffectiveBounds;
            var mon = WindowManager.MonitorContaining(rect, monitors);
            var rule = new AppRule
            {
                ProcessName = w.ProcessName,
                TitleContains = null,
                DisplayTitle = w.Title,
                RelativeX = rect.Left - mon.Bounds.Left,
                RelativeY = rect.Top - mon.Bounds.Top,
                Width = rect.Width,
                Height = rect.Height,
                State = w.State,
                Enabled = true
            };
            rule.SetTargetMonitor(mon);
            profile.Rules.Add(rule);
        }
        return profile;
    }

    /// <summary>
    /// Apply a profile against the current windows/monitors. Returns a per-rule log.
    /// </summary>
    public static List<string> Apply(Profile profile, List<WindowInfo> windows, List<MonitorInfo> monitors)
    {
        var log = new List<string>();
        var used = new HashSet<IntPtr>();

        foreach (var rule in profile.Rules)
        {
            if (!rule.Enabled) continue;

            // First unused window that matches (title rule wins over generic).
            var match = windows.FirstOrDefault(w => !used.Contains(w.Handle) && rule.Matches(w));
            if (match == null)
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

            try
            {
                WindowManager.ApplyPlacement(match.Handle, mon,
                    rule.RelativeX, rule.RelativeY, rule.Width, rule.Height, rule.State);
                used.Add(match.Handle);
                log.Add($"OK    {rule.ProcessName} \"{match.Title}\" → monitor #{mon.Index + 1} ({rule.State})");
            }
            catch (Exception ex)
            {
                log.Add($"FAIL  {rule.ProcessName} — {ex.Message}");
            }
        }
        return log;
    }
}
