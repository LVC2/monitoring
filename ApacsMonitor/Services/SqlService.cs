using System.Data;
using ApacsMonitor.Models;
using Microsoft.Data.SqlClient;

namespace ApacsMonitor.Services;

public sealed class SqlService
{
    private readonly SqlPhotoService _photos = new();
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
        _photos.Configure(_connectionString);
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
                r.FSAHOLDER1,
                e.FINITOBJNAME,
                e.FNUMEVTYPE,
                e.FSEK0,
                e.FSEK1
            FROM dbo.TAPCSYSEVENTSCOMMON e
            INNER JOIN dbo.TAPCCARDHOLDERREF r ON r.FSEK1 = e.FSEK1
            INNER JOIN dbo.TAPCCARDHOLDER h ON h.FID1 = r.FSAHOLDER1
            WHERE LTRIM(RTRIM(e.FINITOBJNAME)) IN
                ('Turn1_Post1_IN', 'Turn1_Post1_OUT', 'Turn2_Post1_IN', 'Turn2_Post1_OUT', 'Canteen_1')
            ORDER BY e.FREALTIME DESC, e.FREGISTERTIME DESC;
            """;

        var result = await ExecuteEventsQueryAsync(sql, command =>
            command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Max(1, limit), cancellationToken);

        _ = AttachEmployeePhotosAsync(result.Take(Math.Min(100, result.Count)).ToList());
        return result;
    }

    public async Task<IReadOnlyList<EventRecord>> GetWorkTimeEventsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        const string sql = """
            SELECT
                e.FREALTIME,
                e.FREGISTERTIME,
                h.FLASTNAME,
                h.FFIRSTNAME,
                h.FMIDDLENAME,
                r.FCARDNUM,
                r.FSAHOLDER1,
                e.FINITOBJNAME,
                e.FNUMEVTYPE,
                e.FSEK0,
                e.FSEK1
            FROM dbo.TAPCSYSEVENTSCOMMON e
            INNER JOIN dbo.TAPCCARDHOLDERREF r ON r.FSEK1 = e.FSEK1
            INNER JOIN dbo.TAPCCARDHOLDER h ON h.FID1 = r.FSAHOLDER1
            WHERE e.FREALTIME >= @From
              AND e.FREALTIME < @To
              AND LTRIM(RTRIM(e.FINITOBJNAME)) IN
                  ('Turn1_Post1_IN', 'Turn1_Post1_OUT', 'Turn2_Post1_IN', 'Turn2_Post1_OUT')
            ORDER BY e.FREALTIME ASC, e.FREGISTERTIME ASC;
            """;

        return await ExecuteEventsQueryAsync(sql, command =>
        {
            command.Parameters.Add("@From", SqlDbType.DateTime).Value = from;
            command.Parameters.Add("@To", SqlDbType.DateTime).Value = to;
        }, cancellationToken);
    }

    private async Task AttachEmployeePhotosAsync(IReadOnlyList<EventRecord> events)
    {
        try
        {
            await _photos.LoadPhotosAsync(events, CancellationToken.None);
        }
        catch
        {
            // A missing/unsupported photo table must never stop the event journal.
        }
    }

    private async Task<List<EventRecord>> ExecuteEventsQueryAsync(
        string sql,
        Action<SqlCommand> configureCommand,
        CancellationToken cancellationToken)
    {
        var result = new List<EventRecord>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        configureCommand(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var lastName = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var firstName = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var middleName = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var objectName = reader.IsDBNull(7) ? "" : reader.GetString(7);

            result.Add(new EventRecord
            {
                RealTime = reader.IsDBNull(0) ? DateTime.MinValue : reader.GetDateTime(0),
                RegisterTime = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1),
                LastName = lastName,
                FirstName = firstName,
                MiddleName = middleName,
                FullName = BuildFullName(lastName, firstName, middleName),
                CardNumber = reader.IsDBNull(5) ? "" : Convert.ToString(reader.GetValue(5)) ?? "",
                HolderId = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6)),
                Location = ResolveLocation(objectName),
                Direction = ResolveDirection(objectName),
                ReaderName = ResolveReaderName(objectName),
                RawObjectName = objectName,
                EventType = reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8)),
                SekId0 = reader.IsDBNull(9) ? 0 : Convert.ToInt32(reader.GetValue(9)),
                SekId1 = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10))
            });
        }

        return result;
    }

    private static string BuildFullName(string lastName, string firstName, string middleName) =>
        string.Join(" ", new[] { lastName, firstName, middleName }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string ResolveDirection(string objectName)
    {
        var value = objectName.Trim();
        if (value.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase)) return "Вход";
        if (value.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase)) return "Выход";
        return value.Equals("Canteen_1", StringComparison.OrdinalIgnoreCase) ? "" : "Проход";
    }

    private static string ResolveLocation(string objectName)
    {
        var value = objectName.Trim();
        if (value.Equals("Canteen_1", StringComparison.OrdinalIgnoreCase)) return "Столовая";
        if (value.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase)) return "Турникет 1 — вход";
        if (value.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase)) return "Турникет 2 — выход";
        return string.IsNullOrWhiteSpace(value) ? "Неизвестно" : value;
    }

    private static string ResolveReaderName(string objectName)
    {
        var value = objectName.Trim();
        if (value.Equals("Canteen_1", StringComparison.OrdinalIgnoreCase)) return "Столовая";
        if (value.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase)) return "Турникет 1 — вход";
        if (value.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase)) return "Турникет 2 — выход";
        return value;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("Подключение к SQL Server ещё не настроено.");
    }
}
