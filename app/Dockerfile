# ---------------------------------------------------------------------------
# Stage 1: Build the Vite/React frontend
# ---------------------------------------------------------------------------
FROM node:22-slim AS build-stage

WORKDIR /frontend

# Auth-mode build selector (design 18.6, issue GH-145 / PR GH-148 review round 2, item B1).
# Defaults to Entra for a real production image; override with
# `--build-arg VITE_AUTH_MODE=Development` for a smoke-test build that doesn't need real Entra
# ids (see the CI docker-build job in .github/workflows/conformance.yml). Wiring the SPA's
# VITE_ENTRA_TENANT_ID/VITE_ENTRA_CLIENT_ID build args for an actual Entra image build is
# tracked separately under issue GH-146 -- intentionally not added here to keep this edit minimal.
ARG VITE_AUTH_MODE=Entra
ENV VITE_AUTH_MODE=${VITE_AUTH_MODE}

# Copy dependency manifests first for optimal layer caching
# Issue #129: paths are repo-root relative -- the build context is the repo root
# (azure.yaml's docker.context), not this app/ directory.
COPY app/frontend/package.json app/frontend/package-lock.json ./

# Create a safe default .env if one is not present.
# Note: Avoid ARG/ENV for these values to prevent accidental secret leakage.
RUN if [ ! -f .env ]; then \
      printf "VITE_AUTH_URL=\nVITE_AUTH_ENABLED=false\n" > .env; \
    fi

RUN npm ci --no-audit --no-fund && npm cache clean --force

# Copy remaining frontend source (changes more often than deps)
COPY app/frontend/ ./

RUN npm run build

# ---------------------------------------------------------------------------
# Stage 2: Production Python runtime
# ---------------------------------------------------------------------------
FROM python:3.12-slim AS production-stage

WORKDIR /app

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# Install Python deps before copying app source for layer caching
COPY ./app/backend/requirements.txt /app/requirements.txt
RUN python -m pip install --no-cache-dir -r requirements.txt

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
