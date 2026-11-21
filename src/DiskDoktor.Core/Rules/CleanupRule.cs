using DiskDoktor.Core.Platform;

namespace DiskDoktor.Core.Rules;

public enum RiskLevel { Safe, Caution, Risky }

public enum TargetKind
{
    /// <summary>Every top-level entry inside the root is a candidate (file or whole sub-folder).</summary>
    DirectoryContents,
    /// <summary>Only files matching <see cref="CleanupRule.Patterns"/> are candidates.</summary>
    Files,
}

public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string Description { get; init; } = "";
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public OsKind[] Platforms { get; init; } = [];
    /// <summary>Root folders. Tokens like {TEMP}, {LOCALAPPDATA}, {XDG_CACHE_HOME}, ~ and a '*' segment are expanded.</summary>
    public string[] Paths { get; init; } = [];
    public TargetKind Kind { get; init; } = TargetKind.DirectoryContents;
    /// <summary>File name patterns (* and ?). Empty means everything.</summary>
    public string[] Patterns { get; init; } = [];
    public bool Recursive { get; init; }
    /// <summary>Only items not modified for this many days are candidates.</summary>
    public int MinAgeDays { get; init; }
    /// <summary>Entry names that are never touched (e.g. lock files, our own extraction folder).</summary>
    public string[] ExcludeNames { get; init; } = [];
    /// <summary>The OS usually denies access without elevation; failures are expected and reported.</summary>
    public bool RequiresAdmin { get; init; }
    /// <summary>Moving to trash makes no sense (e.g. the trash itself), so deletion is always permanent.</summary>
    public bool PermanentOnly { get; init; }
    /// <summary>Selected by default after a scan.</summary>
    public bool DefaultSelected { get; init; } = true;

    public bool AppliesTo(OsKind os) => Platforms.Length == 0 || Platforms.Contains(os);
}
