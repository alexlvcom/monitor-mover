using System.Drawing;

namespace MonitorMover;

/// <summary>
/// Resolves a small (16×16) icon for an application window, the way MultiMonitorTool
/// shows one per row. Prefers the window's own icon (correct for UWP/packaged apps),
/// then falls back to the executable's file icon.
/// </summary>
public static class AppIcons
{
    /// <summary>
    /// A managed copy of the best available icon for <paramref name="w"/>, or null.
    /// The caller owns the returned icon (dispose or hand to an ImageList).
    /// </summary>
    public static Icon? For(WindowInfo w)
    {
        return FromWindow(w.Handle) ?? FromPath(w.ProcessPath);
    }

    /// <summary>Icon advertised by the window itself (WM_GETICON, then the class icon).</summary>
    private static Icon? FromWindow(IntPtr hWnd)
    {
        foreach (var wp in new[] { NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL, NativeMethods.ICON_BIG })
        {
            NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_GETICON, (IntPtr)wp, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 200, out IntPtr h);
            if (h != IntPtr.Zero) return Clone(h, ownsHandle: false);
        }

        IntPtr cls = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICONSM);
        if (cls == IntPtr.Zero) cls = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICON);
        return cls != IntPtr.Zero ? Clone(cls, ownsHandle: false) : null;
    }

    /// <summary>Small file icon for the executable path.</summary>
    private static Icon? FromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var info = new SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        // If the file can't be opened directly, fall back to attribute-based lookup.
        if (!File.Exists(path)) flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

        IntPtr res = NativeMethods.SHGetFileInfo(path, NativeMethods.FILE_ATTRIBUTE_NORMAL,
            ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), flags);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        return Clone(info.hIcon, ownsHandle: true);
    }

    /// <summary>
    /// Copy a native HICON into a standalone managed <see cref="Icon"/> so its lifetime
    /// is independent of the source. Destroys the source handle when we own it.
    /// </summary>
    private static Icon? Clone(IntPtr hIcon, bool ownsHandle)
    {
        try
        {
            using var borrowed = Icon.FromHandle(hIcon);
            return (Icon)borrowed.Clone();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ownsHandle) NativeMethods.DestroyIcon(hIcon);
        }
    }
}
