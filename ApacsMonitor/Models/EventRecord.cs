namespace ApacsMonitor.Models;

public sealed class EventRecord
{
    public DateTime RealTime { get; init; }
    public DateTime RegisterTime { get; init; }
    public string FullName { get; init; } = "";
    public string CardNumber { get; init; } = "";
    public string Location { get; init; } = "";
    public string Direction { get; init; } = "";
    public string ReaderName { get; init; } = "";
    public string RawObjectName { get; init; } = "";
    public int EventType { get; init; }
    public int SekId0 { get; init; }
    public int SekId1 { get; init; }
}
