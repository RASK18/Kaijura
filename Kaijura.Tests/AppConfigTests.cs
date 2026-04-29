using Kaijura.App.Models;

namespace Kaijura.Tests;

public sealed class AppConfigTests
{
    [Fact]
    public void SeedIgnoredCommentAuthorsWithUserNameAddsUserOnce()
    {
        var config = new AppConfig
        {
            UserName = " rafa ",
            IgnoredCommentAuthors = ["ci-bot", "CI-BOT"]
        };

        var changed = config.SeedIgnoredCommentAuthorsWithUserName();
        var changedAgain = config.SeedIgnoredCommentAuthorsWithUserName();

        Assert.True(changed);
        Assert.False(changedAgain);
        Assert.True(config.IgnoredCommentAuthorsSeeded);
        Assert.Equal(["ci-bot", "rafa"], config.IgnoredCommentAuthors);
    }

    [Fact]
    public void SeedIgnoredCommentAuthorsWithUserNameDoesNotReaddAfterItWasRemoved()
    {
        var config = new AppConfig
        {
            UserName = "rafa",
            IgnoredCommentAuthorsSeeded = true
        };

        var changed = config.SeedIgnoredCommentAuthorsWithUserName();

        Assert.False(changed);
        Assert.Empty(config.IgnoredCommentAuthors);
    }
}
