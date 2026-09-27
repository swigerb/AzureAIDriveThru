namespace Backend.Personas;

/// <summary>
/// Port of app/backend/app.py's `_resolve_persona_asset_path` (Rick's PR #102 review item 5):
/// resolves the tail of `/personas/{id}/assets/{assetPath}` to a real file under
/// <see cref="Persona.AssetsDir"/>, or null if it doesn't exist or the path doesn't stay under
/// that directory.
///
/// Traversal defense is structural, not string-matching, exactly like the Python original: split
/// on path separators, reject any segment that is empty, `.`, `..`, or looks like a Windows drive
/// letter (contains `:`), THEN join those pre-validated segments onto the assets directory one at
/// a time. This avoids the classic path-combining pitfall where combining with an absolute
/// right-hand segment silently discards the base -- since no individual validated segment can
/// itself be absolute, that substitution can never happen here.
///
/// Symlink defense: Python's `Path.resolve(strict=True)` canonicalizes symlinks anywhere in the
/// resolved path, including ancestor directories, before the final `relative_to()` containment
/// check. .NET has no single-call equivalent; this resolves the FINAL leaf entry's own link target
/// (if it is itself a symlink or junction) and re-checks containment against that target. This is
/// a narrower guarantee than Python's (it does not walk symlinked ancestor directories), but no
/// symlink scenario is pinned by conformance today -- the traversal test rows are all string-based
/// `../` variants, which the segment-rejection step above already catches on its own.
/// </summary>
public static class PersonaAssetResolver
{
    public static string? Resolve(Persona persona, string requestedPath)
    {
        if (string.IsNullOrEmpty(requestedPath) || requestedPath.Contains('\0'))
        {
            return null;
        }

        var segments = requestedPath.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains(':'))
            {
                return null;
            }
        }

        if (Path.IsPathRooted(requestedPath))
        {
            return null;
        }

        var assetsRoot = Path.GetFullPath(persona.AssetsDir);
        var candidate = assetsRoot;
        foreach (var segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
        }
        candidate = Path.GetFullPath(candidate);

        if (!IsUnderRoot(candidate, assetsRoot))
        {
            return null;
        }

        if (!File.Exists(candidate))
        {
            return null;
        }

        // Best-effort symlink defense on the final leaf entry (see class doc comment).
        var linkTarget = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
        if (linkTarget is not null)
        {
            var resolvedTarget = Path.GetFullPath(linkTarget.FullName);
            if (!IsUnderRoot(resolvedTarget, assetsRoot) || !File.Exists(resolvedTarget))
            {
                return null;
            }
            return resolvedTarget;
        }

        return candidate;
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.Ordinal))
        {
            return true;
        }
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }
}
