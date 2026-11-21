using Avalonia;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Reports;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;

namespace DiskDoktor.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--tara") || args.Contains("--scan"))
            return DryRun();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Read-only scan printed to the console; never deletes anything.</summary>
    private static int DryRun()
    {
        var env = SystemEnvironment.Instance;
        var rules = RuleLoader.LoadAll(env, out var warning);
        if (warning is not null) Console.Error.WriteLine(warning);
        foreach (var d in new DriveService(env).GetDrives())
            Console.WriteLine($"[disk] {d.Label} ({d.Name}): {SizeFormatter.Format(d.FreeBytes)} boş / {SizeFormatter.Format(d.TotalBytes)}");
        var result = new Scanner(env).Scan(rules, null, CancellationToken.None);
        foreach (var f in result.Findings.OrderByDescending(f => f.TotalSize))
            Console.WriteLine($"[{RiskText.Of(f.Rule.Risk),-7}] {SizeFormatter.Format(f.TotalSize),9}  {f.FileCount,7} dosya  {f.Rule.Name}  ({f.Root})");
        Console.WriteLine($"Toplam: {SizeFormatter.Format(result.TotalSize)} · {rules.Count} kural · {result.Duration.TotalSeconds:0.0} sn · hiçbir şey silinmedi.");
        return 0;
    }
}
