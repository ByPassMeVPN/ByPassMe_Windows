using System.Windows;
using System.Windows.Controls;
using ByPassMe.Models;
using ByPassMe.Services;

namespace ByPassMe.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
        Loaded += (_, _) => RestoreSavedState();
        IsVisibleChanged += (_, e) =>
        {
            if (IsVisible)
                RestoreSavedState();
        };
    }

    public void RestoreSavedState()
    {
        if (UrlInput == null)
            return;

        var savedUrl = SettingsStore.Instance.VpnSubscriptionUrl;
        if (!string.IsNullOrEmpty(savedUrl))
            UrlInput.Text = savedUrl;

        var reason = SettingsStore.Instance.VpnRevokeReason;
        if (string.IsNullOrEmpty(reason) || ContextMessage == null)
            return;

        ContextMessage.Text = reason switch
        {
            "expired" => "⏰ Подписка истекла\n\nПродлите подписку в Telegram боте ByPassMe и вставьте новую ссылку",
            "blocked" or "removed" => "Устройство отключено в боте.\n\nСсылка уже на месте — нажмите «Подключить» для переподключения.",
            _ => ContextMessage.Text
        };
        if (reason == "expired")
            ContextMessage.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush")!;
        SettingsStore.Instance.ClearRevokeReason();
    }

    public void FlushPendingDraft()
    {
        if (UrlInput == null)
            return;

        var url = UrlInput.Text.Trim();
        if (!string.IsNullOrEmpty(url))
            SettingsStore.Instance.SaveSubscriptionUrlDraft(url);
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        var url = UrlInput.Text.Trim();
        if (!string.IsNullOrEmpty(url))
            SettingsStore.Instance.SaveSubscriptionUrlDraft(url);
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var url = UrlInput.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;

        SettingsStore.Instance.SaveSubscriptionUrlDraft(url);

        ConnectBtn.IsEnabled = false;
        ConnectBtn.Content = "Получение данных...";
        ErrorText.Visibility = Visibility.Collapsed;

        var (result, error) = await SubscriptionChecker.Instance.FetchAsync(url, reconnect: true);

        ConnectBtn.IsEnabled = true;
        ConnectBtn.Content = "Подключить";

        switch (result)
        {
            case SubscriptionResult.Success:
                await BypassServerManager.Instance.FetchServersAsync();
                break;
            case SubscriptionResult.DeviceLimitExceeded:
                MessageBox.Show(
                    "Достигнут лимит устройств для вашей подписки.\n\n" +
                    "Чтобы добавить это устройство, удалите одно из существующих через Telegram бот ByPassMe.",
                    "Лимит устройств", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case SubscriptionResult.DeviceBlocked:
                ShowError("Устройство заблокировано. Обратитесь в поддержку.");
                break;
            case SubscriptionResult.DeviceRemoved:
                ShowError("Не удалось переподключить. Проверьте лимит устройств в боте.");
                break;
            case SubscriptionResult.Error:
                if (TryContinueWithOfflineCache(url))
                    break;
                ShowError(error ?? "Ошибка");
                break;
        }
    }

    private bool TryContinueWithOfflineCache(string url)
    {
        var store = SettingsStore.Instance;
        if (!store.HasOfflineBypassCache)
            return false;

        var urlKey = SubscriptionChecker.ExtractSubKey(url);
        if (!string.IsNullOrEmpty(store.VpnSubKey)
            && !string.IsNullOrEmpty(urlKey)
            && urlKey != store.VpnSubKey)
            return false;

        SubscriptionChecker.Instance.LoadCached();
        SubscriptionChecker.Instance.EnterOfflineCacheMode();
        BypassServerManager.Instance.LoadCached();
        AppLogger.Instance.Service("Онбординг: API недоступен — вход по сохранённому кэшу");
        return true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
