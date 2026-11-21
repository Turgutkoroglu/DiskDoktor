using System.IO.Enumeration;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;

namespace DiskDoktor.Core.Scanning;

public sealed record ScanItem(string Path, long Size, int FileCount, DateTime LastWriteUtc, bool IsDirectory);

/// <summary>Candidates of one rule under one concrete root folder.</summary>
public sealed class Finding
{
    public required CleanupRule Rule { get; init; }
    public required string Root { get; init; }
    public required IReadOnlyList<ScanItem> Items { get; init; }
    public long TotalSize => Items.Sum(i => i.Size);
    public int FileCount => Items.Sum(i => i.FileCount);
}

public sealed record ScanProgress(string RuleName, int RuleIndex, int RuleCount);

public sealed class ScanResult
{
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required TimeSpan Duration { get; init; }
    public long TotalSize => Findings.Sum(f => f.TotalSize);
}

public interface IScanner
{
    Task<ScanResult> ScanAsync(IReadOnlyList<CleanupRule> rules, IProgress<ScanProgress>? progress, CancellationToken ct);
}

public sealed class Scanner(ISystemEnvironment env, Func<DateTime>? utcNow = null) : IScanner
{
    private readonly PathResolver _resolver = new(env);
    private readonly Func<DateTime> _now = utcNow ?? (() => DateTime.UtcNow);
    private readonly StringComparison _cmp = env.PathsAreCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public Task<ScanResult> ScanAsync(IReadOnlyList<CleanupRule> rules, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(rules, progress, ct), ct);

    public ScanResult Scan(IReadOnlyList<CleanupRule> rules, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = _now();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var applicable = rules.Where(r => r.AppliesTo(env.Os)).ToList();

        // Resolve every root first so overlapping rules never count the same bytes twice:
        // a root claimed by an earlier rule is skipped, and a broader rule skips entries that hold another rule's root.
        var claimed = new HashSet<string>(env.PathsAreCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var plan = new List<(CleanupRule Rule, string Root)>();
        foreach (var rule in applicable)
            foreach (var pattern in rule.Paths)
                foreach (var root in _resolver.Resolve(pattern))
                    if (claimed.Add(Path.GetFullPath(root)))
                        plan.Add((rule, Path.GetFullPath(root)));

        var allRoots = plan.Select(p => p.Root).ToList();
        var findings = new List<Finding>();
        int index = 0;
        var groups = plan.GroupBy(p => p.Rule).ToList();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(group.Key.Name, ++index, groups.Count));
            foreach (var (rule, root) in group)
            {
                var otherRoots = allRoots.Where(r => !string.Equals(r, root, _cmp)).ToList();
                var items = ScanRoot(rule, root, otherRoots, ct);
                if (items.Count > 0)
                    findings.Add(new Finding { Rule = rule, Root = root, Items = items });
            }
        }

        return new ScanResult { Findings = findings, StartedUtc = started, Duration = sw.Elapsed };
    }

    private List<ScanItem> ScanRoot(CleanupRule rule, string root, List<string> otherRoots, CancellationToken ct)
    {
        var cutoff = _now() - TimeSpan.FromDays(rule.MinAgeDays);
        var result = new List<ScanItem>();
        var excluded = new HashSet<string>(rule.ExcludeNames, env.PathsAreCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        if (rule.Kind == TargetKind.Files)
        {
            var opts = Options(rule.Recursive);
            foreach (var file in SafeEnumerate(() => new DirectoryInfo(root).EnumerateFiles("*", opts)))
            {
                ct.ThrowIfCancellationRequested();
                if (excluded.Contains(file.Name) || !Matches(rule, file.Name)) continue;
                if (HasExcludedAncestor(file.FullName, root, excluded) || InsideAny(file.FullName, otherRoots)) continue;
                if (rule.MinAgeDays > 0 && file.LastWriteTimeUtc > cutoff) continue;
                result.Add(new ScanItem(file.FullName, SafeLength(file), 1, file.LastWriteTimeUtc, false));
            }
            return result;
        }

        foreach (var entry in SafeEnumerate(() => new DirectoryInfo(root).EnumerateFileSystemInfos("*", Options(false))))
        {
            ct.ThrowIfCancellationRequested();
            if (excluded.Contains(entry.Name)) continue;
            if (otherRoots.Any(o => string.Equals(o, entry.FullName, _cmp) || IsInside(o, entry.FullName))) continue;

            if (entry is FileInfo f)
            {
                if (!Matches(rule, f.Name)) continue;
                if (rule.MinAgeDays > 0 && f.LastWriteTimeUtc > cutoff) continue;
                result.Add(new ScanItem(f.FullName, SafeLength(f), 1, f.LastWriteTimeUtc, false));
            }
            else if (entry is DirectoryInfo d)
            {
                var (size, count, newest) = Measure(d, ct);
                if (rule.MinAgeDays > 0 && newest > cutoff) continue;   // something inside is still in use
                if (count == 0 && size == 0 && rule.Patterns.Length > 0) continue;
                result.Add(new ScanItem(d.FullName, size, count, newest, true));
            }
        }
        return result;
    }

    private static (long Size, int Count, DateTime Newest) Measure(DirectoryInfo dir, CancellationToken ct)
    {
        long size = 0; int count = 0;
        // A folder's own mtime changes whenever an entry is added or removed, so age comes from its files.
        DateTime newest = DateTime.MinValue;
        foreach (var f in SafeEnumerate(() => dir.EnumerateFiles("*", Options(true))))
        {
            ct.ThrowIfCancellationRequested();
            size += SafeLength(f);
            count++;
            if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
        }
        if (count == 0) newest = dir.LastWriteTimeUtc;
        return (size, count, newest);
    }

    private bool Matches(CleanupRule rule, string name) =>
        rule.Patterns.Length == 0 ||
        rule.Patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: !env.PathsAreCaseSensitive));

    private bool IsInside(string child, string parent) =>
        child.Length > parent.Length + 1 && child.StartsWith(parent, _cmp) && child[parent.Length] == Path.DirectorySeparatorChar;

    private bool InsideAny(string path, List<string> roots) => roots.Any(r => string.Equals(r, path, _cmp) || IsInside(path, r));

    private static bool HasExcludedAncestor(string file, string root, HashSet<string> excluded)
    {
        if (excluded.Count == 0) return false;
        var rel = Path.GetRelativePath(root, file);
        var parts = rel.Split(Path.DirectorySeparatorChar);
        return parts.Take(parts.Length - 1).Any(excluded.Contains);
    }

    private static EnumerationOptions Options(bool recursive) => new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = recursive,
        AttributesToSkip = FileAttributes.ReparsePoint,   // never follow links or junctions
        ReturnSpecialDirectories = false,
    };

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Enumerates lazily and stops quietly on I/O errors (folder removed mid-scan, permission flip).</summary>
    private static IEnumerable<T> SafeEnumerate<T>(Func<IEnumerable<T>> source)
    {
        IEnumerator<T> e;
        try { e = source().GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { yield break; }
        using (e)
        {
            while (true)
            {
                T current;
                try
                {
                    if (!e.MoveNext()) yield break;
                    current = e.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { yield break; }
                yield return current;
            }
        }
    }
}
