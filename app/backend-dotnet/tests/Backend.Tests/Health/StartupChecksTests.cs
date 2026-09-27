using Backend.Health;

namespace Backend.Tests.Health;

public sealed class StartupChecksTests
{
    [Fact]
    public void AllPassed_FalseUntilEveryCheckPasses()
    {
        var checks = new StartupChecks();

        Assert.False(checks.AllPassed);
        checks.Pass("env_vars");
        Assert.False(checks.AllPassed);
        checks.Pass("personas_loaded");
        Assert.False(checks.AllPassed);
        checks.Pass("prompts_loaded");
        Assert.True(checks.AllPassed);
    }

    [Fact]
    public void Pass_UnknownCheckName_Throws()
    {
        var checks = new StartupChecks();

        Assert.Throws<ArgumentOutOfRangeException>(() => checks.Pass("not-a-real-check"));
    }
}
