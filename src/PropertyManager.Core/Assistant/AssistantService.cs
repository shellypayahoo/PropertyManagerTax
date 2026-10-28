using Anthropic.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace PropertyManager.Core.Assistant;

/// <summary>A tool the assistant called while answering, shown on the Ask page so it's clear what was looked up.</summary>
public record AssistantLookup(string Tool, string Arguments);

public record AssistantReply(string Text, IReadOnlyList<AssistantLookup> Lookups, bool Declined, string Provider, IReadOnlyList<ProposedChange>? Proposals = null)
{
    public IReadOnlyList<ProposedChange> Changes => Proposals ?? [];
}

/// <summary>One conversation on the Ask page.</summary>
public class AssistantConversation
{
    internal List<ChatMessage> History { get; } = [];
    internal string? ProviderName { get; set; }

    // Keeps a conversation on the same Claude model once a refusal fallback has switched it.
    internal BetaFallbackState FallbackState { get; } = BetaFallbackState.Create();

    public bool IsEmpty => History.Count == 0;

    /// <summary>Rebuilds what the model remembers from a saved question and answer.</summary>
    public void Remember(string question, string? answer)
    {
        History.Add(new ChatMessage(ChatRole.User, question));
        if (!string.IsNullOrWhiteSpace(answer))
        {
            History.Add(new ChatMessage(ChatRole.Assistant, answer));
        }
    }

    /// <summary>Files attached to questions in this conversation; the assistant can read them again or save them.</summary>
    public List<UploadedFile> Files { get; } = [];

    /// <summary>Changes the assistant proposed, waiting for the owner to apply or dismiss them.</summary>
    public List<ProposedChange> Proposals { get; } = [];

    /// <summary>Proposals made while answering the current question.</summary>
    internal List<ProposedChange> NewProposals { get; } = [];
}

