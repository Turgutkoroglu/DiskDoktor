using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiskDoktor.App.Services;
using DiskDoktor.App.ViewModels;
using DiskDoktor.App.Views;
using DiskDoktor.Core.Cleaning;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;

namespace DiskDoktor.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = CreateViewModel(() => TopLevel.GetTopLevel(window));
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static MainViewModel CreateViewModel(Func<TopLevel?> topLevel)
    {
        var env = SystemEnvironment.Instance;
        IReadOnlyList<CleanupRule> rules;
        string? warning;
        try
        {
            rules = RuleLoader.LoadAll(env, out warning);
        }
        catch (RuleValidationException ex)
        {
            rules = [];
            warning = "Kurallar yüklenemedi: " + ex.Message;
        }
        return new MainViewModel(new Scanner(env), new SafeDeleter(env), new DriveService(env),
            new AvaloniaShell(topLevel, env), rules, warning, env.Os);
    }
}
