using System.Windows;
using System.Windows.Threading;

namespace ByPassMe.Services;

/// <summary>
/// Таймер сессии VPN / Обход (скорость убрана — clash_api давал нестабильные нули).
/// </summary>
public sealed class ConnectionStatsService
{
    public static ConnectionStatsService Vpn { get; } = new();
    public static ConnectionStatsService Bypass { get; } = new();

    private readonly object _gate = new();
    private DispatcherTimer? _timer;
    private DateTime? _startedUtc;

    private ConnectionStatsService() { }

    public bool IsActive { get; private set; }
    public string DurationText { get; private set; } = "00:00";

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            StopLocked();
            _startedUtc = DateTime.UtcNow;
            IsActive = true;
            RefreshDuration();

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1),
                };
                _timer.Tick += (_, _) =>
                {
                    RefreshDuration();
                    Changed?.Invoke();
                };
                _timer.Start();
            }
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
        Changed?.Invoke();
    }

    private void StopLocked()
    {
        try { _timer?.Stop(); } catch { /* ignore */ }
        _timer = null;
        _startedUtc = null;
        IsActive = false;
        DurationText = "00:00";
    }

    private void RefreshDuration()
    {
        if (_startedUtc is not { } start)
        {
            DurationText = "00:00";
            return;
        }

        var elapsed = DateTime.UtcNow - start;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        DurationText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }
}
