# Contributing Guidelines

Thank you for helping improve Microsoft Foundry AI Drive Thru. These steps keep the repository healthy as it moves into its GitHub home.

## Getting started

1. Fork the repository and clone your fork (`git clone https://github.com/swigerb/AzureAIDriveThru.git`).
2. Install the prerequisites listed in the README: Azure Developer CLI, Azure CLI, Docker, Node.js 20+, Python 3.11+, and Git. Install the .NET 11 SDK only if you run the C# backend or conformance suites.
3. For local Python work, use the repository virtual environment or create one, then install the backend dependencies from `app/backend/requirements.txt`.
4. Copy the backend and frontend `.env-sample` files to `.env` only when you are not using `scripts/write_env.ps1` or `scripts/write_env.sh` to populate values from azd.
5. Run `pwsh ./scripts/start.ps1` on Windows, or `./scripts/start.sh` on macOS or Linux, to launch the full stack locally before making UI or API changes.

## Branching and pull requests

- Create feature branches off `main` using the pattern `feature/<short-description>`.
- Keep pull requests focused on a single change. Include screenshots or GIFs for UI changes when possible.
- Reference GitHub Issues that the change addresses so the public backlog stays in sync.

## Testing checklist

Before submitting a pull request, run the smallest checks that cover your change:

1. Frontend: `cd app/frontend; npm test`
2. Frontend build on PowerShell: `cd app/frontend; $env:VITE_AUTH_MODE='Development'; npm run build`
3. Python backend: `python -m pytest app/backend/tests -q`
4. Python style, when Python files change: `ruff check app/backend`
5. C# backend, when C# files change and .NET 11 is installed: `dotnet test app/backend-dotnet/Backend.slnx`
6. Cross-backend conformance, when API or ordering behavior changes and .NET 11 is installed: `dotnet test tests/conformance/Conformance.slnx`

## Documentation updates

- Update `README.md`, `DEPLOY.md`, `DEMO.md`, or `docs/DEMO_SCRIPT.md` when flows or commands change.
- Keep architecture diagrams and screenshots inside `docs/` so they stay version-controlled.
- Open an issue when you add backlog items so GitHub Issues remain the source of truth.

By following these steps, we can keep the repo ready for public consumption and make future releases predictable.
