using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;
using Xunit;

namespace DiskDoktor.Tests;

public class ScannerTests
{
    private static ScanResult Scan(Sandbox sb, params CleanupRule[] rules) =>
        new Scanner(sb.Env).Scan(rules, null, CancellationToken.None);

    [Fact]
    public void DirectoryContents_ListsTopLevelEntries_WithRecursiveSize()
    {
        using var sb = new Sandbox();
        sb.File("home/c/a.bin", 100);
        sb.File("home/c/sub/b.bin", 50);
        sb.File("home/c/sub/deep/c.bin", 25);

        var f = Assert.Single(Scan(sb, R.Rule("~/c")).Findings);
        Assert.Equal(2, f.Items.Count);
        Assert.Equal(175, f.TotalSize);
        Assert.Equal(3, f.FileCount);
        Assert.True(f.Items.Single(i => i.IsDirectory).Size == 75);
    }

    [Fact]
    public void MissingRoot_ProducesNoFinding()
    {
        using var sb = new Sandbox();
        Assert.Empty(Scan(sb, R.Rule("~/nope")).Findings);
    }

    [Fact]
    public void AgeFilter_SkipsRecentFiles_AndFoldersWithAnythingRecent()
    {
        using var sb = new Sandbox();
        sb.File("home/c/old.tmp", 10, ageDays: 30);
        sb.File("home/c/new.tmp", 10);
        sb.File("home/c/olddir/x", 10, ageDays: 30);
        sb.File("home/c/mixed/x", 10, ageDays: 30);
        sb.File("home/c/mixed/y", 10);

        var names = Scan(sb, R.Rule("~/c", minAge: 7)).Findings.Single().Items.Select(i => Path.GetFileName(i.Path)).Order();
        Assert.Equal(["old.tmp", "olddir"], names);
    }

    [Fact]
    public void FilesKind_MatchesPatterns_Recursively_AndHonoursExcludes()
    {
        using var sb = new Sandbox();
        sb.File("home/l/a.log");
        sb.File("home/l/b.txt");
        sb.File("home/l/x/c.log");
        sb.File("home/l/keep/d.log");

        var f = Scan(sb, R.Rule("~/l", TargetKind.Files, patterns: ["*.log"], recursive: true, exclude: ["keep"])).Findings.Single();
        Assert.Equal(["a.log", "c.log"], f.Items.Select(i => Path.GetFileName(i.Path)).Order());
    }

    [Fact]
    public void ExcludedNames_AreNeverListed()
    {
        using var sb = new Sandbox();
        sb.File("home/t/.net/bundle.dll");
        sb.File("home/t/junk");
        var f = Scan(sb, R.Rule("~/t", exclude: [".net"])).Findings.Single();
        Assert.Equal("junk", Path.GetFileName(Assert.Single(f.Items).Path));
    }

    [Fact]
    public void OverlappingRules_DoNotDoubleCount()
    {
        // Broad ~/.cache rule must not swallow the folder another rule owns (old bug: nested caches counted twice).
        using var sb = new Sandbox();
        sb.File("home/.cache/go-build/a", 100);
        sb.File("home/.cache/other/b", 10);
        var result = Scan(sb, R.Rule("~/.cache/go-build", id: "go"), R.Rule("~/.cache", id: "all"),
                          R.Rule("~/.cache/go-build", id: "dup"));
        Assert.Equal(110, result.TotalSize);
        Assert.Equal(100, result.Findings.Single(f => f.Rule.Id == "go").TotalSize);
        Assert.Equal(10, result.Findings.Single(f => f.Rule.Id == "all").TotalSize);
        Assert.DoesNotContain(result.Findings, f => f.Rule.Id == "dup");
    }

    [Fact]
    public void SymlinkedFolders_AreNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges there
        using var sb = new Sandbox();
        sb.File("precious/big.bin", 1000);
        sb.File("home/c/real.bin", 10);
        Directory.CreateSymbolicLink(Path.Combine(sb.Root, "home/c/link"), Path.Combine(sb.Root, "precious"));
        sb.File("home/c/sub/x", 1);
        Directory.CreateSymbolicLink(Path.Combine(sb.Root, "home/c/sub/inner"), Path.Combine(sb.Root, "precious"));

        var f = Scan(sb, R.Rule("~/c")).Findings.Single();
        Assert.Equal(11, f.TotalSize);
        Assert.DoesNotContain(f.Items, i => i.Path.EndsWith("link"));
    }

    [Fact]
    public void Cancellation_Throws()
    {
        using var sb = new Sandbox();
        sb.File("home/c/a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new Scanner(sb.Env).Scan([R.Rule("~/c")], null, cts.Token));
    }
}
