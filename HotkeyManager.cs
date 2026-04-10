using System.Runtime.InteropServices;

namespace DesktopSwitcher;

/// <summary>
/// Manages global hotkey registration via Win32 RegisterHotKey.
/// Creates a hidden message-only window to receive WM_HOTKEY messages.
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [Flags]
    public enum Modifiers : uint
    {
        Alt = 0x0001,
        Ctrl = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    /// <summary>
    /// Optional callback invoked when WM_DISPLAYCHANGE is received.
    /// </summary>
    public Action? OnDisplayChange { get; set; }

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
    }

    /// <summary>
    /// Register a global hotkey. Returns the hotkey ID for later unregistration.
    /// </summary>
    public int Register(Modifiers modifiers, Keys key, Action handler)
    {
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, (uint)(modifiers | Modifiers.NoRepeat), (uint)key))
        {
            int error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"Failed to register hotkey {modifiers}+{key} (error {error})");
            return -1;
        }
        _handlers[id] = handler;
        return id;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
            UnregisterHotKey(Handle, id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _handlers.TryGetValue((int)m.WParam, out var handler))
        {
            handler.Invoke();
            return;
        }
        if (m.Msg == 0x007E) // WM_DISPLAYCHANGE
        {
            OnDisplayChange?.Invoke();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _handlers.Keys.ToList())
            UnregisterHotKey(Handle, id);
        _handlers.Clear();
        DestroyHandle();
    }
}
