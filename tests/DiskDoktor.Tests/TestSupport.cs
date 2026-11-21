using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;

namespace DiskDoktor.Tests;

/// <summary>A sandbox home folder; everything lives under a fresh temp directory.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dd-test-" + Guid.NewGuid().ToString("N"));
    public FakeEnv Env { get; }

    public Sandbox(OsKind? os = null)
    {
        Directory.CreateDirectory(Root);
        var home = Directory.CreateDirectory(Path.Combine(Root, "home")).FullName;
        var temp = Directory.CreateDirectory(Path.Combine(Root, "tmp")).FullName;
        Env = new FakeEnv(os ?? SystemEnvironment.Instance.Os, home, temp, Path.Combine(Root, "app"));
    }

    public string Dir(string rel) => Directory.CreateDirectory(Path.Combine(Root, rel)).FullName;

    public string File(string rel, int bytes = 10, int ageDays = 0)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        if (ageDays > 0) System.IO.File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public sealed class FakeEnv(OsKind os, string home, string temp, string appBase) : ISystemEnvironment
{
    public Dictionary<string, string> Vars { get; } = new();
    public Dictionary<Environment.SpecialFolder, string> Folders { get; } = new();
    public OsKind Os { get; } = os;
    public string HomeDirectory { get; } = home;
    public string TempDirectory { get; } = temp;
    public string AppBaseDirectory { get; } = appBase;
    public string? GetVariable(string name) => Vars.TryGetValue(name, out var v) ? v : null;
    public string? GetSpecialFolder(Environment.SpecialFolder f) => Folders.TryGetValue(f, out var v) ? v : null;
}

public static class R
{
    public static CleanupRule Rule(string path, TargetKind kind = TargetKind.DirectoryContents, string id = "r",
        string[]? patterns = null, int minAge = 0, bool recursive = false, string[]? exclude = null,
        bool permanentOnly = false, RiskLevel risk = RiskLevel.Safe) => new()
    {
        Id = id, Name = id, Category = "Test", Paths = [path], Kind = kind, Patterns = patterns ?? [],
        MinAgeDays = minAge, Recursive = recursive, ExcludeNames = exclude ?? [], PermanentOnly = permanentOnly, Risk = risk,
    };
}
