using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JsonFormatter;

public partial class SettingsWindow : Window
{
    public event Action<string>? ThemeChanged;
    public event Action<double>? FontSizeChanged;

    public SettingsWindow()
    {
        InitializeComponent();

        FontSizeSlider.Value = Settings.Data.FontSize;
        FontSizeLabel.Text = $"{Settings.Data.FontSize:0}";
        FullscreenSwitch.IsChecked = Settings.Data.DefaultFullscreen;

        MarkSelected(Settings.Data.Theme);
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double v = System.Math.Round(e.NewValue);
        FontSizeLabel.Text = $"{v:0}";
        if (System.Math.Abs(v - Settings.Data.FontSize) > 0.01)
            FontSizeChanged?.Invoke(v);
    }

    private void OnFullscreenChanged(object sender, RoutedEventArgs e)
    {
        Settings.Data.DefaultFullscreen = FullscreenSwitch.IsChecked == true;
        Settings.Save();
    }

    private void OnThemeCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ThemeChanged?.Invoke(id);
            MarkSelected(id);
        }
    }

    private void MarkSelected(string id)
    {
        Select(CardGreen, id == "Green");
        Select(CardPaper, id == "Paper");
        Select(CardWhite, id == "White");
    }

    private static void Select(Button card, bool selected)
    {
        card.BorderBrush = selected
            ? (Brush)Application.Current.TryFindResource("Accent")
            : (Brush)Application.Current.TryFindResource("LineStrong");
        card.Background = selected
            ? (Brush)Application.Current.TryFindResource("AccentSoft")
            : (Brush)Application.Current.TryFindResource("BgSurface");
        card.BorderThickness = new Thickness(selected ? 2 : 1.5);
    }
}
