using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ByPassMe.Helpers;
using ByPassMe.Models;
using ByPassMe.Services;

namespace ByPassMe.Views;

public partial class VpnView : UserControl
{
    private readonly ObservableCollection<VpnServerRowViewModel> _serverRows = [];
    private readonly DispatcherTimer _dotsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? _pingCts;
    private bool _pingRunning;
    private int _dotFrame;
    private bool _initialized;
    private int _selectedServer;

    public VpnView()
    {
        _dotsTimer.Tick += (_, _) => { _dotFrame++; UpdateButton(); };

        InitializeComponent();

        ServerCardContent.SizeChanged += (_, _) => UpdateServerCardClip();
        ServerList.ItemsSource = _serverRows;

        ThemeManager.Instance.Changed += () => Dispatcher.Invoke(UpdateUi);
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _pingCts?.Cancel();
            SaveServerIndex();
        };

        SubscriptionChecker.Instance.Changed += OnSubscriptionChanged;
        VpnServerManager.Instance.Changed += OnServersChanged;
        VpnManager.Instance.Changed += OnVpnChanged;
        SettingsStore.Instance.Changed += OnSettingsChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        VpnServerManager.Instance.LoadCached();
        _selectedServer = SettingsStore.Instance.VpnServerIndex;
        RefreshServers();

