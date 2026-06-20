using System.Windows;
using ByPassMe.Models;
using ByPassMe.Services;

namespace ByPassMe.Views;

public partial class SubscriptionDialog : Window
{
    public SubscriptionDialog()
    {
        InitializeComponent();
        UrlInput.Text = SettingsStore.Instance.VpnSubscriptionUrl;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var url = UrlInput.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;

        ApplyBtn.IsEnabled = false;
        ApplyBtn.Content = "Получение данных...";
        ErrorText.Visibility = Visibility.Collapsed;

        var (result, error) = await SubscriptionChecker.Instance.FetchAsync(url);

        ApplyBtn.IsEnabled = true;
        ApplyBtn.Content = "Применить";

        switch (result)
        {
            case SubscriptionResult.Success:
                await BypassServerManager.Instance.FetchServersAsync();
                DialogResult = true;
                Close();
                break;
            case SubscriptionResult.DeviceLimitExceeded:
                MessageBox.Show(
                    "Достигнут лимит устройств для вашей подписки.\n\n" +
                    "Удалите одно из существующих устройств через Telegram бот ByPassMe.",
                    "Лимит устройств", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                break;
            case SubscriptionResult.DeviceBlocked:
            case SubscriptionResult.DeviceRemoved:
                Close();
                break;
            case SubscriptionResult.Error:
                ShowError(error ?? "Ошибка");
                break;
        }
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
