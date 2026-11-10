using StormsBackupSystem.Core;
using Xunit;

namespace StormsBackupSystem.Tests;

public class ExcludeMatcherTests
{
    [Theory]
    [InlineData("cache", true)]
    [InlineData("resources/cache", true)]
    [InlineData("CACHE", true)]
    [InlineData("cache.cfg", false)]
    [InlineData("server.log", true)]
    [InlineData("logs/server.log", true)]
    [InlineData("server.cfg", false)]
    [InlineData("resources/[local]/temp", true)]
    [InlineData("resources\\[local]\\temp", true)]
    [InlineData("resources/[local]/temp2", false)]
    [InlineData("a/b/node_modules", true)]
    public void MatchesNamesWildcardsAndPaths(string path, bool excluded)
    {
        var matcher = new ExcludeMatcher(new[] { "# comment", "cache", "*.log", "resources/[local]/temp", "**/node_modules", "" });
        Assert.Equal(excluded, matcher.IsExcluded(path));
    }

    [Fact]
    public void EmptyRulesExcludeNothing()
    {
        Assert.False(new ExcludeMatcher(Array.Empty<string>()).IsExcluded("anything/at/all.txt"));
    }
}
