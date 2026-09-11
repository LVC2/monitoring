using ApacsMonitor.Models;

namespace ApacsMonitor.Models;

public sealed class AppConfiguration
{
    public DatabaseSettings Database { get; init; } = new();
    public string Password { get; init; } = "";
    public int RefreshSeconds { get; init; } = 2;
    public string Language { get; init; } = "ru";
}
