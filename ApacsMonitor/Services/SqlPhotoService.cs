using System.Data;
using ApacsMonitor.Models;
using Microsoft.Data.SqlClient;

namespace ApacsMonitor.Services;

/// <summary>
/// Reads employee photos directly from the APACS SQL database.
/// No APACS client, COM or SDK is required on the monitoring workstation.
/// </summary>
public sealed class SqlPhotoService
{
    private string? _connectionString;
    private PhotoSchema? _schema;
    private readonly Dictionary<int, byte[]> _cache = new();

    public Action<string>? Log { get; set; }

    public void Configure(string connectionString)
    {
        _connectionString = connectionString;
        _schema = null;
        _cache.Clear();
    }

    public void ClearCache() => _cache.Clear();

    public async Task LoadPhotosAsync(IReadOnlyList<EventRecord> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0 || string.IsNullOrWhiteSpace(_connectionString))
            return;

        var holderIds = events.Select(x => x.HolderId).Where(x => x > 0).Distinct().ToArray();
        if (holderIds.Length == 0)
        {
            Log?.Invoke("Photo load skipped: events contain no positive HolderId values.");
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        _schema ??= await DiscoverSchemaAsync(connection, cancellationToken);
        if (_schema is null)
        {
            Log?.Invoke("Photo schema not found in APACS database.");
            return;
        }

        Log?.Invoke($"Photo schema: dbo.{_schema.TableName}, holder={_schema.HolderIdColumn}, photo={_schema.PhotoColumn}.");

        var missing = holderIds.Where(id => !_cache.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
            await LoadMissingAsync(connection, _schema, missing, cancellationToken);

        var attached = 0;
        foreach (var item in events)
        {
            if (_cache.TryGetValue(item.HolderId, out var bytes) && bytes.Length > 0)
            {
                item.PhotoBytes = bytes;
                attached++;
            }
        }

        Log?.Invoke($"Photos attached: {attached} of {events.Count} events; cache entries={_cache.Count}.");
    }

    private async Task LoadMissingAsync(SqlConnection connection, PhotoSchema schema, IReadOnlyList<int> holderIds, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var parameters = new List<string>(holderIds.Count);

        for (var i = 0; i < holderIds.Count; i++)
        {
            var name = $"@p{i}";
            parameters.Add(name);
            command.Parameters.Add(name, SqlDbType.Int).Value = holderIds[i];
        }

        command.CommandText = $"""
            SELECT {Quote(schema.HolderIdColumn)}, {Quote(schema.PhotoColumn)}
            FROM dbo.{Quote(schema.TableName)}
            WHERE {Quote(schema.HolderIdColumn)} IN ({string.Join(", ", parameters)});
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var loaded = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;

            var holderId = Convert.ToInt32(reader.GetValue(0));
            var bytes = (byte[])reader.GetValue(1);
            if (bytes.Length > 0)
            {
                _cache[holderId] = bytes;
                loaded++;
            }
        }

        foreach (var holderId in holderIds)
            _cache.TryAdd(holderId, Array.Empty<byte>());

        Log?.Invoke($"Photo rows loaded: {loaded}; requested holders={holderIds.Count}.");
    }

    private static async Task<PhotoSchema?> DiscoverSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo'
              AND (
                    UPPER(TABLE_NAME) LIKE '%PHOTO%'
                 OR UPPER(TABLE_NAME) LIKE '%CHMAIN%'
                 OR UPPER(TABLE_NAME) LIKE '%CARDHOLDER%'
              )
            ORDER BY TABLE_NAME, ORDINAL_POSITION;
            """;

        var tables = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!tables.TryGetValue(table, out var columns))
            {
                columns = new List<ColumnInfo>();
                tables[table] = columns;
            }

            columns.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        foreach (var table in tables.OrderByDescending(x => ScoreTable(x.Key)))
        {
            var blob = table.Value.Where(IsBinary)
                .OrderByDescending(x => ScorePhotoColumn(x.Name))
                .ThenBy(x => x.Ordinal)
                .FirstOrDefault();

            if (blob is null || ScorePhotoColumn(blob.Name) <= 0)
                continue;

            var holder = table.Value.Where(x => IsInteger(x.DataType))
                .OrderByDescending(x => ScoreHolderColumn(x.Name))
                .ThenBy(x => x.Ordinal)
                .FirstOrDefault();

            if (holder is null || ScoreHolderColumn(holder.Name) <= 0)
                continue;

            return new PhotoSchema(table.Key, holder.Name, blob.Name);
        }

        return null;
    }

    private static int ScoreTable(string name)
    {
        var value = name.ToUpperInvariant();
        var score = 0;
        if (value.Contains("CHMAINPHOTO")) score += 100;
        if (value.Contains("PHOTO")) score += 80;
        if (value.Contains("CARDHOLDER")) score -= 50;
        return score;
    }

    private static int ScorePhotoColumn(string name)
    {
        var value = name.ToUpperInvariant();
        if (value.Contains("BINBUF") && value.Contains("PHOTO")) return 100;
        if (value.Contains("PHOTO")) return 90;
        if (value.Contains("IMAGE")) return 80;
        if (value.Contains("BLOB")) return 60;
        if (value.Contains("SETTING")) return 30;
        return 0;
    }

    private static int ScoreHolderColumn(string name)
    {
        var value = name.ToUpperInvariant();
        if (value is "FSAHOLDER1" or "FCHOLDER1") return 100;
        if (value.Contains("HOLDER") && value.EndsWith("1")) return 90;
        if (value is "FHOLDERID" or "FCARDHOLDERID") return 80;
        if (value == "FID1") return 50;
        return 0;
    }

    private static bool IsBinary(ColumnInfo column) =>
        column.DataType.Equals("image", StringComparison.OrdinalIgnoreCase)
        || column.DataType.Equals("varbinary", StringComparison.OrdinalIgnoreCase)
        || column.DataType.Equals("binary", StringComparison.OrdinalIgnoreCase);

    private static bool IsInteger(string dataType) =>
        dataType.Equals("int", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("smallint", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("bigint", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private sealed record PhotoSchema(string TableName, string HolderIdColumn, string PhotoColumn);
    private sealed record ColumnInfo(string Name, string DataType, int Ordinal);
}
