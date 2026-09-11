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
                e.FREALTIME,
                e.FREGISTERTIME,
                h.FLASTNAME,
                h.FFIRSTNAME,
                h.FMIDDLENAME,
                r.FCARDNUM,
                e.FINITOBJNAME,
                e.FNUMEVTYPE,
                e.FSEK0,
                e.FSEK1
            FROM dbo.TAPCSYSEVENTSCOMMON e
            INNER JOIN dbo.TAPCCARDHOLDERREF r
                ON r.FSEK1 = e.FSEK1
            INNER JOIN dbo.TAPCCARDHOLDER h
                ON h.FID1 = r.FSAHOLDER1
            WHERE
                (
                    e.FINITOBJNAME LIKE 'Turn%Post%'
                    OR e.FINITOBJNAME LIKE 'Canteen[_]%'
                )
                AND LOWER(LTRIM(RTRIM(e.FINITOBJNAME))) NOT IN
                ('timekeeper', 'admin', 'post1', 'canteen')
            ORDER BY e.FREALTIME DESC, e.FREGISTERTIME DESC;
            """;

        var result = new List<EventRecord>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Max(1, limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var objectName = reader.IsDBNull(6) ? "" : reader.GetString(6);

            result.Add(new EventRecord
            {
                RealTime = reader.IsDBNull(0) ? DateTime.MinValue : reader.GetDateTime(0),
                RegisterTime = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1),
                FullName = BuildFullName(reader.IsDBNull(2) ? "" : reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4)),
                CardNumber = reader.IsDBNull(5) ? "" : Convert.ToString(reader.GetValue(5)) ?? "",
                Location = ResolveLocation(objectName),
                Direction = ResolveDirection(objectName),
                ReaderName = objectName,
                RawObjectName = objectName,
                EventType = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7)),
                SekId0 = reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8)),
                SekId1 = reader.IsDBNull(9) ? 0 : Convert.ToInt32(reader.GetValue(9))
            });
        }

        return result;
    }

    private static string BuildFullName(string lastName, string firstName, string middleName)
    {
        return string.Join(" ", new[] { lastName, firstName, middleName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string ResolveDirection(string objectName)
    {
        var value = objectName.Trim();
        if (value.EndsWith("_IN", StringComparison.OrdinalIgnoreCase) || value.Contains("_IN_", StringComparison.OrdinalIgnoreCase))
            return "Вход";
        if (value.EndsWith("_OUT", StringComparison.OrdinalIgnoreCase) || value.Contains("_OUT_", StringComparison.OrdinalIgnoreCase))
            return "Выход";
        if (value.Contains("IN", StringComparison.OrdinalIgnoreCase) && !value.Contains("OUT", StringComparison.OrdinalIgnoreCase))
            return "Вход";
        if (value.Contains("OUT", StringComparison.OrdinalIgnoreCase))
            return "Выход";
        return "Проход";
    }

    private static string ResolveLocation(string objectName)
    {
        var value = objectName.Trim();
        if (value.Contains("Canteen", StringComparison.OrdinalIgnoreCase) || value.Contains("Столов", StringComparison.OrdinalIgnoreCase))
            return "Столовая";
        if (value.Contains("Post", StringComparison.OrdinalIgnoreCase) || value.Contains("Проход", StringComparison.OrdinalIgnoreCase) || value.Contains("Turn", StringComparison.OrdinalIgnoreCase))
            return "Проходная";
        return string.IsNullOrWhiteSpace(value) ? "Неизвестно" : value;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("Подключение к SQL Server ещё не настроено.");
    }
}
