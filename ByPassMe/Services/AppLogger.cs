using ByPassMe.Models;

namespace ByPassMe.Services;

public sealed class AppLogger
{
    public static AppLogger Instance { get; } = new();

    private readonly object _lock = new();
    private long _counter;
    private readonly List<AppLogEntry> _entries = [];
    private const int MaxEntries = 200;

    public event Action? Changed;

    public IReadOnlyList<AppLogEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    private void Log(LogSource source, LogLevel level, string message)
    {
        var trimmed = message.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        var entry = new AppLogEntry
        {
            Id = Interlocked.Increment(ref _counter),
            Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            Source = source,
            Level = level,
            Message = trimmed
        };

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }

        Changed?.Invoke();
    }

    public void Vpn(string msg) => Log(LogSource.Vpn, LogLevel.Info, msg);
    public void VpnErr(string msg) => Log(LogSource.Vpn, LogLevel.Error, msg);
    public void VpnDbg(string msg) => Log(LogSource.Vpn, LogLevel.Debug, msg);
    public void Bypass(string msg) => Log(LogSource.Bypass, LogLevel.Info, msg);
    public void BypassErr(string msg) => Log(LogSource.Bypass, LogLevel.Error, msg);
    public void BypassDbg(string msg) => Log(LogSource.Bypass, LogLevel.Debug, msg);
    public void Service(string msg) => Log(LogSource.Service, LogLevel.Info, msg);
    public void ServiceErr(string msg) => Log(LogSource.Service, LogLevel.Error, msg);
    public void ServiceDbg(string msg) => Log(LogSource.Service, LogLevel.Debug, msg);
    public void System(string msg) => Log(LogSource.System, LogLevel.Info, msg);

    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Changed?.Invoke();
    }
}
