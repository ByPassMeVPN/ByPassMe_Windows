using System.Diagnostics;
using System.Text;

namespace ByPassMe.TunnelHelper;

internal static class ServiceInstaller
{
    public const string ServiceName = "ByPassMeTunnelHelper";
    private const string DisplayName = "ByPassMe Tunnel Helper";

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ByPassMe", "service-install.log");

    public static int Install()
    {
        try
        {
            var exe = ResolveExePath();
            Log($"=== Install {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Log($"exe: {exe}");
            Log($"base: {AppContext.BaseDirectory}");

            if (!File.Exists(exe))
            {
                Log("ERROR: exe not found");
                return 1;
            }

            RunSc($"stop \"{ServiceName}\"", ignoreErrors: true);
            RunSc($"delete \"{ServiceName}\"", ignoreErrors: true);

            // sc create: binPath= "C:\Program Files\...\ByPassMe.TunnelHelper.exe"
            var createArgs =
                $"create \"{ServiceName}\" binPath= \"{exe}\" start= auto obj= LocalSystem DisplayName= \"{DisplayName}\"";
            if (RunSc(createArgs) != 0)
            {
                Log("ERROR: sc create failed");
                return 1;
            }

            RunSc($"description \"{ServiceName}\" \"Управляет WireGuard-туннелем ByPassMe (FlyFrogLLC)\"", ignoreErrors: true);
            RunSc($"failure \"{ServiceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000", ignoreErrors: true);

            var startCode = RunSc($"start \"{ServiceName}\"");
            Log($"sc start exit code: {startCode}");

            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(500);
                var state = QueryServiceState();
                Log($"query state ({i + 1}): {state ?? "null"}");
                if (state == "RUNNING")
                    return 0;
            }

            if (QueryServiceState() != null)
            {
                Log("WARN: service registered but not RUNNING yet — ok for auto start");
                return 0;
            }

            Log("ERROR: service not registered after create");
            return 1;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex}");
            return 1;
        }
    }

    public static int Uninstall()
    {
        try
        {
            Log($"=== Uninstall {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            WireGuardRunner.StopTunnel();
            RunSc($"stop \"{ServiceName}\"", ignoreErrors: true);
            return RunSc($"delete \"{ServiceName}\"", ignoreErrors: true);
        }
        catch (Exception ex)
        {
            Log($"ERROR uninstall: {ex}");
            return 1;
        }
    }

    private static string ResolveExePath()
    {
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
            return Path.GetFullPath(Environment.ProcessPath);

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ByPassMe.TunnelHelper.exe"));
    }

    private static string? QueryServiceState()
    {
        var (code, output) = RunScCapture($"query \"{ServiceName}\"");
        if (code != 0)
            return null;

        foreach (var line in output.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 4 ? parts[3] : trimmed;
            }
        }

        return "UNKNOWN";
    }

    private static int RunSc(string args, bool ignoreErrors = false)
    {
        var (code, output) = RunScCapture(args);
        if (output.Length > 0)
            Log($"sc {args} -> exit={code} {output.Replace('\n', ' ').Trim()}");
        else
            Log($"sc {args} -> exit={code}");

        return ignoreErrors ? 0 : code;
    }

    private static (int ExitCode, string Output) RunScCapture(string args)
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
            if (proc == null)
                return (1, "");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            return (proc.ExitCode, (stdout + stderr).Trim());
        }
        catch (Exception ex)
        {
            Log($"sc exception ({args}): {ex.Message}");
            return (1, ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, message + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* ignore */ }
    }
}
