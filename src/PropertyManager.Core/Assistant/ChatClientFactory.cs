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

    public static IChatClient Create(AssistantProviderOptions provider)
    {
        IChatClient inner = provider.Type switch
        {
            // Claude uses the beta surface so the refusal-fallback handler applies (it only handles beta requests).
            AssistantProviderType.Anthropic => AnthropicClient(provider).Beta.AsIChatClient(provider.Model, MaxOutputTokens),
            AssistantProviderType.AnthropicFoundry => FoundryClaudeClient(provider).Beta.AsIChatClient(provider.Model, MaxOutputTokens),
            AssistantProviderType.AzureFoundry => new OpenAI.Chat.ChatClient(
                    provider.Model,
                    new ApiKeyCredential(provider.ApiKey!),
                    new OpenAIClientOptions
                    {
                        Endpoint = FoundryEndpoint(provider.Endpoint!),
                        // Azure answers 429 when the deployment's tokens-per-minute limit is hit; wait (honoring Retry-After) and retry.
                        RetryPolicy = new ClientRetryPolicy(maxRetries: 6),
                    })
                .AsIChatClient(),
            AssistantProviderType.Ollama => new OllamaApiClient(new Uri(provider.Endpoint ?? "http://localhost:11434"), provider.Model),
            AssistantProviderType.OpenAI => new OpenAI.Chat.ChatClient(
                    provider.Model,
                    new ApiKeyCredential(string.IsNullOrWhiteSpace(provider.ApiKey) ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")! : provider.ApiKey),
                    new OpenAIClientOptions
                    {
                        Endpoint = string.IsNullOrWhiteSpace(provider.Endpoint) ? null : new Uri(provider.Endpoint),
                        RetryPolicy = new ClientRetryPolicy(maxRetries: 6),
                    })
                .AsIChatClient(),
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
