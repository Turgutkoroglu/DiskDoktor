using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DiskDoktor.App.Services;
using DiskDoktor.App.ViewModels;
using DiskDoktor.App.Views;
using DiskDoktor.Core.Cleaning;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DiskDoktor.Tests.TestAppBuilder))]

namespace DiskDoktor.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DiskDoktor.App.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class FakeScanner(ScanResult result) : IScanner
{
    public Task<ScanResult> ScanAsync(IReadOnlyList<CleanupRule> rules, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.FromResult(result);
}

internal sealed class FakeCleaner : ICleaner
{
    public List<CleanRequest> Received { get; } = [];
    public DeleteMode? Mode { get; private set; }

    public Task<CleanResult> CleanAsync(IReadOnlyList<CleanRequest> requests, DeleteMode mode, IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        Received.AddRange(requests);
        Mode = mode;
        var items = requests.SelectMany(r => r.Items.Select(i =>
            new ItemResult(i.Path, r.Finding.Rule.Name, i.Size, i.Size, ItemOutcome.Deleted, null))).ToList();
        return Task.FromResult(new CleanResult { Items = items, Mode = mode, StartedUtc = DateTime.UtcNow, Duration = TimeSpan.FromSeconds(1) });
    }
}

internal sealed class FakeDrives : IDriveService
{
    public IReadOnlyList<DriveSnapshot> GetDrives() =>
    [
        new("/", "Sistem", 512L << 30, 61L << 30),
        new("/media/veri", "Veri", 1024L << 30, 700L << 30),
    ];
}

internal sealed class FakeShell : IShell
{
    public string MachineName => "test";
    public string? Saved { get; private set; }
    public Task<string?> SaveTextAsync(string n, string e, string d, string content) { Saved = content; return Task.FromResult<string?>("/x." + e); }
    public void OpenFolder(string path) { }
    public void RestartElevated() { }
}

public class MainViewModelTests
{
    internal static ScanResult SampleScan()
    {
        Finding F(string id, string name, string cat, RiskLevel risk, long size, bool selected = true, bool admin = false, bool perm = false) => new()
        {
            Rule = new CleanupRule
            {
                Id = id, Name = name, Category = cat, Risk = risk, Paths = ["~"], DefaultSelected = selected,
                RequiresAdmin = admin, PermanentOnly = perm,
                Description = "Yeniden oluşturulabilen dosyalar; açık uygulamalar ilk açılışta yavaşlayabilir.",
            },
            Root = $"/home/turgut/.cache/{id}",
            Items = [new ScanItem($"/home/turgut/.cache/{id}/a", size, 120, DateTime.UtcNow, true)],
        };
        return new ScanResult
        {
            StartedUtc = DateTime.UtcNow, Duration = TimeSpan.FromSeconds(2),
            Findings =
            [
                F("chrome", "Google Chrome önbelleği", "Tarayıcılar", RiskLevel.Safe, 1_800_000_000),
                F("firefox", "Firefox önbelleği", "Tarayıcılar", RiskLevel.Safe, 420_000_000),
                F("thumbs", "Küçük resim önbelleği", "Sistem", RiskLevel.Safe, 96_000_000),
                F("wintemp", "Windows geçici dosyaları", "Sistem", RiskLevel.Safe, 310_000_000, admin: true),
                F("npm", "npm önbelleği", "Geliştirici", RiskLevel.Caution, 2_400_000_000, selected: false),
                F("trash", "Çöp kutusu", "Çöp kutusu", RiskLevel.Risky, 5_100_000_000, selected: false, perm: true),
            ],
        };
    }

    internal static (MainViewModel Vm, FakeCleaner Cleaner, FakeShell Shell) Make()
    {
        var cleaner = new FakeCleaner();
        var shell = new FakeShell();
        var vm = new MainViewModel(new FakeScanner(SampleScan()), cleaner, new FakeDrives(), shell, [], null, OsKind.Linux);
        return (vm, cleaner, shell);
    }

