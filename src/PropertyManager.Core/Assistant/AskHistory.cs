using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Assistant;

/// <summary>A question on the Ask page and what came back.</summary>
public class AskTurnView
{
    public int Id { get; set; }
    public DateTimeOffset AskedAt { get; init; }
    public string Question { get; init; } = "";
    public List<string> Files { get; } = [];
    public AssistantReply? Reply { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// The Ask page's conversation, shared by every browser tab and device and kept until it's cleared. The transcript is
/// saved in the database; attached files and the ability to apply pending proposals last while the app is running.
/// </summary>
public class AskHistory(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool loaded;

    public AssistantConversation Conversation { get; private set; } = new();
    public List<AskTurnView> Turns { get; } = [];

    /// <summary>Only one question is answered at a time, from any device.</summary>
    public SemaphoreSlim Busy { get; } = new(1, 1);

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (loaded)
        {
            return;
        }
        await gate.WaitAsync(ct);
        try
        {
            if (loaded)
            {
                return;
            }
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            foreach (var row in (await db.AskTurns.AsNoTracking().ToListAsync(ct)).OrderBy(t => t.Id))
            {
                var turn = new AskTurnView { Id = row.Id, AskedAt = row.AskedAt, Question = row.Question, Error = row.Error };
                turn.Files.AddRange(row.Files?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? []);
                if (row.Answer is not null)
                {
                    var lookups = row.LookupsJson is null ? [] : JsonSerializer.Deserialize<List<AssistantLookup>>(row.LookupsJson) ?? [];
                    var changes = (row.ChangesJson is null ? [] : JsonSerializer.Deserialize<List<SavedChange>>(row.ChangesJson) ?? [])
                        .Select(c => ProposedChange.Restored(c.Number, c.Summary, c.Details, c.State, c.Result))
                        .ToList();
                    turn.Reply = new AssistantReply(row.Answer, lookups, row.Declined, row.Provider ?? "", changes);
                    Conversation.Proposals.AddRange(changes);
                    if (!row.Declined)
                    {
                        Conversation.Remember(turn.Files.Count == 0 ? row.Question : $"{row.Question}\n[Attached earlier: {string.Join(", ", turn.Files)}; no longer available.]", row.Answer);
                    }
                }
                Turns.Add(turn);
            }
            loaded = true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Saves a turn (new or updated, e.g. after a change is applied).</summary>
    public async Task SaveAsync(AskTurnView turn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = turn.Id == 0 ? null : await db.AskTurns.FindAsync([turn.Id], ct);
        if (row is null)
        {
            row = new AskTurn { AskedAt = turn.AskedAt, Question = turn.Question };
            db.AskTurns.Add(row);
        }
        row.Files = turn.Files.Count == 0 ? null : string.Join('\n', turn.Files);
        row.Error = turn.Error;
        row.Answer = turn.Reply?.Text;
        row.Provider = turn.Reply?.Provider;
        row.Declined = turn.Reply?.Declined ?? false;
        row.LookupsJson = turn.Reply is null ? null : JsonSerializer.Serialize(turn.Reply.Lookups);
        row.ChangesJson = turn.Reply is null || turn.Reply.Changes.Count == 0 ? null
            : JsonSerializer.Serialize(turn.Reply.Changes.Select(c => new SavedChange(c.Number, c.Summary, [.. c.Details], c.State, c.Result)));
        await db.SaveChangesAsync(ct);
        turn.Id = row.Id;
    }

    /// <summary>Starts over: forgets every question, answer, attached file, and pending proposal.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AskTurns.ExecuteDeleteAsync(ct);
        Turns.Clear();
        Conversation = new AssistantConversation();
        loaded = true;
    }

    private record SavedChange(int Number, string Summary, List<string> Details, ProposalState State, string? Result);
}
