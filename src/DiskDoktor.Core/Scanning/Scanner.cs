using System.IO.Enumeration;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;

namespace DiskDoktor.Core.Scanning;

/// <param name="Note">Extra context shown next to the item (e.g. "Aynısı: …" for duplicates).</param>
public sealed record ScanItem(string Path, long Size, int FileCount, DateTime LastWriteUtc, bool IsDirectory, string? Note = null);

/// <summary>Candidates of one rule under one concrete root folder (or one command rule).</summary>
public sealed class Finding
{
    public required CleanupRule Rule { get; init; }
    public required string Root { get; init; }
    public required IReadOnlyList<ScanItem> Items { get; init; }
    /// <summary>Command rules: what the command is expected to free (null when unknown).</summary>
    public long? EstimatedSize { get; init; }
    /// <summary>Part of the folder could not be read without administrator rights; the size is a lower bound.</summary>
    public bool AccessDenied { get; init; }
    public bool IsCommand => Rule.Kind == TargetKind.Command;
    public bool SizeKnown => Items.Count > 0 || EstimatedSize is > 0;
    public long TotalSize => Items.Count > 0 ? Items.Sum(i => i.Size) : EstimatedSize ?? 0;
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

public sealed class Scanner(ISystemEnvironment env, Func<DateTime>? utcNow = null, DiscoveryScanner? discovery = null) : IScanner
{
    private readonly PathResolver _resolver = new(env);
    private readonly Func<DateTime> _now = utcNow ?? (() => DateTime.UtcNow);
    private readonly StringComparison _cmp = env.PathsAreCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private StringComparer PathComparer => env.PathsAreCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public Task<ScanResult> ScanAsync(IReadOnlyList<CleanupRule> rules, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(rules, progress, ct), ct);

    public ScanResult Scan(IReadOnlyList<CleanupRule> rules, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = _now();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var applicable = rules.Where(r => r.AppliesTo(env.Os)).ToList();

        // Resolve every folder root first. A DirectoryContents root claimed by an earlier rule is skipped, and a broader
        // rule skips entries that hold another rule's root, so nested caches are never counted twice.
        var claimed = new HashSet<string>(PathComparer);
        var plan = new List<(CleanupRule Rule, string? Root)>();
        foreach (var rule in applicable)
        {
            if (rule.Kind == TargetKind.Command) { plan.Add((rule, null)); continue; }
            foreach (var pattern in rule.Paths)
                foreach (var root in _resolver.Resolve(pattern))
                {
                    var full = Path.GetFullPath(root);
                    if (rule.Kind == TargetKind.Files || claimed.Add(full)) plan.Add((rule, full));
                }
        }

        var allRoots = plan.Where(p => p.Root is not null && p.Rule.Kind == TargetKind.DirectoryContents)
                           .Select(p => p.Root!).Distinct(PathComparer).ToList();
        var seenItems = new HashSet<string>(PathComparer);
        var findings = new List<Finding>();
        var groups = plan.GroupBy(p => p.Rule).ToList();
        int total = groups.Count + (discovery is null ? 0 : 1), index = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(group.Key.Name, ++index, total));
            foreach (var (rule, root) in group)
            {
                if (rule.Kind == TargetKind.Command)
                {
                    if (ScanCommand(rule, ct) is { } cf) findings.Add(cf);
                    continue;
                }
                var otherRoots = allRoots.Where(r => !string.Equals(r, root, _cmp)).ToList();
                var (items, denied) = ScanRoot(rule, root!, otherRoots, ct);
                items = items.Where(i => seenItems.Add(i.Path)).ToList();
                if ((items.Count > 0 && items.Sum(i => i.Size) > 0) || (denied && rule.RequiresAdmin && !env.IsElevated))
                    findings.Add(new Finding { Rule = rule, Root = root!, Items = items, AccessDenied = denied });
            }
        }

