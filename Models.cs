using System.Drawing;

namespace MonitorMover;

/// <summary>Window show-state, mirrors the meaningful SW_* values.</summary>
public enum WinState
{
    Normal,
    Minimized,
    Maximized
}

/// <summary>A physical/logical display as reported by Windows.</summary>
public sealed class MonitorInfo
{
    /// <summary>Zero-based index in the current enumeration (1-based for display).</summary>
    public int Index { get; init; }

    /// <summary>Device name, e.g. "\\.\DISPLAY1". Stable-ish key within one location.</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>Full monitor rectangle in virtual-desktop coordinates.</summary>
    public Rectangle Bounds { get; init; }

    /// <summary>Working area (excludes taskbar).</summary>
    public Rectangle WorkingArea { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>Refresh rate in Hz (0 if unknown).</summary>
    public int Frequency { get; init; }

    /// <summary>Bits per pixel (0 if unknown).</summary>
    public int BitsPerPixel { get; init; }

    public string ResolutionText => $"{Bounds.Width} x {Bounds.Height}";

    public override string ToString() =>
        $"#{Index + 1} {ResolutionText}{(IsPrimary ? " (Primary)" : "")} [{DeviceName}]";
}

/// <summary>A top-level application window discovered on the desktop.</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public uint ProcessId { get; init; }
    public Rectangle Bounds { get; init; }

    /// <summary>
    /// Restore ("normal") rectangle from GetWindowPlacement. For minimized/maximized
    /// windows this is the real position to capture, not the parked/zoomed bounds.
    /// </summary>
    public Rectangle NormalBounds { get; init; }

    public WinState State { get; init; }

    /// <summary>Device name of the monitor the window currently sits on.</summary>
    public string MonitorDeviceName { get; init; } = "";

    /// <summary>Bounds to use when saving a profile (restore rect if not in normal state).</summary>
    public Rectangle EffectiveBounds =>
        State == WinState.Normal || NormalBounds.IsEmpty ? Bounds : NormalBounds;

    public string LeftTop => $"{Bounds.Left}, {Bounds.Top}";
    public string RightBottom => $"{Bounds.Right}, {Bounds.Bottom}";
    public string SizeText => $"{Bounds.Width} x {Bounds.Height}";
}
