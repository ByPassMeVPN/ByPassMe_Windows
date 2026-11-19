using System.Windows;
using System.Windows.Controls;
using ByPassMe.Services;

namespace ByPassMe.Controls;

public partial class ConnectionStatsControl : UserControl
{
    private ConnectionStatsService? _service;

    public ConnectionStatsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Refresh();
        };
    }

    public void Bind(ConnectionStatsService service)
    {
        if (_service != null)
            _service.Changed -= OnStatsChanged;

        _service = service;
        _service.Changed += OnStatsChanged;
        Refresh();
    }

    private void OnStatsChanged()
    {
        if (Dispatcher.CheckAccess())
            Refresh();
        else
            Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        if (_service == null || !_service.IsActive)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        DurationText.Text = _service.DurationText;
    }
}
