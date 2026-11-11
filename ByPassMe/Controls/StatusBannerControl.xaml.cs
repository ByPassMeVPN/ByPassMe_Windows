using System.Windows;
using System.Windows.Controls;
using ByPassMe.Helpers;
using ByPassMe.Services;

namespace ByPassMe.Controls;

public partial class StatusBannerControl : UserControl
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(string), typeof(StatusBannerControl),
            new PropertyMetadata("unknown", OnStatusChanged));

    public static readonly DependencyProperty DaysLeftProperty =
        DependencyProperty.Register(nameof(DaysLeft), typeof(int), typeof(StatusBannerControl),
            new PropertyMetadata(0, OnStatusChanged));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public int DaysLeft
    {
        get => (int)GetValue(DaysLeftProperty);
        set => SetValue(DaysLeftProperty, value);
    }

    public StatusBannerControl()
    {
        InitializeComponent();
        UpdateBanner();
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatusBannerControl c) c.UpdateBanner();
    }

    private void UpdateBanner()
    {
        switch (Status)
        {
            case "active" when DaysLeft > 5:
                SetBanner("✓", $"Подписка активна · {DaysLeft} {VkUrlHelper.DayWord(DaysLeft)}",
                    "#4CAF50", "#1A4CAF50", "#594CAF50");
                break;
            case "active" when DaysLeft >= 1:
                SetBanner("⏰", $"Осталось {DaysLeft} {VkUrlHelper.DayWord(DaysLeft)} · Продлите подписку",
                    "#FFB300", "#1AFFB300", "#66FFB300");
                break;
            case "active":
                SetBanner("⏰", "Подписка истекает сегодня · Продлите",
                    "#FFB300", "#1AFFB300", "#66FFB300");
                break;
            case "expired":
                SetBanner("⛔", "Подписка истекла · Продлите в боте",
                    "#E53935", "#1AE53935", "#66E53935");
                break;
            default:
                SetBanner("☁", "Нет связи с сервером · Используется кэш",
                    "#6B7280", "#F0F0F0", "#D0D5DD");
                break;
        }
    }

    private void SetBanner(string icon, string text, string fg, string bg, string border)
    {
        IconText.Text = icon;
        MessageText.Text = text;
        MessageText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(fg)!;
        BannerBorder.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(bg)!;
        BannerBorder.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(border)!;
    }
}
