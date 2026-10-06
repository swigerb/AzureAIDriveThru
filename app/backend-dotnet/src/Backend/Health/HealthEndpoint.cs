using Backend.Personas;

namespace Backend.Health;

/// <summary>Port of app/backend/app.py's _health_handler. Same JSON shape: {status, version,
/// checks, personas} with personas additive once the catalog has loaded (issue #70), 200 if every
/// check passed else 503.</summary>
public static class HealthEndpoint
{
    public const string AppVersion = "1.0.0";

    public static IResult Handle(StartupChecks checks, PersonaCatalog? catalog)
    {
        var allOk = checks.AllPassed;
        var body = new Dictionary<string, object?>
        {
            ["status"] = allOk ? "healthy" : "unhealthy",
            ["version"] = AppVersion,
            ["checks"] = checks.Checks,
        };
        if (catalog is not null)
        {
            body["personas"] = catalog.Ids;
        }

        return Results.Json(body, statusCode: allOk ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
