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
        int h = Array.IndexOf(args, DiskDoktor.Core.Cleaning.ElevatorFactory.HelperFlag);
        if (h >= 0 && args.Length > h + 2)
            return DiskDoktor.Core.Cleaning.AdminHelper.Run(SystemEnvironment.Instance, args[h + 1], args[h + 2]);
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
        var drives = new DriveService(env);
        foreach (var d in drives.GetDrives())
            Console.WriteLine($"[disk] {d.Label} ({d.Name}): {SizeFormatter.Format(d.FreeBytes)} boş / {SizeFormatter.Format(d.TotalBytes)}");
        var result = new Scanner(env, discovery: new DiscoveryScanner(env, drives)).Scan(rules, null, CancellationToken.None);
        foreach (var f in result.Findings.OrderByDescending(f => f.TotalSize))
        {
            string size = !f.SizeKnown ? "?" : (f.AccessDenied ? "≥" : "") + SizeFormatter.Format(f.TotalSize);
            string where = f.IsCommand ? "komut: " + f.Rule.CommandText : f.Root;
            string admin = f.Rule.RequiresAdmin ? " [yönetici]" : "";
            Console.WriteLine($"[{RiskText.Of(f.Rule.Risk),-7}] {size,10}  {f.Items.Count,6} öğe  {f.Rule.Name}{admin}  ({where})");
        }
        Console.WriteLine($"Toplam: {SizeFormatter.Format(result.TotalSize)} · {rules.Count} kural · {result.Duration.TotalSeconds:0.0} sn · hiçbir şey silinmedi.");
        return 0;
    }
}
