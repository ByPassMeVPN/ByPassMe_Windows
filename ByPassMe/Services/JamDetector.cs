using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;

namespace ByPassMe.Services;

/// <summary>
/// Фоновая проверка глушения: TCP к иностранным VPN-серверам (NL / DE / US).
/// Если ни один не отвечает — зона глушения; если хоть один отвечает — интернет открыт,
/// баннер сразу скрывается (Changed → UI).
/// </summary>
public sealed class JamDetector
{
    public static JamDetector Instance { get; } = new();

    public enum State { Unknown, Open, Jammed, Checking }

    public State Current { get; private set; } = State.Unknown;
    public bool IsJammed => Current == State.Jammed;

    public event Action? Changed;

    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private int _checkGate; // 0 = free, 1 = running
    private const int ProbeTimeoutMs = 3_000;
    private static readonly TimeSpan IntervalOpen = TimeSpan.FromSeconds(30);
    /// <summary>В глушении опрашиваем чаще — баннер исчезнет сразу после восстановления.</summary>
    private static readonly TimeSpan IntervalJammed = TimeSpan.FromSeconds(8);

    private static readonly (string Host, int Port)[] FallbackTargets =
    [
        ("144.31.14.197", 443), // NL
        ("177.3.218.224", 443), // FI
        ("177.202.119.100", 443), // US
        ("1.1.1.1", 443),      // Cloudflare — общий иностранный IP
        ("8.8.8.8", 443),      // Google DNS
    ];

    public void Start()
    {
        lock (_lock)
        {
            if (_cts is { IsCancellationRequested: false })
                return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => LoopAsync(ct), ct);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
        }
        SetState(State.Unknown);
    }

    /// <summary>Немедленная проверка (активация окна / смена вкладки).</summary>
    public Task CheckNowAsync(CancellationToken ct = default) => CheckCoreAsync(ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await CheckCoreAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var delay = Current == State.Jammed ? IntervalJammed : IntervalOpen;
                await Task.Delay(delay, ct);
                await CheckCoreAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stop
        }
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        // Не копить параллельные проверки
        if (Interlocked.CompareExchange(ref _checkGate, 1, 0) != 0)
            return;

        try
        {
            var prev = Current;
            if (prev is not (State.Jammed or State.Open))
                SetState(State.Checking);

            var targets = ProbeTargets();
            var anyOk = await AnyReachableAsync(targets, ct).ConfigureAwait(false);

            var next = anyOk ? State.Open : State.Jammed;
            if (next != prev)
            {
                if (next == State.Jammed)
                    AppLogger.Instance.Service("Глушение: иностранные серверы недоступны → используйте Обход");
                else if (prev == State.Jammed)
                    AppLogger.Instance.Service("Глушение снято: иностранный доступ восстановлен");
            }

            SetState(next);
        }
        finally
        {
            Interlocked.Exchange(ref _checkGate, 0);
        }
    }

    private static List<(string Host, int Port)> ProbeTargets()
    {
        var byCountry = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in VpnServerManager.Instance.Servers)
        {
            var key = new string(s.Id.Where(char.IsLetter).ToArray()).ToLowerInvariant();
            if (key is not ("nl" or "de" or "us"))
                continue;
            if (!byCountry.ContainsKey(key))
                byCountry[key] = (s.Address, Math.Max(1, s.Port));
        }

        var picked = new List<(string Host, int Port)>();
        foreach (var code in new[] { "nl", "de", "us" })
        {
            if (byCountry.TryGetValue(code, out var t))
                picked.Add(t);
        }

        // Всегда добавляем стабильные IP — hostname VPN может не резолвиться при глушении/после него
        foreach (var fb in FallbackTargets)
        {
            if (!picked.Exists(p =>
                    string.Equals(p.Host, fb.Host, StringComparison.OrdinalIgnoreCase) && p.Port == fb.Port))
                picked.Add(fb);
        }

        return picked;
    }

    private static async Task<bool> AnyReachableAsync(
        IReadOnlyList<(string Host, int Port)> targets, CancellationToken ct)
    {
        if (targets.Count == 0)
            return false;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = targets.Select(t => TcpReachableAsync(t.Host, t.Port, linked.Token)).ToArray();

        // Первый успешный → Open; отменяем остальные
        while (tasks.Length > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            if (await done.ConfigureAwait(false))
            {
                linked.Cancel();
                return true;
            }

            tasks = tasks.Where(t => t != done).ToArray();
        }

        return false;
    }

    private static async Task<bool> TcpReachableAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeoutMs);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void SetState(State next)
    {
        if (Current == next)
            return;
        Current = next;

        void Raise() => Changed?.Invoke();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Raise, DispatcherPriority.Normal);
        else
            Raise();
    }
}
