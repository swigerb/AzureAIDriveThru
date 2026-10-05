using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Backend.Configuration;
using Backend.Models;
using Microsoft.AspNetCore.Http;

namespace Backend.Personas;

/// <summary>
/// Port of app/backend/app.py's `register_persona_routes` (issue #74, design doc section 5.2):
/// `GET /api/personas`, `GET /api/personas/{id}`, `GET /personas/{id}/menu.json`, and
/// `GET /personas/{id}/assets/{*assetPath}`. A standalone static class rather than inline
/// `Program.cs` lambdas, mirroring Python's own choice to keep this a free function rather than a
/// `create_app()` closure -- both so it reads as one cohesive unit and so a future
/// WebApplicationFactory-based integration test can map these routes on a minimal host without the
/// full startup sequence.
///
/// Rick's PR #106 review item 3 (issue #75): `ModelCatalog` is mandatory here -- `/api/personas/
/// {id}`'s `models` block ALWAYS narrows to what's actually selectable (catalog ∩ deployment ∩
/// persona-allowed, design doc section 7.3), `{id, label, reasoning}` shaped, never bare ids.
/// </summary>
public static class PersonaRoutes
{
    private static readonly Dictionary<string, string> AssetContentTypes = new(StringComparer.Ordinal)
    {
        [".svg"] = "image/svg+xml",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".ico"] = "image/x-icon",
        [".json"] = "application/json",
    };

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Map(WebApplication app, PersonaCatalog catalog, ModelCatalog modelCatalog, AssetCacheConfig cacheConfig)
    {
        app.MapGet("/api/personas", () => Results.Json(BuildPersonasIndexBody(catalog))).WithName("personas-index");

        app.MapGet("/api/personas/{personaId}", (string personaId) =>
        {
            if (!catalog.Contains(personaId))
            {
                return UnknownPersonaResult(personaId);
            }
            return Results.Json(BuildPersonaDetailBody(catalog.Get(personaId), modelCatalog));
        }).WithName("persona-detail");

        app.MapGet("/personas/{personaId}/menu.json", async (string personaId, HttpContext context) =>
        {
            if (!catalog.Contains(personaId))
            {
                await WriteJsonAsync(context, 404, new JsonObject { ["error"] = $"Unknown or disabled persona: '{personaId}'" });
                return;
            }

            var persona = catalog.Get(personaId);
            if (!File.Exists(persona.MenuPath))
            {
                await WriteJsonAsync(context, 404, new JsonObject { ["error"] = $"No menu data for persona: '{personaId}'" });
                return;
            }

            var bytes = await File.ReadAllBytesAsync(persona.MenuPath, context.RequestAborted);
            var hash = PersonaAssetHash.ComputeHash(persona.MenuPath);
            await WriteAssetAsync(context, bytes, "application/json", hash, cacheConfig);
        }).WithName("persona-menu");

        app.MapGet("/personas/{personaId}/assets/{*assetPath}", async (string personaId, string? assetPath, HttpContext context) =>
        {
            if (!catalog.Contains(personaId))
            {
                await WriteJsonAsync(context, 404, new JsonObject { ["error"] = $"Unknown or disabled persona: '{personaId}'" });
                return;
            }

            // Route values are already percent-decoded by ASP.NET Core's routing, same as
            // aiohttp's match_info -- nothing extra to decode here.
            assetPath ??= string.Empty;
            var resolved = PersonaAssetResolver.Resolve(catalog.Get(personaId), assetPath);
            if (resolved is null)
            {
                await WriteJsonAsync(context, 404, new JsonObject { ["error"] = $"Unknown persona asset: '{assetPath}'" });
                return;
            }

            var bytes = await File.ReadAllBytesAsync(resolved, context.RequestAborted);
            var contentType = AssetContentTypes.GetValueOrDefault(Path.GetExtension(resolved).ToLowerInvariant());
            var hash = PersonaAssetHash.ComputeHash(resolved);
            await WriteAssetAsync(context, bytes, contentType, hash, cacheConfig);
        }).WithName(PersonaAssetRouteName);
    }

    /// <summary>
    /// app.py's 'persona-asset' route name (issue #147: the fallback authorization policy's
    /// anonymous-extension escape hatch looks this endpoint name up via
    /// <c>HttpContext.GetEndpoint()</c> to decide whether a given asset request is on the same
    /// per-extension anonymous allow-list Python's `_is_anonymous` grants it, matching
    /// entra_auth.py's PERSONA_ASSET_ROUTE_NAME/ANONYMOUS_ASSET_EXTENSIONS).
    /// </summary>
    public const string PersonaAssetRouteName = "persona-asset";

    private static IResult UnknownPersonaResult(string personaId) =>
        Results.Json(new JsonObject { ["error"] = $"Unknown or disabled persona: '{personaId}'" }, statusCode: 404);

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, JsonObject body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body.ToJsonString());
    }

    /// <summary>
    /// Port of app/backend/app.py's `_asset_cache_headers` (Rick's PR #102 review item 2):
    /// year-long immutable caching ONLY when the request's `?v=` matches this file's CURRENT
    /// content hash -- an unversioned request, or one carrying a stale/mismatched hash, gets the
    /// same short, revalidate-often policy as any other mutable static file. Always sets
    /// X-Content-Type-Options: nosniff (defense-in-depth against content-type sniffing on these
    /// persona-supplied, not first-party-authored, files).
    /// </summary>
    private static async Task WriteAssetAsync(HttpContext context, byte[] bytes, string? contentType, string contentHash, AssetCacheConfig cacheConfig)
    {
        if (contentType is not null)
        {
            context.Response.ContentType = contentType;
        }

        var requestedV = context.Request.Query["v"].ToString();
        context.Response.Headers.CacheControl = requestedV.Length > 0 && requestedV == contentHash
            ? $"public, max-age={cacheConfig.ImmutableMaxAgeSeconds}, immutable"
            : $"public, max-age={cacheConfig.DefaultMaxAgeSeconds}";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static JsonObject BuildPersonasIndexBody(PersonaCatalog catalog)
    {
        var personas = new JsonArray();
        foreach (var id in catalog.Ids)
        {
            personas.Add(BuildPersonaSummaryBody(catalog.Get(id)));
        }

        return new JsonObject
        {
            ["default"] = catalog.DefaultPersonaId,
            ["personas"] = personas,
            ["backends"] = BuildBackendEntries(),
        };
    }

    private static JsonObject BuildPersonaSummaryBody(Persona persona) => new()
    {
        ["id"] = persona.Id,
        ["displayName"] = persona.DisplayName,
        ["logoUrl"] = BuildLogoUrl(persona),
        ["theme"] = ToJsonObject(persona.Ui.Theme),
    };

    /// <summary>
    /// Port of app.py's `_backend_entries` (design doc section 5.2/10.1, Rick's PR #102 review
    /// item 4): each backend's public base URL, not `/realtime`. `python` always has an entry --
    /// unconditionally, exactly like Python's own copy of this logic always lists itself -- so
    /// this backend's `/api/personas` response has the identical shape regardless of which
    /// process answers the request. `dotnet` is omitted entirely when BACKEND_DOTNET_URI isn't
    /// set, never reported with a placeholder.
    /// </summary>
    private static JsonArray BuildBackendEntries()
    {
        var entries = new JsonArray
        {
            new JsonObject { ["id"] = "python", ["url"] = (Environment.GetEnvironmentVariable("BACKEND_URI") ?? string.Empty).Trim() },
        };

        var dotnetUri = (Environment.GetEnvironmentVariable("BACKEND_DOTNET_URI") ?? string.Empty).Trim();
        if (dotnetUri.Length > 0)
        {
            entries.Add(new JsonObject { ["id"] = "dotnet", ["url"] = dotnetUri });
        }

        return entries;
    }

    private static JsonObject BuildPersonaDetailBody(Persona persona, ModelCatalog modelCatalog)
    {
        // Rick's #120 review round 2, required item 4: `roleName` is pinned on the wire
        // (design doc section 5.2), matching Python's `_persona_detail_body`.
        var result = new JsonObject { ["id"] = persona.Id, ["roleName"] = persona.RoleName };

        // Spread `ui`'s own top-level fields (title, theme, assets, strings, hero, legal) into
        // the result, mirroring Python's `**manifest.ui.model_dump(exclude_none=True)` dict
        // spread. A JsonNode can only have one parent, so each child must be detached from the
        // temporary `ui` object before being reparented onto `result`.
        var uiObject = ToJsonObject(persona.Ui);
        foreach (var (key, value) in uiObject.ToList())
        {
            uiObject.Remove(key);
            result[key] = value;
        }

        result["voice"] = new JsonObject { ["default"] = persona.Voice.Default };
        result["locales"] = ToJsonObject(persona.Locales);
        result["features"] = new JsonObject { ["dayparts"] = persona.Features.Dayparts };
        result["menuUrl"] = BuildMenuUrl(persona);
        result["models"] = BuildModelPipelinesBody(persona.Models, modelCatalog);
        // Issue #164 E2: mirrors app.py's `_persona_detail_body`, which forwards `pricing.taxRate`
        // here so the ticket can render "Tax (N%)" instead of a bare "Tax".
        result["taxRate"] = persona.Pricing.TaxRate;
        return result;
    }

    /// <summary>
    /// Port of app.py's `_model_pipelines_body`/`_selectable_models` (issue #75, Rick's PR #106
    /// review item 3): only the pipelines a persona actually declares (cascade is optional),
    /// each narrowed to `{id, label, reasoning}` shaped models that are ACTUALLY
    /// selectable right now (catalog ∩ deployment ∩ persona-allowed) -- deliberately no
    /// carve-out for the pipeline's own `default`; an unselectable default is left out of this
    /// list on purpose (see ModelCatalog.IsSelectable's own doc comment).
    /// </summary>
    private static JsonObject BuildModelPipelinesBody(PersonaModelsBlock models, ModelCatalog modelCatalog)
    {
        var body = new JsonObject
        {
            ["realtime"] = BuildPipelineBody(models.Realtime, "realtime", modelCatalog),
        };
        if (models.Cascade is not null)
        {
            body["cascade"] = BuildPipelineBody(models.Cascade, "cascade", modelCatalog);
        }
        return body;
    }

    private static JsonObject BuildPipelineBody(PersonaModelPipeline pipelineCfg, string pipelineName, ModelCatalog modelCatalog)
    {
        var selectable = new JsonArray();
        foreach (var modelId in pipelineCfg.Allowed)
        {
            if (!modelCatalog.IsSelectable(modelId, pipelineName))
            {
                continue;
            }
            var entry = modelCatalog.Get(modelId);
            selectable.Add(new JsonObject { ["id"] = modelId, ["label"] = entry.Label, ["reasoning"] = entry.Reasoning });
        }

        return new JsonObject { ["default"] = pipelineCfg.Default, ["models"] = selectable };
    }

    /// <summary>
    /// Port of app.py's `_persona_logo_url` (Rick's PR #102 review item 2/5): `ui.assets.logo` is
    /// a path relative to the pack root (e.g. "assets/logo.svg"), while the asset route resolves
    /// its tail relative to `assets_dir` itself -- strip the redundant leading "assets/" so the
    /// URL this builds is one the asset route can actually resolve. Carries `?v=&lt;content-hash&gt;`
    /// when the file can be resolved on disk; falls back to no `?v=` otherwise (schema validation
    /// doesn't check the logo file itself exists) rather than raising.
    /// </summary>
    private static string BuildLogoUrl(Persona persona)
    {
        var logo = persona.Ui.Assets.Logo;
        var relative = logo.StartsWith("assets/", StringComparison.Ordinal) ? logo["assets/".Length..] : logo;
        var route = $"/personas/{persona.Id}/assets/{relative}";
        var resolved = PersonaAssetResolver.Resolve(persona, relative);
        if (resolved is not null)
        {
            route += $"?v={PersonaAssetHash.ComputeHash(resolved)}";
        }
        return route;
    }

    /// <summary>Port of app.py's `_persona_menu_url` -- unlike the logo, the menu file is
    /// guaranteed to exist (PersonaCatalog.Load fails fast at startup otherwise), so this always
    /// carries a `?v=`.</summary>
    private static string BuildMenuUrl(Persona persona) =>
        $"/personas/{persona.Id}/menu.json?v={PersonaAssetHash.ComputeHash(persona.MenuPath)}";

    private static JsonObject ToJsonObject<T>(T value) =>
        JsonSerializer.SerializeToNode(value, SerializeOptions)!.AsObject();
}
