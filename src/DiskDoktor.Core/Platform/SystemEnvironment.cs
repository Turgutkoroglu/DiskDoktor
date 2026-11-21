namespace DiskDoktor.Core.Platform;

public enum OsKind { Windows, Linux, MacOS }

/// <summary>Everything the core needs from the host OS, injectable for tests.</summary>
public interface ISystemEnvironment
{
    OsKind Os { get; }
    string HomeDirectory { get; }
    string TempDirectory { get; }
    string? GetVariable(string name);
    /// <summary>Returns null when the folder does not exist on this OS.</summary>
    string? GetSpecialFolder(Environment.SpecialFolder folder);
    string AppBaseDirectory { get; }
    bool PathsAreCaseSensitive => Os == OsKind.Linux;
}

public sealed class SystemEnvironment : ISystemEnvironment
{
    public static readonly SystemEnvironment Instance = new();

    public OsKind Os { get; } =
        OperatingSystem.IsWindows() ? OsKind.Windows :
        OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux;

    public string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string TempDirectory { get; } = Path.GetTempPath();

    public string AppBaseDirectory { get; } = AppContext.BaseDirectory;

    public string? GetVariable(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public string? GetSpecialFolder(Environment.SpecialFolder folder)
    {
        var p = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
}

/// <summary>Per-OS locations for DiskDoktor's own files.</summary>
public static class AppPaths
{
    public static string DataDirectory(ISystemEnvironment env) => env.Os switch
    {
        OsKind.Windows => Path.Combine(env.GetSpecialFolder(Environment.SpecialFolder.LocalApplicationData)
                                       ?? env.HomeDirectory, "DiskDoktor"),
        OsKind.MacOS => Path.Combine(env.HomeDirectory, "Library", "Application Support", "DiskDoktor"),
        _ => Path.Combine(env.GetVariable("XDG_STATE_HOME") ?? Path.Combine(env.HomeDirectory, ".local", "state"),
                          "DiskDoktor"),
    };

    public static string LogDirectory(ISystemEnvironment env) => Path.Combine(DataDirectory(env), "logs");

    public static string UserRulesFile(ISystemEnvironment env) => env.Os == OsKind.Linux
        ? Path.Combine(env.GetVariable("XDG_CONFIG_HOME") ?? Path.Combine(env.HomeDirectory, ".config"),
                       "DiskDoktor", "rules.json")
        : Path.Combine(DataDirectory(env), "rules.json");
}
