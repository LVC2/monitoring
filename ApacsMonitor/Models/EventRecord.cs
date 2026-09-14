using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ApacsMonitor.Models;

public sealed class EventRecord : INotifyPropertyChanged
{
    private byte[]? _photoBytes;

    public DateTime RealTime { get; init; }
    public DateTime RegisterTime { get; init; }
    public string LastName { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string MiddleName { get; init; } = "";
    public string FullName { get; init; } = "";
    public string CardNumber { get; init; } = "";
    public string Location { get; init; } = "";
    public string Direction { get; init; } = "";
    public string ReaderName { get; init; } = "";
    public string RawObjectName { get; init; } = "";
    public int HolderId { get; init; }

    public byte[]? PhotoBytes
    {
        get => _photoBytes;
        set
        {
            if (ReferenceEquals(_photoBytes, value))
                return;

            _photoBytes = value;
            OnPropertyChanged();
        }
    }

    public int EventType { get; init; }
    public int SekId0 { get; init; }
    public int SekId1 { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
