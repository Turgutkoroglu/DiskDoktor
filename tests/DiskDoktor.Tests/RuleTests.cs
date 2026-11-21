using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Rules;
using Xunit;

namespace DiskDoktor.Tests;

public class RuleTests
{
    [Fact]
    public void BuiltInRules_AreValid_AndCoverEveryOs()
    {
        var rules = RuleLoader.LoadBuiltIn();
        Assert.NotEmpty(rules);
        foreach (var os in Enum.GetValues<OsKind>())
            Assert.True(rules.Count(r => r.AppliesTo(os)) >= 5, $"{os} için yeterli kural yok");
    }

    [Theory]
    [InlineData("""[{"id":"a","name":"A","category":"c","paths":["~/x"]},{"id":"A","name":"B","category":"c","paths":["~/y"]}]""", "Yinelenen")]
    [InlineData("""[{"id":"a","name":"A","category":"c","paths":[]}]""", "en az bir yol")]
    [InlineData("""[{"id":"a","name":"A","category":"c","paths":["~/../etc"]}]""", "..")]
    [InlineData("""[{"id":"a","name":"A","category":"c","paths":["~/x"],"kind":"Files"}]""", "desen")]
    [InlineData("""[{"id":"a","name":"A","category":"c","paths":["~/x"],"minAgeDays":-1}]""", "negatif")]
    [InlineData("""not json""", "okunamadı")]
    public void InvalidRules_AreRejected(string json, string expected)
    {
        var ex = Assert.Throws<RuleValidationException>(() => RuleLoader.Parse(json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void UserRules_OverrideById_AndBrokenFileIsIgnored()
    {
        using var sb = new Sandbox(OsKind.Linux);
        sb.Env.Vars["XDG_CONFIG_HOME"] = sb.Dir("cfg");
        var file = Path.Combine(sb.Root, "cfg", "DiskDoktor", "rules.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        File.WriteAllText(file, """[{"id":"linux-thumbnails","name":"Benim","category":"X","paths":["~/t"],"platforms":["Linux"]}]""");
        var rules = RuleLoader.LoadAll(sb.Env, out var warning);
        Assert.Null(warning);
        Assert.Equal("Benim", rules.Single(r => r.Id == "linux-thumbnails").Name);
        Assert.DoesNotContain(rules, r => r.Id == "win-user-temp");

        File.WriteAllText(file, "{bozuk");
        rules = RuleLoader.LoadAll(sb.Env, out warning);
        Assert.NotNull(warning);
        Assert.Contains(rules, r => r.Id == "linux-thumbnails");
    }
}

public class PathResolverTests
{
    [Fact]
    public void ExpandsHomeTempAndXdgTokens()
    {
        using var sb = new Sandbox(OsKind.Linux);
        var r = new PathResolver(sb.Env);
        Assert.Equal(Path.Combine(sb.Env.HomeDirectory, "a"), r.ExpandTokens("~/a"));
        Assert.Equal(Path.Combine(sb.Env.HomeDirectory, ".cache", "x"), r.ExpandTokens("{XDG_CACHE_HOME}/x"));
        sb.Env.Vars["XDG_CACHE_HOME"] = "/custom/cache";
        Assert.Equal("/custom/cache/x", r.ExpandTokens("{XDG_CACHE_HOME}/x"));
        Assert.StartsWith(sb.Env.TempDirectory.TrimEnd('/'), r.ExpandTokens("{TEMP}/y"));
    }

    [Fact]
    public void WindowsOnlyTokens_YieldNothingElsewhere()
    {
        using var sb = new Sandbox(OsKind.Linux);
        var r = new PathResolver(sb.Env);
        Assert.Null(r.ExpandTokens("{LOCALAPPDATA}/Temp"));
        Assert.Null(r.ExpandTokens("{NOPE}/x"));
        Assert.Empty(r.Resolve("{WINDIR}/Temp"));
    }

    [Fact]
    public void WildcardSegment_MatchesExistingDirectoriesOnly()
    {
        using var sb = new Sandbox();
        sb.Dir("home/prof/p1/cache2");
        sb.Dir("home/prof/p2/cache2");
        sb.Dir("home/prof/p3");
        var found = new PathResolver(sb.Env).Resolve("~/prof/*/cache2").OrderBy(x => x).ToList();
        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.EndsWith("cache2", f));
        Assert.Empty(new PathResolver(sb.Env).Resolve("~/missing/*/cache2"));
    }
}
