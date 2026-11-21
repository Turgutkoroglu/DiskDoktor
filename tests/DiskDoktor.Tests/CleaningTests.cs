using DiskDoktor.Core.Cleaning;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;
using Xunit;

namespace DiskDoktor.Tests;

public class PathGuardTests
{
    [Fact]
    public void AllowsOnlyStrictlyInsideRoot()
    {
        using var sb = new Sandbox();
        var g = new PathGuard(sb.Env);
        var root = sb.Dir("home/cache");
        Assert.Null(g.Check(Path.Combine(root, "x"), root));
        Assert.NotNull(g.Check(root, root));                                     // the root itself
        Assert.NotNull(g.Check(Path.Combine(sb.Root, "home", "other"), root));    // sibling
        Assert.NotNull(g.Check(root + "-evil" + Path.DirectorySeparatorChar + "x", root)); // prefix trick
        Assert.NotNull(g.Check(Path.Combine(root, "..", "x"), root));             // traversal
        Assert.NotNull(g.Check("relative/x", root));
    }

    [Fact]
    public void RefusesHomeSystemAndAppFolders()
    {
        using var sb = new Sandbox();
        var g = new PathGuard(sb.Env);
        var fsRoot = Path.GetPathRoot(sb.Root)!;
        Assert.NotNull(g.Check(sb.Env.HomeDirectory, fsRoot));
        Assert.NotNull(g.Check(Path.Combine(sb.Env.AppBaseDirectory, "DiskDoktor.dll"), sb.Root));
        if (!OperatingSystem.IsWindows())
        {
            Assert.NotNull(g.Check("/usr/lib/x", "/"));
            Assert.NotNull(g.Check("/etc", "/"));
        }
    }

    [Fact]
    public void ProtectedTreeFolders_AreRespected()
    {
        using var sb = new Sandbox();
        var docs = sb.Dir("home/Documents");
        sb.Env.Folders[Environment.SpecialFolder.MyDocuments] = docs;
        var g = new PathGuard(sb.Env);
        Assert.NotNull(g.Check(Path.Combine(docs, "cv.pdf"), sb.Env.HomeDirectory));
    }

    [Fact]
    public void RefusesTargetsBehindASymlinkedFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sb = new Sandbox();
        var root = sb.Dir("home/cache");
        var outside = sb.Dir("outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        Assert.NotNull(new PathGuard(sb.Env).Check(Path.Combine(root, "link", "file"), root));
    }
}

