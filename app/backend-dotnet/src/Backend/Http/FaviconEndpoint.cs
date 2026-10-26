namespace Backend.Http;

/// <summary>
/// Anonymous, empty answer to the browser's automatic <c>GET /favicon.ico</c> probe. index.html
/// carries no <c>&lt;link rel="icon"&gt;</c> (issue #80 F3: PersonaProvider injects the active
/// persona's own favicon at runtime), so a first page load makes the browser ask the origin root
/// for one. Left unmapped, that probe is still subject to Entra mode's fallback authorization
/// policy (the policy applies to unmatched requests too) and comes back 401 -- a console error on
/// every sign-in. A 204 tells the browser there is no root icon without an error; the persona's
/// own favicon is still served from <c>/personas/{id}/assets/</c> exactly as before.
/// </summary>
internal static class FaviconEndpoint
{
    public const string Route = "/favicon.ico";

    public const string CacheControlValue = "public, max-age=86400";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], Handle).AllowAnonymous();

    private static IResult Handle(HttpContext context)
    {
        context.Response.Headers.CacheControl = CacheControlValue;
        return Results.NoContent();
    }
}
