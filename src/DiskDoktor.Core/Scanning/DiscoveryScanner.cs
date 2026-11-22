using System.Security.Cryptography;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;

namespace DiskDoktor.Core.Scanning;

public sealed class DiscoveryOptions
{
    public long LargeFileBytes { get; init; } = 500L * 1024 * 1024;
    public long DuplicateMinBytes { get; init; } = 10L * 1024 * 1024;
    public int MaxDepth { get; init; } = 12;
}

/// <summary>
/// Finds things no fixed rule can know about: very large files, duplicate files and regenerable project build output
/// (node_modules, bin/obj, target, Unity Library...). Everything here is opt-in and listed item by item.
/// </summary>
public sealed class DiscoveryScanner(ISystemEnvironment env, IDriveService? drives = null, DiscoveryOptions? options = null)
{
    private readonly DiscoveryOptions _o = options ?? new();
    private StringComparison Cmp => env.PathsAreCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static readonly CleanupRule LargeRule = new()
    {
        Id = "discover-large", Name = "Büyük dosyalar (500 MB+)", Category = "Kişisel dosyalar", Risk = RiskLevel.Risky,
        Description = "Diskte en çok yer kaplayan dosyalar. Gerekmeyenleri tek tek seçin.",
        Kind = TargetKind.Files, DefaultSelected = false, ItemsSelectedByDefault = false, AllowUserFiles = true,
    };

    public static readonly CleanupRule DuplicateRule = new()
    {
        Id = "discover-duplicates", Name = "Yinelenen dosyalar", Category = "Kişisel dosyalar", Risk = RiskLevel.Caution,
        Description = "İçeriği birebir aynı olan dosyaların fazla kopyaları (en eskisi korunur, listede görünmez). 10 MB üstü.",
        Kind = TargetKind.Files, DefaultSelected = false, ItemsSelectedByDefault = false, AllowUserFiles = true,
    };

    public static readonly CleanupRule ArtifactRule = new()
    {
        Id = "discover-artifacts", Name = "Proje derleme çıktıları", Category = "Geliştirici", Risk = RiskLevel.Caution,
        Description = "node_modules, bin/obj, target, build, Unity Library/Temp, Unreal Intermediate, .godot... Proje bir sonraki derlemede yeniden üretir.",
        Kind = TargetKind.DirectoryContents, DefaultSelected = false, AllowUserFiles = true,
    };

