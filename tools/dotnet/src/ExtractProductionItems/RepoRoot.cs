namespace ExtractProductionItems;

/// <summary>
/// Dev/CI convenience: walks up from a starting directory looking for a folder that contains both
/// <c>app</c> and <c>azure.yaml</c> (and a <c>.git</c> entry of either kind -- a directory in a
/// normal clone, a file in a git worktree), the same repo-root marker
/// tests/conformance/src/Conformance.Harness/RepoPaths.cs uses. Reimplemented here (rather than
/// referenced) because tools/dotnet is an independent solution with no shared library project yet
/// (same duplication UpdateMenuSizes/RepoRoot.cs already accepted -- see docs/dotnet_tooling.md's
/// "Scaffold shape and why").
/// </summary>
public static class RepoRoot
{
    public static string Find(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(Path.Combine(dir.FullName, "app")) &&
                File.Exists(Path.Combine(dir.FullName, "azure.yaml")) &&
                (Directory.Exists(gitPath) || File.Exists(gitPath)))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repo root by walking up from '{startDirectory}' looking for a " +
            "folder containing 'app', 'azure.yaml', and a '.git' directory or file (worktrees use a '.git' file).");
    }
}
