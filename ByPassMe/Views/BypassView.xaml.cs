using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ByPassMe.Helpers;
using ByPassMe.Models;
using ByPassMe.Services;

namespace ByPassMe.Views;

public partial class BypassView : UserControl
{
    private readonly ObservableCollection<ServerRow> _serverRows = [];
    private readonly DispatcherTimer _dotsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private int _dotFrame;
    private bool _initialized;
    private int _selectedServer;

    public BypassView()
    {
        _dotsTimer.Tick += (_, _) => { _dotFrame++; UpdateButton(); };

        InitializeComponent();

        ServerCardContent.SizeChanged += (_, _) => UpdateServerCardClip();
        ServerList.ItemsSource = _serverRows;

        ThemeManager.Instance.Changed += () => Dispatcher.Invoke(UpdateUi);
        Loaded += OnLoaded;
        Unloaded += (_, _) => FlushPendingSave();

        SubscriptionChecker.Instance.Changed += OnSubscriptionChanged;
        BypassServerManager.Instance.Changed += OnServersChanged;
        BypassManager.Instance.Changed += OnBypassChanged;
        SettingsStore.Instance.Changed += OnSettingsChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BypassServerManager.Instance.LoadCached();

        var store = SettingsStore.Instance;
        if (!string.IsNullOrEmpty(store.VkCallUrl))
            VkInput.Text = store.VkCallUrl;
        else
        {
            var hashes = store.VkHashes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var firstHash = hashes.FirstOrDefault() ?? "";
            VkInput.Text = string.IsNullOrEmpty(firstHash)
                ? ""
                : $"https://vk.com/call/join/{firstHash}";
        }

        WorkersSlider.Value = VkUrlHelper.RoundToGroup(store.WorkersPerHash);
        WorkersLabel.Text = ((int)WorkersSlider.Value).ToString();
        _selectedServer = store.BypassServerIndex;

        if (!_initialized)
        {
            _initialized = true;

            if (!string.IsNullOrEmpty(store.VpnUuid) && BypassServerManager.Instance.Servers.Count == 0)
                await RefreshServersAsync();
            else if (store.HasOfflineBypassCache && BypassServerManager.Instance.Servers.Count == 0)
                BypassServerManager.Instance.LoadCached();
        }

        RefreshServers();
        StatusBanner.Status = SubscriptionChecker.Instance.Status;
        StatusBanner.DaysLeft = SubscriptionChecker.Instance.DaysLeft;
        UpdateUi();
    }

    private void OnSubscriptionChanged() => Dispatcher.Invoke(() =>
    {
        StatusBanner.Status = SubscriptionChecker.Instance.Status;
        StatusBanner.DaysLeft = SubscriptionChecker.Instance.DaysLeft;
        UpdateUi();
    });

    private void OnServersChanged() => Dispatcher.Invoke(() =>
    {
        RefreshServers();
        UpdateUi();
    });
    private void OnBypassChanged() => Dispatcher.Invoke(UpdateUi);
    private void OnSettingsChanged() => Dispatcher.Invoke(UpdateUi);

    private void RefreshServers()
    {
        var servers = BypassServerManager.Instance.Servers;
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
            _selectedServer = BypassServerManager.Instance.DefaultServerIndex(servers.ToList());

        for (var i = 0; i < servers.Count; i++)
        {
            var s = servers[i];
            _serverRows.Add(new ServerRow
            {
                FlagImage = CountryFlagHelper.ResolveFlagImage(s.Id, s.Name),
                Country = CountryFlagHelper.ResolveCountryName(s.Name)
            });
        }

        ServerList.SelectedIndex = _selectedServer;
        if (ServerList.SelectedIndex >= 0)
            ServerList.ScrollIntoView(ServerList.SelectedItem);
    }

