using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MonitorMover;

/// <summary>
/// Discovers monitors and top-level windows, and repositions windows across monitors.
/// </summary>
public static class WindowManager
{
    // ---------------------------------------------------------------- Monitors

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            int freq = 0, bpp = s.BitsPerPixel;
            // Screen.DeviceName gives "\\.\DISPLAYn"; use it directly.
            list.Add(new MonitorInfo
            {
                Index = i,
                DeviceName = s.DeviceName,
                Bounds = s.Bounds,
                WorkingArea = s.WorkingArea,
                IsPrimary = s.Primary,
                Frequency = freq,
                BitsPerPixel = bpp
            });
        }
        return list;
    }

    public static MonitorInfo? PrimaryMonitor(IEnumerable<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

    public static MonitorInfo MonitorForWindow(WindowInfo w, List<MonitorInfo> monitors)
    {
        // Prefer the device the window is recorded on.
        var byName = monitors.FirstOrDefault(m => m.DeviceName == w.MonitorDeviceName);
        if (byName != null) return byName;
        return MonitorContaining(w.Bounds, monitors);
    }

    public static MonitorInfo MonitorContaining(Rectangle r, List<MonitorInfo> monitors)
    {
        var center = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var hit = monitors.FirstOrDefault(m => m.Bounds.Contains(center));
        if (hit != null) return hit;
        // Fall back to the monitor with the largest overlap, else primary.
        MonitorInfo best = monitors[0];
        long bestArea = -1;
        foreach (var m in monitors)
        {
            var inter = Rectangle.Intersect(m.Bounds, r);
            long area = (long)inter.Width * inter.Height;
            if (area > bestArea) { bestArea = area; best = m; }
        }
        return best;
    }

    // ---------------------------------------------------------------- Windows

    public static List<WindowInfo> GetWindows()
    {
        var monitors = GetMonitors();
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!IsAltTabWindow(hWnd)) return true;

            int len = NativeMethods.GetWindowTextLength(hWnd);
            var sbTitle = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
            string title = sbTitle.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            var sbClass = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            (string procName, string procPath) = GetProcessInfo(pid);

            var bounds = GetWindowBounds(hWnd);
            var state = GetState(hWnd);
            var normal = GetNormalBounds(hWnd);

            // For minimized windows the live rect is parked off-screen; pick the
            // monitor from the restore rectangle instead so it's assigned sensibly.
            var forMonitor = (state == WinState.Minimized && !normal.IsEmpty) ? normal : bounds;
            var mon = MonitorContaining(forMonitor, monitors);

            result.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ClassName = sbClass.ToString(),
                ProcessName = procName,
                ProcessPath = procPath,
                ProcessId = pid,
                Bounds = bounds,
                NormalBounds = normal,
                State = state,
                MonitorDeviceName = mon.DeviceName
            });
            return true;
        }, IntPtr.Zero);

        return result
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Heuristic matching the Alt-Tab list: visible, top-level, not a tool window.</summary>
    private static bool IsAltTabWindow(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;
        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return false;

        long style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CHILD) != 0) return false;

        long ex = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_APPWINDOW) != 0) return true;
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;
        return true;
    }

    private static (string name, string path) GetProcessInfo(uint pid)
    {
        string path = "";
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int cap = sb.Capacity;
                if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref cap))
                    path = sb.ToString();
            }
            finally { NativeMethods.CloseHandle(h); }
        }

        string name;
        if (!string.IsNullOrEmpty(path))
        {
            name = Path.GetFileName(path);
        }
        else
        {
            try { name = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
            catch { name = "(unknown)"; }
        }
        return (name, path);
    }

    /// <summary>
    /// True window bounds, trimming the invisible DWM resize border present on
    /// Win10/11 so coordinates match what the user sees.
    /// </summary>
    private static Rectangle GetWindowBounds(IntPtr hWnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT ext, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0
            && ext.Width > 0 && ext.Height > 0)
        {
            return Rectangle.FromLTRB(ext.Left, ext.Top, ext.Right, ext.Bottom);
        }
        if (NativeMethods.GetWindowRect(hWnd, out RECT r))
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return Rectangle.Empty;
    }

    /// <summary>Restore rectangle from GetWindowPlacement (virtual-screen coords).</summary>
    private static Rectangle GetNormalBounds(IntPtr hWnd)
    {
        var wp = WINDOWPLACEMENT.Default();
        if (NativeMethods.GetWindowPlacement(hWnd, ref wp))
        {
            var r = wp.rcNormalPosition;
            if (r.Width > 0 && r.Height > 0)
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
        return Rectangle.Empty;
    }

    private static WinState GetState(IntPtr hWnd)
    {
        if (NativeMethods.IsIconic(hWnd)) return WinState.Minimized;
        if (NativeMethods.IsZoomed(hWnd)) return WinState.Maximized;
        return WinState.Normal;
    }

    // ---------------------------------------------------------------- Moving

    /// <summary>
    /// Move a window to <paramref name="target"/>, preserving its position relative
    /// to its current monitor's top-left (MultiMonitorTool "Next Monitor" behaviour).
    /// A maximized window is restored, moved with physical coordinates, then
    /// re-maximized so it fills the target monitor — reliable across mixed DPI.
    /// </summary>
    public static void MoveToMonitorKeepRelative(WindowInfo w, MonitorInfo target, List<MonitorInfo> monitors)
    {
        var current = MonitorForWindow(w, monitors);
        if (current.DeviceName == target.DeviceName) return;

        // Physical-pixel translation between the two monitor origins.
        int deltaX = target.Bounds.Left - current.Bounds.Left;
        int deltaY = target.Bounds.Top - current.Bounds.Top;

        // Minimized windows are parked off-screen; relocate their restore rectangle
        // via placement so they reappear on the target monitor when restored.
        if (w.State == WinState.Minimized)
        {
            var wp = WINDOWPLACEMENT.Default();
            if (NativeMethods.GetWindowPlacement(w.Handle, ref wp))
            {
                wp.rcNormalPosition.Left += deltaX; wp.rcNormalPosition.Right += deltaX;
                wp.rcNormalPosition.Top += deltaY; wp.rcNormalPosition.Bottom += deltaY;
                wp.showCmd = NativeMethods.SW_SHOWMINIMIZED;
                wp.flags = 0;
                NativeMethods.SetWindowPlacement(w.Handle, ref wp);
            }
            return;
        }

        bool wasMax = w.State == WinState.Maximized;

        // Restore first so the window is normal and its rect can be relocated; a
        // still-maximized window ignores SetWindowPos.
        if (wasMax) NativeMethods.ShowWindow(w.Handle, NativeMethods.SW_RESTORE);

        NativeMethods.GetWindowRect(w.Handle, out RECT r);
        MoveRaw(w.Handle, r.Left + deltaX, r.Top + deltaY, r.Right - r.Left, r.Bottom - r.Top);

        // Re-maximize; it now fills the target monitor the window sits on.
        if (wasMax) NativeMethods.ShowWindow(w.Handle, NativeMethods.SW_SHOWMAXIMIZED);
    }

    /// <summary>
    /// Place a window at an absolute position/size on the target monitor and apply a
    /// window state. Used when applying a saved profile. <paramref name="relX"/>/<paramref name="relY"/>
    /// are offsets from the target monitor's top-left.
    /// </summary>
    public static void ApplyPlacement(IntPtr hWnd, MonitorInfo target,
        int relX, int relY, int width, int height, WinState state)
    {
        int x = target.Bounds.Left + relX;
        int y = target.Bounds.Top + relY;

        if (width <= 0) width = Math.Min(1000, target.Bounds.Width);
        if (height <= 0) height = Math.Min(700, target.Bounds.Height);

        // Keep the restore rectangle on the target monitor.
        x = Clamp(x, target.Bounds.Left, Math.Max(target.Bounds.Left, target.Bounds.Right - width));
        y = Clamp(y, target.Bounds.Top, Math.Max(target.Bounds.Top, target.Bounds.Bottom - height));

        // Normalize first so the physical move takes effect, then apply the target state.
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
        MoveRaw(hWnd, x, y, width, height);

        if (state == WinState.Maximized)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWMAXIMIZED);
        else if (state == WinState.Minimized)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWMINIMIZED);
    }

    private static void MoveRaw(IntPtr hWnd, int x, int y, int w, int h)
    {
        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private static int Clamp(int v, int lo, int hi) => hi < lo ? lo : Math.Max(lo, Math.Min(hi, v));
}
