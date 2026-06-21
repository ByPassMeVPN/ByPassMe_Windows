using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using ByPassMe.Services;
using ByPassMe.Views;

namespace ByPassMe;

public partial class MainWindow : Window
{
    private const int GwlStyle = -16;
    private const int WsMaximizebox = 0x10000;

    private readonly OnboardingView _onboarding = new();
    private readonly BypassView _bypass = new();
    private readonly LogsView _logs = new();
    private int _selectedTab;
    private double _tabWidth;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        NavBar.SizeChanged += (_, _) => UpdateNavMetrics();
        SettingsStore.Instance.Changed += OnSettingsChanged;
        SubscriptionChecker.Instance.Changed += OnSubscriptionCheckerChanged;
        ThemeManager.Instance.Changed += () => Dispatcher.Invoke(RefreshChrome);
        UpdateContent();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Запрет разворачивания на весь экран — планшетный размер фиксирован
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlStyle);
        SetWindowLong(hwnd, GwlStyle, style & ~WsMaximizebox);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateNavMetrics();
        RefreshChrome();
        _ = TryRestoreSessionAsync();
    }

    private async Task TryRestoreSessionAsync()
    {
        var store = SettingsStore.Instance;
        if (!string.IsNullOrEmpty(store.VpnUuid) || store.HasOfflineBypassCache)
            return;

        var url = store.VpnSubscriptionUrl;
        if (string.IsNullOrEmpty(url))
            return;

        AppLogger.Instance.Service("Восстановление сессии из сохранённой подписки...");
        try
        {
            await SubscriptionChecker.Instance.RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.ServiceDbg($"Восстановление сессии: {ex.Message}");
        }

        UpdateContent();
    }

    private void UpdateNavMetrics()
    {
        if (NavGrid.ActualWidth <= 0)
            return;

        _tabWidth = NavGrid.ActualWidth / 2.0;
        NavIndicator.Width = _tabWidth;
        NavIndicatorTransform.X = _selectedTab * _tabWidth;
    }

    private void OnSettingsChanged() => Dispatcher.Invoke(UpdateContent);

    private void OnSubscriptionCheckerChanged() => Dispatcher.Invoke(UpdateContent);

    private void UpdateContent()
    {
        if (!SettingsStore.Instance.HasActiveSession)
        {
            MainContent.Content = _onboarding;
            NavBar.Visibility = Visibility.Collapsed;
            _onboarding.RestoreSavedState();
        }
        else
        {
            NavBar.Visibility = Visibility.Visible;
            ShowTab(_selectedTab, animate: false);
        }
    }

    private void OnTabBypass(object sender, RoutedEventArgs e) => ShowTab(0);
    private void OnTabLogs(object sender, RoutedEventArgs e) => ShowTab(1);

    private void ShowTab(int tab, bool animate = true)
    {
        _selectedTab = tab;
        MainContent.Content = tab == 0 ? _bypass : _logs;
        _logs.SetActive(tab == 1);

        UpdateNavMetrics();
        var target = tab * _tabWidth;
        if (animate)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NavIndicatorTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }
        else
        {
            NavIndicatorTransform.X = target;
        }

        SetTabActive(TabBypass, TabBypassIcon, tab == 0);
        SetTabActive(TabLogs, TabLogsIcon, tab == 1);
    }

    private void SetTabActive(System.Windows.Controls.Button btn, System.Windows.Controls.TextBlock icon, bool active)
    {
        btn.Opacity = active ? 1.0 : 0.55;
        icon.Opacity = active ? 1.0 : 0.7;
        var label = (System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)btn.Content).Children[1];
        label.Foreground = active
            ? (System.Windows.Media.Brush)FindResource("PrimaryBrush")!
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")!;
        label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
    }

    private void RefreshChrome()
    {
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush")!;
    }

    public void FlushState()
    {
        _onboarding.FlushPendingDraft();
        _bypass.FlushPendingSave();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _onboarding.FlushPendingDraft();
        _bypass.FlushPendingSave();
        BypassManager.Instance.Stop();
        WireGuardService.Instance.StopSync();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        WireGuardService.Instance.StopSync();
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
