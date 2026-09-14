using System.Data;
using System.Reflection;
using System.Runtime.InteropServices;
using ApacsMonitor.Models;
using Microsoft.Data.SqlClient;

namespace ApacsMonitor.Services;

public sealed class SqlService
{
    private readonly ApacsSdkPhotoService _apacsPhotos = new();
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
        _apacsPhotos.ClearCache();
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

        // Photo loading must never block the SQL journal refresh. The APACS SDK is
        // queried in the background only for the first visible batch of events.
        var photoEvents = result.Take(Math.Min(100, result.Count)).ToList();
        _ = AttachEmployeePhotosAsync(photoEvents);

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

    private async Task AttachEmployeePhotosAsync(IReadOnlyList<EventRecord> events)
    {
        try
        {
            await _apacsPhotos.LoadPhotosAsync(events, CancellationToken.None);
        }
        catch
        {
            // APACS SDK is optional for the journal. SQL monitoring must remain usable
            // even when the APACS COM service is unavailable or not registered.
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

/// <summary>
/// Read-only bridge to the installed APACS 3000 COM SDK.
/// APACS 7.1 registers ApcSrvSDK as a 32-bit COM server, therefore the main
/// application is built x86. The bridge deliberately uses late binding so the
/// repository does not depend on a machine-specific generated interop assembly.
/// </summary>
internal sealed class ApacsSdkPhotoService
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<int, byte[]?> _photoCache = new();
    private readonly HashSet<int> _photoLoadFailed = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private DateTime _disabledUntilUtc;

    public async Task LoadPhotosAsync(IReadOnlyList<EventRecord> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0 || DateTime.UtcNow < _disabledUntilUtc)
            return;

        var candidates = events
            .Where(x => x.HolderId > 0 &&
                        !string.IsNullOrWhiteSpace(x.LastName) &&
                        !string.IsNullOrWhiteSpace(x.FirstName))
            .GroupBy(x => x.HolderId)
            .Select(x => x.First())
            .ToList();

        lock (_cacheLock)
        {
            candidates = candidates
                .Where(x => !_photoCache.ContainsKey(x.HolderId) && !_photoLoadFailed.Contains(x.HolderId))
                .ToList();
        }

        if (candidates.Count == 0)
            return;

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            lock (_cacheLock)
            {
                candidates = candidates
                    .Where(x => !_photoCache.ContainsKey(x.HolderId) && !_photoLoadFailed.Contains(x.HolderId))
                    .ToList();
            }

            if (candidates.Count == 0 || DateTime.UtcNow < _disabledUntilUtc)
                return;

            await Task.Run(() => LoadPhotos(candidates, cancellationToken), cancellationToken);
        }
        catch
        {
            // A missing/temporarily unavailable SDK must not break SQL monitoring.
            _disabledUntilUtc = DateTime.UtcNow.AddSeconds(30);
        }
        finally
        {
            _loadGate.Release();
        }

        foreach (var item in events)
        {
            lock (_cacheLock)
            {
                if (_photoCache.TryGetValue(item.HolderId, out var bytes))
                    item.PhotoBytes = bytes;
            }
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _photoCache.Clear();
            _photoLoadFailed.Clear();
        }

        _disabledUntilUtc = DateTime.MinValue;
    }

