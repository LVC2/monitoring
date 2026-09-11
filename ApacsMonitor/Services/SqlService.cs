using System.Data;
using ApacsMonitor.Models;
using Microsoft.Data.SqlClient;

namespace ApacsMonitor.Services;

public sealed class SqlService
{
    private string? _connectionString;

    public void Configure(DatabaseSettings settings, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            InitialCatalog = settings.Database,
            ConnectTimeout = 5,
            TrustServerCertificate = true,
            Encrypt = false,
            ApplicationName = "ApacsMonitor"
        };

        if (string.Equals(settings.Authentication, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = settings.UserName;
            builder.Password = password;
            builder.IntegratedSecurity = false;
        }

        _connectionString = builder.ConnectionString;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EventRecord>> GetRecentEventsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        const string sql = """
            SELECT TOP (@Limit)
                FREALTIME,
                FREGISTERTIME,
                FINITOBJNAME,
                FNUMEVTYPE,
                FSAINITOBJ0,
                FSAINITOBJ1,
                FSEK0,
                FSEK1
            FROM dbo.TAPCSYSEVENTSCOMMON
            ORDER BY FREALTIME DESC, FREGISTERTIME DESC;
            """;

        var result = new List<EventRecord>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EventRecord
            {
                RealTime = reader.IsDBNull(0) ? DateTime.MinValue : reader.GetDateTime(0),
                RegisterTime = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1),
                InitObjectName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                EventType = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                InitObjectId0 = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                InitObjectId1 = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5)),
                SekId0 = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6)),
                SekId1 = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7))
            });
        }

        return result;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("Подключение к SQL Server ещё не настроено.");
    }
}
