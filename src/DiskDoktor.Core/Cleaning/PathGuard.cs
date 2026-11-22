using DiskDoktor.Core.Platform;

namespace DiskDoktor.Core.Cleaning;

/// <summary>
/// Last line of defence before anything is deleted. A path is allowed only when it is strictly inside the
/// rule root it was found under, no folder between root and target is a link, and it does not touch a
/// protected system or user location.
/// </summary>
public sealed class PathGuard
{
    private readonly ISystemEnvironment _env;
    private readonly StringComparison _cmp;
    private readonly List<string> _protectedExact = [];   // never delete these or their ancestors
    private readonly List<string> _protectedTrees = [];   // never delete anything inside these
    private readonly List<string> _userTrees = [];        // personal folders: only rules that allow user files

    public PathGuard(ISystemEnvironment env)
    {
        _env = env;
        _cmp = env.PathsAreCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        string home = env.HomeDirectory;
        AddExact(home);
        AddExact(env.TempDirectory);
        AddTree(env.AppBaseDirectory);

        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos,
                 })
        {
            var p = env.GetSpecialFolder(f);
            // On Linux some of these fall back to $HOME when unset; protecting $HOME as a tree would block everything.
            if (p is not null && !Same(p, home)) { _userTrees.Add(Normalize(p)); AddExact(p); }
        }

        switch (env.Os)
        {
            case OsKind.Windows:
                foreach (var f in new[]
                         {
                             Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                             Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                             Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                         })
                {
                    var p = env.GetSpecialFolder(f);
                    if (p is not null) AddExact(p);
                }
                var win = env.GetSpecialFolder(Environment.SpecialFolder.Windows);
                if (win is not null)
                {
                    AddTree(Path.Combine(win, "System32"));
                    AddTree(Path.Combine(win, "SysWOW64"));
                    AddTree(Path.Combine(win, "WinSxS"));
                }
                break;
            case OsKind.MacOS:
                foreach (var p in new[] { "/System", "/bin", "/sbin", "/usr", "/etc", "/private/etc", "/Applications", "/Library" })
                    AddTree(p);
                AddExact(Path.Combine(home, "Library"));
                AddTree(Path.Combine(home, "Library", "Keychains"));
                break;
            default:
                foreach (var p in new[] { "/bin", "/sbin", "/usr", "/etc", "/boot", "/lib", "/lib64", "/opt", "/srv", "/proc", "/sys", "/dev", "/run", "/snap" })
                    AddTree(p);
                foreach (var p in new[] { "/var", "/home", "/root", "/tmp", "/media", "/mnt" })
                    AddExact(p);
                AddExact(Path.Combine(home, ".cache"));
                AddExact(Path.Combine(home, ".config"));
                AddExact(Path.Combine(home, ".local"));
                AddExact(Path.Combine(home, ".local", "share"));
                AddTree(Path.Combine(home, ".ssh"));
                AddTree(Path.Combine(home, ".gnupg"));
                break;
        }
    }

    private void AddExact(string p) => _protectedExact.Add(Normalize(p));
    private void AddTree(string p) => _protectedTrees.Add(Normalize(p));

    private static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), _cmp);

    /// <summary>True when <paramref name="child"/> is strictly below <paramref name="parent"/>.</summary>
    public bool IsStrictlyInside(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        if (Path.GetPathRoot(p) == p) return c.Length > p.Length && c.StartsWith(p, _cmp);
        return c.Length > p.Length + 1 && c.StartsWith(p, _cmp) && c[p.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>Returns null when deletion is allowed, otherwise a human readable reason.</summary>
    public string? Check(string target, string allowedRoot, bool allowUserFiles = false)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            return "Yol tam değil.";
        if (target.Replace('\\', '/').Split('/').Any(s => s == ".."))
            return "Yolda '..' var.";

        string t = Normalize(target);
        string root = Normalize(allowedRoot);

        if (Path.GetPathRoot(t) == t) return "Sürücü kökü silinemez.";
        if (!IsStrictlyInside(t, root)) return "Hedef, kuralın klasörünün dışında.";

        foreach (var p in _protectedExact)
            if (Same(t, p) || IsStrictlyInside(p, t)) return $"Korunan konum: {p}";
        foreach (var p in _protectedTrees)
            if (Same(t, p) || IsStrictlyInside(t, p) || IsStrictlyInside(p, t)) return $"Korunan konum: {p}";
        if (!allowUserFiles)
            foreach (var p in _userTrees)
                if (IsStrictlyInside(t, p)) return $"Kişisel klasör: {p}";

        // No link between the root and the target (the target itself may be a link: only the link is removed).
        var dir = Path.GetDirectoryName(t);
        while (dir is not null && IsStrictlyInside(dir, root))
        {
            if (IsLink(dir)) return $"Arada sembolik bağlantı var: {dir}";
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    internal static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists && !Directory.Exists(path)) return false;
            return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unknown means unsafe
        }
    }
}