    private void OnServerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedIndex < 0 || BypassManager.Instance.IsRunning) return;
        _selectedServer = ServerList.SelectedIndex;
        SettingsStore.Instance.SaveBypassServerIndex(_selectedServer);
        ScheduleSave();
        UpdateUi();
    }

    private void OnVkChanged(object sender, TextChangedEventArgs e)
    {
        var hash = VkUrlHelper.StripVkUrl(VkInput.Text);
        var valid = string.IsNullOrEmpty(VkInput.Text) || VkUrlHelper.IsValidVkHash(hash);
        VkError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        ScheduleSave();
        UpdateUi();
    }

    private void OnWorkersChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WorkersLabel != null)
            WorkersLabel.Text = ((int)e.NewValue).ToString();
        if (!IsLoaded)
            return;
        ScheduleSave();
        UpdateUi();
    }

    private DispatcherTimer? _saveTimer;
    private void ScheduleSave()
    {
        _saveTimer?.Stop();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            FlushPendingSave();
        };
        _saveTimer.Start();
    }

    public void FlushPendingSave()
    {
        _saveTimer?.Stop();

        if (VkInput == null || WorkersSlider == null)
            return;

        var vkText = VkInput.Text.Trim();
        if (!string.IsNullOrEmpty(vkText))
            SettingsStore.Instance.SaveVkCallUrl(vkText);

        var servers = BypassServerManager.Instance.Servers;
        var peer = _selectedServer < servers.Count ? servers[_selectedServer].Host : SettingsStore.Instance.Peer;
        var hash = VkUrlHelper.StripVkUrl(VkInput.Text);
        SettingsStore.Instance.SaveBypass(peer, hash, (int)WorkersSlider.Value);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshServersAsync();

    private async Task RefreshServersAsync()
    {
        RefreshBtn.IsEnabled = false;
        var result = await BypassServerManager.Instance.FetchServersAsync();
        RefreshBtn.IsEnabled = true;
        UpdateUi();
        MessageBox.Show(MessageForFetch(result), "ByPassMe", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string MessageForFetch(FetchResult result) => result switch
    {
        FetchResult.Success => "Список серверов обновлён",
        FetchResult.MissingHubToken => "Нет токена серверов — пересоберите установщик",
        FetchResult.NoAccess => "Нет доступа к обходу · проверьте подписку",
        FetchResult.NotFound => "Подписка не найдена",
        FetchResult.NoSubscription => "Сначала введите ссылку подписки",
        _ => "Не удалось загрузить · используется кэш"
    };

    private void OnShowSubscription(object sender, RoutedEventArgs e)
    {
        var dlg = new SubscriptionDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private async void OnToggleConnect(object sender, RoutedEventArgs e)
    {
        var bypass = BypassManager.Instance;
        if (bypass.IsRunning)
        {
            bypass.Stop();
            return;
        }

        var servers = BypassServerManager.Instance.Servers;
        if (servers.Count == 0)
        {
            MessageBox.Show("Список серверов пуст. Нажмите ↻ для загрузки.", "ByPassMe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_selectedServer >= servers.Count) return;

        var hash = VkUrlHelper.StripVkUrl(VkInput.Text);
        if (!VkUrlHelper.IsValidVkHash(hash))
        {
            VkError.Visibility = Visibility.Visible;
            MessageBox.Show("Вставьте полную ссылку VK звонка:\nhttps://vk.com/call/join/…", "ByPassMe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var server = servers[_selectedServer];
        var password = string.IsNullOrEmpty(SettingsStore.Instance.ConnectionPassword)
            ? "ByPassMe" : SettingsStore.Instance.ConnectionPassword;

        SettingsStore.Instance.SaveBypass(server.Host, hash, (int)WorkersSlider.Value);
        SettingsStore.Instance.SaveConnectionPassword(password);

        await bypass.StartAsync(server.Peer, hash, (int)WorkersSlider.Value);
    }

    private void UpdateUi()
    {
        if (!IsLoaded || ConnectBtn == null)
            return;

        var bypass = BypassManager.Instance;
        var isConnected = bypass.IsRunning && bypass.IsReady;

        if (isConnected)
        {
            OrbOuter.Fill = (Brush)FindResource("OrbOuterBrush")!;
            OrbMiddle.Fill = (Brush)FindResource("OrbMiddleBrush")!;
            OrbInner.Fill = (Brush)FindResource("PrimaryBrush")!;
            OrbText.Text = "ON";
            OrbText.Foreground = (Brush)FindResource("OnPrimaryBrush")!;
            ConnectBtn.Background = (Brush)FindResource("ErrorBrush")!;
        }
        else if (bypass.IsRunning)
        {
            OrbOuter.Fill = (Brush)FindResource("OrbOuterBrush")!;
            OrbMiddle.Fill = (Brush)FindResource("OrbMiddleBrush")!;
            OrbInner.Fill = (Brush)FindResource("PrimaryBrush")!;
            OrbText.Text = "…";
            OrbText.Foreground = (Brush)FindResource("OnPrimaryBrush")!;
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
            ConnectBtn.Background = (Brush)FindResource("PrimaryBrush")!;
        }

        UpdateButton();
        UpdateKeyButton();

        var canConnect = VkUrlHelper.IsValidVkHash(VkUrlHelper.StripVkUrl(VkInput.Text))
            && BypassServerManager.Instance.Servers.Count > 0
            && bypass.CooldownSeconds == 0;
        ConnectBtn.IsEnabled = bypass.IsRunning || canConnect;

        VkInput.IsEnabled = !bypass.IsRunning;
        WorkersSlider.IsEnabled = !bypass.IsRunning;
        ServerList.IsEnabled = !bypass.IsRunning;
    }

    private void UpdateButton()
    {
        if (ConnectBtn == null)
            return;

        var bypass = BypassManager.Instance;
        var dots = new[] { "", ".", "..", "..." }[_dotFrame % 4];

        ConnectBtn.Content = bypass.IsRunning && bypass.IsReady ? "Остановить"
            : bypass.IsRunning ? $"Подключение{dots}"
            : bypass.CooldownSeconds > 0 ? $"Подождите ({bypass.CooldownSeconds})"
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
        if (ServerCardContent.ActualWidth <= 0 || ServerCardContent.ActualHeight <= 0)
            return;

        ServerCardContent.Clip = new RectangleGeometry(
            new Rect(0, 0, ServerCardContent.ActualWidth, ServerCardContent.ActualHeight), 28, 28);
    }

    private sealed class ServerRow
    {
        public ImageSource FlagImage { get; init; } = null!;
        public string Country { get; init; } = "";
    }
}
