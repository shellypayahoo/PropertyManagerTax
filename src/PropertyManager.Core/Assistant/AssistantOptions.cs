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
    /// <summary>
    /// What the Ask page and Assistant:DefaultProvider call this choice: the provider's key in
    /// <see cref="AssistantOptions.Providers"/> for its main model, or "key/model" for one of its <see cref="Models"/>.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The provider's key in <see cref="AssistantOptions.Providers"/>, where its settings live.</summary>
    public string ProviderName { get; set; } = "";

    /// <summary>Shown in model lists, e.g. "AzureFoundry: gpt-5-mini".</summary>
    public string Label => $"{ProviderName}: {Model}";
    public AssistantProviderType Type { get; set; }

    /// <summary>Model id, or the deployment name for Azure AI Foundry.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Other models (Foundry deployments) reachable with the same endpoint and key, offered next to <see cref="Model"/>
    /// on the Ask page. Set on the Settings page.
    /// </summary>
    public List<string> Models { get; set; } = [];

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

    /// <summary>The same provider with another of its models.</summary>
    public AssistantProviderOptions WithModel(string model)
    {
        var copy = (AssistantProviderOptions)MemberwiseClone();
        copy.Model = model;
        copy.Name = $"{ProviderName}/{model}";
        copy.Models = [];
        return copy;
    }

    // Placeholder values in appsettings.json look like "<your-resource>".
    internal static bool IsFilled(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('<');
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

    /// <summary>Every model to choose from: each provider's main model, followed by its other models.</summary>
    public IReadOnlyList<AssistantProviderOptions> Choices()
    {
        var choices = new List<AssistantProviderOptions>();
        foreach (var (key, provider) in Providers)
        {
            provider.Name = provider.ProviderName = key;
            choices.Add(provider);
            foreach (var model in provider.Models.Select(m => m.Trim())
                .Where(m => AssistantProviderOptions.IsFilled(m) && !string.Equals(m, provider.Model, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                choices.Add(provider.WithModel(model));
            }
        }
        return choices;
    }

    /// <summary>The model to start on: <see cref="DefaultProvider"/>, else the first one that's set up.</summary>
    public AssistantProviderOptions? Default()
    {
        var choices = Choices();
        return choices.FirstOrDefault(p => p.Name == DefaultProvider) ?? choices.FirstOrDefault(p => p.IsConfigured) ?? choices.FirstOrDefault();
    }
}
