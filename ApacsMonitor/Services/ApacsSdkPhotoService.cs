using System.Reflection;
using System.Runtime.InteropServices;
using ApacsMonitor.Models;

namespace ApacsMonitor.Services;

/// <summary>
/// Read-only bridge to the installed APACS 3000 COM SDK.
/// APACS 7.1 registers ApcSrvSDK as a 32-bit COM server, therefore the main
/// application is built x86. Late binding keeps the repository independent from
/// a machine-specific generated interop assembly.
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
        if (events.Count == 0)
            return;

        ApplyCachedPhotos(events);

        if (DateTime.UtcNow < _disabledUntilUtc)
            return;

        var candidates = GetMissingCandidates(events);
        if (candidates.Count == 0)
            return;

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            candidates = GetMissingCandidates(events);
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

        ApplyCachedPhotos(events);
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

    private List<EventRecord> GetMissingCandidates(IReadOnlyList<EventRecord> events)
    {
        var candidates = events
            .Where(x => x.HolderId > 0 &&
                        !string.IsNullOrWhiteSpace(x.LastName) &&
                        !string.IsNullOrWhiteSpace(x.FirstName))
            .GroupBy(x => x.HolderId)
            .Select(x => x.First())
            .ToList();

        lock (_cacheLock)
        {
            return candidates
                .Where(x => !_photoCache.ContainsKey(x.HolderId) && !_photoLoadFailed.Contains(x.HolderId))
                .ToList();
        }
    }

    private void ApplyCachedPhotos(IReadOnlyList<EventRecord> events)
    {
        foreach (var item in events)
        {
            lock (_cacheLock)
            {
                if (_photoCache.TryGetValue(item.HolderId, out var bytes))
                    item.PhotoBytes = bytes;
            }
        }
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
            _disabledUntilUtc = DateTime.MinValue;

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
