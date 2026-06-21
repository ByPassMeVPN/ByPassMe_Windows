using System.Diagnostics;

namespace ByPassMe.TunnelHelper;

internal static class WireGuardRunner
{
    private const string TunnelName = "ByPassMe-Bypass";
    private const string LegacyTunnelName = "wg-bypass";
    private static string ServiceName => TunnelServiceName(TunnelName);
    private static string LegacyServiceName => TunnelServiceName(LegacyTunnelName);

    public static bool StartTunnel(string configPath)
    {
        if (!File.Exists(configPath))
            return false;

        var wg = FindWireGuardExe();
        if (wg == null)
            return false;

        StopTunnel();

        var code = RunProcess(wg, $"/installtunnelservice \"{configPath}\"");
        return code == 0;
    }

    public static void StopTunnel()
    {
        var wg = FindWireGuardExe();
        var hasBypass = ServiceExists(ServiceName);
        var hasLegacy = ServiceExists(LegacyServiceName);

        if (wg != null && (hasBypass || hasLegacy))
        {
            if (hasBypass)
                RunProcess(wg, $"/uninstalltunnelservice \"{TunnelName}\"");
            if (hasLegacy)
                RunProcess(wg, $"/uninstalltunnelservice \"{LegacyTunnelName}\"");
        }

        if (hasBypass)
            TryDeleteService(ServiceName);
        if (hasLegacy)
            TryDeleteService(LegacyServiceName);

        KillProcess("wireguard");
        KillProcess("wg");
        RunTaskKill("wireguard.exe");
        RunTaskKill("wg.exe");
    }

    private static string? FindWireGuardExe()
    {
        // tools\wireguard.exe рядом с TunnelHelper (Program Files\ByPassMe\tools)
        var bundled = Path.Combine(AppContext.BaseDirectory, "wireguard.exe");
        if (File.Exists(bundled))
            return bundled;

        var installRoot = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
        if (installRoot != null)
        {
            var fromTools = Path.Combine(installRoot, "tools", "wireguard.exe");
            if (File.Exists(fromTools))
                return fromTools;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\FlyFrogLLC\ByPassMe");
            var installPath = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(installPath))
            {
                var fromReg = Path.Combine(installPath, "tools", "wireguard.exe");
                if (File.Exists(fromReg))
                    return fromReg;
            }
        }
        catch { /* ignore */ }

        var pf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WireGuard", "wireguard.exe");
        return File.Exists(pf) ? pf : null;
    }

    private static string TunnelServiceName(string tunnelName) => $"WireGuardTunnel${tunnelName}";

    private static bool ServiceExists(string serviceName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query \"{serviceName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteService(string serviceName)
    {
        if (!ServiceExists(serviceName))
            return;
        RunSc($"stop \"{serviceName}\"");
        if (ServiceExists(serviceName))
            RunSc($"delete \"{serviceName}\"");
    }

    private static int RunProcess(string exe, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(30000);
            return proc?.ExitCode ?? 1;
        }
        catch
        {
            return 1;
        }
    }

    private static void RunSc(string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(5000);
        }
        catch { /* ignore */ }
    }

    private static void RunTaskKill(string imageName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/F /IM {imageName} /T",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(5000);
        }
        catch { /* ignore */ }
    }

    private static void KillProcess(string name)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { /* ignore */ }
    }
}
