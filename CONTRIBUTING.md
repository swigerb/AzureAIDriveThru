# Contributing Guidelines

Thank you for helping improve Microsoft Foundry AI Drive Thru. These steps keep the repository healthy as it moves into its GitHub home.

## Getting started

1. Fork the repository and clone your fork (`git clone https://github.com/swigerb/AzureAIDriveThru.git`).
2. Install the prerequisites listed in the README: Azure Developer CLI, Azure CLI, Docker, Node.js 24 LTS (24.15+), Python 3.15 (3.12-3.14 also supported), and Git. Install the .NET 11 RC2 SDK pinned in `global.json` only if you run the C# backend or conformance suites.
3. For local Python work, run `pwsh ./scripts/load_python_env.ps1` to create/update the repository virtual environment and install backend dependencies. It configures PyYAML's pure-Python build so Python 3.15 does not require a C/C++ compiler.
4. Copy the backend and frontend `.env-sample` files to `.env` only when you are not using `scripts/write_env.ps1` or `scripts/write_env.sh` to populate values from azd.
5. Run `pwsh ./scripts/start.ps1` on Windows, or `./scripts/start.sh` on macOS or Linux, to launch the full stack locally before making UI or API changes.

Both launchers now use the repository-root `.venv`. Activate it in your current shell before
using bare `python`, `pytest`, or `ruff`; running the setup script in a child `pwsh` process
does not activate your parent terminal. For test tooling, install
`app/backend/requirements-dev.txt` with `.venv`'s interpreter and
`PYYAML_FORCE_LIBYAML=0` (PowerShell: `$env:PYYAML_FORCE_LIBYAML='0'`).
The runtime-only requirements intentionally exclude test tools. The Microsoft devcontainer
uses Python 3.13 because 3.15 images are not available there; production and CI use 3.15.

## Branching and pull requests

- Create feature branches off `dev` and target pull requests to `dev`.
- Keep pull requests focused on a single change. Include screenshots or GIFs for UI changes when possible.
- Reference GitHub Issues that the change addresses so the public backlog stays in sync.

## Testing checklist

Before submitting a pull request, run the smallest checks that cover your change:

1. Frontend: `cd app/frontend; npm test`
2. Frontend build on PowerShell: `cd app/frontend; $env:VITE_AUTH_MODE='Development'; npm run build`
3. Python backend: `python -m pytest app/backend/tests -q`
4. Python style, when Python files change: `ruff check app/backend`
5. C# backend, when C# files change and .NET 11 is installed: `dotnet test app/backend-dotnet/Backend.slnx`
6. Non-browser conformance, when API or ordering behavior changes: set `$env:CONFORMANCE_BACKEND='python'` and run `dotnet test tests/conformance/Conformance.slnx --filter "Category!=Browser"`; repeat with `$env:CONFORMANCE_BACKEND='dotnet'` and `--filter "Dotnet=ready&Category!=Browser"`. The Python leg requires the root `.venv`. See the [harness setup](tests/conformance/README.md#local-validation) for harness dependencies and browser checks.
7. C# tooling, when ingestion tools change: `dotnet test tools/dotnet/Tooling.slnx`.

Container builds support optional BuildKit secrets named `npm_config`, `pip_config`, and
`nuget_config` to inherit approved local package-feed configuration without copying it into
image layers. Pass the applicable config file with `docker build --secret id=<name>,src=<path>`;
leave these unset in environments that already use their standard feeds.

When the build host cannot reach the Python package index, download the `python-wheelhouse`
artifact from the **Python wheelhouse** workflow and pass it with
`docker build -f app/Dockerfile --build-context python-wheelhouse=<artifact-dir> ...`. The image
then installs offline from the hash-locked wheels, after verifying the checksums and that the lock
was built from the current `app/backend/requirements.txt`. Without that build context, the build
installs from the configured index as usual.

## Dependency maintenance

Use configured, approved package sources. Do not add a repository NuGet config that re-enables
nuget.org or switch npm/pip to a public registry to work around a proxy error. Never commit
package-feed credentials. These checks inspect different contracts; `pip check` is dependency
consistency, not a vulnerability scan.

| Surface | Inventory and checks |
| --- | --- |
| Frontend | From `app/frontend`: `npm ci`, `npm outdated`, `npm outdated --all`, `npm audit`, then tests and build. `npm update` refreshes parent-compatible transitives; commit both manifests and lockfile. |
| Python runtime and tools | From the root `.venv`: `python -m pip list --outdated`, `python -m pip check`; runtime pins are in `app/backend/requirements.txt`, test pins in `requirements-dev.txt`, and independent JWT validation in `tests/conformance/requirements-harness.txt`. Check preview availability separately with `python -m pip index versions azure-ai-inference --pre`. Python has no committed transitive lockfile. |
| NuGet | For each of `app/backend-dotnet/Backend.slnx`, `tests/conformance/Conformance.slnx`, and `tools/dotnet/Tooling.slnx`: `dotnet restore <solution> --locked-mode`, `dotnet package list --project <solution> --outdated --include-transitive`, and `dotnet package list --project <solution> --vulnerable --include-transitive`. Version changes require `dotnet restore <solution> --force-evaluate` and committing every affected lockfile. |
| Runtime images | Compare `global.json`, both `app/Dockerfile*` files, `.devcontainer/devcontainer.json`, and CI runtime selections with official release metadata and image tags. .NET 11 RC2 and the Python 3.13 devcontainer are deliberate exceptions, not stable/latest-runtime claims. |
| CI automation | Compare every SHA-pinned Action with its latest stable release and verify the tag resolves to that SHA. `.github/dependabot.yml` covers frontend npm, both Python requirement directories (including development tools), all three NuGet roots, Docker, devcontainers, and Actions, targeting `dev`. `scripts/package-lock.json` contains no dependencies; `.squad/templates/package.json` only selects CommonJS. |

An empty advisory report means no vulnerabilities were reported by that source at audit time,
not a guarantee of security. Some npm transitives legitimately lag upstream latest because
their parents restrict versions. Avoid untested forced overrides.

## Documentation updates

- Update `README.md`, `DEPLOY.md`, `DEMO.md`, or `docs/DEMO_SCRIPT.md` when flows or commands change.
- Keep architecture diagrams and screenshots inside `docs/` so they stay version-controlled.
- Open an issue when you add backlog items so GitHub Issues remain the source of truth.

By following these steps, we can keep the repo ready for public consumption and make future releases predictable.
