using System.Runtime.InteropServices;

namespace DesktopSwitcher;

static class Program
{
    private static NotifyIcon? _trayIcon;
    private static HotkeyManager? _hotkeys;
    private static WindowWatcher? _watcher;

    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Log.Clear();

        // Prevent multiple instances
        using var mutex = new Mutex(true, "DesktopSwitcher_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Desktop Switcher is already running.", "Desktop Switcher",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var config = Config.Load();
        _hotkeys = new HotkeyManager();
        RegisterHotkeys(_hotkeys, config);

        if (config.AppRules is { Count: > 0 })
        {
            _watcher = new WindowWatcher(config.AppRules);
            _hotkeys.OnDisplayChange = () => _watcher.OnDisplayChange();
        }

        _trayIcon = CreateTrayIcon();
        _trayIcon.Visible = true;

        Log.Info($"Desktop Switcher running. {DesktopManager.GetDesktopCount()} desktops available.");

        Application.Run();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _watcher?.Dispose();
        _hotkeys.Dispose();
    }

    private static void RegisterHotkeys(HotkeyManager hk, Config config)
    {
        // Alt+1 through Alt+9 — switch to desktop
        for (int i = 0; i < config.MaxDesktops; i++)
        {
            int index = i; // capture for closure
            Keys key = Keys.D1 + i; // D1=1, D2=2, ..., D9=9
            hk.Register(HotkeyManager.Modifiers.Alt, key, () =>
            {
                Log.Info($"Alt+{index + 1} pressed — switch to desktop {index + 1}");
                DesktopManager.SwitchTo(index);
                UpdateTrayTooltip();
            });
        }

        // Alt+Shift+1 through Alt+Shift+9 — move window to desktop
        for (int i = 0; i < config.MaxDesktops; i++)
        {
            int index = i;
            Keys key = Keys.D1 + i;
            hk.Register(HotkeyManager.Modifiers.Alt | HotkeyManager.Modifiers.Shift, key, () =>
            {
                Log.Info($"Alt+Shift+{index + 1} pressed — move window to desktop {index + 1}");
                if (config.FollowWindow)
                    DesktopManager.MoveWindowToAndFollow(index);
                else
                    DesktopManager.MoveWindowTo(index);
                UpdateTrayTooltip();
            });
        }

        // Alt+Shift+P — toggle pin
        hk.Register(HotkeyManager.Modifiers.Alt | HotkeyManager.Modifiers.Shift, Keys.P, () =>
        {
            Log.Info("Alt+Shift+P pressed — toggle pin");
            DesktopManager.TogglePin();
        });

        // Alt+R — rearrange all windows
        hk.Register(HotkeyManager.Modifiers.Alt, Keys.R, () =>
        {
            Log.Info("Alt+R pressed — rearranging all windows");
            _watcher?.RearrangeAll();
        });

        // Alt+Shift+T — tile windows on current desktop
        int tileId = hk.Register(HotkeyManager.Modifiers.Alt | HotkeyManager.Modifiers.Shift, Keys.T, () =>
        {
            Log.Info("Alt+Shift+T pressed — tiling...");
            WindowWatcher.TileCurrentDesktop();
        });
        Log.Info(tileId > 0 ? "Alt+Shift+T registered OK" : "Alt+Shift+T FAILED to register");

        Log.Info($"Registered hotkeys: Alt+1-{config.MaxDesktops} (switch), " +
                 $"Alt+Shift+1-{config.MaxDesktops} (move), Alt+Shift+P (pin), " +
                 "Alt+R (rearrange), Alt+Shift+T (tile)");
    }

    private static NotifyIcon CreateTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();

        contextMenu.Items.Add("Desktop Switcher", null, null!);
        contextMenu.Items[0]!.Enabled = false;
        contextMenu.Items[0]!.Font = new Font(contextMenu.Font, FontStyle.Bold);

        contextMenu.Items.Add(new ToolStripSeparator());

        contextMenu.Items.Add("Reload Config", null, (_, _) =>
        {
            var config = Config.Load();
            Log.Info("Config reloaded");
        });

        contextMenu.Items.Add("Exit", null, (_, _) =>
        {
            Application.Exit();
        });

        var icon = new NotifyIcon
        {
            Text = GetTooltipText(),
            Icon = CreateDesktopIcon(DesktopManager.GetCurrentIndex() + 1),
            ContextMenuStrip = contextMenu,
            Visible = true,
        };

        return icon;
    }

    private static void UpdateTrayTooltip()
    {
        if (_trayIcon == null) return;
        int current = DesktopManager.GetCurrentIndex() + 1;
        _trayIcon.Text = GetTooltipText();
        _trayIcon.Icon = CreateDesktopIcon(current);
    }

    private static string GetTooltipText()
    {
        int current = DesktopManager.GetCurrentIndex() + 1;
        int total = DesktopManager.GetDesktopCount();
        return $"Desktop {current}/{total}";
    }

    /// <summary>
    /// Creates a tray icon showing the desktop number in a rounded box.
    /// </summary>
    private static Icon CreateDesktopIcon(int number)
    {
        const int size = 64;
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Rounded rectangle background
            var rect = new Rectangle(2, 2, size - 4, size - 4);
            int radius = 10;
            using var path = RoundedRect(rect, radius);
            using var bgBrush = new SolidBrush(Color.FromArgb(230, 230, 230));
            g.FillPath(bgBrush, path);
            using var borderPen = new Pen(Color.FromArgb(120, 120, 120), 2f);
            g.DrawPath(borderPen, path);

            // Number text
            using var font = new Font("Segoe UI", 42f, FontStyle.Bold, GraphicsUnit.Pixel);
            string text = number.ToString();
            var textSize = g.MeasureString(text, font);
            using var textBrush = new SolidBrush(Color.FromArgb(30, 30, 30));
            g.DrawString(text, font, textBrush,
                (size - textSize.Width) / 2,
                (size - textSize.Height) / 2);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
