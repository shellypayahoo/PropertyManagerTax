using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Foundry;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace PropertyManager.Core.Assistant;

/// <summary>Builds a Microsoft.Extensions.AI chat client for a configured provider, with tool calling enabled.</summary>
public static class ChatClientFactory
{
    public const int MaxOutputTokens = 16000;

    /// <param name="http">Only for tests: where OpenAI-style requests are sent.</param>
    public static IChatClient Create(AssistantProviderOptions provider, HttpClient? http = null)
    {
        IChatClient inner = provider.Type switch
        {
            // Claude uses the beta surface so the refusal-fallback handler applies (it only handles beta requests).
            AssistantProviderType.Anthropic => AnthropicClient(provider).Beta.AsIChatClient(provider.Model, MaxOutputTokens),
            AssistantProviderType.AnthropicFoundry => FoundryClaudeClient(provider).Beta.AsIChatClient(provider.Model, MaxOutputTokens),
            AssistantProviderType.AzureFoundry => OpenAIClient(provider, provider.ApiKey!, FoundryEndpoint(provider.Endpoint!), http),
            AssistantProviderType.Ollama => new OllamaApiClient(new Uri(provider.Endpoint ?? "http://localhost:11434"), provider.Model),
            AssistantProviderType.OpenAI => OpenAIClient(
                provider,
                string.IsNullOrWhiteSpace(provider.ApiKey) ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")! : provider.ApiKey,
                string.IsNullOrWhiteSpace(provider.Endpoint) ? null : new Uri(provider.Endpoint),
                http),
            _ => throw new NotSupportedException($"Unknown provider type {provider.Type}."),
        };

        return new ChatClientBuilder(inner)
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 15)
            .Build();
    }

    /// <summary>
    /// The resource's OpenAI-style v1 endpoint, <c>https://&lt;resource&gt;.…azure.com/openai/v1/</c>. The Foundry portal
    /// shows several URLs for the same resource (project endpoint, a deployment's target URI, the bare resource), so
    /// for Azure hosts only the host name is kept.
    /// </summary>
    public static Uri FoundryEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint.Trim());
        return uri.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase)
            ? new Uri($"https://{uri.Host}/openai/v1/")
            : new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    /// <summary>
    /// An Azure AI Foundry or OpenAI model, through the Responses API unless the provider asks for chat completions.
    /// Newer reasoning models only accept tools (the assistant's lookups) through the Responses API.
    /// </summary>
    private static IChatClient OpenAIClient(AssistantProviderOptions provider, string apiKey, Uri? endpoint, HttpClient? http)
    {
        var credential = new ApiKeyCredential(apiKey);
        // Azure answers 429 when the deployment's tokens-per-minute limit is hit; wait (honoring Retry-After) and retry.
        var retry = new ClientRetryPolicy(maxRetries: 6);
        var transport = http is null ? null : new HttpClientPipelineTransport(http);
        if (provider.UsesChatCompletions)
        {
            var options = new OpenAIClientOptions { Endpoint = endpoint, RetryPolicy = retry };
            if (transport is not null)
            {
                options.Transport = transport;
            }
            return new OpenAI.Chat.ChatClient(provider.Model, credential, options).AsIChatClient();
        }
        // The OpenAI library still labels its Responses API client "for evaluation" (OPENAI001), though the API itself
        // is OpenAI's and Azure's current one; it's the only way newer reasoning models accept tools.
#pragma warning disable OPENAI001
        var responsesOptions = new OpenAI.Responses.ResponsesClientOptions { Endpoint = endpoint, RetryPolicy = retry };
        if (transport is not null)
        {
            responsesOptions.Transport = transport;
        }
        return new OpenAI.Responses.ResponsesClient(credential, responsesOptions).AsIChatClient(provider.Model);
#pragma warning restore OPENAI001
    }

    /// <summary>Whether requests for this provider should run inside a <see cref="BetaFallbackState"/> scope.</summary>
    public static bool UsesRefusalFallback(AssistantProviderOptions provider) =>
        provider.Type is AssistantProviderType.Anthropic or AssistantProviderType.AnthropicFoundry;

    // If Claude declines a request (stop_reason "refusal"), retry it on Claude Opus 4.8.
    private static BetaRefusalFallbackHandler RefusalFallback() => new() { Fallbacks = [new(Model.ClaudeOpus4_8)] };

    private static AnthropicClient AnthropicClient(AssistantProviderOptions provider) =>
        string.IsNullOrWhiteSpace(provider.ApiKey)
            ? new AnthropicClient { Handlers = [RefusalFallback()] } // uses ANTHROPIC_API_KEY
            : new AnthropicClient { ApiKey = provider.ApiKey, Handlers = [RefusalFallback()] };

    private static AnthropicFoundryClient FoundryClaudeClient(AssistantProviderOptions provider) =>
        new(new AnthropicFoundryApiKeyCredentials(provider.ApiKey!, provider.Resource!)) { Handlers = [RefusalFallback()] };
}
