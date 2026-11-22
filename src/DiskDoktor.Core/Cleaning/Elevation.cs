using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;

namespace DiskDoktor.Core.Cleaning;

/// <summary>What the normal process asks the elevated helper to do. Only rule ids and paths: never commands.</summary>
public sealed record AdminTask(string RuleId, string Root, List<string> Paths);
public sealed record AdminRequest(List<AdminTask> Tasks);

public interface IElevator
{
    bool IsAvailable { get; }
    /// <summary>Runs "DiskDoktor --yonetici-yardimcisi req res" with admin rights and waits. Returns an error or null.</summary>
    Task<string?> RunHelperAsync(string requestFile, string resultFile, CancellationToken ct);
}

public static class ElevatorFactory
{
    public const string HelperFlag = "--yonetici-yardimcisi";

    public static IElevator Create(ISystemEnvironment env) => env.Os switch
    {
        OsKind.Windows => new WindowsElevator(),
        OsKind.MacOS => new MacElevator(env),
        _ => new PkexecElevator(env),
    };

    /// <summary>The command that starts this same program (single-file exe, or "dotnet DiskDoktor.dll" in development).</summary>
    public static List<string> SelfCommand()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Program yolu bulunamadı.");
        var list = new List<string> { exe };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(dll)) list.Add(dll);
        }
        return list;
    }
}

internal static class ProcessHelper
{
    public static async Task<(int Code, string Output)> RunAsync(IReadOnlyList<string> argv, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{argv[0]} başlatılamadı.");
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var text = (await outTask + "\n" + await errTask).Trim();
        return (p.ExitCode, text.Length > 600 ? "…" + text[^600..] : text);
    }
}

public sealed class WindowsElevator : IElevator
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public async Task<string?> RunHelperAsync(string requestFile, string resultFile, CancellationToken ct)
    {
        var self = ElevatorFactory.SelfCommand();
        var args = self.Skip(1).Concat([ElevatorFactory.HelperFlag, requestFile, resultFile]).Select(Quote);
        var psi = new ProcessStartInfo(self[0], string.Join(' ', args)) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "Yönetici yardımcısı başlatılamadı.";
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? null : $"Yönetici yardımcısı hata verdi ({p.ExitCode}).";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Yönetici izni verilmedi.";
        }
    }

    private static string Quote(string a) => "\"" + a.Replace("\"", "\\\"") + "\"";
}

public sealed class PkexecElevator(ISystemEnvironment env) : IElevator
{
    private readonly string? _pkexec = env.FindProgram("pkexec");
    public bool IsAvailable => _pkexec is not null;

    public async Task<string?> RunHelperAsync(string requestFile, string resultFile, CancellationToken ct)
    {
        if (_pkexec is null) return "pkexec bulunamadı (polkit kurulu değil).";
        var argv = new List<string> { _pkexec };
        argv.AddRange(ElevatorFactory.SelfCommand());
        argv.AddRange([ElevatorFactory.HelperFlag, requestFile, resultFile]);
        var (code, output) = await ProcessHelper.RunAsync(argv, ct);
        return code switch
        {
            0 => null,
            126 or 127 => "Yönetici izni verilmedi.",
            _ => $"Yönetici yardımcısı hata verdi ({code}): {output}",
        };
    }
}

public sealed class MacElevator(ISystemEnvironment env) : IElevator
{
    public bool IsAvailable => env.FindProgram("osascript") is not null;