    [Fact]
    public async Task Scan_GroupsByCategory_AndAppliesDefaultSelection()
    {
        var (vm, _, _) = Make();
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(Screen.Results, vm.Screen);
        Assert.Equal(4, vm.Categories.Count);
        Assert.Equal(4, vm.SelectedCount);           // caution/risky are opt-in
        Assert.False(vm.HasRiskySelected);
        Assert.Equal(2, vm.Drives.Count);
    }

    [Fact]
    public async Task Filters_And_Search_NarrowTheList()
    {
        var (vm, _, _) = Make();
        await vm.ScanCommand.ExecuteAsync(null);
        vm.ShowSafe = false;
        Assert.Equal(new HashSet<string> { "Geliştirici", "Çöp kutusu" }, vm.Categories.Select(c => c.Name).ToHashSet());
        vm.ShowSafe = true;
        vm.SearchText = "firefox";
        Assert.Equal("Firefox önbelleği", vm.Categories.Single().Items.Single().Name);
    }

    [Fact]
    public async Task CleanFlow_SendsOnlySelected_InChosenMode_AndExports()
    {
        var (vm, cleaner, shell) = Make();
        await vm.ScanCommand.ExecuteAsync(null);
        vm.AllFindings.Single(f => f.Name == "Çöp kutusu").IsSelected = true;
        Assert.True(vm.HasRiskySelected);
        vm.UseTrash = false;

        vm.RequestCleanCommand.Execute(null);
        Assert.Equal(Screen.Confirm, vm.Screen);
        Assert.Equal(5, vm.ConfirmItems.Count);
        await vm.ConfirmCleanCommand.ExecuteAsync(null);

        Assert.Equal(Screen.Done, vm.Screen);
        Assert.Equal(DeleteMode.Permanent, cleaner.Mode);
        Assert.Equal(5, cleaner.Received.Count);
        Assert.DoesNotContain(cleaner.Received, r => r.Finding.Rule.Id == "npm");
        Assert.Single(vm.AllFindings);              // cleaned ones drop out of the list

        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Contains("Çöp kutusu", shell.Saved);
    }

    [Fact]
    public async Task CleanIsDisabled_WhenNothingSelected()
    {
        var (vm, _, _) = Make();
        await vm.ScanCommand.ExecuteAsync(null);
        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.RequestCleanCommand.CanExecute(null));
    }
}

public class MainWindowRenderTests
{
    private static string OutDir => Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "screenshots")).FullName;

    private static async Task<MainWindow> Show(ThemeVariant theme, int width, int height, Func<MainViewModel, Task>? setup = null)
    {
        Application.Current!.RequestedThemeVariant = theme;
        var (vm, _, _) = MainViewModelTests.Make();
        await vm.ScanCommand.ExecuteAsync(null);
        if (setup is not null) await setup(vm);
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return w;
    }

    [AvaloniaTheory]
    [InlineData("light", 1040, 720)]
    [InlineData("dark", 1040, 720)]
    [InlineData("narrow", 560, 640)]
    public async Task RendersResults(string name, int width, int height)
    {
        var w = await Show(name == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, width, height, vm =>
        {
            vm.AllFindings.First(f => f.IsSafe).IsExpanded = true;
            return Task.CompletedTask;
        });
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(OutDir, $"results-{name}.png"));
        w.Close();
    }

    [AvaloniaFact]
    public async Task RendersConfirmDialog()
    {
        var w = await Show(ThemeVariant.Light, 1040, 720, vm =>
        {
            vm.AllFindings.Single(f => f.Name == "Çöp kutusu").IsSelected = true;
            vm.RequestCleanCommand.Execute(null);
            return Task.CompletedTask;
        });
        w.CaptureRenderedFrame()!.Save(Path.Combine(OutDir, "confirm.png"));
        w.Close();
    }
}