    private void LoadPhotos(IReadOnlyList<EventRecord> candidates, CancellationToken cancellationToken)
    {
        object? connection = null;
        object? session = null;
        object? server = null;

        try
        {
            connection = CreateComObject("ApcSrvSDK.TApcConnection");
            session = InvokeOutObject(connection, "createSession", GetLogin(), GetPassword());
            server = InvokeOutObject(session, "getServer");

            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var bytes = LoadPhoto(server, item);
                    lock (_cacheLock)
                    {
                        _photoCache[item.HolderId] = bytes;
                        if (bytes is null)
                            _photoLoadFailed.Add(item.HolderId);
                    }
                }
                catch
                {
                    lock (_cacheLock)
                        _photoLoadFailed.Add(item.HolderId);
                }
            }
        }
        finally
        {
            if (session is not null)
            {
                try { InvokeMethod(session, "close"); } catch { }
            }

            ReleaseComObject(server);
            ReleaseComObject(session);
            ReleaseComObject(connection);
        }
    }

    private static byte[]? LoadPhoto(object server, EventRecord item)
    {
        var filter = CreateComObject("ApcSrvSDK.TApcANDObjFilter");
        var first = CreateEqualFilter("strFirstName", item.FirstName);
        var last = CreateEqualFilter("strLastName", item.LastName);
        var middle = string.IsNullOrWhiteSpace(item.MiddleName)
            ? null
            : CreateEqualFilter("strMiddleName", item.MiddleName);

        try
        {
            InvokeMethod(filter, "addCondition", first);
            InvokeMethod(filter, "addCondition", last);
            if (middle is not null)
                InvokeMethod(filter, "addCondition", middle);

            var holders = ToObjectArray(InvokeOutObject(server, "getObjectsByFilter", "TApcCardHolder", filter));
            if (holders.Length == 0)
                return null;

            var holder = holders[0];
            try
            {
                var photos = ToObjectArray(InvokeOutObject(holder, "getChildrenObjsByTypes", new[] { "TApcCHMainPhoto" }));
                if (photos.Length == 0)
                    return null;

                var photo = photos[0];
                try
                {
                    var settings = InvokeOutObject(photo, "getCurrentSettings");
                    try
                    {
                        return GetProperty(settings, "binBufPhoto") as byte[];
                    }
                    finally
                    {
                        ReleaseComObject(settings);
                    }
                }
                finally
                {
                    foreach (var photoObject in photos)
                        ReleaseComObject(photoObject);
                }
            }
            finally
            {
                foreach (var holderObject in holders)
                    ReleaseComObject(holderObject);
            }
        }
        finally
        {
            ReleaseComObject(middle);
            ReleaseComObject(last);
            ReleaseComObject(first);
            ReleaseComObject(filter);
        }
    }

    private static object CreateEqualFilter(string propertyName, string value)
    {
        var filter = CreateComObject("ApcSrvSDK.TApcEQUALObjFilter");
        SetProperty(filter, "strName", propertyName);
        SetProperty(filter, "Value", value);
        return filter;
    }

    private static object CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)
            ?? throw new InvalidOperationException($"COM-класс APACS не найден: {progId}");

        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Не удалось создать COM-класс APACS: {progId}");
    }

    private static object InvokeOutObject(object target, string methodName, params object[] inputArguments)
    {
        var arguments = new object[inputArguments.Length + 1];
        inputArguments.CopyTo(arguments, 0);

        var modifier = new ParameterModifier(arguments.Length);
        modifier[arguments.Length - 1] = true;

        var result = target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            null,
            target,
            arguments,
            new[] { modifier },
            null,
            null);

        if (result is int errorCode && errorCode != 0)
            throw new ApacsSdkException(methodName, errorCode);

        return arguments[^1]
            ?? throw new InvalidOperationException($"APACS SDK не вернул результат: {methodName}");
    }

    private static object? InvokeMethod(object target, string methodName, params object[] arguments)
    {
        var result = target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            null,
            target,
            arguments);

        if (result is int errorCode && errorCode != 0)
            throw new ApacsSdkException(methodName, errorCode);

        return result;
    }

    private static object? GetProperty(object target, string propertyName) =>
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            null,
            target,
            null);

    private static void SetProperty(object target, string propertyName, object value) =>
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            null,
            target,
            new[] { value });

    private static object[] ToObjectArray(object value)
    {
        if (value is object[] objects)
            return objects;

        if (value is Array array)
        {
            var result = new object[array.Length];
            array.CopyTo(result, 0);
            return result;
        }

        throw new InvalidOperationException("APACS SDK вернул неожиданный тип коллекции объектов.");
    }

    private static string GetLogin() =>
        Environment.GetEnvironmentVariable("APACS_SDK_LOGIN") ?? "inst";

    private static string GetPassword() =>
        Environment.GetEnvironmentVariable("APACS_SDK_PASSWORD") ?? "";

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { }
        }
    }

    private sealed class ApacsSdkException : Exception
    {
        public ApacsSdkException(string methodName, int errorCode)
            : base($"APACS SDK {methodName} завершился с кодом {errorCode}.")
        {
        }
    }
}
