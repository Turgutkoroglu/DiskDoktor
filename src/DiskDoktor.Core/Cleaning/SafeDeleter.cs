using System.Text.Json;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Scanning;

namespace DiskDoktor.Core.Cleaning;

public enum DeleteMode { Trash, Permanent }

public enum ItemOutcome { Deleted, Trashed, Partial, Skipped, Failed }

public sealed record ItemResult(string Path, string RuleName, long Size, long BytesFreed, ItemOutcome Outcome, string? Error);

public sealed record CleanProgress(int Done, int Total, long BytesFreed, string CurrentPath);

public sealed class CleanResult
{
    public required IReadOnlyList<ItemResult> Items { get; init; }
    public required DeleteMode Mode { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }
    public string? LogFile { get; init; }
    public long BytesFreed => Items.Sum(i => i.BytesFreed);
    public int SucceededCount => Items.Count(i => i.Outcome is ItemOutcome.Deleted or ItemOutcome.Trashed);
    public int FailedCount => Items.Count(i => i.Outcome is ItemOutcome.Failed or ItemOutcome.Partial or ItemOutcome.Skipped);
}

public sealed record CleanRequest(Finding Finding, IReadOnlyList<ScanItem> Items);

public interface ICleaner
{
    Task<CleanResult> CleanAsync(IReadOnlyList<CleanRequest> requests, DeleteMode mode,
        IProgress<CleanProgress>? progress, CancellationToken ct);
}

public sealed class SafeDeleter(ISystemEnvironment env, ITrash trash, string? logDirectory, IElevator? elevator = null) : ICleaner
{
    private readonly PathGuard _guard = new(env);
    private readonly Rules.PathResolver _resolver = new(env);

    public SafeDeleter(ISystemEnvironment env)
        : this(env, TrashFactory.Create(env), AppPaths.LogDirectory(env), ElevatorFactory.Create(env)) { }

    public Task<CleanResult> CleanAsync(IReadOnlyList<CleanRequest> requests, DeleteMode mode,
        IProgress<CleanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Clean(requests, mode, progress, ct), CancellationToken.None);

    public CleanResult Clean(IReadOnlyList<CleanRequest> requests, DeleteMode mode,
        IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<ItemResult>();
        long freed = 0;
        bool cancelled = false;

        // Admin work first, in one elevated run, so the password prompt comes once and early.
        var admin = requests.Where(r => r.Finding.Rule.RequiresAdmin && !env.IsElevated).ToList();
        var local = requests.Except(admin).ToList();
        var work = local.SelectMany(r => r.Finding.IsCommand
            ? [(r.Finding, Item: (ScanItem?)null)]
            : r.Items.Select(i => (r.Finding, Item: (ScanItem?)i))).ToList();
        int total = work.Count + (admin.Count > 0 ? 1 : 0), done = 0;

        if (admin.Count > 0)
        {
            progress?.Report(new CleanProgress(0, total, 0, "Yönetici izni bekleniyor…"));
            var adminResults = RunElevated(admin, ct);
            results.AddRange(adminResults);
            freed += adminResults.Sum(r => r.BytesFreed);
            done++;
        }

        foreach (var (finding, item) in work)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            progress?.Report(new CleanProgress(done, total, freed, item?.Path ?? finding.Rule.CommandText));
            ItemResult r;
            if (item is null) r = RunCommand(finding, ct);
            else
            {
                var effective = finding.Rule.PermanentOnly || !trash.IsAvailable ? DeleteMode.Permanent : mode;
                r = DeleteOne(finding, item, effective, ct);
            }
            freed += r.BytesFreed;
            results.Add(r);
            done++;
        }
        progress?.Report(new CleanProgress(done, total, freed, ""));

