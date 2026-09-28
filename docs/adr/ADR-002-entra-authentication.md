# ADR-002: Entra authentication following Retail Pulse

- **Status:** Proposed, 2026-09-27. Brian reviews the PR; nothing is implemented until he accepts it.
- **Issues:** #85 (staging stays locked until this ships). Implemented by #143 to #147 (design doc
  section 18.12). Part of epic #6.
- **Supersedes:** the EasyAuth plan in the design doc (sections 5.2, 10.1, 10.2, 17 item 2), `DEPLOY.md` and #85.
- **Deciders:** Brian Swiger (owner), Rick (lead)
- **Full design:** [`docs/persona-architecture.md` section 18](../persona-architecture.md#18-authentication-entra-id-following-retail-pulse-adr-002)
- **Reference implementation:** `swigerb/retail-pulse` ADR-005, `docs/authentication-entra.md`,
  `scripts/Setup-EntraAuth.ps1`, `scripts/Verify-*.ps1`, `src/RetailPulse.Api/Security/`, `src/RetailPulse.Web/src/auth/`.

## Context

The first staging deploy (#87) was public with no authentication. Anyone with the URL could open realtime
sessions on our Foundry quota. On 2026-09-27 the app was locked (ingress disabled, min replicas 0; the undo
commands are on #85). Brian's direction: lock the app down the same way Retail Pulse does, and not with EasyAuth.

Retail Pulse, deployed in the same subscription and tenant, validates Entra tokens inside the app: one
single-tenant app registration, MSAL in the SPA, a pinned JWT bearer check on the API, deny by default, health
anonymous, a query-string token only on the WebSocket path, fail fast in Production, and EasyAuth explicitly off.

EasyAuth is the wrong tool for us:
- it needs a client secret, and its login redirects break the SPA's fetch and WebSocket calls;
- it is configured per container app outside the code, so the conformance suite can't prove it;
- a backend that forgot a check would still pass every test.

In-app validation is code that both backends share through one contract, and the shared suite can pin it.

## Decision

1. **One app registration** `AzureAIDriveThru`, single tenant (`AzureADMyOrg`):
   - a public SPA client (auth code + PKCE) with no secret or certificate;
   - App ID URI `api://{clientId}`, delegated scope `access_as_user`, v2 access tokens;
   - app role `DriveThru.User` (member type User) on a service principal with `appRoleAssignmentRequired = true`;
   - SPA redirect URIs for both backend hostnames plus localhost;
   - the Azure CLI client pre-authorized for headless checks.
2. **Deny by default.** Every route needs a valid token with the role and the scope, except an explicit
   anonymous allow-list:
   - the SPA shell and its bundle;
   - `/health`;
   - public branding assets: images, icons and audio under `/personas/{id}/assets/`.

   Everything else is protected, including every `/api/*` route (`/api/personas`, `/api/personas/{id}`,
   `/api/auth/session`), `/personas/{id}/menu.json`, JSON under `assets/`, and `/realtime`. A missing or
   invalid token gets 401 with `WWW-Authenticate: Bearer`. A valid token without the role or scope gets 403.
3. **Branding assets are public.** `<img>`, `<audio>` and the favicon can't carry a bearer. Logos, icons and
   apology clips aren't sensitive. Signed URLs and a cookie session were rejected as cost with no benefit.
4. **WebSocket:** `/realtime` takes the Entra access token as `?access_token`, which is read on that path
   only. **The HMAC session token is kept and layered**, not replaced:
   - only an authenticated `GET /api/auth/session` can mint it;
   - it carries the caller's `oid`;
   - in Entra mode it is always required on `/realtime`, and its `oid` must match the Entra token's.

   The checks run in this order: Entra (401/403), Origin (403), session token (401), then the existing limits
   and the persona and model 404s.
5. **The same validation on both backends:**
   - RS256 only, with signing keys from the tenant's JWKS (cached);
   - issuer exactly `https://login.microsoftonline.com/{tenant}/v2.0`, and `tid` equal to the tenant;
   - audience `{clientId}` or `api://{clientId}`;
   - `roles` contains `DriveThru.User`, and `scp` contains `access_as_user`, so app-only tokens are rejected;
   - lifetime checked with 5 minutes of clock skew.

   Python uses PyJWT with `cryptography` and `PyJWKClient`. C# uses `Microsoft.AspNetCore.Authentication.JwtBearer`
   with explicit parameters, not Microsoft.Identity.Web. Tokens never reach a log: access logs record the
   path without the query string. In Python that takes a path-only access logger class wired into both the
   gunicorn factory and `python app.py`, because the aiohttp gunicorn worker rejects gunicorn format
   directives and aiohttp's `%r` includes the query string (design 18.4).
6. **Two modes: `Entra` and `Development`.**
   - With a tenant and client id configured, the backend runs Entra in any environment.
   - Pass-through, with a synthetic local identity, happens only when nothing is configured and the process is
     not Production. `AUTH_MODE=Development` with any Entra id set fails fast: it can't switch validation off.
   - Production fails fast unless `AUTH_MODE=Entra` and the ids are valid.
   - An unknown mode fails in every environment.
   - The frontend mirrors this. A pass-through bundle needs an explicit `VITE_AUTH_MODE=Development`, the
     Dockerfile defaults the mode to `Entra`, and the build guard reads what Vite bakes in (`loadEnv`).

   We don't adopt Retail Pulse's provider-neutral mode contract.
7. **Infra:**
   - both container apps pin `AUTH_MODE=Entra`, their production flag and the `ENTRA_*` ids;
   - EasyAuth is removed from Bicep, and the postprovision hook disables it on both apps;
   - the SPA's public ids reach the image build as Docker build args from the azd env (`azure.yaml`
     `docker.buildArgs`, and `scripts/docker-build.sh`), never through a `.env` file. `ARG VITE_AUTH_MODE`
     defaults to `Entra`, so a build that loses its args fails instead of shipping a pass-through bundle.
8. **Two hostnames, one registration.** There's one SPA redirect URI per origin, and MSAL caches per origin in
   `sessionStorage`. The Entra SSO session makes the backend switch a silent redirect. No CORS is needed:
   each host serves its own SPA.
9. **Scripts, ported from Retail Pulse:** `Setup-EntraAuth.ps1` (preview by default, `-Apply` to write),
   `Verify-EntraAuth.ps1` and `Verify-ProductionAuth.ps1`. All three are idempotent and print only public
   identifiers.
10. **Conformance proves it on both backends.**
    - The harness gets a fake issuer and JWKS that mint test tokens.
    - The rows are: no token, wrong tenant, wrong audience, missing role, missing scope, expired, bad
      signature, and valid. They run on REST and on `/realtime`, plus the mode and logging rows.
    - The default fixture runs in Entra mode.
11. **Unlock gate.** Staging ingress is re-enabled only when Python, frontend and infra have shipped, and it
    stays on only if `Verify-ProductionAuth.ps1` passes against the live app. Because the apps run in
    single-revision mode, a failed new revision leaves the old one active; so every pre-auth revision is
    deactivated before `azd provision`, the active revisions are checked right after it, and Verify fails if
    any active revision isn't the new image with `AUTH_MODE=Entra` (design 18.10). The C# app never goes
    public without its parity work.
12. **Sign-in never loops.** The gate starts `loginRedirect` automatically only on a clean load. After a
    sign-out, a 403, or a failed redirect (cancelled, admin approval needed, `AADSTS50105` not assigned) it
    shows the error and a manual **Sign in** button. An unassigned user is stopped by Entra at sign-in, not by
    our 403 screen, which is for a token without the role or scope.

## Consequences

**Good**
- The quota is behind sign-in plus an explicit role assignment, which Brian controls per user.
- The security boundary is code, and one suite checks it on both backends.
- No secrets anywhere: the SPA is a public client, and the APIs use Entra's published keys.
- It matches Retail Pulse, so one mental model and one set of scripts covers both demos.

**Costs and risks**
- Every demo viewer needs an account in the tenant (a member or a B2B guest) and the `DriveThru.User` role.
  That is the point, but it adds a step before a customer demo.
- `azd provision` re-enables external ingress, and in single-revision mode a failed new revision leaves the
  old unauthenticated one active. The rollout in design 18.10 deactivates every pre-auth revision before
  provisioning and checks the active revisions after it, so an unauthenticated revision is never public.
- The Entra access token is in the `/realtime` URL, as it is in Retail Pulse. The mitigations: the
  query-string token is read only on that path, and access logs record only the path on both backends
  (pinned by conformance on `python app.py`, and by unit and Dockerfile tests on the gunicorn path).
- The default conformance fixture moves to Entra mode. That touches the harness clients once.
- The Playwright UX runs use Development pass-through, because MSAL can't sign in against a fake issuer.
  Sign-in UX is covered by vitest with a mocked MSAL, plus the manual checklist.
- A built pass-through bundle is a deliberate difference from Retail Pulse, which passes through only under
  `import.meta.env.DEV`. We allow it only with an explicit `VITE_AUTH_MODE=Development`, because the harness
  and Playwright serve built bundles.

## Alternatives considered

| Option | Why not |
| --- | --- |
| ACA EasyAuth (the old plan) | Needs a client secret; its redirects break the SPA's fetch and WebSocket calls; configured outside the code, so conformance can't prove it. Brian ruled it out |
| Retail Pulse's provider-neutral mode contract (Entra, GitHub, Anonymous) | We have one audience and no second provider planned. Porting it would double the auth surface on two backends. The mode resolver stays explicit, so a mode can be added later |
| Replace the HMAC session token with the Entra token | It saves little code, but it would change a contract that both backends and the suite already pin, in the middle of the C# port, and lose the Development-mode guard. Layering costs about 20 lines per backend |
| Session ticket only on `/realtime`, so the Entra token never appears in a URL | Stronger against URL logging, but it departs from Retail Pulse and adds a single-use store per replica. Kept as the upgrade path if a security review asks for it |
| Signed URLs or a cookie for persona assets | They'd break immutable caching or bring back cookie auth and CSRF handling, all for public logos and clips |
| Microsoft.Identity.Web (C#) | Its conventions (the `AzureAd` section, its own issuer handling) make parity with PyJWT and pointing at the fake issuer harder. Plain JwtBearer is what Retail Pulse uses |
| MSAL or azure-identity for validation (Python) | Both acquire tokens; neither validates incoming ones |
| Runtime SPA config endpoint instead of build args | It would need an extra anonymous request and differs from Retail Pulse. The ids are the same for both hostnames, so one build serves both |
