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

    // Named multi-pane layouts (e.g. "thirds" -> {left, middle, right}).
    private static Dictionary<string, Dictionary<string, ZoneRect>> _layouts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _layoutOrder = new();
    private static string? _activeLayout;

    public static void LoadCustomZones(Dictionary<string, ZoneDef>? zones)
    {
        _customZones.Clear();
        if (zones == null) return;
        foreach (var (name, def) in zones)
        {
            _customZones[name] = new ZoneRect(def.X, def.Y, def.Width, def.Height);
        }
    }

    public static void LoadLayouts(
        Dictionary<string, Dictionary<string, ZoneDef>>? layouts, string? defaultLayout)
    {
        _layouts.Clear();
        _layoutOrder.Clear();
        _activeLayout = null;
        if (layouts == null || layouts.Count == 0) return;

        foreach (var (layoutName, panes) in layouts)
        {
            var resolved = new Dictionary<string, ZoneRect>(StringComparer.OrdinalIgnoreCase);
            foreach (var (paneName, def) in panes)
                resolved[paneName] = new ZoneRect(def.X, def.Y, def.Width, def.Height);
            _layouts[layoutName] = resolved;
            _layoutOrder.Add(layoutName);
        }

        // Activate the requested default, or the first defined layout.
        string initial = (defaultLayout != null && _layouts.ContainsKey(defaultLayout))
            ? defaultLayout : _layoutOrder[0];
        SetActiveLayout(initial);
        Log.Info($"Layouts loaded: {string.Join(", ", _layoutOrder)} (active: {_activeLayout})");
    }

    /// <summary>Names of all defined layouts, in config order.</summary>
    public static IReadOnlyList<string> LayoutNames => _layoutOrder;

    public static string? ActiveLayout => _activeLayout;

    /// <summary>
    /// Makes the named layout active. Its panes become resolvable as
    /// "pane:&lt;paneName&gt;" zones, so app rules referencing those follow the
    /// active layout. Returns false if the name is unknown.
    /// </summary>
    public static bool SetActiveLayout(string name)
    {
        if (!_layouts.TryGetValue(name, out var panes))
        {
            Log.Error($"Unknown layout: \"{name}\". Available: {string.Join(", ", _layoutOrder)}");
            return false;
        }
        _activeLayout = name;
        return true;
    }

    /// <summary>Activates the layout at the given index (0-based) in config order.</summary>
    public static bool SetActiveLayoutByIndex(int index)
    {
        if (index < 0 || index >= _layoutOrder.Count) return false;
        return SetActiveLayout(_layoutOrder[index]);
    }

    /// <summary>Advances to the next layout in config order, wrapping around.</summary>
    public static bool CycleLayout()
    {
        if (_layoutOrder.Count == 0) return false;
        int idx = _activeLayout != null ? _layoutOrder.IndexOf(_activeLayout) : -1;
        return SetActiveLayout(_layoutOrder[(idx + 1) % _layoutOrder.Count]);
    }

    public static ZoneRect? ResolveZone(string name)
    {
        // "pane:<name>" resolves against the active layout (e.g. "pane:middle").
        if (name.StartsWith("pane:", StringComparison.OrdinalIgnoreCase))
        {
            string pane = name.Substring(5);
            if (_activeLayout != null &&
                _layouts.TryGetValue(_activeLayout, out var panes) &&
                panes.TryGetValue(pane, out var paneRect))
                return paneRect;

            Log.Error($"Unknown pane \"{pane}\" in active layout \"{_activeLayout}\".");
            return null;
        }

        if (_customZones.TryGetValue(name, out var custom))
            return custom;
        if (BuiltInZones.TryGetValue(name, out var builtin))
            return builtin;

        Log.Error($"Unknown zone: \"{name}\". Available: {string.Join(", ", BuiltInZones.Keys.Concat(_customZones.Keys))}");
        return null;
    }
}
