using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopSwitcher;

/// <summary>
/// Monitors for new top-level windows and applies app rules (move to desktop, position in zone).
/// Also supports rearranging all existing windows on demand (Alt+R) or on display change.
/// </summary>
public sealed class WindowWatcher : IDisposable
{
    // --- Win32 delegates and imports ---

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(long pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CHILD = 0x40000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int WM_DISPLAYCHANGE = 0x007E;

    private readonly List<AppRule> _rules;
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private readonly HashSet<IntPtr> _handled = new();
    private readonly System.Windows.Forms.Timer _displayChangeTimer;

    // Cache of connected monitor device names -> model IDs (e.g. "\\.\DISPLAY1" -> "DELA1C5")
    private Dictionary<string, string> _monitorModels = new();

    public WindowWatcher(List<AppRule> rules)
    {
        _rules = rules;
        RefreshMonitorCache();

        // Must keep a reference to the delegate to prevent GC
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
            IntPtr.Zero, _callback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
            Log.Error("Failed to set WinEvent hook for window watcher");
        else
            Log.Info($"Window watcher active with {_rules.Count} app rule(s)");

        // Timer for delayed rearrange after display change
        _displayChangeTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _displayChangeTimer.Tick += (_, _) =>
        {
            _displayChangeTimer.Stop();
            Log.Info("Display change detected — rearranging windows");
            RearrangeAll();
        };
    }

    /// <summary>
    /// Call this from WndProc when WM_DISPLAYCHANGE is received.
    /// Restarts a 3-second timer to allow the display to settle.
    /// </summary>
    public void OnDisplayChange()
    {
        _displayChangeTimer.Stop();
        _displayChangeTimer.Start();
    }

    /// <summary>
    /// The WM_DISPLAYCHANGE constant, for use in WndProc routing.
    /// </summary>
    public const int MSG_DISPLAYCHANGE = WM_DISPLAYCHANGE;

    // --- Monitor detection ---

    private void RefreshMonitorCache()
    {
        _monitorModels.Clear();

        // Enumerate adapters (\\.\DISPLAY1, \\.\DISPLAY2, etc.)
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;

            // Enumerate monitors on this adapter
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0))
            {
                // DeviceID looks like: "MONITOR\DELA1C5\{guid}"
                // Extract the model ID between the first and second backslash
                string modelId = ExtractModelId(monitor.DeviceID);
                if (!string.IsNullOrEmpty(modelId))
                {
                    _monitorModels[adapter.DeviceName] = modelId;
                }
            }
        }

        Log.Info($"Monitors detected: {string.Join(", ", _monitorModels.Values)}");
    }

    private static string ExtractModelId(string deviceId)
    {
        // "MONITOR\DELA1C5\{guid}" -> "DELA1C5"
        var parts = deviceId.Split('\\');
        return parts.Length >= 2 ? parts[1] : "";
    }

    private bool IsMonitorConnected(string monitorId)
    {
        return _monitorModels.ContainsValue(monitorId.ToUpperInvariant()) ||
               _monitorModels.Values.Any(m => m.Contains(monitorId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the HMONITOR and work area for a monitor matching the given model ID.
    /// </summary>
    private (IntPtr hMonitor, RECT workArea)? GetMonitorByModel(string monitorId)
    {
        // Find the device name for this model ID
        string? deviceName = null;
        foreach (var (devName, model) in _monitorModels)
        {
            if (string.Equals(model, monitorId, StringComparison.OrdinalIgnoreCase) ||
                model.Contains(monitorId, StringComparison.OrdinalIgnoreCase))
            {
                deviceName = devName;
                break;
            }
        }
        if (deviceName == null) return null;

        // Find the HMONITOR matching this device name
        (IntPtr hMonitor, RECT workArea)? result = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi) && mi.szDevice == deviceName)
            {
                result = (hMon, mi.rcWork);
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // --- New window detection ---

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || idObject != 0) return;
        if (!IsTopLevelAppWindow(hwnd)) return;
        if (_handled.Contains(hwnd)) return;

        string? processName = GetProcessName(hwnd);
        if (processName == null) return;

        var rule = FindMatchingRule(processName);
        if (rule != null)
        {
            _handled.Add(hwnd);
            Task.Delay(rule.DelayMs).ContinueWith(_ => ApplyRule(hwnd, rule),
                TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    // --- Rule matching ---

    /// <summary>
    /// Finds the first matching rule for a process. Rules with a monitor field
    /// only match when that monitor is connected. First match wins.
    /// </summary>
    private AppRule? FindMatchingRule(string processName)
    {
        foreach (var rule in _rules)
        {
            if (!string.Equals(rule.Process, processName, StringComparison.OrdinalIgnoreCase))
                continue;

            // If rule specifies a monitor, only match when it's connected
            if (!string.IsNullOrEmpty(rule.Monitor) && !IsMonitorConnected(rule.Monitor))
                continue;

            return rule;
        }
        return null;
    }

    // --- Apply rules ---

    private void ApplyRule(IntPtr hwnd, AppRule rule)
    {
        try
        {
            if (!IsWindowVisible(hwnd)) return;

            // Move to desktop
            if (rule.Desktop > 0)
            {
                DesktopManager.MoveWindowToDesktop(hwnd, rule.Desktop - 1);
                Log.Info($"  {rule.Process} -> desktop {rule.Desktop}");
            }

            // Position in zone
            if (rule.Zone != null)
            {
                var zone = ZoneManager.ResolveZone(rule.Zone);
                if (zone != null)
                {
                    if (!string.IsNullOrEmpty(rule.Monitor))
                    {
                        // Position on specific monitor
                        var monInfo = GetMonitorByModel(rule.Monitor);
                        if (monInfo != null)
                        {
                            PositionInZoneOnMonitor(hwnd, zone.Value, monInfo.Value.workArea);
                            Log.Info($"  {rule.Process} -> zone \"{rule.Zone}\" on {rule.Monitor}");
                            return;
                        }
                    }
                    // Fall back to current monitor
                    PositionInZone(hwnd, zone.Value);
                    Log.Info($"  {rule.Process} -> zone \"{rule.Zone}\"");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to apply rule for {rule.Process}: {ex.Message}");
        }
    }

    // --- Rearrange all existing windows ---

    /// <summary>
    /// Scans all existing top-level windows and applies the first matching rule for each.
    /// Called by Alt+R hotkey and after display changes.
    /// </summary>
    public void RearrangeAll()
    {
        RefreshMonitorCache();
        _handled.Clear();

        // Build a lookup of process name -> list of window handles
        var windowsByProcess = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((hwnd, _) =>
        {
            if (!IsTopLevelAppWindow(hwnd)) return true;
            string? name = GetProcessName(hwnd);
            if (name == null) return true;

            if (!windowsByProcess.TryGetValue(name, out var list))
            {
                list = new List<IntPtr>();
                windowsByProcess[name] = list;
            }
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        // For each process that has rules, apply the first matching rule
        int moved = 0;
        foreach (var (processName, windows) in windowsByProcess)
        {
            var rule = FindMatchingRule(processName);
            if (rule == null) continue;

            foreach (var hwnd in windows)
            {
                _handled.Add(hwnd);
                ApplyRule(hwnd, rule);
                moved++;
            }
        }

        Log.Info($"Rearrange complete: {moved} window(s) repositioned");
    }

    // --- Zone positioning ---

    private void PositionInZone(IntPtr hwnd, ZoneRect zone)
    {
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref mi)) return;

        PositionInZoneOnMonitor(hwnd, zone, mi.rcWork);
    }

    private void PositionInZoneOnMonitor(IntPtr hwnd, ZoneRect zone, RECT workArea)
    {
        int workW = workArea.Right - workArea.Left;
        int workH = workArea.Bottom - workArea.Top;

        int x = workArea.Left + (int)(workW * zone.X / 100.0);
        int y = workArea.Top + (int)(workH * zone.Y / 100.0);
        int w = (int)(workW * zone.Width / 100.0);
        int h = (int)(workH * zone.Height / 100.0);

        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // --- Tile current desktop ---

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private const int SW_RESTORE = 9;
    private const int SW_SHOWNORMAL = 1;
    private const int WPF_ASYNCWINDOWPLACEMENT = 0x0004;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint SWP_NOCOPYBITS = 0x0100;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// Gets the invisible border/shadow size around a window.
    /// Returns (left, top, right, bottom) insets.
    /// </summary>
    private static (int left, int top, int right, int bottom) GetWindowBorderInsets(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out RECT windowRect);
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frameRect,
                Marshal.SizeOf<RECT>()) != 0)
            return (0, 0, 0, 0);

        return (
            frameRect.Left - windowRect.Left,
            frameRect.Top - windowRect.Top,
            windowRect.Right - frameRect.Right,
            windowRect.Bottom - frameRect.Bottom
        );
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    /// <summary>
    /// Tiles all visible, non-minimized windows on the current virtual desktop
    /// into equal-width columns on the same monitor as the foreground window.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var buf = new char[len + 1];
        GetWindowText(hwnd, buf, buf.Length);
        return new string(buf, 0, len);
    }

    public static void TileCurrentDesktop()
    {
        var foreground = GetForegroundWindow();
        Log.Info($"Tile: foreground hwnd={foreground}");

        var hMonitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
        {
            int err = Marshal.GetLastWin32Error();
            Log.Error($"Tile: GetMonitorInfo failed, hMonitor={hMonitor}, cbSize={mi.cbSize}, error={err}");
            return;
        }
        Log.Info($"Tile: monitor={mi.szDevice} work=({mi.rcWork.Left},{mi.rcWork.Top})-({mi.rcWork.Right},{mi.rcWork.Bottom})");

        // Collect windows on the current desktop, on this monitor, that are visible and not minimized
        int totalEnum = 0, topLevel = 0, notMinimized = 0, onMonitor = 0, onDesktop = 0;
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            totalEnum++;
            if (!IsTopLevelAppWindow(hwnd)) return true;
            topLevel++;
            if (IsIconic(hwnd)) return true;
            notMinimized++;
            if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != hMonitor) return true;
            onMonitor++;

            try
            {
                if (!WindowsDesktop.VirtualDesktop.IsCurrentVirtualDesktop(hwnd))
                    return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Tile: IsCurrentVirtualDesktop failed for {hwnd}: {ex.Message}");
                return true;
            }

            onDesktop++;
            string title = GetWindowTitle(hwnd);
            string proc = GetProcessName(hwnd) ?? "?";

            // Skip windows with no title (hidden shell windows)
            if (string.IsNullOrWhiteSpace(title))
            {
                Log.Info($"Tile: skipping (no title) hwnd={hwnd} proc={proc}");
                return true;
            }

            // Skip known system/overlay processes
            if (IsTileExcludedProcess(proc))
            {
                Log.Info($"Tile: skipping (excluded) hwnd={hwnd} proc={proc} title=\"{title}\"");
                return true;
            }

            Log.Info($"Tile: found window hwnd={hwnd} proc={proc} title=\"{title}\"");
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        Log.Info($"Tile: enumerated={totalEnum} topLevel={topLevel} notMinimized={notMinimized} onMonitor={onMonitor} onDesktop={onDesktop}");

        if (windows.Count == 0)
        {
            Log.Info("Tile: no windows to tile");
            return;
        }

        int workW = mi.rcWork.Right - mi.rcWork.Left;
        int workH = mi.rcWork.Bottom - mi.rcWork.Top;
        int colWidth = workW / windows.Count;

        Log.Info($"Tile: workArea={workW}x{workH}, colWidth={colWidth} for {windows.Count} windows");

        for (int i = 0; i < windows.Count; i++)
        {
            int x = mi.rcWork.Left + (i * colWidth);
            int w = (i == windows.Count - 1) ? (mi.rcWork.Right - x) : colWidth;

            string proc = GetProcessName(windows[i]) ?? "?";

            // First, restore so we can measure the shadow border
            ShowWindow(windows[i], SW_RESTORE);
            var insets = GetWindowBorderInsets(windows[i]);

            // Expand rect into the shadow area so visible edges are flush
            int adjX = x - insets.left;
            int adjY = mi.rcWork.Top - insets.top;
            int adjW = w + insets.left + insets.right;
            int adjH = workH + insets.top + insets.bottom;

            // Restore via SetWindowPlacement with target rect
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(windows[i], ref wp);
            wp.showCmd = SW_SHOWNORMAL;
            wp.flags = WPF_ASYNCWINDOWPLACEMENT;
            wp.rcNormalPosition = new RECT { Left = adjX, Top = adjY, Right = adjX + adjW, Bottom = adjY + adjH };
            SetWindowPlacement(windows[i], ref wp);

            // SetWindowPos with flags that prevent the app from rejecting the resize
            uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS
                       | SWP_NOSENDCHANGING | SWP_FRAMECHANGED;
            SetWindowPos(windows[i], IntPtr.Zero, adjX, adjY, adjW, adjH, flags);
            SetWindowPos(windows[i], IntPtr.Zero, adjX, adjY, adjW, adjH, flags);

            Log.Info($"Tile: {proc} x={x} w={w} adj=({adjX},{adjY},{adjW},{adjH}) insets=({insets.left},{insets.top},{insets.right},{insets.bottom})");
        }

        Log.Info($"Tiled {windows.Count} window(s) into equal columns");
    }

    // --- Helpers ---

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",         // taskbar, shell, hidden windows
        "TextInputHost",    // Windows input experience
        "M365Copilot",      // Copilot overlay
        "SearchHost",       // Windows search
        "ShellExperienceHost", // Start menu, action center
        "SystemSettings",   // Settings flyouts
        "DesktopSwitcher",  // ourselves
    };

    private static bool IsTileExcludedProcess(string processName)
    {
        return ExcludedProcesses.Contains(processName);
    }

    private static bool IsTopLevelAppWindow(IntPtr hwnd)
    {
        if (GetParent(hwnd) != IntPtr.Zero) return false;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        if ((style & WS_CHILD) != 0) return false;
        if ((style & WS_VISIBLE) == 0) return false;

        long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return false;

        return true;
    }

    private static string? GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _displayChangeTimer.Stop();
        _displayChangeTimer.Dispose();
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
