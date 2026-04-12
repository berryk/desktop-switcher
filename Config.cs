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
