using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopSwitcher;

public class Config
{
    [JsonPropertyName("maxDesktops")]
    public int MaxDesktops { get; set; } = 9;

    [JsonPropertyName("followWindow")]
    public bool FollowWindow { get; set; } = false;

    [JsonPropertyName("autoCreateDesktops")]
    public bool AutoCreateDesktops { get; set; } = true;

    [JsonPropertyName("appRules")]
    public List<AppRule>? AppRules { get; set; }

    [JsonPropertyName("zones")]
    public Dictionary<string, ZoneDef>? Zones { get; set; }

    /// <summary>
    /// Named multi-pane layouts. Each layout maps pane names (e.g. "left",
    /// "middle", "right") to a rectangle. The active layout's panes are exposed
    /// as zones named "pane:&lt;name&gt;" (e.g. "pane:middle") for app rules.
    /// Switch the active layout at runtime with the layout hotkeys.
    /// </summary>
    [JsonPropertyName("layouts")]
    public Dictionary<string, Dictionary<string, ZoneDef>>? Layouts { get; set; }

    /// <summary>Name of the layout to activate at startup. Defaults to the first defined.</summary>
    [JsonPropertyName("defaultLayout")]
    public string? DefaultLayout { get; set; }

    [JsonPropertyName("focusHighlight")]
    public bool FocusHighlight { get; set; } = true;

    [JsonPropertyName("focusBorderColor")]
    public string FocusBorderColor { get; set; } = "#7BA7E1";

    [JsonPropertyName("focusBorderWidth")]
    public int FocusBorderWidth { get; set; } = 3;

    [JsonPropertyName("focusFollowsMouse")]
    public bool FocusFollowsMouse { get; set; } = false;

    [JsonPropertyName("focusFollowsMouseDelayMs")]
    public int FocusFollowsMouseDelayMs { get; set; } = 250;

    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<Config>(json);
                if (config != null)
                {
                    Log.Info($"Config loaded: maxDesktops={config.MaxDesktops}, " +
                            $"followWindow={config.FollowWindow}, " +
                            $"appRules={config.AppRules?.Count ?? 0}");
                    ZoneManager.LoadCustomZones(config.Zones);
                    ZoneManager.LoadLayouts(config.Layouts, config.DefaultLayout);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load config: {ex.Message}");
        }

        Log.Info("Using default config");
        var defaults = new Config();
        Save(defaults);
        return defaults;
    }

    private static void Save(Config config)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}

public class AppRule
{
    [JsonPropertyName("process")]
    public string Process { get; set; } = "";

    [JsonPropertyName("desktop")]
    public int Desktop { get; set; } = 0;

    [JsonPropertyName("zone")]
    public string? Zone { get; set; }

    [JsonPropertyName("monitor")]
    public string? Monitor { get; set; }

    /// <summary>Only match windows whose title contains this string (case-insensitive).</summary>
    [JsonPropertyName("titleContains")]
    public string? TitleContains { get; set; }

    /// <summary>Only match windows whose title does NOT contain this string (case-insensitive).</summary>
    [JsonPropertyName("titleExcludes")]
    public string? TitleExcludes { get; set; }

    /// <summary>Only match windows whose title matches this regex (case-insensitive).</summary>
    [JsonPropertyName("titleRegex")]
    public string? TitleRegex { get; set; }

    /// <summary>Only match windows whose title does NOT match this regex (case-insensitive).</summary>
    [JsonPropertyName("titleNotRegex")]
    public string? TitleNotRegex { get; set; }

    [JsonPropertyName("delayMs")]
    public int DelayMs { get; set; } = 500;
}

public class ZoneDef
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}
