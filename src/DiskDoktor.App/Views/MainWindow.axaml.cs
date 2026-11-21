using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace DiskDoktor.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeLabel();
    }

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app is null) return;
        var current = app.RequestedThemeVariant;
        app.RequestedThemeVariant =
            current == ThemeVariant.Default ? ThemeVariant.Light :
            current == ThemeVariant.Light ? ThemeVariant.Dark : ThemeVariant.Default;
        UpdateThemeLabel();
    }

    private void UpdateThemeLabel()
    {
        var v = Application.Current?.RequestedThemeVariant;
        ThemeButton.Content = v == ThemeVariant.Light ? "Tema: Açık" : v == ThemeVariant.Dark ? "Tema: Koyu" : "Tema: Sistem";
    }
}
