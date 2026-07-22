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

    // Target monitor resolution — several keys for robustness across sessions.
    public string MonitorDeviceName { get; set; } = "";
    public int MonitorIndex { get; set; }
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }

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

    /// <summary>Resolve the monitor this rule targets in the current monitor set.</summary>
    public MonitorInfo? ResolveMonitor(List<MonitorInfo> monitors)
    {
        var byName = monitors.FirstOrDefault(m => m.DeviceName == MonitorDeviceName);
        if (byName != null) return byName;

        var byIndex = monitors.FirstOrDefault(m => m.Index == MonitorIndex);
        // Prefer index match only if its resolution is close to the captured one,
        // otherwise pick the monitor with the nearest resolution.
        var byRes = monitors
            .OrderBy(m => Math.Abs(m.Bounds.Width - MonitorWidth) + Math.Abs(m.Bounds.Height - MonitorHeight))
            .FirstOrDefault();

        if (byIndex != null &&
            Math.Abs(byIndex.Bounds.Width - MonitorWidth) <= 200 &&
            Math.Abs(byIndex.Bounds.Height - MonitorHeight) <= 200)
            return byIndex;

        return byRes ?? byIndex;
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
                monitors.Select(m => $"{m.DeviceName}={m.Bounds.Width}x{m.Bounds.Height}{(m.IsPrimary ? "*" : "")}"))
        };

        foreach (var w in windows)
        {
            // Use the restore rectangle so minimized/maximized windows capture the
            // position they will actually occupy once restored.
            var rect = w.EffectiveBounds;
            var mon = WindowManager.MonitorContaining(rect, monitors);
            profile.Rules.Add(new AppRule
            {
                ProcessName = w.ProcessName,
                TitleContains = null,
                DisplayTitle = w.Title,
                MonitorDeviceName = mon.DeviceName,
                MonitorIndex = mon.Index,
                MonitorWidth = mon.Bounds.Width,
                MonitorHeight = mon.Bounds.Height,
                RelativeX = rect.Left - mon.Bounds.Left,
                RelativeY = rect.Top - mon.Bounds.Top,
                Width = rect.Width,
                Height = rect.Height,
                State = w.State,
                Enabled = true
            });
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
