using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ByPassMe.Models;
using ByPassMe.Services;

namespace ByPassMe.Views;

public partial class LogsView : UserControl
{
    private readonly ObservableCollection<LogRow> _rows = [];
    private LogSource? _filter;
    private int _syncedCount;
    private bool _isActive;
    private bool _refreshScheduled;

    public LogsView()
    {
        InitializeComponent();
        LogList.ItemsSource = _rows;
        AppLogger.Instance.Changed += OnLogsChanged;
        Loaded += (_, _) => SyncLogs(force: true);
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active) ScheduleRefresh();
    }

    private void OnLogsChanged() => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (!_isActive || _refreshScheduled) return;
        _refreshScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshScheduled = false;
            if (!_isActive) return;
            SyncLogs();
        }, DispatcherPriority.Background);
    }

    private void SyncLogs(bool force = false)
    {
        var entries = GetFilteredEntries();

        if (force || entries.Count < _syncedCount)
        {
            _rows.Clear();
            foreach (var e in entries)
                _rows.Add(LogRow.From(e));
            _syncedCount = entries.Count;
        }
        else if (entries.Count > _syncedCount)
        {
            for (var i = _syncedCount; i < entries.Count; i++)
                _rows.Add(LogRow.From(entries[i]));
            _syncedCount = entries.Count;
        }

        var hasItems = _rows.Count > 0;
        EmptyText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        LogScroller.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

        if (hasItems) LogScroller.ScrollToEnd();
    }

    private List<AppLogEntry> GetFilteredEntries()
    {
        var entries = AppLogger.Instance.Entries;
        if (_filter.HasValue)
            return entries.Where(e => e.Source == _filter.Value).ToList();
        return entries.ToList();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = string.Join("\n", AppLogger.Instance.Entries.Select(x =>
            $"[{x.Timestamp}][{x.SourceLabel}] {x.Message}"));
        Clipboard.SetText(text);
        MessageBox.Show("Скопировано", "ByPassMe", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        AppLogger.Instance.Clear();
        _syncedCount = 0;
        _rows.Clear();
        EmptyText.Visibility = Visibility.Visible;
        LogScroller.Visibility = Visibility.Collapsed;
    }

    private void OnFilterAll(object sender, RoutedEventArgs e)
    {
        _filter = null;
        FilterAll.IsChecked = true;
        FilterVpn.IsChecked = FilterBypass.IsChecked = FilterService.IsChecked = false;
        SyncLogs(force: true);
    }

    private void OnFilterVpn(object sender, RoutedEventArgs e)
    {
        _filter = LogSource.Vpn;
        FilterVpn.IsChecked = true;
        FilterAll.IsChecked = FilterBypass.IsChecked = FilterService.IsChecked = false;
        SyncLogs(force: true);
    }

    private void OnFilterBypass(object sender, RoutedEventArgs e)
    {
        _filter = LogSource.Bypass;
        FilterBypass.IsChecked = true;
        FilterAll.IsChecked = FilterVpn.IsChecked = FilterService.IsChecked = false;
        SyncLogs(force: true);
    }

    private void OnFilterService(object sender, RoutedEventArgs e)
    {
        _filter = LogSource.Service;
        FilterService.IsChecked = true;
        FilterAll.IsChecked = FilterVpn.IsChecked = FilterBypass.IsChecked = false;
        SyncLogs(force: true);
    }

    private sealed class LogRow
    {
        public string Timestamp { get; init; } = "";
        public string SourceLabel { get; init; } = "";
        public string Message { get; init; } = "";
        public Brush BadgeBg { get; init; } = Brushes.Gray;
        public Brush BadgeFg { get; init; } = Brushes.White;
        public Brush MessageBrush { get; init; } = Brushes.White;
        public FontWeight MessageWeight { get; init; } = FontWeights.Normal;

        public static LogRow From(AppLogEntry e)
        {
            var (badgeBg, badgeFg) = e.Source switch
            {
                LogSource.Vpn => (C(0x1F, 0x6F, 0xEB, 0x40), C(0xCB, 0xE0, 0xFF)),
                LogSource.Bypass => (C(0x23, 0x86, 0x36, 0x40), C(0xAF, 0xF5, 0xB4)),
                LogSource.Service => (C(0x6E, 0x40, 0xC9, 0x40), C(0xD2, 0xA8, 0xFF)),
                _ => (C(0x5B, 0x5B, 0x5B, 0x40), C(0xCC, 0xCC, 0xCC))
            };

            var msgBrush = e.Level switch
            {
                LogLevel.Error => C(0xFF, 0x7B, 0x72),
                LogLevel.Warn => C(0xE3, 0xB3, 0x41),
                LogLevel.Debug => C(0x8B, 0x94, 0x9E),
                _ => C(0xE6, 0xED, 0xF3)
            };

            return new LogRow
            {
                Timestamp = e.Timestamp,
                SourceLabel = e.SourceLabel,
                Message = e.Message,
                BadgeBg = badgeBg,
                BadgeFg = badgeFg,
                MessageBrush = msgBrush,
                MessageWeight = e.Level == LogLevel.Error ? FontWeights.Bold : FontWeights.Normal
            };
        }

        private static SolidColorBrush C(byte r, byte g, byte b, byte a = 0xFF) =>
            new(Color.FromArgb(a, r, g, b));
    }
}
