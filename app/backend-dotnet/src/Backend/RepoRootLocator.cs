namespace Backend;

/// <summary>
/// Finds the repo root so this backend can find the same shared, backend-agnostic assets the
/// Python backend uses without duplicating them under app/backend-dotnet: personas/ (this repo's
/// persona packs), app/backend/config.yaml (the shared model/business-rules/etc. config), and
/// app/backend/static (the one frontend build both backends serve). Mirrors
/// tests/conformance/src/Conformance.Harness/RepoPaths.cs's own walk-up search, but uses
/// "personas" + "azure.yaml" as the two markers instead of "app" + "azure.yaml" + ".git" -- a
/// production container copies personas/ and azure.yaml won't exist there, but by then
/// PERSONAS_DIR/CONFIG_PATH/STATIC_FILES_DIR env vars are expected to be set explicitly (see
/// docs/dotnet_mapping.md), so this walk-up is a dev/CI convenience, not a production requirement.
/// </summary>
internal static class RepoRootLocator
{
    /// <exception cref="InvalidOperationException">No ancestor directory of <paramref name="startDirectory"/>
    /// (default: the running assembly's directory) has both a "personas" subdirectory and an
    /// "azure.yaml" file.</exception>
    public static string Find(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var personas = Path.Combine(dir.FullName, "personas");
            var azureYaml = Path.Combine(dir.FullName, "azure.yaml");
            if (Directory.Exists(personas) && File.Exists(azureYaml))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repo root (a directory containing both 'personas/' and " +
            $"'azure.yaml') above '{startDirectory ?? AppContext.BaseDirectory}'. Set PERSONAS_DIR, " +
            "CONFIG_PATH, and STATIC_FILES_DIR explicitly (e.g. in a container) instead of relying " +
            "on repo-tree discovery.");
    }
}
