# ---------------------------------------------------------------------------
# Stage 1: Build the Vite/React frontend
# ---------------------------------------------------------------------------
FROM node:24-slim AS build-stage

WORKDIR /frontend

# Auth-mode build selector (design 18.6, ADR-002 18.8). Defaults to Entra for a real production
# image; override with `--build-arg VITE_AUTH_MODE=Development` for a smoke-test build that
# doesn't need real Entra ids (see the CI docker-build job in .github/workflows/conformance.yml).
# The three VITE_ENTRA_* values are public identifiers (a tenant id, an app id, and a scope name --
# none is a secret), so they are safe to bake into the built bundle via ARG/ENV like VITE_AUTH_MODE
# itself; azd (azure.yaml docker.buildArgs), CI and scripts/docker-build.sh all pass them the same
# way. A build that loses these args (e.g. a remote build dropping buildArgs) still defaults
# VITE_AUTH_MODE to Entra, so it fails the frontend's Vite guard (missing/placeholder ids) instead
# of silently shipping an unauthenticated Development bundle.
ARG VITE_AUTH_MODE=Entra
ARG VITE_ENTRA_TENANT_ID
ARG VITE_ENTRA_CLIENT_ID
ARG VITE_ENTRA_API_SCOPE
ENV VITE_AUTH_MODE=${VITE_AUTH_MODE}
ENV VITE_ENTRA_TENANT_ID=${VITE_ENTRA_TENANT_ID}
ENV VITE_ENTRA_CLIENT_ID=${VITE_ENTRA_CLIENT_ID}
ENV VITE_ENTRA_API_SCOPE=${VITE_ENTRA_API_SCOPE}

# Copy dependency manifests first for optimal layer caching
# Issue #129: paths are repo-root relative -- the build context is the repo root
# (azure.yaml's docker.context), not this app/ directory.
COPY app/frontend/package.json app/frontend/package-lock.json ./

# Optional local feed configuration stays out of image layers.
RUN --mount=type=secret,id=npm_config,target=/root/.npmrc \
    npm ci --no-audit --no-fund && npm cache clean --force

# Copy remaining frontend source (changes more often than deps)
COPY app/frontend/ ./

RUN npm run build

# ---------------------------------------------------------------------------
# Optional prebuilt Python wheelhouse. Empty by default; override with
# `docker build --build-context python-wheelhouse=<dir>` using the artifact from
# .github/workflows/python-wheelhouse.yml when the build host cannot reach the package index.
# ---------------------------------------------------------------------------
FROM scratch AS python-wheelhouse

# ---------------------------------------------------------------------------
# Stage 2: Production Python runtime
# ---------------------------------------------------------------------------
FROM python:3.15-slim AS production-stage

WORKDIR /app

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# Install Python deps before copying app source for layer caching
COPY ./app/backend/requirements.txt /app/requirements.txt
# With a wheelhouse, install offline from its hash-locked set only after verifying every wheel
# checksum and that the lock was resolved from this exact requirements.txt (line endings ignored,
# so a CRLF Windows checkout matches the LF hash recorded by CI).
RUN --mount=type=secret,id=pip_config,target=/etc/pip.conf \
    --mount=type=bind,from=python-wheelhouse,target=/wheelhouse \
    if [ -f /wheelhouse/requirements.lock.txt ]; then \
      (cd /wheelhouse && sha256sum -c --strict SHA256SUMS) \
      && test "$(tr -d '\r' < requirements.txt | sha256sum | cut -d' ' -f1)" = "$(cat /wheelhouse/requirements.txt.sha256)" \
      && python -m pip install --no-cache-dir --no-index --find-links /wheelhouse \
           --require-hashes -r /wheelhouse/requirements.lock.txt; \
    else \
      PYYAML_FORCE_LIBYAML=0 python -m pip install --no-cache-dir -r requirements.txt; \
    fi

# Copy backend application source
COPY ./app/backend/ /app

# Persona packs (issue #70): prompts, menu data, and schemas the backend loads at startup.
# PERSONAS_DIR is set explicitly because the container's directory depth differs from the repo
# checkout (backend/ is flattened onto /app), so the module-level relative-path fallback in
# persona_loader.py / prompt_loader.py / menu_utils.py / setup_search_index.py would not resolve
# correctly without it.
COPY ./personas /app/personas
ENV PERSONAS_DIR=/app/personas

# Copy the freshly-built frontend assets last so they are not overwritten by the backend source copy.
COPY --from=build-stage /backend/static /app/static

EXPOSE 8000

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8000/health')" || exit 1

# Production gunicorn config:
# - ONE aiohttp worker. Order state, the resume credential and the detached-session
#   grace hold are in-process memory; a second worker would own a different copy,
#   so a reconnect could land where the order doesn't exist. Ingress affinity is
#   sticky (infra/main.bicep) so the replica is stable too.
# - 120s timeout for long-lived WebSocket upgrades
# - `app:create_runner` (issue #144, design doc section 18.4): an async factory
#   returning a `web.AppRunner`, not a plain `web.Application`. The runner itself
#   carries the real keep-alive (65s, to outlast the Azure LB's 60s idle timeout)
#   and graceful-shutdown (28.5s, inside gunicorn's own 30s below) timeouts and the
#   path-only access logger -- see create_runner()'s docstring for why gunicorn's
#   own `--keep-alive`/`--access-logformat` flags are dropped rather than set here.
# - Graceful 30s shutdown window (gunicorn's own arbiter-level SIGTERM->SIGKILL grace)
CMD ["python3", "-m", "gunicorn", "app:create_runner", \
     "-b", "0.0.0.0:8000", \
     "--worker-class", "aiohttp.GunicornWebWorker", \
     "--workers", "1", \
     "--timeout", "120", \
     "--graceful-timeout", "30", \
     "--access-logfile", "-", \
     "--error-logfile", "-", \
     "--log-level", "info"]
