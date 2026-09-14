using System.IO;
using System.Text;

namespace ApacsMonitor.Services;

public sealed class DebugLogService
{
    private readonly bool _enabled;
    private readonly string _logDirectory;
    private readonly object _sync = new();

    public DebugLogService(bool enabled, string? directory = null)
    {
        _enabled = enabled;
        _logDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory,
            "logs");

        if (_enabled)
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
            }
            catch
            {
                // Logging must never break application startup.
            }
        }
    }

    public string LogDirectory => _logDirectory;

    public string LogFilePath => Path.Combine(
        _logDirectory,
        $"apacs-monitor-{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        if (!_enabled)
            return;

        var text = exception is null
            ? message
            : $"{message}{Environment.NewLine}{exception}";

        Write("ERROR", text);
    }

    private void Write(string level, string message)
    {
        if (!_enabled)
            return;

        try
        {
            Directory.CreateDirectory(_logDirectory);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

            lock (_sync)
                File.AppendAllText(LogFilePath, line, new UTF8Encoding(false));
        }
        catch
        {
            // Logging must never break the application.
        }
    }
}
