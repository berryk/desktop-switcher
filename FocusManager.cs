using System.Runtime.InteropServices;

namespace DesktopSwitcher;

/// <summary>
/// Manages a focus highlight overlay around the foreground window,
/// optional focus-follows-mouse behavior, and focus-cycling helpers.
/// </summary>
public sealed class FocusManager : IDisposable
{
    // --- Win32 imports ---

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    private static string GetWindowClass(IntPtr hwnd)
    {
        var buf = new char[256];
        int len = GetClassName(hwnd, buf, buf.Length);
        return new string(buf, 0, len);
    }

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman",          // desktop window
        "WorkerW",          // desktop layer / wallpaper engine
        "Shell_TrayWnd",    // taskbar
        "Shell_SecondaryTrayWnd", // taskbar on secondary monitor
        "NotifyIconOverflowWindow", // tray overflow
        "Windows.UI.Core.CoreWindow", // system popups
    };

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint GA_ROOT = 2;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CHILD = 0x40000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // --- State ---

    private readonly Config _config;
    private readonly WinEventDelegate _foregroundCallback;
    private readonly WinEventDelegate _locationCallback;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private FocusOverlay? _overlay;
    private IntPtr _currentForeground;

    // Focus-follows-mouse state
    private readonly System.Windows.Forms.Timer? _mouseTimer;
    private IntPtr _lastHoverWindow;
    private DateTime _hoverStart;
    private POINT _lastMousePos;
    private DateTime _lastMouseMove = DateTime.UtcNow;

    // Debounce for location changes (coalesces rapid fires during animations)
    private readonly System.Windows.Forms.Timer _locationDebounce;

    public FocusManager(Config config)
    {
        _config = config;

        if (_config.FocusHighlight)
        {
            _overlay = new FocusOverlay(_config.FocusBorderColor, _config.FocusBorderWidth);
            _overlay.Show();
        }

        // Hook foreground changes
        _foregroundCallback = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Hook window move/resize for the foreground window
        _locationCallback = OnLocationChanged;
        _locationHook = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locationCallback, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Track initial foreground
        UpdateForeground(GetForegroundWindow());

        // Debounce timer for location changes
        _locationDebounce = new System.Windows.Forms.Timer { Interval = 30 };
        _locationDebounce.Tick += (_, _) =>
        {
            _locationDebounce.Stop();
            UpdateOverlayPosition();
        };

        // Focus-follows-mouse timer
        if (_config.FocusFollowsMouse)
        {
            _mouseTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _mouseTimer.Tick += (_, _) => PollMouse();
            _mouseTimer.Start();
        }

        Log.Info($"FocusManager active: highlight={_config.FocusHighlight}, follows-mouse={_config.FocusFollowsMouse}");
    }

    // --- Foreground highlight ---

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0) return; // only window-level events
        if (_overlay != null) // skip log spam when the highlight is off
            Log.Info($"[focus] FG change -> hwnd={hwnd} class={GetWindowClass(hwnd)}");
        UpdateForeground(hwnd);
    }

    private void OnLocationChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd != _currentForeground || idObject != 0) return;
        // Debounce: coalesce rapid fires during focus animations
        _locationDebounce.Stop();
        _locationDebounce.Start();
    }

    private void UpdateForeground(IntPtr hwnd)
    {
        // Ignore transient foreground changes — popups, tooltips, notifications,
        // our own hidden message windows, shell, etc. These bounce rapidly and
        // would cause the overlay to flash on/off. Keep the overlay on the last
        // real app window until a new real app window takes foreground.
        if (hwnd == IntPtr.Zero) return;
        if (!IsTopLevelAppWindow(hwnd)) return;
        if (IsShellWindow(hwnd)) return;
        if (IsPopupWindow(hwnd)) return;
        if (IsSmallPopup(hwnd)) return;

        _currentForeground = hwnd;
        UpdateOverlayPosition();
    }

    /// <summary>
    /// Heuristic: windows smaller than ~400x200 are notifications, tooltips,
    /// dropdowns, autocomplete flyouts, etc. They shouldn't affect the focus highlight.
    /// </summary>
    private static bool IsSmallPopup(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return false;
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        return w < 400 || h < 200;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        return ExcludedClasses.Contains(GetWindowClass(hwnd));
    }

    private void UpdateOverlayPosition()
    {
        if (_overlay == null) return;
        if (_currentForeground == IntPtr.Zero) { _overlay.Hide(); return; }

        if (!IsWindowVisible(_currentForeground) || IsIconic(_currentForeground))
        {
            _overlay.Hide();
            return;
        }

        // Don't highlight maximized/full-screen windows — a full-screen border is ugly
        // and adds no information (there's nothing else on screen to distinguish from).
        var hMonitor = MonitorFromWindow(_currentForeground, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hMonitor, ref mi) && IsEffectivelyMaximized(_currentForeground, mi.rcWork))
        {
            Log.Info($"[focus] overlay hidden (maximized) hwnd={_currentForeground}");
            _overlay.Hide();
            return;
        }

        // Use extended frame bounds (excludes invisible shadow) for accurate visual border
        RECT rect;
        if (DwmGetWindowAttribute(_currentForeground, DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect, Marshal.SizeOf<RECT>()) != 0)
        {
            GetWindowRect(_currentForeground, out rect);
        }

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        Log.Info($"[focus] overlay -> hwnd={_currentForeground} rect=({rect.Left},{rect.Top},{w},{h})");
        _overlay.UpdateBounds(rect.Left, rect.Top, w, h);
    }

    // --- Focus-follows-mouse ---

    private void PollMouse()
    {
        if (!GetCursorPos(out POINT pt)) return;

        // Track when the mouse last moved
        if (pt.X != _lastMousePos.X || pt.Y != _lastMousePos.Y)
        {
            _lastMousePos = pt;
            _lastMouseMove = DateTime.UtcNow;
        }
        // Only steal focus if the mouse has moved recently — prevents killing popups
        // (Flow Launcher, Everything, etc.) that open while the cursor is stationary.
        else if ((DateTime.UtcNow - _lastMouseMove).TotalSeconds > 1)
        {
            return;
        }

        // Don't steal focus from popup/tool windows (launchers, notifications, etc.)
        var currentFg = GetForegroundWindow();
        if (currentFg != IntPtr.Zero && IsPopupWindow(currentFg)) return;

        IntPtr hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return;

        // Walk up to the top-level window
        IntPtr topLevel = GetAncestor(hwnd, GA_ROOT);
        if (topLevel == IntPtr.Zero || !IsTopLevelAppWindow(topLevel)) return;

        // Skip if already foreground
        if (topLevel == currentFg) return;

        // Track hover time
        if (topLevel != _lastHoverWindow)
        {
            _lastHoverWindow = topLevel;
            _hoverStart = DateTime.UtcNow;
            return;
        }

        // Focus once hover threshold is met
        if ((DateTime.UtcNow - _hoverStart).TotalMilliseconds >= _config.FocusFollowsMouseDelayMs)
        {
            FocusWindow(topLevel);
            _lastHoverWindow = IntPtr.Zero; // avoid re-focus loop
        }
    }

    /// <summary>
    /// Heuristic for detecting popup/launcher/tool windows we shouldn't steal focus from.
    /// </summary>
    private static bool IsPopupWindow(IntPtr hwnd)
    {
        if (IsShellWindow(hwnd)) return true;

        long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        // Tool windows are transient by design
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

        // Windows marked as topmost-without-taskbar are usually overlays/launchers
        const long WS_EX_TOPMOST = 0x00000008;
        if ((exStyle & WS_EX_TOPMOST) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return true;

        return false;
    }

    /// <summary>
    /// Force a refresh — re-read the current foreground and reposition the overlay.
    /// Call this after virtual desktop changes or other events that EVENT_SYSTEM_FOREGROUND
    /// may not catch.
    /// </summary>
    public void Refresh()
    {
        UpdateForeground(GetForegroundWindow());
    }

    /// <summary>True if the focus highlight overlay is currently active.</summary>
    public bool HighlightEnabled => _overlay != null;

    /// <summary>
    /// Toggles the focus highlight overlay on/off at runtime. When off, the
    /// overlay is destroyed so foreground/location events stop redrawing it.
    /// Returns the new state (true = on).
    /// </summary>
    public bool ToggleHighlight()
    {
        if (_overlay != null)
        {
            _overlay.Hide();
            _overlay.Dispose();
            _overlay = null;
            Log.Info("[focus] highlight toggled OFF");
            return false;
        }

        _overlay = new FocusOverlay(_config.FocusBorderColor, _config.FocusBorderWidth);
        _overlay.Show();
        _currentForeground = IntPtr.Zero;
        UpdateForeground(GetForegroundWindow());
        Log.Info("[focus] highlight toggled ON");
        return true;
    }

    // --- Focus cycling (called from hotkey handlers) ---

    /// <summary>
    /// Cycle focus to the next (or previous) tiled window on the current desktop and monitor.
    /// Excludes maximized/full-screen windows that aren't part of a tile arrangement.
    /// </summary>
    public static void CycleFocus(int direction)
    {
        var current = GetForegroundWindow();

        // Determine the work area on the current foreground's monitor
        var hMonitor = MonitorFromWindow(current, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi)) return;

        var windows = EnumerateTileableWindowsOnCurrentDesktop()
            .Where(hwnd => hwnd == current || !IsEffectivelyMaximized(hwnd, mi.rcWork))
            .ToList();

        if (windows.Count <= 1) return;

        // Sort by screen x position so "next" means to the right
        windows.Sort((a, b) =>
        {
            GetWindowRect(a, out RECT ra);
            GetWindowRect(b, out RECT rb);
            int c = ra.Left.CompareTo(rb.Left);
            return c != 0 ? c : ra.Top.CompareTo(rb.Top);
        });

        int idx = windows.IndexOf(current);
        if (idx < 0) idx = 0;
        int next = ((idx + direction) % windows.Count + windows.Count) % windows.Count;

        Log.Info($"[focus] cycle dir={direction} windows={windows.Count} from idx={idx} to idx={next} hwnd={windows[next]}");
        FocusWindow(windows[next]);
    }

    private static List<IntPtr> EnumerateTileableWindowsOnCurrentDesktop()
    {
        var list = new List<IntPtr>();
        WindowWatcher.EnumTopLevelWindows(hwnd =>
        {
            if (!IsTopLevelAppWindow(hwnd)) return true;
            if (IsIconic(hwnd)) return true;
            if (IsShellWindow(hwnd)) return true;
            if (IsPopupWindow(hwnd)) return true;
            if (IsSmallPopup(hwnd)) return true;
            try
            {
                if (!WindowsDesktop.VirtualDesktop.IsCurrentVirtualDesktop(hwnd)) return true;
            }
            catch { return true; }
            list.Add(hwnd);
            return true;
        });
        return list;
    }

    /// <summary>
    /// A window is "effectively maximized" if it's flagged maximized OR its bounds
    /// cover 95%+ of the work area. Such windows aren't participating in tiling.
    /// </summary>
    private static bool IsEffectivelyMaximized(IntPtr hwnd, RECT workArea)
    {
        if (IsZoomed(hwnd)) return true;
        if (!GetWindowRect(hwnd, out RECT r)) return false;

        int winW = r.Right - r.Left;
        int winH = r.Bottom - r.Top;
        int workW = workArea.Right - workArea.Left;
        int workH = workArea.Bottom - workArea.Top;
        return winW >= workW * 0.95 && winH >= workH * 0.95;
    }

    // --- Helpers ---

    private static bool IsTopLevelAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        if ((style & WS_CHILD) != 0) return false;

        long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return false;

        return true;
    }

    /// <summary>
    /// Brings a window to the foreground using AttachThreadInput trick to bypass
    /// SetForegroundWindow restrictions.
    /// </summary>
    public static void FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint myThread = GetCurrentThreadId();

        if (fgThread != myThread)
            AttachThreadInput(fgThread, myThread, true);

        SetForegroundWindow(hwnd);

        if (fgThread != myThread)
            AttachThreadInput(fgThread, myThread, false);
    }

    public void Dispose()
    {
        _mouseTimer?.Stop();
        _mouseTimer?.Dispose();
        _locationDebounce.Stop();
        _locationDebounce.Dispose();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_locationHook != IntPtr.Zero) UnhookWinEvent(_locationHook);
        _overlay?.Close();
        _overlay?.Dispose();
    }

    // --- Close window ---

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    /// <summary>
    /// Gracefully close the foreground window (sends WM_CLOSE, so the app can prompt to save).
    /// </summary>
    public static void CloseActiveWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        string cls = GetWindowClass(fg);
        if (!IsTopLevelAppWindow(fg) || ExcludedClasses.Contains(cls))
        {
            Log.Info($"CloseActiveWindow: refusing to close foreground (class={cls})");
            return;
        }

        Log.Info($"CloseActiveWindow: posting WM_CLOSE to hwnd={fg}");
        PostMessage(fg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}

