namespace DiskDoktor.Core.Platform;

public sealed record DriveSnapshot(string Name, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

public interface IDriveService
{
    IReadOnlyList<DriveSnapshot> GetDrives();
}

public sealed class DriveService(ISystemEnvironment env) : IDriveService
{
    private static readonly HashSet<string> PseudoFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "overlay", "squashfs", "securityfs",
        "debugfs", "tracefs", "pstore", "bpf", "configfs", "fusectl", "mqueue", "hugetlbfs", "autofs", "binfmt_misc",
        "efivarfs", "ramfs", "nsfs", "fuse.portal", "fuse.gvfsd-fuse", "devfs", "autofs", "nullfs",
    };

    private static readonly string[] PseudoPrefixes =
        ["/proc", "/sys", "/dev", "/run", "/snap", "/var/snap", "/boot/efi", "/System/Volumes", "/private/var/vm"];

    public IReadOnlyList<DriveSnapshot> GetDrives()
    {
        var list = new List<(DriveInfo Info, string Format)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                list.Add((d, d.DriveFormat));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Filter(list.Select(x => (x.Info.Name, x.Format, x.Info.DriveType, SafeLabel(x.Info),
                                        SafeTotal(x.Info), SafeFree(x.Info))), env.Os);
    }

    /// <summary>Pure filtering so it can be tested with synthetic mounts.</summary>
    internal static IReadOnlyList<DriveSnapshot> Filter(
        IEnumerable<(string Name, string Format, DriveType Type, string Label, long Total, long Free)> drives, OsKind os)
    {
        var result = new List<DriveSnapshot>();
        var seenSizes = new HashSet<(long, long)>();
        foreach (var d in drives.OrderBy(d => d.Name.Length).ThenBy(d => d.Name, StringComparer.Ordinal))
        {
            if (d.Total <= 0) continue;
            if (d.Type == DriveType.NoRootDirectory) continue;
            if (os == OsKind.Windows && d.Type is DriveType.Ram or DriveType.Unknown) continue;
            if (os != OsKind.Windows)
            {
                if (PseudoFormats.Contains(d.Format)) continue;
                if (PseudoPrefixes.Any(p => d.Name == p || d.Name.StartsWith(p + "/", StringComparison.Ordinal))) continue;
                // Bind mounts and btrfs subvolumes show the same device twice: keep the shortest path.
                if (!seenSizes.Add((d.Total, d.Free / (1024 * 1024)))) continue;
            }
            string label = string.IsNullOrWhiteSpace(d.Label) || d.Label == d.Name ? DefaultLabel(d.Name, os) : d.Label;
            result.Add(new DriveSnapshot(d.Name, label, d.Total, Math.Clamp(d.Free, 0, d.Total)));
        }
        return result;
    }

    private static string DefaultLabel(string name, OsKind os) => os switch
    {
        OsKind.Windows => "Yerel Disk",
        _ when name == "/" => "Sistem",
        _ => Path.GetFileName(Path.TrimEndingDirectorySeparator(name)) is { Length: > 0 } n ? n : name,
    };

    private static string SafeLabel(DriveInfo d) { try { return d.VolumeLabel; } catch { return ""; } }
    private static long SafeTotal(DriveInfo d) { try { return d.TotalSize; } catch { return 0; } }
    private static long SafeFree(DriveInfo d) { try { return d.AvailableFreeSpace; } catch { return 0; } }
}
