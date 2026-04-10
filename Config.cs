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
                    Console.WriteLine($"Config loaded: maxDesktops={config.MaxDesktops}, " +
                                      $"followWindow={config.FollowWindow}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load config: {ex.Message}");
        }

        Console.WriteLine("Using default config");
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
