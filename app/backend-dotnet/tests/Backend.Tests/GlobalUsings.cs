// #236 Rick re-review item 6 (shared rate-limit helper extraction, agreed with #235/Unity):
// global using aliases are per-COMPILATION (i.e. per project), not transitively visible across a
// project reference -- so Backend.csproj's own aliases (in CascadeRateLimit.cs/
// RateLimitRecovery.cs) don't reach this test project. Mirrored here so every existing test call
// site (`new RateLimitSettings(...)`, `new CascadeRateLimitSettings(...)`,
// `CascadeRateLimitSettings.FromAppConfig(...)`) keeps compiling unchanged against the single
// shared Backend.Shared.RateLimitSettings record.
global using RateLimitSettings = Backend.Shared.RateLimitSettings;
global using CascadeRateLimitSettings = Backend.Shared.RateLimitSettings;
