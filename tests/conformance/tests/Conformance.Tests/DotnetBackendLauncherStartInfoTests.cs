using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for issue #135: the C# backend launcher must never re-trigger a build at launch
/// time (that's what raced concurrent parallel fixtures against each other, MSB4018
/// GenerateDepsFile file-lock flake). <see cref="DotnetBackendLauncher.BuildStartInfo"/> and
/// <see cref="DotnetBackendLauncher.ResolveDllPath"/> are pure, process-free functions, so this
/// asserts the exact launch command/arguments directly -- no real process, no SDK required.
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class DotnetBackendLauncherStartInfoTests
{
    [Fact]
    public void BuildStartInfo_launches_dotnet_with_the_dll_path_and_nothing_else()
    {
        var dllPath = Path.Combine("C:", "fake", "bin", "Debug", "net11.0", "Backend.dll");

        var startInfo = DotnetBackendLauncher.BuildStartInfo(dllPath);

        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal($"\"{dllPath}\"", startInfo.Arguments);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("build")]
    [InlineData("restore")]
    public void BuildStartInfo_arguments_never_contain_a_build_step(string bannedVerb)
    {
        var dllPath = Path.Combine("C:", "fake", "bin", "Debug", "net11.0", "Backend.dll");

        var startInfo = DotnetBackendLauncher.BuildStartInfo(dllPath);

        // Split on whitespace so this can't accidentally match "build" appearing inside the dll
        // path itself -- only a standalone `dotnet <verb>` argument counts as a build step.
        var tokens = startInfo.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(bannedVerb, tokens);
    }

    [Fact]
    public void BuildStartInfo_working_directory_is_the_dlls_own_output_folder()
    {
        var dllPath = Path.Combine("C:", "fake", "bin", "Debug", "net11.0", "Backend.dll");

        var startInfo = DotnetBackendLauncher.BuildStartInfo(dllPath);

        Assert.Equal(Path.GetDirectoryName(dllPath), startInfo.WorkingDirectory);
    }

    [Fact]
    public void ResolveDllPath_is_the_default_sdk_bin_debug_tfm_layout_next_to_the_csproj()
    {
        var csprojPath = Path.Combine("C:", "repo", "app", "backend-dotnet", "src", "Backend", "Backend.csproj");

        var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);

        Assert.Equal(
            Path.Combine("C:", "repo", "app", "backend-dotnet", "src", "Backend", "bin", "Debug", "net11.0", "Backend.dll"),
            dllPath);
    }
}
