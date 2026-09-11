namespace ApacsMonitor.Models;

public sealed class DatabaseSettings
{
    public string Server { get; set; } = "localhost\\DEV";
    public string Database { get; set; } = "apacs3000rus";
    public string Authentication { get; set; } = "SqlServer";
    public string UserName { get; set; } = "";
}
