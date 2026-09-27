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
/// Symlink defense (Rick's PR #122 review item 3): Python's `Path.resolve(strict=True)`
/// canonicalizes symlinks in EVERY ancestor of the resolved path, not just the final leaf, before
/// the `relative_to()` containment check -- so a symlinked directory planted inside `assets/`
/// that points back out of the pack is caught even for a request several segments below that
/// symlink. An earlier draft here only resolved the final leaf entry's own link target, which
/// missed exactly that case: a symlinked directory two levels above a real file. This now walks
/// EVERY path component from the assets root down to the leaf (<see cref="ResolveRealPath"/>),
/// resolving each one's link target (<see cref="FileSystemInfo.ResolveLinkTarget"/>) if it is
/// itself a symlink or junction, before combining the next segment onto the (possibly
/// substituted) real directory -- matching `Path.resolve()`'s own component-by-component
/// semantics. The assets root itself is resolved the same way, so a symlinked pack directory (or
/// a symlinked `personas/` itself) doesn't defeat the final containment check either. The
/// resolved candidate must still land under the resolved assets root's own real path.
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

        var assetsRoot = ResolveRealPath(Path.GetFullPath(persona.AssetsDir));
        if (assetsRoot is null)
        {
            return null;
        }

        var candidate = assetsRoot;
        foreach (var segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        var resolved = ResolveRealPath(candidate);
        if (resolved is null || !IsUnderRoot(resolved, assetsRoot))
        {
            return null;
        }

        if (!File.Exists(resolved))
        {
            return null;
        }

        return resolved;
    }

    /// <summary>
    /// Canonicalizes <paramref name="path"/> the way Python's `Path.resolve()` does: walks every
    /// path component from the root down, and whenever a component is itself a symlink or
    /// junction, substitutes its (fully resolved) link target before combining the NEXT
    /// component onto it. Returns null on a broken/unreadable link or an access error, rather
    /// than throwing -- callers treat that the same as "does not resolve to anything under the
    /// assets root".
    /// </summary>
    private static string? ResolveRealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var relative = full[root.Length..];
            var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            var current = root.Length > 0 ? root.TrimEnd(Path.DirectorySeparatorChar) : string.Empty;
            if (current.Length == 0)
            {
                current = Path.DirectorySeparatorChar.ToString();
            }

            foreach (var part in parts)
            {
                current = Path.Combine(current, part);
                var linkTarget = TryResolveLinkTarget(current);
                if (linkTarget is not null)
                {
                    current = linkTarget;
                }
            }

            return current;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// If <paramref name="path"/> itself is a symlink or junction (file or directory), returns its
    /// fully-resolved final target's full path; otherwise null (including when nothing exists at
    /// <paramref name="path"/> yet -- a not-yet-existing intermediate segment is not a link).
    /// </summary>
    private static string? TryResolveLinkTarget(string path)
    {
        FileSystemInfo? info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : File.Exists(path) ? new FileInfo(path) : null;
        if (info is null)
        {
            return null;
        }

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? null : Path.GetFullPath(target.FullName);
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
