namespace Conformance.Harness;

/// <summary>Locates repo-relative paths the harness needs, independent of the test runner's cwd.</summary>
public static class RepoPaths
{
    /// <summary>
    /// Walks up from the test assembly's directory until it finds a folder containing both
    /// `app` and `azure.yaml` (a unique repo-root marker), plus a `.git` entry of either kind,
    /// which is the repo root regardless of whether tests run from `tests/conformance`, the repo
    /// root, or a `dotnet test` working directory in CI. `.git` is a *directory* in a normal
    /// clone but a plain *file* (containing a `gitdir: ...` pointer) inside a git worktree, so
    /// both are accepted rather than requiring `Directory.Exists`.
    /// </summary>
    public static string FindRepoRoot() => FindRepoRoot(AppContext.BaseDirectory);

    /// <summary>Testable overload: walks up from an arbitrary starting directory.</summary>
    public static string FindRepoRoot(string startDirectory)
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
            $"Could not locate the SonicAIDriveThru repo root by walking up from '{startDirectory}' " +
            "looking for a folder containing 'app', 'azure.yaml', and a '.git' directory or file (worktrees " +
            "use a '.git' file).");
    }

    public static string BackendDirectory(string repoRoot) => Path.Combine(repoRoot, "app", "backend");

    /// <summary>
    /// Issue #76: the persona-packs root both app/backend/persona_loader.py and
    /// app/backend-dotnet/src/Backend/Personas/PersonaCatalog.cs discover from by default (an
    /// explicit `PERSONAS_DIR` env var overrides this for the backend process itself, but the
    /// harness's own disk-discovery fallback -- <see cref="ConformancePersonas.DiscoverFromDisk()"/> --
    /// always looks here, matching the backends' own un-overridden default).
    /// </summary>
    public static string PersonasDirectory(string repoRoot) => Path.Combine(repoRoot, "personas");

    /// <summary>
    /// Rick's PR #102 review item 1: the TEST-ONLY, two-persona fixture pack
    /// app/backend/tests/test_persona_binding.py already uses (test-alpha/test-beta) --
    /// schema-valid, brand-neutral, self-contained. Reused here (rather than duplicating a second
    /// fixture pack under tests/conformance/testdata/) so the two-pack persona_mismatch
    /// conformance row can launch the backend with PERSONAS_DIR pointed here, without waiting on
    /// #78/#79 to land a real, user-facing second persona pack under personas/.
    /// </summary>
    public static string FixturePersonasDirectory(string repoRoot) =>
        Path.Combine(repoRoot, "app", "backend", "tests", "fixtures", "personas");

    public static string PythonExecutable(string repoRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(repoRoot, ".venv", "bin", "python");

    /// <summary>
    /// Issue #70: menu data now lives in the Sonic persona pack (personas/sonic/menu/menuItems.json),
    /// not app/frontend/src/data/ -- the frontend keeps its own copy for now (out of scope for #70;
    /// wiring the frontend to the pack is future work), but the backend and this conformance suite
    /// read the persona pack's copy, which is the new source of truth.
    /// </summary>
    public static string MenuItemsJsonPath(string repoRoot) =>
        Path.Combine(repoRoot, "personas", "sonic", "menu", "menuItems.json");

    /// <summary>
    /// Issue #9: the golden order-pricing/combo/Route-44 dataset ported from
    /// app/backend/tests/test_order_state*.py, test_tool_calling.py, and test_combo_orders.py,
    /// so both the S1-3 conformance scenarios and a future C# backend's own test suite (S4) can
    /// assert against the exact same cent-accurate cases from one shared file.
    /// </summary>
    public static string GoldenOrderPricingJsonPath(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "conformance", "testdata", "golden-order-pricing.json");

    /// <summary>
    /// Issue #39: the golden category/combo-slot-bucket table (every app/frontend/src/data/
    /// menuItems.json entry mapped to app/backend/menu_utils.py::infer_combo_component's
    /// sides/drinks/none bucket), so both the Python unit tests and this C# scenario suite assert
    /// against the exact same 60-item table from one shared file.
    /// </summary>
    public static string GoldenMenuCategoriesJsonPath(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "conformance", "testdata", "golden-menu-categories.json");

    /// <summary>
    /// Rick's PR #108 second review: per-pack smoke expectations move OUT of shared C# (which must
    /// carry no brand-specific literals -- e.g. no "sonic", no greeting text -- so a data-only pack
    /// PR like #111/#112 can add its own persona without touching this repo's shared test code)
    /// and into one small JSON file per persona, alongside the pack itself rather than inside it
    /// (this data describes what the SMOKE TEST expects to see, not what the persona declares about
    /// itself -- keeping it out of personas/&lt;id&gt;/ avoids a test-only file inside a pack a
    /// future real-world sync process might treat as authoritative pack content).
    /// </summary>
    public static string PersonaSmokeDataDirectory(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "conformance", "testdata", "personas");

    /// <summary>The one smoke-expectations file for a single persona id -- see <see cref="PersonaSmokeDataDirectory"/>.</summary>
    public static string PersonaSmokeDataPath(string repoRoot, string personaId) =>
        Path.Combine(PersonaSmokeDataDirectory(repoRoot), personaId, "smoke.json");

    /// <summary>
    /// app/backend/static is gitignored — populated only by `npm run build` in app/frontend
    /// (vite's outDir points there). aiohttp's `add_static` raises at app-creation time if this
    /// directory doesn't exist, so the Python backend fails immediately on startup without it.
    /// </summary>
    public static string FrontendStaticIndexHtmlPath(string repoRoot) =>
        Path.Combine(repoRoot, "app", "backend", "static", "index.html");
}
