namespace DesktopSwitcher;

/// <summary>
/// Zone rectangle in screen percentages (0-100).
/// </summary>
public struct ZoneRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public ZoneRect(double x, double y, double width, double height)
    {
        X = x; Y = y; Width = width; Height = height;
    }
}

/// <summary>
/// Resolves zone names to screen-percentage rectangles.
/// Supports built-in presets and custom zone definitions from config.
/// </summary>
public static class ZoneManager
{
    private static readonly Dictionary<string, ZoneRect> BuiltInZones = new(StringComparer.OrdinalIgnoreCase)
    {
        // Halves
        ["left-half"]           = new(0, 0, 50, 100),
        ["right-half"]          = new(50, 0, 50, 100),
        ["top-half"]            = new(0, 0, 100, 50),
        ["bottom-half"]         = new(0, 50, 100, 50),

        // Thirds
        ["left-third"]          = new(0, 0, 33.33, 100),
        ["center-third"]        = new(33.33, 0, 33.34, 100),
        ["right-third"]         = new(66.67, 0, 33.33, 100),

        // Two-thirds
        ["left-two-thirds"]     = new(0, 0, 66.67, 100),
        ["right-two-thirds"]    = new(33.33, 0, 66.67, 100),

        // Quarters
        ["top-left"]            = new(0, 0, 50, 50),
        ["top-right"]           = new(50, 0, 50, 50),
        ["bottom-left"]         = new(0, 50, 50, 50),
        ["bottom-right"]        = new(50, 50, 50, 50),

        // Priority grid (3-zone, matches FancyZones default)
        // Zone 0: large center 50%, Zone 1: left 25%, Zone 2: right 25%
        ["priority-center"]     = new(25, 0, 50, 100),
        ["priority-left"]       = new(0, 0, 25, 100),
        ["priority-right"]      = new(75, 0, 25, 100),

        // Full
        ["maximize"]            = new(0, 0, 100, 100),
    };

    private static Dictionary<string, ZoneRect> _customZones = new(StringComparer.OrdinalIgnoreCase);

    public static void LoadCustomZones(Dictionary<string, ZoneDef>? zones)
    {
        _customZones.Clear();
        if (zones == null) return;
        foreach (var (name, def) in zones)
        {
            _customZones[name] = new ZoneRect(def.X, def.Y, def.Width, def.Height);
        }
    }

    public static ZoneRect? ResolveZone(string name)
    {
        if (_customZones.TryGetValue(name, out var custom))
            return custom;
        if (BuiltInZones.TryGetValue(name, out var builtin))
            return builtin;

        Console.Error.WriteLine($"Unknown zone: \"{name}\". Available: {string.Join(", ", BuiltInZones.Keys.Concat(_customZones.Keys))}");
        return null;
    }
}
