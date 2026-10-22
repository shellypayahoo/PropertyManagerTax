using System.ClientModel;
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
                    new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint!) })
                .AsIChatClient(),
            AssistantProviderType.Ollama => new OllamaApiClient(new Uri(provider.Endpoint ?? "http://localhost:11434"), provider.Model),
            _ => throw new NotSupportedException($"Unknown provider type {provider.Type}."),
        };

        return new ChatClientBuilder(inner)
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 15)
            .Build();
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
