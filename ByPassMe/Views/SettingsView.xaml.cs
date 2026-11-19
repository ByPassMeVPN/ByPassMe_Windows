using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByPassMe.Services;

namespace ByPassMe.Views;

/// <summary>Вкладка «Настройки»: работа в фоне, оформление, логи, версия.</summary>
public partial class SettingsView : UserControl
{
    private readonly LogsView _logs = new();
    private bool _active;

    public SettingsView()
    {
        InitializeComponent();
        LogsHost.Content = _logs;
        ThemeManager.Instance.Changed += () => Dispatcher.Invoke(UpdateTheme);
        Loaded += (_, _) => Refresh();
        var v = typeof(SettingsView).Assembly.GetName().Version;
        VersionText.Text = v == null ? "ByPassMe" : $"ByPassMe {v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Вкладка видна/скрыта — логи обновляются только на экране.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        _logs.SetActive(active && LogsPanel.Visibility == Visibility.Visible);
        if (active) Refresh();
    }

    private void Refresh()
    {
        StartWithWindows.IsChecked = StartupTask.IsEnabled();
        AutoConnect.IsChecked = SettingsStore.Instance.AutoConnect;
        var last = SettingsStore.Instance.LastMode == "bypass" ? "Обход" : "VPN";
        AutoConnectHint.Text = $"При запуске включается то, что было включено последним (сейчас: {last})";
        UpdateTheme();
    }

    private void OnStartWithWindows(object sender, RoutedEventArgs e)
    {
        var want = StartWithWindows.IsChecked == true;
        var ok = want ? StartupTask.Enable() : StartupTask.Disable();
        if (!ok)
        {
            StartWithWindows.IsChecked = StartupTask.IsEnabled();
            MessageBox.Show("Не удалось изменить автозапуск. Запустите ByPassMe от имени администратора.",
                "ByPassMe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAutoConnect(object sender, RoutedEventArgs e)
    {
        var want = AutoConnect.IsChecked == true;
        SettingsStore.Instance.SaveAutoConnect(want);
        AppLogger.Instance.Service($"Подключаться автоматически: {(want ? "включено" : "выключено")}");
    }

    private void OnThemeSystem(object sender, RoutedEventArgs e) => ThemeManager.Instance.SetThemeMode("system");
    private void OnThemeLight(object sender, RoutedEventArgs e) => ThemeManager.Instance.SetThemeMode("light");
    private void OnThemeDark(object sender, RoutedEventArgs e) => ThemeManager.Instance.SetThemeMode("dark");
    private void OnIndigo(object sender, MouseButtonEventArgs e) => ThemeManager.Instance.SetPalette("indigo");
    private void OnForest(object sender, MouseButtonEventArgs e) => ThemeManager.Instance.SetPalette("forest");
    private void OnEspresso(object sender, MouseButtonEventArgs e) => ThemeManager.Instance.SetPalette("espresso");

    private void UpdateTheme()
    {
        var mode = ThemeManager.Instance.ThemeMode;
        ThemeSystem.IsChecked = mode == "system";
        ThemeLight.IsChecked = mode == "light";
        ThemeDark.IsChecked = mode == "dark";
        var palette = ThemeManager.Instance.Palette;
        var ring = (Brush)FindResource("PrimaryBrush");
        foreach (var (el, id) in new[] { (PalIndigo, "indigo"), (PalForest, "forest"), (PalEspresso, "espresso") })
        {
            el.Stroke = palette == id ? ring : null;
            el.StrokeThickness = palette == id ? 3 : 0;
        }
    }

    private void OnOpenLogs(object sender, MouseButtonEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        LogsPanel.Visibility = Visibility.Visible;
        _logs.SetActive(_active);
    }

    private void OnCloseLogs(object sender, RoutedEventArgs e)
    {
        _logs.SetActive(false);
        LogsPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
    }
}