    public async Task<string?> RunHelperAsync(string requestFile, string resultFile, CancellationToken ct)
    {
        var parts = ElevatorFactory.SelfCommand().Concat([ElevatorFactory.HelperFlag, requestFile, resultFile]);
        string shell = string.Join(' ', parts.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
        string script = "do shell script \"" + shell.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" with administrator privileges";
        var (code, output) = await ProcessHelper.RunAsync(["osascript", "-e", script], ct);
        if (code == 0) return null;
        return output.Contains("-128") ? "Yönetici izni verilmedi." : $"Yönetici yardımcısı hata verdi: {output}";
    }
}

/// <summary>
/// Runs inside the elevated process. Trusts nothing from the request file: each task must name a built-in admin rule,
/// the root must be one that rule resolves to, and every path still goes through PathGuard.
/// </summary>
public static class AdminHelper
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(ISystemEnvironment env, string requestFile, string resultFile)
    {
        CleanResult result;
        try
        {
            var req = JsonSerializer.Deserialize<AdminRequest>(File.ReadAllText(requestFile)) ?? new AdminRequest([]);
            var requests = BuildRequests(env, req, out var rejected);
            var deleter = new SafeDeleter(env, new NoTrash(), AppPaths.LogDirectory(env));
            var r = deleter.Clean(requests, DeleteMode.Permanent, null, CancellationToken.None);
            result = new CleanResult
            {
                Items = [.. rejected, .. r.Items], Mode = DeleteMode.Permanent,
                StartedUtc = r.StartedUtc, Duration = r.Duration, LogFile = r.LogFile,
            };
        }
        catch (Exception ex)
        {
            result = new CleanResult
            {
                Items = [new ItemResult(requestFile, "Yönetici yardımcısı", 0, 0, ItemOutcome.Failed, ex.Message)],
                Mode = DeleteMode.Permanent, StartedUtc = DateTime.UtcNow, Duration = TimeSpan.Zero,
            };
        }
        // The result file was created by the normal user; writing into it keeps it user-owned.
        using (var fs = new FileStream(resultFile, FileMode.OpenOrCreate, FileAccess.Write))
        {
            fs.SetLength(0);
            JsonSerializer.Serialize(fs, ResultDto.From(result), Json);
        }
        return 0;
    }

    internal static List<CleanRequest> BuildRequests(ISystemEnvironment env, AdminRequest req, out List<ItemResult> rejected)
    {
        rejected = [];
        var rules = RuleLoader.LoadBuiltIn().Where(r => r.AppliesTo(env.Os) && r.RequiresAdmin).ToDictionary(r => r.Id);
        var resolver = new PathResolver(env);
        var cmp = env.PathsAreCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var list = new List<CleanRequest>();

        foreach (var t in req.Tasks)
        {
            if (!rules.TryGetValue(t.RuleId, out var rule))
            {
                rejected.Add(new ItemResult(t.Root, t.RuleId, 0, 0, ItemOutcome.Skipped, "Yerleşik yönetici kuralı değil."));
                continue;
            }
            if (rule.Kind == TargetKind.Command)
            {
                list.Add(new CleanRequest(new Finding { Rule = rule, Root = t.Root, Items = [] }, []));
                continue;
            }
            var roots = rule.Paths.SelectMany(resolver.Resolve).Select(Path.GetFullPath).ToHashSet(cmp);
            if (!roots.Contains(Path.GetFullPath(t.Root)))
            {
                rejected.Add(new ItemResult(t.Root, rule.Name, 0, 0, ItemOutcome.Skipped, "Klasör bu kurala ait değil."));
                continue;
            }
            // Re-list as root and keep only requested items that still qualify (age, pattern, excludes).
            var (fresh, _) = new Scanner(env).ScanRoot(rule, t.Root, [], CancellationToken.None);
            var byPath = fresh.ToDictionary(i => i.Path, cmp);
            var items = t.Paths.Count == 0 ? fresh : t.Paths.Where(byPath.ContainsKey).Select(p => byPath[p]).ToList();
            foreach (var p in t.Paths.Where(p => !byPath.ContainsKey(p)))
                rejected.Add(new ItemResult(p, rule.Name, 0, 0, ItemOutcome.Skipped, "Artık kurala uymuyor veya yok."));
            list.Add(new CleanRequest(new Finding { Rule = rule, Root = t.Root, Items = items }, items));
        }
        return list;
    }

    private sealed class NoTrash : ITrash
    {
        public bool IsAvailable => false;
        public void MoveToTrash(string path) => throw new NotSupportedException();
    }
}

/// <summary>JSON shape of a CleanResult passed back from the helper.</summary>
public sealed class ResultDto
{
    public List<ItemResult> Items { get; set; } = [];
    public string? LogFile { get; set; }

    public static ResultDto From(CleanResult r) => new() { Items = r.Items.ToList(), LogFile = r.LogFile };
}
