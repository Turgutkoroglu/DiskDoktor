using DiskDoktor.Core.Platform;

namespace DiskDoktor.Core.Rules;

public enum RiskLevel { Safe, Caution, Risky }

public enum TargetKind
{
    /// <summary>Every top-level entry inside the root is a candidate (file or whole sub-folder).</summary>
    DirectoryContents,
    /// <summary>Only files matching <see cref="CleanupRule.Patterns"/> are candidates.</summary>
    Files,
    /// <summary>A tool does the cleaning (apt-get clean, DISM, journalctl...). Built-in rules only.</summary>
    Command,
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
    /// <summary>Needs administrator/root. Cleaning goes through the elevated helper and is permanent.</summary>
    public bool RequiresAdmin { get; init; }
    /// <summary>Moving to trash makes no sense (e.g. the trash itself), so deletion is always permanent.</summary>
    public bool PermanentOnly { get; init; }
    /// <summary>Selected by default after a scan.</summary>
    public bool DefaultSelected { get; init; } = true;
    /// <summary>Individual items start selected when the finding is selected (false for "pick what you want" lists).</summary>
    public bool ItemsSelectedByDefault { get; init; } = true;
    /// <summary>May touch files inside Documents/Desktop/Pictures/... (personal-file discovery, Downloads).</summary>
    public bool AllowUserFiles { get; init; }

    /// <summary>Command kind: program and arguments, run without a shell.</summary>
    public string[] Command { get; init; } = [];
    /// <summary>Command kind: a built-in action instead of a program (e.g. "empty-recycle-bin").</summary>
    public string? Builtin { get; init; }
    /// <summary>Command kind: folders whose size shows what the command will free; the rule is hidden when none exists.</summary>
    public string[] SizePaths { get; init; } = [];
    /// <summary>Program that must be on PATH for the rule to apply (e.g. "apt-get", "docker").</summary>
    public string? Requires { get; init; }

    public bool AppliesTo(OsKind os) => Platforms.Length == 0 || Platforms.Contains(os);

    public string CommandText
    {
        get
        {
            if (Builtin is not null) return Builtin;
            // "sh -c <script>" / "powershell -Command <script>": show the tool, not the whole script.
            if (Command.Length > 2 && Command[^1].Length > 60) return string.Join(' ', Command.Take(Command.Length - 1)) + " …";
            return string.Join(' ', Command.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }
    }
}