/// <summary>
/// Transparent, click-through, topmost overlay that draws a colored border.
/// </summary>
internal sealed class FocusOverlay : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly Color _borderColor;
    private readonly int _borderWidth;

    public FocusOverlay(string colorHex, int borderWidth)
    {
        _borderColor = ParseColor(colorHex);
        _borderWidth = Math.Max(1, borderWidth);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;     // used as transparency key
        TransparencyKey = Color.Magenta;
        Size = new Size(100, 100);     // placeholder, will be set by UpdateBounds
        Location = new Point(-32000, -32000);
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void UpdateBounds(int x, int y, int w, int h)
    {
        if (!Visible) Show();
        if (Bounds.X == x && Bounds.Y == y && Bounds.Width == w && Bounds.Height == h) return;
        SetBounds(x, y, w, h);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Magenta);
        using var pen = new Pen(_borderColor, _borderWidth);
        // Draw inside the bounds so the border stays within the window rect
        int off = _borderWidth / 2;
        g.DrawRectangle(pen, off, off, Width - _borderWidth, Height - _borderWidth);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Don't paint background — OnPaint clears everything
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            if (hex.StartsWith("#")) hex = hex[1..];
            if (hex.Length == 6)
            {
                return Color.FromArgb(
                    Convert.ToInt32(hex[0..2], 16),
                    Convert.ToInt32(hex[2..4], 16),
                    Convert.ToInt32(hex[4..6], 16));
            }
        }
        catch { }
        return Color.DodgerBlue;
    }
}
