using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByPassMe.Services;

namespace ByPassMe.Controls;

public partial class FloatingToolbarControl : UserControl
{
    private bool _expanded;

    public FloatingToolbarControl()
    {
        InitializeComponent();
        ThemeManager.Instance.Changed += () => Dispatcher.Invoke(UpdateSelection);
        Loaded += (_, _) => UpdateSelection();
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        _expanded = !_expanded;
        ThemePanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSystem(object sender, RoutedEventArgs e) { ThemeManager.Instance.SetThemeMode("system"); Collapse(); }
    private void OnLight(object sender, RoutedEventArgs e) { ThemeManager.Instance.SetThemeMode("light"); Collapse(); }
    private void OnDark(object sender, RoutedEventArgs e) { ThemeManager.Instance.SetThemeMode("dark"); Collapse(); }
    private void OnIndigo(object sender, MouseButtonEventArgs e) { ThemeManager.Instance.SetPalette("indigo"); UpdateSelection(); }
    private void OnForest(object sender, MouseButtonEventArgs e) { ThemeManager.Instance.SetPalette("forest"); UpdateSelection(); }
    private void OnEspresso(object sender, MouseButtonEventArgs e) { ThemeManager.Instance.SetPalette("espresso"); UpdateSelection(); }

    private void Collapse()
    {
        _expanded = false;
        ThemePanel.Visibility = Visibility.Collapsed;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var tm = ThemeManager.Instance;
        HighlightBtn(BtnSystem, tm.ThemeMode == "system");
        HighlightBtn(BtnLight, tm.ThemeMode == "light");
        HighlightBtn(BtnDark, tm.ThemeMode == "dark");

        SetPalRing(PalIndigo, tm.Palette == "indigo");
        SetPalRing(PalForest, tm.Palette == "forest");
        SetPalRing(PalEspresso, tm.Palette == "espresso");
    }

    private static void HighlightBtn(Button btn, bool selected)
    {
        btn.Background = selected
            ? (Brush)Application.Current.FindResource("PrimaryContainerBrush")!
            : Brushes.Transparent;
        btn.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
    }

    private static void SetPalRing(System.Windows.Shapes.Ellipse el, bool selected)
    {
        el.Stroke = selected
            ? (Brush)Application.Current.FindResource("PrimaryBrush")!
            : Brushes.Transparent;
        el.StrokeThickness = selected ? 3 : 0;
    }
}
