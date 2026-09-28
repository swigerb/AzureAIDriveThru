# S7 (#17) prep: side-by-side C# container image. Serves the SAME built frontend static files and
# reads the SAME personas/ + app/backend/config.yaml as app/Dockerfile's Python image (section 10,
# design doc) -- neither is duplicated under app/backend-dotnet. Issue #131's repo-root build
# context convention applies here too (COPY paths below are repo-root relative); azure.yaml's
# backend-dotnet service sets docker.context accordingly (two levels up from ./app/backend-dotnet).
#
# Image tags pin the exact SDK/runtime band app/backend-dotnet/global.json requires
# (11.0.100-rc.1.26425.128, rollForward: latestPatch) -- mcr.microsoft.com/dotnet/sdk publishes an
# unsuffixed manifest list per feature band (11.0.100-rc.1) and mcr.microsoft.com/dotnet/aspnet
# publishes one per patch version without the SDK's three-digit feature band (11.0.0-rc.1); both
# are the default (non-alpine, non-chiseled) Debian-based variant, required because
# Directory.Build.props sets InvariantGlobalization=false (full ICU) and PublishAot=false
# (Beth's charter: Azure SDK trimming warnings rule out Native AOT for now -- plain JIT publish;
# see stage 2's publish step for why ReadyToRun isn't enabled either, this wave).

# ---------------------------------------------------------------------------
# Stage 1: Build the Vite/React frontend (identical to app/Dockerfile's build-stage -- the C# app
# serves the same bundle, not a second copy of the frontend source).
# ---------------------------------------------------------------------------
FROM node:22-slim AS frontend-build-stage

WORKDIR /frontend

COPY app/frontend/package.json app/frontend/package-lock.json ./

RUN if [ ! -f .env ]; then \
      printf "VITE_AUTH_URL=\nVITE_AUTH_ENABLED=false\n" > .env; \
    fi

RUN npm ci --no-audit --no-fund && npm cache clean --force

COPY app/frontend/ ./

RUN npm run build

# ---------------------------------------------------------------------------
# Stage 2: Restore and publish the .NET backend
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1 AS dotnet-build-stage

WORKDIR /src

# global.json pins the SDK band for local/CI dev machines that resolve among several installed
# SDKs; copying it here lets `rollForward: latestPatch` tolerate this image's exact patch build
# without failing, even though the image itself is already the pinned band.
COPY global.json ./

# Copy manifests first for optimal layer caching: Directory.Build.props / Directory.Packages.props
# (central package management, MSBuild's directory-walk-up resolution) and the project's own
# packages.lock.json must sit at the same relative depth as the real repo for `dotnet restore` to
# find them and for `--locked-mode` to validate against the committed lock file.
COPY app/backend-dotnet/Directory.Build.props app/backend-dotnet/Directory.Packages.props app/backend-dotnet/
COPY app/backend-dotnet/src/Backend/Backend.csproj app/backend-dotnet/src/Backend/packages.lock.json app/backend-dotnet/src/Backend/

RUN dotnet restore app/backend-dotnet/src/Backend/Backend.csproj --locked-mode

# Copy the remaining backend source (changes more often than deps) and publish.
COPY app/backend-dotnet/src/Backend/ app/backend-dotnet/src/Backend/

# Portable, framework-dependent publish (matches Directory.Build.props's PublishAot=false -- no
# Native AOT). ReadyToRun is deliberately NOT enabled here: it requires a RID (PublishReadyToRun
# needs -r <rid>), and Backend.csproj declares none today -- adding one (plus regenerating
# packages.lock.json for the RID-specific restore graph) would be a src/** change beyond "the
# minimum needed for container startup paths" this PREP task is scoped to (#17 coordinates with
# Beth's #13 / Summer's #14). A later wave can add RuntimeIdentifiers + flip this on once that's
# their call, not this one's.
RUN dotnet publish app/backend-dotnet/src/Backend/Backend.csproj \
      -c Release \
      --no-restore \
      -o /app/publish

# ---------------------------------------------------------------------------
# Stage 3: Production ASP.NET Core runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1 AS production-stage

WORKDIR /app

COPY --from=dotnet-build-stage /app/publish ./

# Same personas/ tree and app/backend/config.yaml the Python image reads (docs/dotnet_mapping.md,
# AppConfig.cs, PersonaCatalog.cs) -- not duplicated under app/backend-dotnet. PERSONAS_DIR,
# CONFIG_PATH and STATIC_FILES_DIR are set explicitly because the container's directory depth
# differs from the repo checkout, so PersonaCatalog.Load() / AppConfig.Load() / Program.cs's
# TryFindStaticDir() repo-root walk (RepoRootLocator.cs) would not resolve correctly without them.
# PersonaCatalog.Load() fails loudly (throws PersonaValidationException) if PERSONAS_DIR doesn't
# exist or a persona under it lacks persona.schema.json -- same existing-by-schema resolution rule
# app/backend/persona_loader.py enforces for the Python image (issue #131).
COPY personas/ /app/personas
COPY app/backend/config.yaml /app/config.yaml
ENV PERSONAS_DIR=/app/personas
ENV CONFIG_PATH=/app/config.yaml

# Copy the freshly-built frontend assets last so they are not overwritten by anything above.
COPY --from=frontend-build-stage /backend/static /app/static
ENV STATIC_FILES_DIR=/app/static

# HOST defaults to 127.0.0.1 in Program.cs (loopback-only) -- must be overridden here so the
# container's port 8000 is reachable from outside it (matches Container Apps' targetPort: 8000,
# infra/main.bicep's acaBackendDotnet module).
ENV HOST=0.0.0.0
ENV PORT=8000

EXPOSE 8000

# No Docker-level HEALTHCHECK: the minimal aspnet runtime image ships no HTTP client tool (curl/
# wget), and adding one just for this would mean an extra apt-get layer. Azure Container Apps'
# own liveness/readiness/startup probes (infra/core/host/container-app.bicep's healthProbePath:
# '/health', wired from acaBackendDotnet) already cover this in the deployed environment; the CI
# Docker verify job (docker-build-dotnet) curls /health from outside the container instead.
ENTRYPOINT ["dotnet", "Backend.dll"]