public class SafeDeleterTests
{
    private sealed class RecordingTrash : ITrash
    {
        public List<string> Moved { get; } = [];
        public bool IsAvailable => true;
        public void MoveToTrash(string path) { Moved.Add(path); if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); }
    }

    private static (SafeDeleter Deleter, RecordingTrash Trash) Make(Sandbox sb)
    {
        var t = new RecordingTrash();
        return (new SafeDeleter(sb.Env, t, Path.Combine(sb.Root, "logs")), t);
    }

    private static Finding Find(Sandbox sb, CleanupRule rule) =>
        new Scanner(sb.Env).Scan([rule], null, CancellationToken.None).Findings.Single();

    [Fact]
    public void PermanentDelete_RemovesEverything_AndCountsBytes()
    {
        using var sb = new Sandbox();
        sb.File("home/c/a", 100);
        sb.File("home/c/d/b", 50);
        sb.File("home/c/d/e/c", 25);
        var f = Find(sb, R.Rule("~/c"));
        var (d, _) = Make(sb);

        var r = d.Clean([new CleanRequest(f, f.Items)], DeleteMode.Permanent, null, CancellationToken.None);

        Assert.Equal(175, r.BytesFreed);
        Assert.Equal(2, r.SucceededCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Root));
        Assert.True(Directory.Exists(f.Root), "root folder itself must stay");
        Assert.NotNull(r.LogFile);
        Assert.True(File.Exists(r.LogFile));
    }

    [Fact]
    public void TrashMode_UsesTrash_ButPermanentOnlyRulesBypassIt()
    {
        using var sb = new Sandbox();
        sb.File("home/c/a", 10);
        sb.File("home/t/b", 10);
        var normal = Find(sb, R.Rule("~/c", id: "c"));
        var perm = Find(sb, R.Rule("~/t", id: "t", permanentOnly: true));
        var (d, trash) = Make(sb);

        var r = d.Clean([new(normal, normal.Items), new(perm, perm.Items)], DeleteMode.Trash, null, CancellationToken.None);

        Assert.Equal([Path.Combine(normal.Root, "a")], trash.Moved);
        Assert.Contains(r.Items, i => i.Outcome == ItemOutcome.Trashed);
        Assert.Contains(r.Items, i => i.Outcome == ItemOutcome.Deleted);
    }

    [Fact]
    public void ForgedItemOutsideRoot_IsSkipped_NotDeleted()
    {
        using var sb = new Sandbox();
        var victim = sb.File("home/important.txt", 10);
        sb.File("home/c/a", 1);
        var f = Find(sb, R.Rule("~/c"));
        var forged = new ScanItem(victim, 10, 1, DateTime.UtcNow, false);
        var (d, _) = Make(sb);

        var r = d.Clean([new(f, [forged])], DeleteMode.Permanent, null, CancellationToken.None);

        Assert.Equal(ItemOutcome.Skipped, r.Items.Single().Outcome);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void SymlinkInsideDeletedFolder_RemovesLinkOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sb = new Sandbox();
        var precious = sb.File("precious/keep.bin", 100);
        sb.File("home/c/d/x", 1);
        Directory.CreateSymbolicLink(Path.Combine(sb.Root, "home/c/d/link"), Path.GetDirectoryName(precious)!);
        var f = Find(sb, R.Rule("~/c"));
        var (d, _) = Make(sb);

        d.Clean([new(f, f.Items)], DeleteMode.Permanent, null, CancellationToken.None);

        Assert.True(File.Exists(precious));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "home/c/d")));
    }

    [Fact]
    public void UndeletableChild_ReportsPartial_AndKeepsGoing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        using var sb = new Sandbox();
        sb.File("home/c/d/locked/x", 5);
        sb.File("home/c/d/free", 7);
        sb.File("home/c/z", 3);
        var locked = Path.Combine(sb.Root, "home/c/d/locked");
        var f = Find(sb, R.Rule("~/c"));
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (d, _) = Make(sb);
            var r = d.Clean([new(f, f.Items)], DeleteMode.Permanent, null, CancellationToken.None);
            Assert.Equal(ItemOutcome.Partial, r.Items.Single(i => i.Path.EndsWith("d")).Outcome);
            Assert.Equal(ItemOutcome.Deleted, r.Items.Single(i => i.Path.EndsWith("z")).Outcome);
            Assert.Equal(10, r.BytesFreed);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void VanishedItem_IsSkippedGracefully()
    {
        using var sb = new Sandbox();
        var a = sb.File("home/c/a", 5);
        var f = Find(sb, R.Rule("~/c"));
        File.Delete(a);
        var (d, _) = Make(sb);
        var r = d.Clean([new(f, f.Items)], DeleteMode.Permanent, null, CancellationToken.None);
        Assert.Equal(ItemOutcome.Skipped, r.Items.Single().Outcome);
    }

    [Fact]
    public void Cancellation_StopsBeforeRemainingItems()
    {
        using var sb = new Sandbox();
        sb.File("home/c/a"); sb.File("home/c/b");
        var f = Find(sb, R.Rule("~/c"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (d, _) = Make(sb);
        var r = d.Clean([new(f, f.Items)], DeleteMode.Permanent, null, cts.Token);
        Assert.True(r.Cancelled);
        Assert.Empty(r.Items);
        Assert.Equal(2, Directory.EnumerateFiles(f.Root).Count());
    }
}

public class FreedesktopTrashTests
{
    [Fact]
    public void MovesIntoFiles_WritesTrashInfo_AndAvoidsNameClashes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sb = new Sandbox(OsKind.Linux);
        var trashDir = Path.Combine(sb.Root, "Trash");
        var t = new FreedesktopTrash(trashDir);
        var a1 = sb.File("home/x/şey dosya.txt", 3);
        t.MoveToTrash(a1);
        var a2 = sb.File("home/x/şey dosya.txt", 4);
        t.MoveToTrash(a2);

        Assert.False(File.Exists(a1));
        Assert.True(File.Exists(Path.Combine(trashDir, "files", "şey dosya.txt")));
        Assert.True(File.Exists(Path.Combine(trashDir, "files", "şey dosya.txt.2")));
        var info = File.ReadAllText(Path.Combine(trashDir, "info", "şey dosya.txt.trashinfo"));
        Assert.StartsWith("[Trash Info]\nPath=/", info);
        Assert.Contains("%C5%9Fey%20dosya.txt", info);
        Assert.Contains("DeletionDate=", info);
    }
}
