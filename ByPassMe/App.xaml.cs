using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ByPassMe.Services;

namespace ByPassMe;

public partial class App : Application
{
    private DispatcherTimer? _pollTimer;
    private static int _cleanupDone;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            MessageBox.Show(
                $"Ошибка ByPassMe:\n{args.Exception.Message}",
                "ByPassMe",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog(ex);
        };

        SessionEnding += (_, _) => ShutdownCleanup();
        Exit += (_, _) => ShutdownCleanup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownCleanup();

        ThemeManager.Instance.Apply();

        base.OnStartup(e);

        SubscriptionChecker.Instance.LoadCached();
        BypassServerManager.Instance.LoadCached();

        // Даём UI загрузить кэш до фоновой проверки /meta (иначе revoke мог сбрасывать экран)
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            SubscriptionChecker.Instance.RefreshInBackground();
            BypassServerManager.Instance.RefreshInBackground();
        });

        Task.Run(() =>
        {
            try { WireGuardService.Instance.CleanupOrphanedTunnel(); }
            catch (Exception ex) { WriteCrashLog(ex); }
        });

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += (_, _) =>
        {
            // Как Android: опрос /meta каждые 5 с (device_removed → онбординг)
            if (!string.IsNullOrEmpty(SettingsStore.Instance.VpnSubscriptionUrl))
                SubscriptionChecker.Instance.RefreshInBackground();
        };
        _pollTimer.Start();
    }

    private static void ShutdownCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupDone, 1) != 0)
            return;

        try
        {
            if (Current?.MainWindow is MainWindow mw)
                mw.FlushState();
        }
        catch { /* ignore */ }

        try { BypassManager.Instance.Stop(); }
        catch { /* ignore */ }
        try { WireGuardService.Instance.StopSync(); }
        catch { /* ignore */ }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ByPassMe");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n");
        }
        catch { /* ignore */ }
    }
}
