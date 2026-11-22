using System.Text.RegularExpressions;
using DiskDoktor.Core.Platform;

namespace DiskDoktor.Core.Rules;

/// <summary>Turns rule paths with tokens and '*' segments into concrete, existing directories.</summary>
public sealed partial class PathResolver(ISystemEnvironment env)
{
    [GeneratedRegex(@"\{([A-Z_]+)\}")]
    private static partial Regex TokenRegex();

    /// <summary>Returns existing directories for the pattern; an unknown/unavailable token yields nothing.</summary>
    public IEnumerable<string> Resolve(string pattern)
    {
        var expanded = ExpandTokens(pattern);
        if (expanded is null) return [];
        return ExpandWildcards(expanded);
    }

    internal string? ExpandTokens(string pattern)
    {
        string p = pattern.Trim();
        if (p == "~" || p.StartsWith("~/") || p.StartsWith("~\\"))
            p = env.HomeDirectory + p[1..];

        bool missing = false;
        p = TokenRegex().Replace(p, m =>
        {
            var v = Token(m.Groups[1].Value);
            if (v is null) { missing = true; return ""; }
            return v.TrimEnd('/', '\\');
        });
        if (missing) return null;

        // Rules are written with '/', normalise to the host separator.
        p = env.Os == OsKind.Windows ? p.Replace('/', '\\') : p.Replace('\\', '/');
        return Path.IsPathFullyQualified(p) ? p : null;
    }

    private string? Token(string name)
    {
        string home = env.HomeDirectory;
        bool win = env.Os == OsKind.Windows;
        return name switch
        {
            "HOME" => home,
            "TEMP" => env.TempDirectory,
            "LOCALAPPDATA" => win ? env.GetSpecialFolder(Environment.SpecialFolder.LocalApplicationData) : null,
            "APPDATA" => win ? env.GetSpecialFolder(Environment.SpecialFolder.ApplicationData) : null,
            "PROGRAMDATA" => win ? env.GetSpecialFolder(Environment.SpecialFolder.CommonApplicationData) : null,
            "WINDIR" => win ? env.GetSpecialFolder(Environment.SpecialFolder.Windows) : null,
            "SYSTEMDRIVE" => win && env.GetSpecialFolder(Environment.SpecialFolder.Windows) is { } w ? Path.GetPathRoot(w) : null,
            "PROGRAMFILES" => win ? env.GetSpecialFolder(Environment.SpecialFolder.ProgramFiles) : null,
            "PROGRAMFILESX86" => win ? env.GetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86) : null,
            "XDG_CACHE_HOME" => win ? null : env.Os == OsKind.MacOS ? null : env.GetVariable(name) ?? Path.Combine(home, ".cache"),
            "XDG_CONFIG_HOME" => win ? null : env.Os == OsKind.MacOS ? null : env.GetVariable(name) ?? Path.Combine(home, ".config"),
            "XDG_DATA_HOME" => win ? null : env.Os == OsKind.MacOS ? null : env.GetVariable(name) ?? Path.Combine(home, ".local", "share"),
            _ => null,
        };
    }

    private IEnumerable<string> ExpandWildcards(string path)
    {
        char sep = Path.DirectorySeparatorChar;
        if (!path.Contains('*') && !path.Contains('?'))
            return Directory.Exists(path) ? [Path.TrimEndingDirectorySeparator(path)] : [];

        string root = Path.GetPathRoot(path) ?? "";
        var segments = path[root.Length..].Split(sep, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [root];
        foreach (var seg in segments)
        {
            if (seg.Contains('*') || seg.Contains('?'))
            {
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = env.PathsAreCaseSensitive ? MatchCasing.CaseSensitive : MatchCasing.CaseInsensitive,
                };
                current = current.SelectMany(dir =>
                {
                    try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, seg, opts).ToList() : []; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
                }).ToList();
            }
            else
            {
                current = current.Select(d => Path.Combine(d, seg)).Where(Directory.Exists).ToList();
            }
        }
        return current.Select(Path.TrimEndingDirectorySeparator).ToList();
    }
}
