using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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

        FontSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                double v = System.Math.Round(FontSizeSlider.Value);
                FontSizeLabel.Text = $"{v:0}";
                FontSizeChanged?.Invoke(v);
            }
        };
        FullscreenSwitch.IsCheckedChanged += (_, _) =>
        {
            Settings.Data.DefaultFullscreen = FullscreenSwitch.IsChecked == true;
            Settings.Save();
        };

        MarkSelected(Settings.Data.Theme);
    }

    private void OnThemeCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: string id })
        {
            ThemeChanged?.Invoke(id);
            MarkSelected(id);
        }
    }

    private void MarkSelected(string id)
    {
        CardGreen.Classes.Set("sel", id == "Green");
        CardPaper.Classes.Set("sel", id == "Paper");
        CardWhite.Classes.Set("sel", id == "White");
    }
}
