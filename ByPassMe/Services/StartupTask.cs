using System.Diagnostics;

namespace ByPassMe.Services;

/// <summary>
/// «Запускать вместе с Windows». Приложению нужны права администратора
/// (адаптер wintun), а обычный автозапуск из реестра (Run) такие программы
/// при входе молча не запускает — поэтому задача Планировщика с наивысшими
/// правами при входе пользователя.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "ByPassMe";
    public const string AutostartArg = "--autostart";

    public static bool IsEnabled() => Run("/Query", "/TN", TaskName) == 0;

    public static bool Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        // Пауза 15 с после входа: сеть успевает подняться.
        var ok = Run("/Create", "/F", "/RL", "HIGHEST", "/SC", "ONLOGON", "/DELAY", "0000:15",
            "/TN", TaskName, "/TR", $"\"{exe}\" {AutostartArg}") == 0;
        AppLogger.Instance.Service(ok ? "Автозапуск с Windows включён" : "Не удалось включить автозапуск с Windows");
        return ok;
    }

    public static bool Disable()
    {
        var ok = Run("/Delete", "/F", "/TN", TaskName) == 0;
        AppLogger.Instance.Service(ok ? "Автозапуск с Windows выключен" : "Не удалось выключить автозапуск с Windows");
        return ok;
    }

    private static int Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}
