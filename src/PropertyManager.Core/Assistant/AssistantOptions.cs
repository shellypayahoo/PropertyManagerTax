namespace PropertyManager.Core.Assistant;

public enum AssistantProviderType
{
    /// <summary>Claude through the Anthropic API (official Anthropic SDK).</summary>
    Anthropic,

    /// <summary>Claude deployed in Microsoft (Azure AI) Foundry (official Anthropic SDK, Foundry client).</summary>
    AnthropicFoundry,

    /// <summary>Any model deployed in Azure AI Foundry / Azure OpenAI, through its OpenAI-style v1 endpoint.</summary>
    AzureFoundry,

    /// <summary>A model running locally in Ollama; nothing leaves the Mac.</summary>
    Ollama,

    /// <summary>An OpenAI model through the OpenAI API (or any OpenAI-compatible endpoint).</summary>
    OpenAI,
}

/// <summary>One AI model the Ask page can use. Keys come from user secrets or environment variables, never source control.</summary>
public class AssistantProviderOptions
{
    /// <summary>Filled in from the provider's key in <see cref="AssistantOptions.Providers"/>.</summary>
    public string Name { get; set; } = "";
    public AssistantProviderType Type { get; set; }

    /// <summary>Model id, or the deployment name for Azure AI Foundry.</summary>
    public string Model { get; set; } = "";

    /// <summary>Azure AI Foundry endpoint (…/openai/v1/) or Ollama URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Foundry resource name, for Claude on Foundry.</summary>
    public string? Resource { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>
    /// For AzureFoundry and OpenAI: "Responses" (the default; needed for tools with newer reasoning models) or
    /// "ChatCompletions" for services that only offer the older chat completions API.
    /// </summary>
    public string? Api { get; set; }

    public bool UsesChatCompletions => string.Equals(Api, "ChatCompletions", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether enough settings are filled in to try this provider.</summary>
    public bool IsConfigured => MissingSettings.Count == 0;

    /// <summary>The settings still empty or holding a placeholder like "&lt;deployment-name&gt;".</summary>
    public IReadOnlyList<string> MissingSettings => Type switch
    {
        AssistantProviderType.Anthropic => IsFilled(ApiKey) || IsFilled(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? [] : [nameof(ApiKey)],
        AssistantProviderType.AnthropicFoundry => Missing((nameof(Resource), Resource), (nameof(ApiKey), ApiKey)),
        AssistantProviderType.AzureFoundry => Missing((nameof(Model), Model), (nameof(Endpoint), Endpoint), (nameof(ApiKey), ApiKey)),
        AssistantProviderType.Ollama => Missing((nameof(Model), Model)),
        AssistantProviderType.OpenAI => [.. Missing((nameof(Model), Model)),
            .. IsFilled(ApiKey) || IsFilled(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) ? Array.Empty<string>() : [nameof(ApiKey)]],
        _ => ["Type"],
    };

    private static List<string> Missing(params (string Name, string? Value)[] settings) =>
        settings.Where(s => !IsFilled(s.Value)).Select(s => s.Name).ToList();

    /// <summary>Who receives the texts and records looked up to answer a question.</summary>
    public string DataNotice => Type switch
    {
        AssistantProviderType.Anthropic => "The texts and records needed to answer are sent to Anthropic.",
        AssistantProviderType.AnthropicFoundry or AssistantProviderType.AzureFoundry => "The texts and records needed to answer are sent to your Azure AI Foundry resource.",
        AssistantProviderType.OpenAI => IsFilled(Endpoint) ? $"The texts and records needed to answer are sent to {Endpoint}." : "The texts and records needed to answer are sent to OpenAI.",
        _ => "This model runs on this Mac; nothing is sent elsewhere.",
    };

    // Placeholder values in appsettings.json look like "<your-resource>".
    private static bool IsFilled(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('<');
}

public class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>The provider the Ask page starts on; when empty, the first one that's set up.</summary>
    public string? DefaultProvider { get; set; }

    /// <summary>Whether "Let it look at photos" starts ticked on the Ask page.</summary>
    public bool AllowPhotos { get; set; } = true;

    /// <summary>Providers by name, so a key can be set as e.g. <c>Assistant:Providers:Claude:ApiKey</c>.</summary>
    public Dictionary<string, AssistantProviderOptions> Providers { get; set; } = [];
}