public class AssistantService(
    IOptionsMonitor<AssistantOptions> options, AssistantTools tools, Func<AssistantProviderOptions, IChatClient>? clientFactory = null,
    AssistantActions? actions = null, Messaging.IImageConverter? images = null)
{
    private readonly Func<AssistantProviderOptions, IChatClient> createClient = clientFactory ?? (p => ChatClientFactory.Create(p));

    public bool AllowPhotosByDefault => options.CurrentValue.AllowPhotos;

    /// <summary>The configured default provider, or else the first one that's set up.</summary>
    public AssistantProviderOptions? DefaultProvider =>
        Providers.FirstOrDefault(p => p.Name == options.CurrentValue.DefaultProvider)
        ?? Providers.FirstOrDefault(p => p.IsConfigured)
        ?? Providers.FirstOrDefault();

    public IReadOnlyList<AssistantProviderOptions> Providers =>
        options.CurrentValue.Providers.Select(p => { p.Value.Name = p.Key; return p.Value; }).ToList();

    /// <param name="allowPhotos">Whether the assistant may look at photos (each one it looks at is an extra request with the image).</param>
    /// <summary>Reads files the owner attached so they can go with a question (photos converted to JPEG where possible).</summary>
    public Task<UploadedFile> PrepareFileAsync(string fileName, byte[] content, CancellationToken ct = default) =>
        UploadedFile.PrepareAsync(fileName, content, images, ct);

    /// <param name="files">Files attached to this question; added to the conversation so later questions can use them.</param>
    public async Task<AssistantReply> AskAsync(AssistantConversation conversation, string providerName, string question, CancellationToken ct = default, bool allowPhotos = false,
        IReadOnlyList<UploadedFile>? files = null)
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

        // The files go with this question in full; later questions see only their names and can read them again.
        var attached = files ?? [];
        conversation.Files.AddRange(attached);
        List<AIContent> contents = [new TextContent(question)];
        foreach (var file in attached)
        {
            contents.Add(new TextContent(file.Describe()));
            if (file.Image is { } image)
            {
                contents.Add(new DataContent(image, file.ImageType!));
            }
        }
        var userMessage = new ChatMessage(ChatRole.User, contents);
        var remembered = new ChatMessage(ChatRole.User, attached.Count == 0 ? question
            : $"{question}\n[Attached: {string.Join(", ", attached.Select(f => f.Name))}. read_uploaded_file reads them again.]");

        List<ChatMessage> messages = [new(ChatRole.System, SystemPrompt(DateTime.Now)), .. conversation.History, userMessage];
        using var client = createClient(provider);
        tools.PhotoDescriber = allowPhotos ? (bytes, mediaType, ask, token) => DescribePhotoAsync(client, bytes, mediaType, ask, token) : null;
        conversation.NewProposals.Clear();
        if (actions is not null)
        {
            actions.Conversation = conversation;
        }
        var chatOptions = new ChatOptions { Tools = actions is null ? tools.All() : [.. tools.All(), .. actions.All()] };
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
            // Keep only the question and answer: re-sending earlier lookups with every question uses up the provider's
            // tokens-per-minute limit quickly, and the model can look things up again when needed.
            conversation.History.Add(remembered);
            conversation.History.Add(new ChatMessage(ChatRole.Assistant, text));
        }
        return new AssistantReply(text, lookups, declined, provider.Name, [.. conversation.NewProposals]);
    }

    /// <summary>Carries out a proposed change the owner approved. Returns what happened.</summary>
    public async Task<string> ApplyAsync(AssistantConversation conversation, ProposedChange change, CancellationToken ct = default)
    {
        if (change.State != ProposalState.Pending)
        {
            return change.Result ?? "";
        }
        try
        {
            change.Result = await change.Apply(ct);
            change.State = ProposalState.Applied;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException or IOException)
        {
            change.Result = $"Couldn't apply: {ex.Message}";
            change.State = ProposalState.Failed;
        }
        // So follow-up questions know what was (or wasn't) done.
        conversation.History.Add(new ChatMessage(ChatRole.User, $"[Change {change.Number} \"{change.Summary}\": {(change.State == ProposalState.Applied ? "applied by the owner" : "failed")}. {change.Result}]"));
        return change.Result;
    }

    public void Dismiss(AssistantConversation conversation, ProposedChange change)
    {
        if (change.State == ProposalState.Pending)
        {
            change.State = ProposalState.Dismissed;
            conversation.History.Add(new ChatMessage(ChatRole.User, $"[Change {change.Number} \"{change.Summary}\" was dismissed by the owner; it wasn't made.]"));
        }
    }

    /// <summary>A separate request to the same model with just the photo and the question, so it works with any provider.</summary>
    private static async Task<string> DescribePhotoAsync(IChatClient client, byte[] bytes, string mediaType, string question, CancellationToken ct)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You look at photos for the owner of rental properties. Answer the question about the photo plainly and specifically: what it shows, any damage or condition issues, and any readable text, amounts, or dates. Say so if the photo doesn't show what's asked."),
            new(ChatRole.User, [new TextContent(question), new DataContent(bytes, mediaType)]),
        ];
        var response = await client.GetResponseAsync(messages, new ChatOptions(), ct);
        return response.FinishReason == ChatFinishReason.ContentFilter || string.IsNullOrWhiteSpace(response.Text)
            ? "(the model didn't describe this photo)"
            : response.Text;
    }

    private static string Describe(IDictionary<string, object?>? arguments) =>
        arguments is null ? "" : string.Join(", ", arguments.Where(a => a.Value is not null && a.Value.ToString() != "").Select(a => $"{a.Key}: {a.Value}"));

    public static string SystemPrompt(DateTime now) => $"""
        You help the owner of four rental units in Washington, DC answer questions about their rental business. Today is {now:dddd, MMMM d, yyyy}.

        You can look things up with the tools: text messages and emails exchanged with tenants and vendors (with the photos and files sent in them), the contacts those messages are with, business documents (LLC papers, tax returns, insurance policies, deeds and closing papers, loan papers, bank statements), uploaded lease documents and email attachments (signed leases, addenda, inspection reports), appointments on their synced Outlook calendars, properties, units, leases and tenants, rent payments, expenses, maintenance requests, and the annual Schedule E profit and loss. Look things up rather than guessing, and make as many lookups as the question needs. Message search matches every word given, so search for one or two distinctive words, try other wordings (for example "leak", "water", "drip") if the first search finds nothing, and read the surrounding conversation with get_conversation when context matters.

        For facts about the properties themselves (when each was bought, purchase price, how long owned, units, rents, bedrooms), use list_properties_and_tenants first and answer for every property; say so when a detail isn't recorded for one of them rather than leaving it out.

        For questions about lease terms or rules (pets, late fees, repairs, utilities, notice periods), search the uploaded documents and read the relevant part. When an answer relies on a document, name the file and page and quote the key wording briefly. The lease records hold only dates, rent, and deposit; the documents hold the actual terms. For questions about the LLC, insurance, closing costs, loans, or taxes, search the business documents the same way. The owner can keep some documents (usually tax returns) away from you; if you can't find one, say it may be kept private and that they can open it on the Documents page.

        Tenants and vendors communicate by both text and email, so for questions about what someone said or agreed, search both. When an answer relies on text messages or emails, say who said it and when (date, and time if it matters), give an email's subject, and quote the key words briefly. When it relies on rent or expense records, give the dates and amounts. If the records don't answer the question, say so plainly and say what you searched; don't fill gaps with assumptions. "Me" in a conversation is the owner.

        The owner may attach files (receipts, invoices, leases, photos, spreadsheets) to a question; their text or image comes with the question, and read_uploaded_file reads them again later.

        You can propose changes with the propose_ tools: adding or editing an expense (with an attached receipt), recording a rent payment, adding or updating a maintenance request, adding a vendor, updating a tenant's contact details, or saving an attached file as a receipt, a lease document, or a business document (Documents page; give it the type and a short description like "Closing" or "Tax 2026"). You can also propose adding an appointment to their Outlook calendar (propose_add_calendar_event): a repair visit for a maintenance request, a showing or inspection, or a reminder before a lease ends. A proposal shows the owner a card with Apply and Dismiss buttons; nothing changes until they click Apply. Look things up first so proposals use the right property, unit, tenant, vendor, and numbers; propose only what the owner asked for or clearly agreed to; then say plainly what you proposed and that it's waiting for their approval. Never say a change has been made. Notes like "[Change 2 ...: applied by the owner]" in the conversation tell you what happened to earlier proposals. You can't delete anything; for that, tell them which page to use.

        Keep answers short and direct, using plain text or simple lists.
        """;
}
