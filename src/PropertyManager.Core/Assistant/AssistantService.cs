using Anthropic.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace PropertyManager.Core.Assistant;

/// <summary>A tool the assistant called while answering, shown on the Ask page so it's clear what was looked up.</summary>
public record AssistantLookup(string Tool, string Arguments);

public record AssistantReply(string Text, IReadOnlyList<AssistantLookup> Lookups, bool Declined, string Provider);

/// <summary>One conversation on the Ask page.</summary>
public class AssistantConversation
{
    internal List<ChatMessage> History { get; } = [];
    internal string? ProviderName { get; set; }

    // Keeps a conversation on the same Claude model once a refusal fallback has switched it.
    internal BetaFallbackState FallbackState { get; } = BetaFallbackState.Create();

    public bool IsEmpty => History.Count == 0;
}

public class AssistantService(IOptionsMonitor<AssistantOptions> options, AssistantTools tools, Func<AssistantProviderOptions, IChatClient>? clientFactory = null)
{
    private readonly Func<AssistantProviderOptions, IChatClient> createClient = clientFactory ?? ChatClientFactory.Create;

    public IReadOnlyList<AssistantProviderOptions> Providers =>
        options.CurrentValue.Providers.Select(p => { p.Value.Name = p.Key; return p.Value; }).ToList();

    public async Task<AssistantReply> AskAsync(AssistantConversation conversation, string providerName, string question, CancellationToken ct = default)
    {
        var provider = Providers.FirstOrDefault(p => p.Name == providerName)
            ?? throw new InvalidOperationException($"No AI model named \"{providerName}\" is set up.");
        if (!provider.IsConfigured)
        {
            throw new InvalidOperationException($"\"{provider.Name}\" is missing its settings. See the Ask section of the README.");
        }

        if (conversation.ProviderName is not null && conversation.ProviderName != provider.Name)
        {
            // Another provider can't read the previous one's tool calls or reasoning, so carry over only the questions and answers.
            var plain = conversation.History
                .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
                .Select(m => new ChatMessage(m.Role, m.Text))
                .Where(m => m.Text.Length > 0)
                .ToList();
            conversation.History.Clear();
            conversation.History.AddRange(plain);
        }
        conversation.ProviderName = provider.Name;

        var userMessage = new ChatMessage(ChatRole.User, question);
        List<ChatMessage> messages = [new(ChatRole.System, SystemPrompt(DateTime.Now)), .. conversation.History, userMessage];
        var chatOptions = new ChatOptions { Tools = tools.All() };

        using var client = createClient(provider);
        ChatResponse response;
        using (conversation.FallbackState.Use())
        {
            response = await client.GetResponseAsync(messages, chatOptions, ct);
        }

        var declined = response.FinishReason == ChatFinishReason.ContentFilter;
        var lookups = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => new AssistantLookup(c.Name, Describe(c.Arguments)))
            .ToList();
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = declined ? "The model declined to answer this question." : "(The model returned no answer.)";
        }

        // A declined turn isn't kept, so it doesn't affect later questions.
        if (!declined)
        {
            conversation.History.Add(userMessage);
            conversation.History.AddRange(response.Messages);
        }
        return new AssistantReply(text, lookups, declined, provider.Name);
    }

    private static string Describe(IDictionary<string, object?>? arguments) =>
        arguments is null ? "" : string.Join(", ", arguments.Where(a => a.Value is not null && a.Value.ToString() != "").Select(a => $"{a.Key}: {a.Value}"));

    public static string SystemPrompt(DateTime now) => $"""
        You help the owner of four rental units in Washington, DC answer questions about their rental business. Today is {now:dddd, MMMM d, yyyy}.

        You can look things up with the tools: text messages exchanged with tenants and vendors, the contacts those messages are with, properties, units, leases and tenants, rent payments, expenses, maintenance requests, and the annual Schedule E profit and loss. Look things up rather than guessing, and make as many lookups as the question needs. Message search matches every word given, so search for one or two distinctive words, try other wordings (for example "leak", "water", "drip") if the first search finds nothing, and read the surrounding conversation with get_conversation when context matters.

        When an answer relies on text messages, say who said it and when (date, and time if it matters), quoting the key words briefly. When it relies on rent or expense records, give the dates and amounts. If the records don't answer the question, say so plainly and say what you searched; don't fill gaps with assumptions. "Me" in a conversation is the owner.

        Keep answers short and direct, using plain text or simple lists. You can read data but cannot change it; if the owner wants something recorded, tell them which page of the app to use (Rent, Expenses, Leases, Maintenance, Messages).
        """;
}
