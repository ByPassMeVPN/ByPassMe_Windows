using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace ByPassMe.Services;

/// <summary>Команды фоновой службе ByPassMeTunnelHelper (без UAC у пользователя).</summary>
internal static class TunnelHelperClient
{
    public const string HelperServiceName = "ByPassMeTunnelHelper";
    private const string PipeName = "ByPassMeTunnel";
    private const int ConnectTimeoutMs = 5000;
    private const int CommandTimeoutMs = 45000;
    private const int QuickConnectMs = 1000;
    private const int QuickReadMs = 2000;

    public static bool IsHelperInstalled => ServiceExists(HelperServiceName);

    public static bool IsAvailable => SendCommand("PING", QuickReadMs, QuickConnectMs) == "OK";

    public static bool EnsureReady()
    {
        if (!IsHelperInstalled)
            return false;

        for (var i = 0; i < 6; i++)
        {
            if (IsAvailable)
                return true;
            Thread.Sleep(400);
        }

        return false;
    }

    public static bool Start(string configPath) =>
        SendCommand($"START|{configPath}", CommandTimeoutMs, ConnectTimeoutMs) == "OK";

    public static bool Stop() =>
        SendCommand("STOP", CommandTimeoutMs, ConnectTimeoutMs) == "OK";

    public static bool TryStopSilent()
    {
        if (!IsHelperInstalled)
            return false;
        return SendCommand("STOP", QuickReadMs, QuickConnectMs) == "OK";
    }

    public static string? GetInstallPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\FlyFrogLLC\ByPassMe");
            return key?.GetValue("InstallPath") as string;
        }
        catch
        {
            return null;
        }
    }

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

    private static string? SendCommand(string command, int readTimeoutMs, int connectTimeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            client.Connect(connectTimeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

            writer.WriteLine(command);

            using var cts = new CancellationTokenSource(readTimeoutMs);
            return reader.ReadLineAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }
}
