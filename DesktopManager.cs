using System.Runtime.InteropServices;
using WindowsDesktop;

namespace DesktopSwitcher;

/// <summary>
/// Wraps Slions.VirtualDesktop to provide simple switch/move/pin operations.
/// </summary>
public static class DesktopManager
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Switch to desktop at the given index (0-based). Creates desktops if needed.
    /// </summary>
    public static void SwitchTo(int index)
    {
        try
        {
            EnsureDesktopsExist(index + 1);
            var desktops = VirtualDesktop.GetDesktops();
            if (index < desktops.Length)
            {
                desktops[index].Switch();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to switch to desktop {index + 1}: {ex.Message}");
        }
    }

    /// <summary>
    /// Move the currently focused window to the desktop at the given index.
    /// </summary>
    public static void MoveWindowTo(int index)
    {
        try
        {
            EnsureDesktopsExist(index + 1);
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            var desktops = VirtualDesktop.GetDesktops();
            if (index < desktops.Length)
            {
                VirtualDesktopHelper.MoveToDesktop(hwnd, desktops[index]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to move window to desktop {index + 1}: {ex.Message}");
        }
    }

    /// <summary>
    /// Move the currently focused window to the desktop and follow it there.
    /// </summary>
    public static void MoveWindowToAndFollow(int index)
    {
        MoveWindowTo(index);
        SwitchTo(index);
    }

    /// <summary>
    /// Pin or unpin the currently focused window across all desktops.
    /// </summary>
    public static void TogglePin()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            if (VirtualDesktop.IsPinnedWindow(hwnd))
            {
                VirtualDesktop.UnpinWindow(hwnd);
                Console.WriteLine("Window unpinned");
            }
            else
            {
                VirtualDesktop.PinWindow(hwnd);
                Console.WriteLine("Window pinned to all desktops");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to toggle pin: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the current desktop index (0-based) or -1 on error.
    /// </summary>
    public static int GetCurrentIndex()
    {
        try
        {
            var current = VirtualDesktop.Current;
            var desktops = VirtualDesktop.GetDesktops();
            for (int i = 0; i < desktops.Length; i++)
            {
                if (desktops[i].Id == current.Id) return i;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// Returns the total number of desktops.
    /// </summary>
    public static int GetDesktopCount()
    {
        try { return VirtualDesktop.GetDesktops().Length; }
        catch { return 0; }
    }

    private static void EnsureDesktopsExist(int count)
    {
        var desktops = VirtualDesktop.GetDesktops();
        while (desktops.Length < count)
        {
            VirtualDesktop.Create();
            desktops = VirtualDesktop.GetDesktops();
        }
    }
}
