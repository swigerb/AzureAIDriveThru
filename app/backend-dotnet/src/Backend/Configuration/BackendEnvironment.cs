namespace Backend.Configuration;

internal static class BackendEnvironment
{
    public const string AzureAiFoundryEndpoint = "AZURE_AI_FOUNDRY_ENDPOINT";
    public const string AzureAiModelDeployments = "AZURE_AI_MODEL_DEPLOYMENTS";
    public const string AzureOpenAiEastUs2ApiKey = "AZURE_OPENAI_EASTUS2_API_KEY";
    public const string AzureOpenAiEastUs2Endpoint = "AZURE_OPENAI_EASTUS2_ENDPOINT";
    public const string AzureOpenAiRealtimeDeployment = "AZURE_OPENAI_REALTIME_DEPLOYMENT";
    public const string AzureOpenAiRealtimeReasoningEffort = "AZURE_OPENAI_REALTIME_REASONING_EFFORT";
    public const string AzureOpenAiRealtimeReasoningModel = "AZURE_OPENAI_REALTIME_REASONING_MODEL";
    public const string AzureOpenAiRealtimeTranscriptionModel = "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL";
    public const string AzureOpenAiRealtimeVoiceChoice = "AZURE_OPENAI_REALTIME_VOICE_CHOICE";
    public const string AzureSearchApiKey = "AZURE_SEARCH_API_KEY";
    public const string AzureSearchContentField = "AZURE_SEARCH_CONTENT_FIELD";
    public const string AzureSearchEmbeddingField = "AZURE_SEARCH_EMBEDDING_FIELD";
    public const string AzureSearchEndpoint = "AZURE_SEARCH_ENDPOINT";
    public const string AzureSearchIdentifierField = "AZURE_SEARCH_IDENTIFIER_FIELD";
    public const string AzureSearchSemanticConfiguration = "AZURE_SEARCH_SEMANTIC_CONFIGURATION";
    public const string AzureSearchSemanticRanker = "AZURE_SEARCH_SEMANTIC_RANKER";
    public const string AzureSearchUseVectorQuery = "AZURE_SEARCH_USE_VECTOR_QUERY";
    public const string BackendDotnetUri = "BACKEND_DOTNET_URI";
    public const string BackendUri = "BACKEND_URI";
    public const string ConfigPath = "CONFIG_PATH";
    public const string DefaultPersona = "DEFAULT_PERSONA";
    public const string Host = "HOST";
    public const string Personas = "PERSONAS";
    public const string PersonasDir = "PERSONAS_DIR";
    public const string Port = "PORT";
    public const string RunningInProduction = "RUNNING_IN_PRODUCTION";
    public const string StaticFilesDir = "STATIC_FILES_DIR";

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);
}
