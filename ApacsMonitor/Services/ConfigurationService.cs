using System.IO;
using System.Text.Json;
using ApacsMonitor.Models;

namespace ApacsMonitor.Services;

public sealed class ConfigurationService
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public AppConfiguration Load()
    {
        if (!File.Exists(_path))
            return new AppConfiguration();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            var database = root.TryGetProperty("Database", out var db) ? db : default;
            var monitoring = root.TryGetProperty("Monitoring", out var mon) ? mon : default;

            return new AppConfiguration
            {
                Database = new DatabaseSettings
                {
                    Server = GetString(database, "Server"),
                    Database = GetString(database, "Database"),
                    Authentication = GetString(database, "Authentication", "SqlServer"),
                    UserName = GetString(database, "UserName")
                },
                Password = GetString(database, "Password"),
                RefreshSeconds = GetInt(monitoring, "RefreshSeconds", 2),
                Language = GetString(root, "Language", "ru")
            };
        }
        catch
        {
            return new AppConfiguration();
        }
    }

    private static string GetString(JsonElement element, string property, string fallback = "") =>
        element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(property, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : fallback;
}

public sealed class AppConfiguration
{
    public DatabaseSettings Database { get; init; } = new();
    public string Password { get; init; } = "";
    public int RefreshSeconds { get; init; } = 2;
    public string Language { get; init; } = "ru";
}