    // Skipped anywhere: app data handled by rules, OS folders on secondary drives.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "$Recycle.Bin", "System Volume Information", "Windows", "Program Files", "Program Files (x86)",
        "ProgramData", "Recovery", "PerfLogs", "lost+found",
    };

    // Skipped only directly under a scan root (home or drive): ~/snap, ~/Library, a drive's /usr...
    private static readonly HashSet<string> SkipAtRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "snap", "Library", "Applications", "System", "proc", "sys", "dev", "run", "boot", "usr", "bin", "sbin",
        "lib", "lib64", "etc", "var", "opt", "srv",
    };

    public IEnumerable<string> Roots()
    {
        var roots = new List<string> { env.HomeDirectory };
        if (drives is null) return roots;
        string? sysRoot = env.Os == OsKind.Windows
            ? Path.GetPathRoot(env.GetSpecialFolder(Environment.SpecialFolder.Windows) ?? "C:\\")
            : "/";
        foreach (var d in drives.GetDrives())
        {
            if (string.Equals(d.Name.TrimEnd('\\', '/'), sysRoot?.TrimEnd('\\', '/'), Cmp)) continue;
            if (d.Name == "/" || IsUnder(env.HomeDirectory, d.Name) || IsUnder(d.Name, env.HomeDirectory)) continue;
            if (env.Os != OsKind.Windows && !(d.Name.StartsWith("/media/") || d.Name.StartsWith("/mnt/") ||
                                              d.Name.StartsWith("/run/media/") || d.Name.StartsWith("/Volumes/"))) continue;
            roots.Add(d.Name);
        }
        return roots;
    }

    public IReadOnlyList<Finding> Discover(IReadOnlyList<string> excludedRoots, CancellationToken ct)
    {
        var findings = new List<Finding>();
        foreach (var root in Roots().Where(Directory.Exists))
        {
            var large = new List<ScanItem>();
            var artifacts = new List<ScanItem>();
            var bySize = new Dictionary<long, List<FileInfo>>();
            Walk(new DirectoryInfo(root), 0, excludedRoots, large, artifacts, bySize, ct);

            var dups = FindDuplicates(bySize, ct);
            var dupPaths = dups.Select(d => d.Path).ToHashSet(env.PathsAreCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            large = large.Where(l => !dupPaths.Contains(l.Path)).OrderByDescending(l => l.Size).ToList();

            if (artifacts.Count > 0) findings.Add(new Finding { Rule = ArtifactRule, Root = root, Items = artifacts.OrderByDescending(a => a.Size).ToList() });
            if (dups.Count > 0) findings.Add(new Finding { Rule = DuplicateRule, Root = root, Items = dups });
            if (large.Count > 0) findings.Add(new Finding { Rule = LargeRule, Root = root, Items = large });
        }
        return findings;
    }

    private void Walk(DirectoryInfo dir, int depth, IReadOnlyList<string> excluded, List<ScanItem> large,
        List<ScanItem> artifacts, Dictionary<long, List<FileInfo>> bySize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > _o.MaxDepth) return;

        List<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos("*", Scanner.Options(false)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return; }

        var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        if (depth > 0 && names.Contains("pyvenv.cfg")) return;   // a Python virtual env: needed as is, never picked apart
        foreach (var e in entries)
        {
            if (e is FileInfo f)
            {
                long len = Scanner.SafeLength(f);
                if (len >= _o.LargeFileBytes) large.Add(new ScanItem(f.FullName, len, 1, f.LastWriteTimeUtc, false));
                if (len >= _o.DuplicateMinBytes)
                {
                    if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = [];
                    list.Add(f);
                }
                continue;
            }
            if (e is not DirectoryInfo d) continue;
            if (excluded.Any(x => string.Equals(x, d.FullName, Cmp) || IsUnder(d.FullName, x))) continue;

            if (ArtifactReason(d.Name, names, entries) is { } reason)
            {
                var (size, count, newest) = Scanner.Measure(d, ct);
                if (size > 0) artifacts.Add(new ScanItem(d.FullName, size, count, newest, true, reason));
                continue;   // never descend into build output
            }
            if (d.Name.StartsWith('.') || SkipDirs.Contains(d.Name) || d.Name == "node_modules") continue;
            if (depth == 0 && SkipAtRoot.Contains(d.Name)) continue;
            Walk(d, depth + 1, excluded, large, artifacts, bySize, ct);
        }
    }

    /// <summary>Why this folder is regenerable build output, judged by its sibling project files; null if it is not.</summary>
    internal static string? ArtifactReason(string name, HashSet<string> siblings, List<FileSystemInfo> entries)
    {
        bool Has(string n) => siblings.Contains(n);
        bool HasExt(params string[] exts) => entries.Any(e => e is FileInfo && exts.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
        switch (name)
        {
            case "node_modules" when Has("package.json"): return "npm bağımlılıkları (package.json)";
            case ".next" or ".nuxt" or ".turbo" or ".parcel-cache" or ".angular" when Has("package.json"): return "web derleme önbelleği";
            case "target" when Has("Cargo.toml"): return "Rust derleme çıktısı";
            case "target" when Has("pom.xml"): return "Maven derleme çıktısı";
            case "bin" or "obj" when HasExt(".csproj", ".fsproj", ".vbproj"): return ".NET derleme çıktısı";
            case "build" or ".gradle" when Has("build.gradle") || Has("build.gradle.kts") || Has("settings.gradle") || Has("settings.gradle.kts"): return "Gradle derleme çıktısı";
            case "Library" or "Temp" or "Obj" when Has("ProjectSettings") && Has("Assets"): return "Unity projesi (yeniden içe aktarılır)";
            case ".godot" when Has("project.godot"): return "Godot içe aktarma önbelleği";
            case "Intermediate" or "DerivedDataCache" or "Binaries" when HasExt(".uproject"): return "Unreal derleme çıktısı";
            case ".dart_tool" when Has("pubspec.yaml"): return "Flutter/Dart önbelleği";
            case "DerivedData" when HasExt(".xcodeproj", ".xcworkspace"): return "Xcode derleme çıktısı";
            default: return null;
        }
    }

    private List<ScanItem> FindDuplicates(Dictionary<long, List<FileInfo>> bySize, CancellationToken ct)
    {
        var result = new List<ScanItem>();
        foreach (var (size, files) in bySize)
        {
            if (files.Count < 2) continue;
            // Cheap first pass on the first 64 KB, then full hash only for files that still collide.
            foreach (var head in files.GroupBy(f => Hash(f, 64 * 1024, ct)).Where(g => g.Key is not null && g.Count() > 1))
                foreach (var same in head.GroupBy(f => Hash(f, long.MaxValue, ct)).Where(g => g.Key is not null && g.Count() > 1))
                {
                    var ordered = same.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName.Length).ToList();
                    var keep = ordered[0];
                    foreach (var copy in ordered.Skip(1))
                        result.Add(new ScanItem(copy.FullName, size, 1, copy.LastWriteTimeUtc, false, "Aynısı korunuyor: " + keep.FullName));
                }
        }
        return result.OrderByDescending(r => r.Size).ToList();
    }

    private static string? Hash(FileInfo f, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 16];
            long left = maxBytes;
            int n;
            while (left > 0 && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buf, 0, n);
                left -= n;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private bool IsUnder(string child, string parent)
    {
        var p = parent.TrimEnd('/', '\\');
        return child.Length > p.Length + 1 && child.StartsWith(p, Cmp) && (child[p.Length] == '/' || child[p.Length] == '\\');
    }
}
