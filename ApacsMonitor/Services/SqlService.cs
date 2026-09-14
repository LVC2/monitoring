using System.Data;
using ApacsMonitor.Models;
using Microsoft.Data.SqlClient;

namespace ApacsMonitor.Services;

public sealed class SqlService
{
    private readonly Dictionary<int, byte[]?> _photoCache = new();
    private readonly HashSet<int> _photoLoadFailed = new();
    private readonly object _photoCacheLock = new();
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

        lock (_photoCacheLock)
        {
            _photoCache.Clear();
            _photoLoadFailed.Clear();
        }
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
            INNER JOIN dbo.TAPCCARDHOLDERREF r
                ON r.FSEK1 = e.FSEK1
            INNER JOIN dbo.TAPCCARDHOLDER h
                ON h.FID1 = r.FSAHOLDER1
            WHERE LTRIM(RTRIM(e.FINITOBJNAME)) IN
                ('Turn1_Post1_IN', 'Turn1_Post1_OUT', 'Turn2_Post1_IN', 'Turn2_Post1_OUT', 'Canteen_1')
            ORDER BY e.FREALTIME DESC, e.FREGISTERTIME DESC;
            """;

        var result = await ExecuteEventsQueryAsync(sql, command =>
        {
            command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Max(1, limit);
        }, cancellationToken);

        // Only cards currently close to the visible top of the journal need photos.
        // The remaining events are still kept lightweight; their photos are not read
        // from SQL until they become part of a later refresh.
        var photoEvents = result.Take(Math.Min(100, result.Count)).ToList();
        await AttachEmployeePhotosAsync(photoEvents, cancellationToken);
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
            INNER JOIN dbo.TAPCCARDHOLDERREF r
                ON r.FSEK1 = e.FSEK1
            INNER JOIN dbo.TAPCCARDHOLDER h
                ON h.FID1 = r.FSAHOLDER1
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

    private async Task AttachEmployeePhotosAsync(List<EventRecord> events, CancellationToken cancellationToken)
    {
        var holderIds = events
            .Select(x => x.HolderId)
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (holderIds.Count == 0)
            return;

        List<int> missingIds;
        lock (_photoCacheLock)
        {
            missingIds = holderIds
                .Where(id => !_photoCache.ContainsKey(id) && !_photoLoadFailed.Contains(id))
                .ToList();
        }

        if (missingIds.Count > 0)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var batch in missingIds.Chunk(1000))
            {
                var parameterNames = batch.Select((_, index) => $"@Holder{index}").ToArray();
                var sql = $"""
                    SELECT FID1, FOWNSG
                    FROM dbo.TAPCCARDHOLDER
                    WHERE FID1 IN ({string.Join(", ", parameterNames)});
                    """;

                await using var command = new SqlCommand(sql, connection);
                for (var i = 0; i < batch.Length; i++)
                    command.Parameters.Add(parameterNames[i], SqlDbType.Int).Value = batch[i];

                var foundIds = new HashSet<int>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var holderId = reader.GetInt32(0);
                    foundIds.Add(holderId);

                    byte[]? bytes = null;
                    if (!reader.IsDBNull(1))
                    {
                        var value = reader.GetValue(1);
                        if (value is byte[] data && data.Length > 0)
                            bytes = data;
                    }

                    lock (_photoCacheLock)
                    {
                        _photoCache[holderId] = bytes;
                        if (bytes is null)
                            _photoLoadFailed.Add(holderId);
                    }
                }

                lock (_photoCacheLock)
                {
                    foreach (var holderId in batch)
                    {
                        if (!foundIds.Contains(holderId))
                            _photoLoadFailed.Add(holderId);
                    }
                }
            }
        }

        foreach (var item in events)
        {
            lock (_photoCacheLock)
            {
                if (_photoCache.TryGetValue(item.HolderId, out var bytes))
                    item.PhotoBytes = bytes;
            }
        }
    }

    private async Task<List<EventRecord>> ExecuteEventsQueryAsync(string sql, Action<SqlCommand> configureCommand, CancellationToken cancellationToken)
    {
        var result = new List<EventRecord>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        configureCommand(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var objectName = reader.IsDBNull(7) ? "" : reader.GetString(7);

            result.Add(new EventRecord
            {
                RealTime = reader.IsDBNull(0) ? DateTime.MinValue : reader.GetDateTime(0),
                RegisterTime = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1),
                FullName = BuildFullName(reader.IsDBNull(2) ? "" : reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4)),
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

    private static string BuildFullName(string lastName, string firstName, string middleName)
    {
        return string.Join(" ", new[] { lastName, firstName, middleName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string ResolveDirection(string objectName)
    {
        var value = objectName.Trim();

        if (value.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase))
            return "Вход";

        if (value.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase))
            return "Выход";

        return value.Equals("Canteen_1", StringComparison.OrdinalIgnoreCase) ? "" : "Проход";
    }

    private static string ResolveLocation(string objectName)
    {
        var value = objectName.Trim();

        if (value.Equals("Canteen_1", StringComparison.OrdinalIgnoreCase))
            return "Столовая";

        if (value.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase))
            return "Турникет 1 — вход";

        if (value.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase))
            return "Турникет 2 — выход";

        return string.IsNullOrWhiteSpace(value) ? "Неизвестно" : value;
    }

    private static string ResolveReaderName(string objectName)
    {
        var value = objectName.Trim();

        if (value.Equals("Canteen_1", StringComparison.OrdinalIgnoreCase))
            return "Столовая";

        if (value.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase))
            return "Турникет 1 — вход";

        if (value.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase))
            return "Турникет 2 — выход";

        return value;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("Подключение к SQL Server ещё не настроено.");
    }
}
