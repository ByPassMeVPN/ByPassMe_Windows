using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ByPassMe.Models;

public sealed class VpnServerRowViewModel : INotifyPropertyChanged
{
    public required ImageSource FlagImage { get; init; }
    public required string Country { get; init; }
    public required string Protocol { get; init; }
    public required VpnServer Server { get; init; }

    private const int Pinging = -2;

    private int? _pingMs;

    public int? PingMs
    {
        get => _pingMs;
        set
        {
            if (_pingMs == value) return;
            _pingMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PingText));
            OnPropertyChanged(nameof(HasPing));
        }
    }

    public string PingText => _pingMs switch
    {
        null => "пинг",
        Pinging => "…",
        -1 => "таймаут",
        _ => $"{_pingMs} мс",
    };

    public bool HasPing => _pingMs is not null;

    public void MarkPinging() => PingMs = Pinging;

    public void ClearPing() => PingMs = null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
