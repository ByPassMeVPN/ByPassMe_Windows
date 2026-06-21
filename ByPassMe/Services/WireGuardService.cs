using System.IO;

namespace ByPassMe.Services;

/// <summary>Управление WireGuard через фоновую службу (без UAC и окон у пользователя).</summary>
public sealed class WireGuardService
{
    public static WireGuardService Instance { get; } = new();

    private const string TunnelName = "ByPassMe-Bypass";
    private volatile bool _tunnelActive;

    private static string TunnelConfigDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ByPassMe", "tunnels");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public bool IsActive => _tunnelActive;

    public void CleanupOrphanedTunnel()
    {
        if (!TunnelHelperClient.IsHelperInstalled)
            return;
        if (TunnelHelperClient.TryStopSilent())
            _tunnelActive = false;
    }

    public async Task StartAsync(string configString, CancellationToken ct = default)
    {
        await StopAsync();

        if (!TunnelHelperClient.IsHelperInstalled)
        {
            throw new InvalidOperationException(
                "VPN-служба не установлена. Переустановите ByPassMe — при установке один раз потребуются права администратора.");
        }

        if (!TunnelHelperClient.EnsureReady())
        {
            throw new InvalidOperationException(
                "VPN-служба не отвечает. Перезагрузите компьютер или переустановите ByPassMe.");
        }

        var configPath = Path.Combine(TunnelConfigDir, $"{TunnelName}.conf");
        await File.WriteAllTextAsync(configPath, configString, ct);

        AppLogger.Instance.Vpn($"WireGuard конфиг сохранён ({configString.Length} байт)");

        if (!TunnelHelperClient.Start(configPath))
            throw new InvalidOperationException("Не удалось запустить WireGuard-туннель");

        _tunnelActive = true;
        AppLogger.Instance.Vpn("WireGuard туннель установлен ✓");
    }

    public Task StopAsync()
    {
        StopSync();
        return Task.CompletedTask;
    }

    public void StopSync()
    {
        if (!TunnelHelperClient.IsHelperInstalled)
        {
            _tunnelActive = false;
            return;
        }

        if (TunnelHelperClient.TryStopSilent())
            AppLogger.Instance.Vpn("WireGuard туннель остановлен");

        _tunnelActive = false;
    }
}
