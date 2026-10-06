namespace Backend.Configuration;

internal sealed class RequiredBackendOptions
{
    public const string MissingEnvironmentVariablesMessagePrefix = "Missing required environment variables: ";

    public static readonly IReadOnlyList<string> RequiredEnvironmentVariableNames =
    [
        BackendEnvironment.AzureOpenAiEastUs2Endpoint,
        BackendEnvironment.AzureOpenAiRealtimeDeployment,
        BackendEnvironment.AzureSearchEndpoint,
        BackendEnvironment.AzureSearchIndex,
    ];

    public string AzureOpenAiEastUs2Endpoint { get; set; } = string.Empty;
    public string AzureOpenAiRealtimeDeployment { get; set; } = string.Empty;
    public string AzureSearchEndpoint { get; set; } = string.Empty;
    public string AzureSearchIndex { get; set; } = string.Empty;

    public static void ConfigureFromEnvironment(RequiredBackendOptions options)
    {
        options.AzureOpenAiEastUs2Endpoint = BackendEnvironment.Get(BackendEnvironment.AzureOpenAiEastUs2Endpoint) ?? string.Empty;
        options.AzureOpenAiRealtimeDeployment = BackendEnvironment.Get(BackendEnvironment.AzureOpenAiRealtimeDeployment) ?? string.Empty;
        options.AzureSearchEndpoint = BackendEnvironment.Get(BackendEnvironment.AzureSearchEndpoint) ?? string.Empty;
        options.AzureSearchIndex = BackendEnvironment.Get(BackendEnvironment.AzureSearchIndex) ?? string.Empty;
    }

    public IReadOnlyList<string> GetMissingEnvironmentVariableNames()
    {
        var missing = new List<string>(RequiredEnvironmentVariableNames.Count);
        if (string.IsNullOrEmpty(AzureOpenAiEastUs2Endpoint))
        {
            missing.Add(BackendEnvironment.AzureOpenAiEastUs2Endpoint);
        }
        if (string.IsNullOrEmpty(AzureOpenAiRealtimeDeployment))
        {
            missing.Add(BackendEnvironment.AzureOpenAiRealtimeDeployment);
        }
        if (string.IsNullOrEmpty(AzureSearchEndpoint))
        {
            missing.Add(BackendEnvironment.AzureSearchEndpoint);
        }
        if (string.IsNullOrEmpty(AzureSearchIndex))
        {
            missing.Add(BackendEnvironment.AzureSearchIndex);
        }
        return missing;
    }
}
