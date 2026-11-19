using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ByPassMe.Converters;

/// <summary>Цвет задержки пинга — как v2rayN DelayColorConverter.</summary>
public sealed class PingDelayColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Как на Android (PingChip): до 200 мс — зелёный, до 500 — жёлтый,
        // дальше и при таймауте — красный. Пока не мерили — серый.
        if (value is not int ms)
            return new SolidColorBrush(Color.FromRgb(0x6B, 0x73, 0x85));

        var color = ms switch
        {
            -2 => Color.FromRgb(0x6B, 0x73, 0x85),
            < 0 => Colors.IndianRed,
            < 200 => Color.FromRgb(0x2E, 0x9E, 0x4F),
            < 500 => Color.FromRgb(0xE0, 0xA1, 0x00),
            _ => Colors.IndianRed,
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
