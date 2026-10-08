### 2026-10-06: EntraSettings stays outside startup options binding
**By:** Beth
**What:** I left `EntraSettings.Resolve(...)` on Program.cs's pre-`builder.Build()` path instead of trying to move Entra auth mode into `IOptions<T>` + `ValidateOnStart`.
**Why:** Entra mode decides whether JwtBearer authentication services are registered at build time, so deferring it to options validation would move the decision until after `Build()` and break the current fail-fast/auth-registration ordering. The rest of issue #337's env/config binding can use options safely without changing that startup contract.
