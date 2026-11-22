using DiskDoktor.App.ViewModels;
using DiskDoktor.Core.Cleaning;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;
using Xunit;

namespace DiskDoktor.Tests;

public class CommandRuleTests
{
    [Fact]
    public void UserRuleFile_CannotDefineCommands()
    {
        var json = """[{"id":"x","name":"X","category":"c","kind":"Command","command":["rm","-rf","/"]}]""";
        var ex = Assert.Throws<RuleValidationException>(() => RuleLoader.Parse(json));
        Assert.Contains("yerleşik", ex.Message);
        Assert.Single(RuleLoader.Parse(json, allowCommands: true));
    }

    [Fact]
    public void BuiltInRules_IncludeShaderCachesAndSystemTools()
    {
        var ids = RuleLoader.LoadBuiltIn().Select(r => r.Id).ToHashSet();
        foreach (var id in new[] { "shader-cache-win", "shader-cache-linux", "win-previous-install", "win-recycle-bin",
                                   "linux-apt-cache", "linux-journal", "mac-brew", "docker-prune", "downloads-old" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void CommandRule_NeedsItsProgram_AndReportsSizeOfSizePaths()
    {
        using var sb = new Sandbox();
        sb.File("home/cache/a.deb", 300);
        var rule = new CleanupRule
        {
            Id = "c", Name = "c", Category = "c", Kind = TargetKind.Command, Command = ["tool", "clean"],
            Requires = "tool", SizePaths = ["~/cache"],
        };
        Assert.Empty(new Scanner(sb.Env).Scan([rule], null, CancellationToken.None).Findings);

        sb.Env.Programs["tool"] = "/usr/bin/tool";
        var f = Assert.Single(new Scanner(sb.Env).Scan([rule], null, CancellationToken.None).Findings);
        Assert.True(f.IsCommand);
        Assert.Equal(300, f.TotalSize);
    }

    [Fact]
    public void FilesAndContentsRules_OnSameRoot_BothRun_WithoutDuplicateItems()
    {
        using var sb = new Sandbox();
        sb.File("home/Downloads/setup.exe", 50, ageDays: 200);
        sb.File("home/Downloads/old.pdf", 20, ageDays: 200);
        var installers = R.Rule("~/Downloads", TargetKind.Files, id: "inst", patterns: ["*.exe"]);
        var old = R.Rule("~/Downloads", id: "old", minAge: 90);
        var res = new Scanner(sb.Env).Scan([installers, old], null, CancellationToken.None);
        Assert.Equal(["setup.exe"], res.Findings.Single(f => f.Rule.Id == "inst").Items.Select(i => Path.GetFileName(i.Path)));
        Assert.Equal(["old.pdf"], res.Findings.Single(f => f.Rule.Id == "old").Items.Select(i => Path.GetFileName(i.Path)));
    }
}

public class DiscoveryTests
{
    private static IReadOnlyList<Finding> Discover(Sandbox sb, long large = 1000, long dup = 100) =>
        new DiscoveryScanner(sb.Env, null, new DiscoveryOptions { LargeFileBytes = large, DuplicateMinBytes = dup })
            .Discover([], CancellationToken.None);

    [Fact]
    public void FindsBuildOutput_OnlyNextToProjectFiles()
    {
        using var sb = new Sandbox();
        sb.File("home/code/web/package.json");
        sb.File("home/code/web/node_modules/react/index.js", 200);
        sb.File("home/code/notaproject/node_modules/x.js", 200);     // no package.json: leave alone
        sb.File("home/code/api/Api.csproj");
        sb.File("home/code/api/bin/Debug/api.dll", 300);
        sb.File("home/code/api/obj/project.assets.json", 100);
        sb.File("home/code/game/project.godot");
        sb.File("home/code/game/.godot/imported/a.ctex", 50);

        var f = Discover(sb).Single(x => x.Rule.Id == "discover-artifacts");
        var names = f.Items.Select(i => Path.GetRelativePath(sb.Env.HomeDirectory, i.Path).Replace('\\', '/')).Order().ToList();
        Assert.Equal(["code/api/bin", "code/api/obj", "code/game/.godot", "code/web/node_modules"], names);
        Assert.All(f.Items, i => Assert.NotNull(i.Note));
    }

    [Fact]
    public void DoesNotWalkIntoHiddenFoldersOrExcludedRoots()
    {
        using var sb = new Sandbox();
        sb.File("home/.vscode/extensions/ext/package.json");
        sb.File("home/.vscode/extensions/ext/node_modules/a.js", 500);   // old bug: extension deps counted
        sb.File("home/big/excluded/huge.bin", 5000);
        var excluded = new[] { Path.Combine(sb.Env.HomeDirectory, "big", "excluded") };
        var found = new DiscoveryScanner(sb.Env, null, new DiscoveryOptions { LargeFileBytes = 1000 }).Discover(excluded, CancellationToken.None);
        Assert.Empty(found);
    }

    [Fact]
    public void LargeFiles_And_Duplicates_KeepTheOldestCopy()
    {
        using var sb = new Sandbox();
        var orig = sb.File("home/Videos/film.mkv", 2000, ageDays: 30);
        var copy = sb.File("home/Desktop/film (1).mkv", 2000, ageDays: 1);
        File.WriteAllBytes(orig, Enumerable.Repeat((byte)7, 2000).ToArray());
        File.WriteAllBytes(copy, Enumerable.Repeat((byte)7, 2000).ToArray());
        File.SetLastWriteTimeUtc(orig, DateTime.UtcNow.AddDays(-30));
        var other = sb.File("home/Videos/other.mkv", 2000);       // same size, different content
        File.WriteAllBytes(other, Enumerable.Repeat((byte)9, 2000).ToArray());

        var found = Discover(sb);
        var dup = Assert.Single(found.Single(f => f.Rule.Id == "discover-duplicates").Items);
        Assert.Equal(copy, dup.Path);
        Assert.Contains(orig, dup.Note);
        var large = found.Single(f => f.Rule.Id == "discover-large").Items.Select(i => i.Path).ToList();
        Assert.Contains(orig, large);
        Assert.Contains(other, large);
        Assert.DoesNotContain(copy, large);                      // listed once, under duplicates
    }
}

public class UserFileGuardTests
{
    [Fact]
    public void PersonalFolders_NeedAnExplicitRuleFlag()
    {
        using var sb = new Sandbox();
        var docs = sb.Dir("home/Documents");
        sb.Env.Folders[Environment.SpecialFolder.MyDocuments] = docs;
        var g = new PathGuard(sb.Env);
        var file = Path.Combine(docs, "big.iso");
        Assert.NotNull(g.Check(file, sb.Env.HomeDirectory));
        Assert.Null(g.Check(file, sb.Env.HomeDirectory, allowUserFiles: true));
        Assert.NotNull(g.Check(docs, sb.Env.HomeDirectory, allowUserFiles: true));   // the folder itself, never
    }
}

public class ElevationTests
{
    private sealed class FakeElevator(string? error) : IElevator
    {
        public int Calls { get; private set; }
        public AdminRequest? Seen { get; private set; }
        public bool IsAvailable => true;

        public Task<string?> RunHelperAsync(string requestFile, string resultFile, CancellationToken ct)
        {
            Calls++;
            Seen = System.Text.Json.JsonSerializer.Deserialize<AdminRequest>(File.ReadAllText(requestFile));
            if (error is null)
                File.WriteAllText(resultFile, System.Text.Json.JsonSerializer.Serialize(new ResultDto
                {
                    Items = Seen!.Tasks.SelectMany(t => t.Paths).Select(p => new ItemResult(p, "adm", 10, 10, ItemOutcome.Deleted, null)).ToList(),
                }));
            return Task.FromResult(error);
        }
    }

    private static (Finding Admin, Finding Normal, Sandbox Sb) Setup()
    {
        var sb = new Sandbox();
        sb.File("home/sys/a", 10);
        sb.File("home/usr/b", 5);
        var adminRule = new CleanupRule { Id = "adm", Name = "adm", Category = "c", Paths = ["~/sys"], RequiresAdmin = true };
        var res = new Scanner(sb.Env).Scan([adminRule, R.Rule("~/usr", id: "n")], null, CancellationToken.None);
        return (res.Findings.Single(f => f.Rule.Id == "adm"), res.Findings.Single(f => f.Rule.Id == "n"), sb);
    }

    [Fact]
    public void AdminItems_GoThroughOneElevatedRun_OthersLocally()
    {
        var (admin, normal, sb) = Setup();
        using var _ = sb;
        var elev = new FakeElevator(null);
        var d = new SafeDeleter(sb.Env, new FreedesktopTrash(Path.Combine(sb.Root, "Trash")), null, elev);

        var r = d.Clean([new(admin, admin.Items), new(normal, normal.Items)], DeleteMode.Permanent, null, CancellationToken.None);

        Assert.Equal(1, elev.Calls);
        Assert.Equal("adm", elev.Seen!.Tasks.Single().RuleId);
        Assert.Equal(15, r.BytesFreed);
        Assert.True(File.Exists(Path.Combine(sb.Root, "home/sys/a")), "the fake helper deletes nothing itself");
        Assert.False(File.Exists(Path.Combine(sb.Root, "home/usr/b")));
    }

    [Fact]
    public void RefusedElevation_MarksAdminItemsFailed()
    {
        var (admin, _, sb) = Setup();
        using var _s = sb;
        var d = new SafeDeleter(sb.Env, new FreedesktopTrash(Path.Combine(sb.Root, "Trash")), null, new FakeElevator("Yönetici izni verilmedi."));
        var r = d.Clean([new(admin, admin.Items)], DeleteMode.Permanent, null, CancellationToken.None);
        var item = Assert.Single(r.Items);
        Assert.Equal(ItemOutcome.Failed, item.Outcome);
        Assert.Contains("izni", item.Error);
    }

    [Fact]
    public void Helper_RejectsUnknownRulesAndForeignRoots()
    {
        using var sb = new Sandbox(OsKind.Linux);
        var req = new AdminRequest(
        [
            new AdminTask("not-a-rule", "/etc", ["/etc/passwd"]),
            new AdminTask("linux-thumbnails", sb.Env.HomeDirectory, []),        // exists but is not an admin rule
            new AdminTask("linux-old-logs", "/etc", ["/etc/shadow"]),          // admin rule, wrong root
        ]);
        var built = AdminHelper.BuildRequests(sb.Env, req, out var rejected);
        Assert.Empty(built);
        Assert.Equal(3, rejected.Count);
        Assert.All(rejected, r => Assert.Equal(ItemOutcome.Skipped, r.Outcome));
    }

    [Fact]
    public void LocalCommandRule_Runs()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sb = new Sandbox();
        var marker = sb.File("home/marker", 1);
        sb.Env.Programs["sh"] = "/bin/sh";
        var rule = new CleanupRule
        {
            Id = "cmd", Name = "cmd", Category = "c", Kind = TargetKind.Command, Command = ["sh", "-c", $"rm '{marker}'"],
        };
        var f = new Finding { Rule = rule, Root = "", Items = [] };
        var r = new SafeDeleter(sb.Env, new FreedesktopTrash(Path.Combine(sb.Root, "T")), null).Clean([new(f, [])], DeleteMode.Trash, null, CancellationToken.None);
        Assert.Equal(ItemOutcome.Deleted, r.Items.Single().Outcome);
        Assert.False(File.Exists(marker));
    }
}

public class PickListTests
{
    private static FindingViewModel Make(bool itemsOn, Action? changed = null)
    {
        var rule = new CleanupRule { Id = "l", Name = "l", Category = "c", Paths = ["~"], DefaultSelected = false, ItemsSelectedByDefault = itemsOn };
        var f = new Finding
        {
            Rule = rule, Root = "/h",
            Items = [new ScanItem("/h/a", 100, 1, DateTime.UtcNow, false), new ScanItem("/h/b", 50, 1, DateTime.UtcNow, false)],
        };
        return new FindingViewModel(f, changed ?? (() => { }));
    }

    [Fact]
    public void PickingAnItem_SelectsTheFinding_WithOnlyThatItem()
    {
        var f = Make(itemsOn: false);
        f.Items[1].IsSelected = true;
        Assert.True(f.IsSelected);
        Assert.Equal(50, f.SelectedSize);
        Assert.Equal(["/h/b"], f.SelectedItems.Select(i => i.Path));
        Assert.True(f.HasPartialSelection);
    }

    [Fact]
    public void TickingAnEmptyPickList_TakesEverything_AndUntickingAllItemsClearsIt()
    {
        var f = Make(itemsOn: false);
        f.IsSelected = true;
        Assert.Equal(150, f.SelectedSize);
        f.Items[0].IsSelected = false;
        f.Items[1].IsSelected = false;
        Assert.False(f.IsSelected);
        Assert.Equal(0, f.SelectedSize);
    }
}