        var result = new CleanResult
        {
            Items = results, Mode = mode, StartedUtc = started, Duration = sw.Elapsed, Cancelled = cancelled,
        };
        var log = WriteLog(result);
        return log is null ? result : new CleanResult
        {
            Items = results, Mode = mode, StartedUtc = started, Duration = sw.Elapsed, Cancelled = cancelled, LogFile = log,
        };
    }

    private List<ItemResult> RunElevated(List<CleanRequest> admin, CancellationToken ct)
    {
        List<ItemResult> Fail(string why) => admin.SelectMany(r => r.Finding.IsCommand
                ? [new ItemResult(r.Finding.Rule.CommandText, r.Finding.Rule.Name, r.Finding.TotalSize, 0, ItemOutcome.Failed, why)]
                : r.Items.Select(i => new ItemResult(i.Path, r.Finding.Rule.Name, i.Size, 0, ItemOutcome.Failed, why))).ToList();

        if (elevator is null || !elevator.IsAvailable) return Fail("Bu sistemde yönetici izni istenemiyor.");

        string dir = Path.Combine(Path.GetTempPath(), "DiskDoktor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string req = Path.Combine(dir, "istek.json"), res = Path.Combine(dir, "sonuc.json");
            var payload = new AdminRequest(admin.Select(r => new AdminTask(r.Finding.Rule.Id, r.Finding.Root,
                r.Finding.IsCommand ? [] : r.Items.Select(i => i.Path).ToList())).ToList());
            File.WriteAllText(req, JsonSerializer.Serialize(payload));
            File.WriteAllText(res, "");
            var error = elevator.RunHelperAsync(req, res, ct).GetAwaiter().GetResult();
            if (error is not null) return Fail(error);
            var dto = JsonSerializer.Deserialize<ResultDto>(File.ReadAllText(res));
            return dto?.Items ?? Fail("Yönetici yardımcısı sonuç yazmadı.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private ItemResult RunCommand(Finding finding, CancellationToken ct)
    {
        var rule = finding.Rule;
        long before = MeasureSizePaths(rule, ct);
        try
        {
            if (rule.Builtin == "empty-recycle-bin")
            {
                var (bytes, _) = WindowsRecycleBinApi.Query();
                var err = WindowsRecycleBinApi.Empty();
                return new ItemResult(rule.CommandText, rule.Name, bytes, err is null ? bytes : 0,
                    err is null ? ItemOutcome.Deleted : ItemOutcome.Failed, err);
            }
            var argv = rule.Command.ToList();
            argv[0] = env.FindProgram(argv[0]) ?? argv[0];
            var (code, output) = ProcessHelper.RunAsync(argv, ct).GetAwaiter().GetResult();
            long after = MeasureSizePaths(rule, ct);
            long freed = Math.Max(0, before - after);
            return code == 0
                ? new ItemResult(rule.CommandText, rule.Name, finding.TotalSize, freed, ItemOutcome.Deleted, null)
                : new ItemResult(rule.CommandText, rule.Name, finding.TotalSize, freed, ItemOutcome.Failed, $"Çıkış kodu {code}: {output}");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException
                                       or OperationCanceledException or PlatformNotSupportedException)
        {
            return new ItemResult(rule.CommandText, rule.Name, finding.TotalSize, 0, ItemOutcome.Failed, ex.Message);
        }
    }

    private long MeasureSizePaths(Rules.CleanupRule rule, CancellationToken ct)
    {
        long size = 0;
        foreach (var p in rule.SizePaths)
        {
            foreach (var d in _resolver.Resolve(p)) size += Scanner.Measure(new DirectoryInfo(d), ct).Size;
            if (_resolver.ExpandTokens(p) is { } f && File.Exists(f)) size += Scanner.SafeLength(new FileInfo(f));
        }
        return size;
    }

    private ItemResult DeleteOne(Finding finding, ScanItem item, DeleteMode mode, CancellationToken ct)
    {
        string rule = finding.Rule.Name;
        var reason = _guard.Check(item.Path, finding.Root, finding.Rule.AllowUserFiles);
        if (reason is not null) return new(item.Path, rule, item.Size, 0, ItemOutcome.Skipped, reason);

        bool isLink = PathGuard.IsLink(item.Path);
        bool exists = isLink || (item.IsDirectory ? Directory.Exists(item.Path) : File.Exists(item.Path));
        if (!exists) return new(item.Path, rule, item.Size, 0, ItemOutcome.Skipped, "Artık yok (başka biri silmiş).");

        try
        {
            if (mode == DeleteMode.Trash && !isLink)
            {
                trash.MoveToTrash(item.Path);
                return new(item.Path, rule, item.Size, item.Size, ItemOutcome.Trashed, null);
            }

            if (isLink || !item.IsDirectory)
            {
                long len = isLink ? 0 : new FileInfo(item.Path).Length;
                DeleteFile(item.Path);
                return new(item.Path, rule, item.Size, len, ItemOutcome.Deleted, null);
            }

            var (freed, errors) = DeleteTree(item.Path, ct);
            if (errors.Count == 0) return new(item.Path, rule, item.Size, freed, ItemOutcome.Deleted, null);
            return new(item.Path, rule, item.Size, freed, ItemOutcome.Partial,
                $"{errors.Count} öğe silinemedi (kullanımda veya izin yok). İlk hata: {errors[0]}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or PlatformNotSupportedException or OperationCanceledException)
        {
            return new(item.Path, rule, item.Size, 0, ItemOutcome.Failed, ex.Message);
        }
    }

    private static void DeleteFile(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Attributes.HasFlag(FileAttributes.ReadOnly) && fi.LinkTarget is null)
            fi.Attributes &= ~FileAttributes.ReadOnly;
        fi.Delete();
    }

    /// <summary>Deletes what it can, never follows links (links are removed, not their targets), keeps going on errors.</summary>
    private static (long Freed, List<string> Errors) DeleteTree(string root, CancellationToken ct)
    {
        long freed = 0;
        var errors = new List<string>();
        var stack = new Stack<string>();
        var dirs = new List<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            dirs.Add(dir);
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{dir}: {ex.Message}"); continue; }

            foreach (var e in entries)
            {
                bool link = e.LinkTarget is not null || e.Attributes.HasFlag(FileAttributes.ReparsePoint);
                try
                {
                    if (e is DirectoryInfo d && !link) { stack.Push(d.FullName); continue; }
                    if (e is DirectoryInfo dl) { dl.Delete(false); continue; }  // link/junction to a folder: remove link only
                    long len = link ? 0 : ((FileInfo)e).Length;
                    DeleteFile(e.FullName);
                    freed += len;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{e.FullName}: {ex.Message}"); }
            }
        }
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            try { Directory.Delete(dirs[i], false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-empty because a child failed: already reported. Anything else is a new error.
                if (!Directory.EnumerateFileSystemEntries(dirs[i]).Any()) errors.Add($"{dirs[i]}: {ex.Message}");
            }
        }
        return (freed, errors);
    }

    private string? WriteLog(CleanResult result)
    {
        if (logDirectory is null) return null;
        try
        {
            Directory.CreateDirectory(logDirectory);
            string file = Path.Combine(logDirectory, $"temizlik-{result.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}.json");
            var payload = new
            {
                started = result.StartedUtc,
                mode = result.Mode.ToString(),
                cancelled = result.Cancelled,
                bytesFreed = result.BytesFreed,
                items = result.Items.Select(i => new
                {
                    path = i.Path, rule = i.RuleName, size = i.Size, freed = i.BytesFreed,
                    outcome = i.Outcome.ToString(), error = i.Error,
                }),
            };
            File.WriteAllText(file, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // logging must never break cleaning
        }
    }
}
