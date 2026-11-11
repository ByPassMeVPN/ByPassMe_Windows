using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private readonly VpnView _vpn = new();
    private readonly BypassView _bypass = new();
    private readonly LogsView _logs = new();
    /// <summary>0=VPN, 1=Обход, 2=Логи. После подписки по умолчанию VPN.</summary>
    private int _selectedTab = 0;
    private double _tabHeight;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += (_, _) =>
        {
            if (SettingsStore.Instance.HasActiveSession)
                _ = JamDetector.Instance.CheckNowAsync();
        };
        NavBar.SizeChanged += (_, _) => UpdateNavMetrics();
        SettingsStore.Instance.Changed += OnSettingsChanged;
        SubscriptionChecker.Instance.Changed += OnSubscriptionCheckerChanged;
        ThemeManager.Instance.Changed += () => Dispatcher.Invoke(RefreshChrome);
        UpdateContent();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlStyle);
        SetWindowLong(hwnd, GwlStyle, style & ~WsMaximizebox);
        FitToScreen();
        DpiChanged += (_, _) => FitToScreen();
    }

    /// <summary>
    /// Подгоняет окно под экран: на ноутбуках с невысоким разрешением или при
    /// укрупнении шрифтов Windows (125%, 150%) окно 1000x630 не помещалось бы
    /// и часть кнопок оказалась бы за краем — менять размер пользователь не
    /// может. Содержимое ужимается через Viewbox, пропорции сохраняются.
    /// </summary>
    private void FitToScreen()
    {
        var work = SystemParameters.WorkArea;
        var width = Math.Min(DesignWidth, work.Width - 16);
        var height = Math.Min(DesignHeight, work.Height - 16);
        if (width < MinWidth) width = MinWidth;
        if (height < MinHeight) height = MinHeight;

        if (Math.Abs(Width - width) > 0.5 || Math.Abs(Height - height) > 0.5)
        {
            Width = width;
            Height = height;
        }

        // Окно могло оказаться частично за краем — возвращаем в рабочую область.
        Left = Math.Max(work.Left, Math.Min(work.Left + (work.Width - Width) / 2, work.Right - Width));
        Top = Math.Max(work.Top, Math.Min(work.Top + (work.Height - Height) / 2, work.Bottom - Height));
    }

    private const double DesignWidth = 1000;
    private const double DesignHeight = 630;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateNavMetrics();
        RefreshChrome();
        _ = TryRestoreSessionAsync();
        if (SettingsStore.Instance.HasActiveSession)
            VpnServerManager.Instance.RefreshInBackground();
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
        if (NavGrid.ActualHeight <= 0)
            return;

        _tabHeight = NavGrid.ActualHeight / 3.0;
        NavIndicator.Height = _tabHeight;
        NavIndicatorTransform.Y = _selectedTab * _tabHeight;
    }

    private void OnSettingsChanged() => Dispatcher.Invoke(UpdateContent);

    private void OnSubscriptionCheckerChanged() => Dispatcher.Invoke(UpdateContent);

    private void UpdateContent()
    {
        if (!SettingsStore.Instance.HasActiveSession)
        {
            JamDetector.Instance.Stop();

            // На онбординге правая колонка (112px) пустая — растягиваем контент на всю ширину,
            // иначе блок «висит» слева, а не по центру окна.
            Grid.SetColumnSpan(MainContent, 2);
            MainContent.Margin = new Thickness(0);
            MainContent.Content = _onboarding;
            NavBar.Visibility = Visibility.Collapsed;
            _onboarding.RestoreSavedState();
        }
        else
        {
            JamDetector.Instance.Start();

            Grid.SetColumnSpan(MainContent, 1);
            MainContent.Margin = new Thickness(0, 0, 12, 0);
            NavBar.Visibility = Visibility.Visible;
            ShowTab(_selectedTab, animate: false);
        }
    }

    private void OnTabVpn(object sender, RoutedEventArgs e)
    {
        ShowTab(0);
        VpnServerManager.Instance.RefreshInBackground();
    }
    private void OnTabBypass(object sender, RoutedEventArgs e) => ShowTab(1);
    private void OnTabLogs(object sender, RoutedEventArgs e) => ShowTab(2);

    private void ShowTab(int tab, bool animate = true)
    {
        _selectedTab = tab;
        MainContent.Content = tab switch
        {
            0 => _vpn,
            1 => _bypass,
            _ => _logs
        };
        _logs.SetActive(tab == 2);

        UpdateNavMetrics();
        var target = tab * _tabHeight;
        if (animate)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NavIndicatorTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
        }
        else
        {
            NavIndicatorTransform.Y = target;
        }

        SetTabActive(TabVpn, TabVpnIcon, tab == 0);
        SetTabActive(TabBypass, TabBypassIcon, tab == 1);
        SetTabActive(TabLogs, TabLogsIcon, tab == 2);
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
        VpnManager.Instance.Stop();
        BypassManager.Instance.Stop();
        WireGuardService.Instance.StopSync();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        VpnManager.Instance.Stop();
        WireGuardService.Instance.StopSync();
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
