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
            using var document = JsonDocument.Parse(
                File.ReadAllText(_path),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

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
                RefreshSeconds = Math.Max(1, GetInt(monitoring, "RefreshSeconds", 2)),
                DisplayMode = NormalizeDisplayMode(GetString(monitoring, "DisplayMode", "all")),
                Language = NormalizeLanguage(GetString(root, "Language", "ru"))
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Не удалось прочитать конфигурацию: {_path}\n{ex.Message}", ex);
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

    private static string NormalizeDisplayMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "post" => "post",
            "canteen" => "canteen",
            _ => "all"
        };

    private static string NormalizeLanguage(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "tr" => "tr",
            _ => "ru"
        };
}

public sealed class AppConfiguration
{
    public DatabaseSettings Database { get; init; } = new();
    public string Password { get; init; } = "";
    public int RefreshSeconds { get; init; } = 2;
    public string DisplayMode { get; init; } = "all";
    public string Language { get; init; } = "ru";
}
