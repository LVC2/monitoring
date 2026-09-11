namespace ApacsMonitor.Models;

public sealed class EventRecord
{
    public DateTime RealTime { get; init; }
    public DateTime RegisterTime { get; init; }
    public string InitObjectName { get; init; } = "";
    public int EventType { get; init; }
    public int InitObjectId0 { get; init; }
    public int InitObjectId1 { get; init; }
    public int SekId0 { get; init; }
    public int SekId1 { get; init; }
}
