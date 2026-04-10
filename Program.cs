namespace DesktopSwitcher;

static class Program
{
    private static NotifyIcon? _trayIcon;
    private static HotkeyManager? _hotkeys;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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

        _trayIcon = CreateTrayIcon();
        _trayIcon.Visible = true;

        Console.WriteLine($"Desktop Switcher running. {DesktopManager.GetDesktopCount()} desktops available.");

        Application.Run();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
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
            DesktopManager.TogglePin();
        });

        Console.WriteLine($"Registered hotkeys: Alt+1-{config.MaxDesktops} (switch), " +
                          $"Alt+Shift+1-{config.MaxDesktops} (move), Alt+Shift+P (pin)");
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
            Console.WriteLine("Config reloaded");
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
    /// Creates a simple icon showing the desktop number.
    /// </summary>
    private static Icon CreateDesktopIcon(int number)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(137, 180, 250)); // Catppuccin blue
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
            string text = number.ToString();
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, Brushes.Black,
                (16 - size.Width) / 2,
                (16 - size.Height) / 2);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}