        if (discovery is not null)
        {
            progress?.Report(new ScanProgress("Büyük, yinelenen dosyalar ve proje çıktıları", ++index, total));
            var excluded = allRoots.Concat(plan.Where(p => p.Root is not null).Select(p => p.Root!)).ToList();
            foreach (var f in discovery.Discover(excluded, ct))
            {
                var items = f.Items.Where(i => seenItems.Add(i.Path)).ToList();
                if (items.Count > 0) findings.Add(new Finding { Rule = f.Rule, Root = f.Root, Items = items });
            }
        }

        return new ScanResult { Findings = findings, StartedUtc = started, Duration = sw.Elapsed };
    }

    private Finding? ScanCommand(CleanupRule rule, CancellationToken ct)
    {
        if (rule.Builtin == "empty-recycle-bin")
        {
            var (bytes, count) = WindowsRecycleBinApi.Query();
            return count == 0 ? null : new Finding { Rule = rule, Root = "", Items = [], EstimatedSize = bytes };
        }
        if (rule.Requires is not null && env.FindProgram(rule.Requires) is null) return null;

        var sizeRoots = rule.SizePaths.SelectMany(_resolver.Resolve).ToList();
        var sizeFiles = rule.SizePaths.Select(p => _resolver.ExpandTokens(p)).Where(p => p is not null && File.Exists(p)).Select(p => p!).ToList();
        if (rule.SizePaths.Length > 0 && sizeRoots.Count == 0 && sizeFiles.Count == 0) return null;

        long size = 0;
        bool denied = false;
        foreach (var r in sizeRoots)
        {
            var (s, _, _) = Measure(new DirectoryInfo(r), ct);
            size += s;
            denied |= IsAccessDenied(r);
        }
        foreach (var f in sizeFiles) size += SafeLength(new FileInfo(f));
        if (rule.SizePaths.Length > 0 && size == 0 && !denied) return null;   // nothing to clean
        return new Finding
        {
            Rule = rule, Root = sizeRoots.FirstOrDefault() ?? sizeFiles.FirstOrDefault() ?? "",
            Items = [], EstimatedSize = size > 0 ? size : null, AccessDenied = denied,
        };
    }

    /// <summary>Lists one rule root. Shared with the elevated helper, which re-enumerates admin roots itself.</summary>
    internal (List<ScanItem> Items, bool Denied) ScanRoot(CleanupRule rule, string root, List<string> otherRoots, CancellationToken ct)
    {
        var cutoff = _now() - TimeSpan.FromDays(rule.MinAgeDays);
        var result = new List<ScanItem>();
        var excluded = new HashSet<string>(rule.ExcludeNames, PathComparer);
        bool denied = IsAccessDenied(root);

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
            return (result, denied);
        }

        foreach (var entry in SafeEnumerate(() => new DirectoryInfo(root).EnumerateFileSystemInfos("*", Options(false))))
        {
            ct.ThrowIfCancellationRequested();
            if (excluded.Contains(entry.Name) || rule.ExcludeNames.Any(x => entry.Name.StartsWith(x + "-", _cmp))) continue;
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
                result.Add(new ScanItem(d.FullName, size, count, newest, true));
            }
        }
        return (result, denied);
    }

    internal static (long Size, int Count, DateTime Newest) Measure(DirectoryInfo dir, CancellationToken ct)
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
        if (count == 0) newest = SafeLastWrite(dir);
        return (size, count, newest);
    }

    private static bool IsAccessDenied(string root)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            e.MoveNext();
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException) { return false; }
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

    internal static EnumerationOptions Options(bool recursive) => new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = recursive,
        AttributesToSkip = FileAttributes.ReparsePoint,   // never follow links or junctions
        ReturnSpecialDirectories = false,
    };

    internal static long SafeLength(FileInfo f)
    {
        try { return f.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static DateTime SafeLastWrite(FileSystemInfo f)
    {
        try { return f.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>Enumerates lazily and stops quietly on I/O errors (folder removed mid-scan, permission flip).</summary>
    internal static IEnumerable<T> SafeEnumerate<T>(Func<IEnumerable<T>> source)
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
