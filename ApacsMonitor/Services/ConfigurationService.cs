using System.Text.Json;
using ApacsMonitor.Models;

namespace ApacsMonitor.Services;

public sealed class ConfigurationService
{
    private readonly string _path;

    public ConfigurationService()
    {
        _path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public DatabaseSettings LoadDatabaseSettings()
    {
        if (!File.Exists(_path))
            return new DatabaseSettings();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (!document.RootElement.TryGetProperty("Database", out var database))
                return new DatabaseSettings();

            return new DatabaseSettings
            {
                Server = database.TryGetProperty("Server", out var server) ? server.GetString() ?? "" : "",
                Database = database.TryGetProperty("Database", out var db) ? db.GetString() ?? "" : "",
                Authentication = database.TryGetProperty("Authentication", out var auth) ? auth.GetString() ?? "SqlServer" : "SqlServer",
                UserName = database.TryGetProperty("UserName", out var user) ? user.GetString() ?? "" : ""
            };
        }
        catch
        {
            return new DatabaseSettings();
        }
    }

    public void SaveDatabaseSettings(DatabaseSettings settings)
    {
        var json = new
        {
            Database = new
            {
                settings.Server,
                settings.Database,
                settings.Authentication,
                settings.UserName
            },
            Monitoring = new
            {
                RefreshSeconds = 2,
                EventTable = "dbo.TAPCSYSEVENTSCOMMON"
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_path, JsonSerializer.Serialize(json, options));
    }
}
