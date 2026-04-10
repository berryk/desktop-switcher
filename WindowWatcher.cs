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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(long pt, uint dwFlags);

    [DllImport("user32.dll")]
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
            Console.Error.WriteLine("Failed to set WinEvent hook for window watcher");
        else
            Console.WriteLine($"Window watcher active with {_rules.Count} app rule(s)");

        // Timer for delayed rearrange after display change
        _displayChangeTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _displayChangeTimer.Tick += (_, _) =>
        {
            _displayChangeTimer.Stop();
            Console.WriteLine("Display change detected — rearranging windows");
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

        Console.WriteLine($"Monitors detected: {string.Join(", ", _monitorModels.Values)}");
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
                Console.WriteLine($"  {rule.Process} -> desktop {rule.Desktop}");
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
                            Console.WriteLine($"  {rule.Process} -> zone \"{rule.Zone}\" on {rule.Monitor}");
                            return;
                        }
                    }
                    // Fall back to current monitor
                    PositionInZone(hwnd, zone.Value);
                    Console.WriteLine($"  {rule.Process} -> zone \"{rule.Zone}\"");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to apply rule for {rule.Process}: {ex.Message}");
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

        Console.WriteLine($"Rearrange complete: {moved} window(s) repositioned");
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

    // --- Helpers ---

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