        if (!_initialized)
        {
            _initialized = true;
            await RefreshServersAsync(silent: true);
        }
        StatusBanner.Status = SubscriptionChecker.Instance.Status;
        StatusBanner.DaysLeft = SubscriptionChecker.Instance.DaysLeft;
        JamBanner.HasBypassAccess = SettingsStore.Instance.HasBypassAccess;
        JamBanner.Update();
        UpdateUi();
    }

    private void OnSubscriptionChanged() => Dispatcher.Invoke(() =>
    {
        StatusBanner.Status = SubscriptionChecker.Instance.Status;
        StatusBanner.DaysLeft = SubscriptionChecker.Instance.DaysLeft;
        JamBanner.HasBypassAccess = SettingsStore.Instance.HasBypassAccess;
        JamBanner.Update();
        UpdateUi();
    });

    private void OnServersChanged() => Dispatcher.Invoke(() =>
    {
        RefreshServers();
        UpdateUi();
    });

    private void OnVpnChanged() => Dispatcher.Invoke(UpdateUi);
    private void OnSettingsChanged() => Dispatcher.Invoke(() =>
    {
        JamBanner.HasBypassAccess = SettingsStore.Instance.HasBypassAccess;
        JamBanner.Update();
        UpdateUi();
    });

    private void RefreshServers()
    {
        var servers = VpnServerManager.Instance.Servers;
        _serverRows.Clear();

        if (servers.Count == 0)
        {
            ServersEmptyText.Visibility = Visibility.Visible;
            ServerList.Visibility = Visibility.Collapsed;
            ServersEmptyText.Text = SettingsStore.Instance.HasActiveSession
                ? "Список пуст · нажмите ↻ для загрузки"
                : "Сначала введите ссылку подписки (🔑)";
            return;
        }

        ServersEmptyText.Visibility = Visibility.Collapsed;
        ServerList.Visibility = Visibility.Visible;

        if (_selectedServer >= servers.Count)
            _selectedServer = VpnServerManager.Instance.DefaultServerIndex(servers.ToList());

        for (var i = 0; i < servers.Count; i++)
        {
            var s = servers[i];
            _serverRows.Add(new VpnServerRowViewModel
            {
                FlagImage = CountryFlagHelper.ResolveFlagImage(s.Id, s.Name),
                Country = CountryFlagHelper.ResolveCountryName(s.Name),
                Protocol = VpnProtocolHelper.Format(s),
                Server = s,
                PingMs = VpnPingService.Cached(s.Id),
            });
        }

        ServerList.SelectedIndex = _selectedServer;
        if (ServerList.SelectedIndex >= 0)
            ServerList.ScrollIntoView(ServerList.SelectedItem);

        // Пинг сразу при запуске — как на Android и iPhone: цифры видны без нажатий.
        // Повторно не меряем: при возврате на вкладку показываем прошлые цифры.
        if (!_autoPinged && !VpnPingService.HasResults)
        {
            _autoPinged = true;
            Dispatcher.BeginInvoke(new Action(() => OnPingServers(this, new RoutedEventArgs())));
        }
    }

    private static bool _autoPinged;

    private async void OnServerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedIndex < 0) return;

        var newIndex = ServerList.SelectedIndex;
        if (newIndex == _selectedServer) return;

        _selectedServer = newIndex;
        SettingsStore.Instance.SaveVpnServerIndex(_selectedServer);
        UpdateUi();

        var vpn = VpnManager.Instance;
        if (!vpn.IsRunning && !vpn.IsConnecting) return;

        var servers = VpnServerManager.Instance.Servers;
        if (_selectedServer >= servers.Count) return;

        await vpn.SwitchServerAsync(servers[_selectedServer]);

        if (!vpn.IsRunning && !string.IsNullOrEmpty(vpn.LastError))
        {
            MessageBox.Show(vpn.LastError, "ByPassMe — VPN",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveServerIndex()
    {
        if (_selectedServer >= 0)
            SettingsStore.Instance.SaveVpnServerIndex(_selectedServer);
    }

    /// <summary>Сервер под кругом: флаг и имя (как на телефонах).</summary>
    private void ShowCurrentServer(bool visible)
    {
        var servers = VpnServerManager.Instance.Servers;
        if (!visible || _selectedServer < 0 || _selectedServer >= servers.Count)
        {
            OrbServerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var s = servers[_selectedServer];
        OrbServerName.Text = CountryFlagHelper.ResolveCountryName(s.Name);
        OrbServerFlag.Source = CountryFlagHelper.ResolveFlagImage(s.Id, s.Name);
        OrbServerPanel.Visibility = Visibility.Visible;
    }

    private async void OnPingServer(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VpnServerRowViewModel row }) return;
        if (row.PingText == "…") return;

        row.MarkPinging();
        try
        {
            var ms = await VpnPingService.PingAsync(row.Server.Address, row.Server.Port, row.Server.Network);
            VpnPingService.Remember(row.Server.Id, ms);
            row.PingMs = ms;
        }
        catch (OperationCanceledException)
        {
            row.ClearPing();
        }
        catch
        {
            VpnPingService.Remember(row.Server.Id, -1);
            row.PingMs = -1;
        }
    }

    private async void OnPingServers(object sender, RoutedEventArgs e)
    {
        if (_pingRunning || _serverRows.Count == 0)
            return;

        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;

        _pingRunning = true;
        PingBtn.IsEnabled = false;
        foreach (var row in _serverRows)
            row.MarkPinging();

        try
        {
            using var gate = new SemaphoreSlim(6);
            var tasks = _serverRows.Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var ms = await VpnPingService.PingAsync(
                        row.Server.Address, row.Server.Port, row.Server.Network, ct);
                    VpnPingService.Remember(row.Server.Id, ms);
                    await Dispatcher.InvokeAsync(() => row.PingMs = ms);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            foreach (var row in _serverRows.Where(r => r.PingMs == -2))
                row.ClearPing();
        }
        finally
        {
            _pingRunning = false;
            UpdateUi();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await RefreshServersAsync(silent: false);

    private async Task RefreshServersAsync(bool silent)
    {
        RefreshBtn.IsEnabled = false;
        var result = await VpnServerManager.Instance.FetchServersAsync();
        RefreshBtn.IsEnabled = true;
        UpdateUi();

        if (!silent)
            MessageBox.Show(MessageForFetch(result), "ByPassMe", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string MessageForFetch(FetchResult result) => result switch
    {
        FetchResult.Success => "Список VPN-серверов обновлён",
        FetchResult.MissingHubToken => "Не удалось обновить список серверов — обновите приложение",
        FetchResult.NoAccess => "Не удалось обновить список серверов — обновите приложение",
        FetchResult.NotFound => "Список серверов не найден",
        FetchResult.NoSubscription => "Сначала введите ссылку подписки",
        _ => "Не удалось загрузить список · используется кэш"
    };

    private void OnShowSubscription(object sender, RoutedEventArgs e)
    {
        var dlg = new SubscriptionDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private async void OnToggleConnect(object sender, RoutedEventArgs e)
    {
        var vpn = VpnManager.Instance;
        if (vpn.IsRunning || vpn.IsConnecting)
        {
            vpn.Stop();
            return;
        }

        if (string.IsNullOrEmpty(SettingsStore.Instance.VpnUuid))
        {
            MessageBox.Show("Сначала введите ссылку подписки (🔑).", "ByPassMe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var servers = VpnServerManager.Instance.Servers;
        if (servers.Count == 0)
        {
            MessageBox.Show("Список серверов пуст. Нажмите ↻ для загрузки.", "ByPassMe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_selectedServer >= servers.Count) return;

        await vpn.StartAsync(servers[_selectedServer]);

        if (!vpn.IsRunning && !string.IsNullOrEmpty(vpn.LastError))
        {
            MessageBox.Show(vpn.LastError, "ByPassMe — VPN",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateUi()
    {
        if (!IsLoaded || ConnectBtn == null)
            return;

        var vpn = VpnManager.Instance;
        var isConnected = vpn.IsRunning && vpn.IsReady;

        if (isConnected)
        {
            OrbOuter.Fill = (Brush)FindResource("OrbOuterBrush")!;
            OrbMiddle.Fill = (Brush)FindResource("OrbMiddleBrush")!;
            OrbInner.Fill = (Brush)FindResource("PrimaryBrush")!;
            OrbText.Text = "ON";
            OrbText.Foreground = (Brush)FindResource("OnPrimaryBrush")!;
            ShowCurrentServer(true);
            ConnectBtn.Background = (Brush)FindResource("ErrorBrush")!;
            if (!ConnectionStatsService.Vpn.IsActive)
                ConnectionStatsService.Vpn.Start();
        }
        else if (vpn.IsRunning || vpn.IsConnecting)
        {
            OrbOuter.Fill = (Brush)FindResource("OrbOuterBrush")!;
            OrbMiddle.Fill = (Brush)FindResource("OrbMiddleBrush")!;
            OrbInner.Fill = (Brush)FindResource("PrimaryBrush")!;
            OrbText.Text = "…";
            OrbText.Foreground = (Brush)FindResource("OnPrimaryBrush")!;
            ShowCurrentServer(true);
            _dotsTimer.Start();
        }
        else
        {
            _dotsTimer.Stop();
            OrbOuter.Fill = (Brush)FindResource("OrbOuterBrush")!;
            OrbMiddle.Fill = (Brush)FindResource("OrbMiddleBrush")!;
            OrbInner.Fill = (Brush)FindResource("OrbOffBrush")!;
            OrbText.Text = "OFF";
            OrbText.Foreground = (Brush)FindResource("TextSecondaryBrush")!;
            ShowCurrentServer(false);
            ConnectBtn.Background = (Brush)FindResource("PrimaryBrush")!;
        }

        UpdateButton();
        UpdateKeyButton();

        var busy = vpn.IsRunning || vpn.IsConnecting;
        var canConnect = !string.IsNullOrEmpty(SettingsStore.Instance.VpnUuid)
            && VpnServerManager.Instance.Servers.Count > 0
            && vpn.CooldownSeconds == 0;
        ConnectBtn.IsEnabled = busy || canConnect;
        // Можно переключать сервер/протокол пока VPN подключён или уже переключается
        ServerList.IsEnabled = !busy || vpn.IsRunning;
        RefreshBtn.IsEnabled = !busy || (vpn.IsRunning && vpn.IsReady);
        PingBtn.IsEnabled = (!busy || (vpn.IsRunning && vpn.IsReady))
            && !_pingRunning && VpnServerManager.Instance.Servers.Count > 0;
    }

    private void UpdateButton()
    {
        if (ConnectBtn == null)
            return;

        var vpn = VpnManager.Instance;
        var dots = new[] { "", ".", "..", "..." }[_dotFrame % 4];

        ConnectBtn.Content = vpn.IsRunning && vpn.IsReady ? "Остановить"
            : vpn.IsRunning || vpn.IsConnecting ? $"Подключение{dots}"
            : vpn.CooldownSeconds > 0 ? $"Подождите ({vpn.CooldownSeconds})"
            : "Подключить";
    }

    private void UpdateKeyButton()
    {
        KeyBtn.Foreground = SettingsStore.Instance.HasActiveSession
            ? (Brush)FindResource("PrimaryBrush")!
            : (Brush)FindResource("ErrorBrush")!;
    }

    private void UpdateServerCardClip()
    {
        if (ServerCardContent == null)
            return;
        if (ServerCardContent.ActualWidth <= 0 || ServerCardContent.ActualHeight <= 0)
            return;

        ServerCardContent.Clip = new RectangleGeometry(
            new Rect(0, 0, ServerCardContent.ActualWidth, ServerCardContent.ActualHeight), 28, 28);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var nested = FindScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
