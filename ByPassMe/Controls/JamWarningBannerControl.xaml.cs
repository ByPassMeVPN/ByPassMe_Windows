using System.Windows;
using System.Windows.Controls;
using ByPassMe.Services;

namespace ByPassMe.Controls;

public partial class JamWarningBannerControl : UserControl
{
    public static readonly DependencyProperty HasBypassAccessProperty =
        DependencyProperty.Register(
            nameof(HasBypassAccess),
            typeof(bool),
            typeof(JamWarningBannerControl),
            new PropertyMetadata(true, OnHasBypassAccessChanged));

    public bool HasBypassAccess
    {
        get => (bool)GetValue(HasBypassAccessProperty);
        set => SetValue(HasBypassAccessProperty, value);
    }

    private static void OnHasBypassAccessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JamWarningBannerControl c)
            c.Update();
    }

    public JamWarningBannerControl()
    {
        InitializeComponent();
        // Подписка на весь срок жизни контрола — не отписываемся на Unloaded,
        // иначе при смене вкладок баннер может «залипнуть» Visible.
        JamDetector.Instance.Changed += OnJamChanged;
        SettingsStore.Instance.Changed += OnJamChanged;
        Loaded += (_, _) =>
        {
            Update();
            _ = JamDetector.Instance.CheckNowAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Update();
        };
    }

    private void OnJamChanged()
    {
        if (Dispatcher.CheckAccess())
            Update();
        else
            Dispatcher.BeginInvoke(Update);
    }

    public void Update()
    {
        var jammed = JamDetector.Instance.IsJammed;
        Visibility = jammed ? Visibility.Visible : Visibility.Collapsed;
        if (!jammed)
            return;

        var bypass = HasBypassAccess && SettingsStore.Instance.HasBypassAccess;
        HintText.Text = bypass
            ? "Возможно глушение интернета. Перейдите на вкладку «Обход»"
            : "Возможно глушение интернета. Обход включается в боте ByPassMe VPN";
    }
}
