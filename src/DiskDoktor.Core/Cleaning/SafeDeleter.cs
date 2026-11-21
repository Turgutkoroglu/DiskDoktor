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

public sealed class SafeDeleter(ISystemEnvironment env, ITrash trash, string? logDirectory) : ICleaner
{
    private readonly PathGuard _guard = new(env);

    public SafeDeleter(ISystemEnvironment env) : this(env, TrashFactory.Create(env), AppPaths.LogDirectory(env)) { }

    public Task<CleanResult> CleanAsync(IReadOnlyList<CleanRequest> requests, DeleteMode mode,
        IProgress<CleanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Clean(requests, mode, progress, ct), CancellationToken.None);

    public CleanResult Clean(IReadOnlyList<CleanRequest> requests, DeleteMode mode,
        IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var work = requests.SelectMany(r => r.Items.Select(i => (r.Finding, Item: i))).ToList();
        var results = new List<ItemResult>(work.Count);
        long freed = 0;
        bool cancelled = false;

        for (int n = 0; n < work.Count; n++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var (finding, item) = work[n];
            progress?.Report(new CleanProgress(n, work.Count, freed, item.Path));

            var effective = finding.Rule.PermanentOnly || !trash.IsAvailable ? DeleteMode.Permanent : mode;
            var r = DeleteOne(finding, item, effective, ct);
            freed += r.BytesFreed;
            results.Add(r);
        }
        progress?.Report(new CleanProgress(results.Count, work.Count, freed, ""));

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

    private ItemResult DeleteOne(Finding finding, ScanItem item, DeleteMode mode, CancellationToken ct)
    {
        string rule = finding.Rule.Name;
        var reason = _guard.Check(item.Path, finding.Root);
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
