using Microsoft.Extensions.Options;

namespace Backend.Configuration;

internal sealed class RequiredBackendOptionsValidator : IValidateOptions<RequiredBackendOptions>
{
    public ValidateOptionsResult Validate(string? name, RequiredBackendOptions options)
    {
        var missing = options.GetMissingEnvironmentVariableNames();
        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                RequiredBackendOptions.MissingEnvironmentVariablesMessagePrefix + string.Join(", ", missing));
    }

    public static string GetMissingEnvironmentVariables(OptionsValidationException exception)
    {
        var failure = exception.Failures.FirstOrDefault();
        if (failure is not null &&
            failure.StartsWith(RequiredBackendOptions.MissingEnvironmentVariablesMessagePrefix, StringComparison.Ordinal))
        {
            return failure[RequiredBackendOptions.MissingEnvironmentVariablesMessagePrefix.Length..];
        }
        return string.Join("; ", exception.Failures);
    }
}
