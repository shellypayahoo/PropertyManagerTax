using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;

namespace PropertyManager.Tests;

public sealed class AskHistoryTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;

    public AskHistoryTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task Saved_turns_come_back_after_a_restart()
    {
        var history = new AskHistory(dbFactory);
        await history.EnsureLoadedAsync();
        var applied = ProposedChange.Restored(1, "Add expense", ["$120 to Ace Plumbing"], ProposalState.Applied, "Expense #9 added.");
        var pending = ProposedChange.Restored(2, "Add vendor", ["Bob's HVAC"], ProposalState.Applied, null);
        pending.State = ProposalState.Pending;
        var turn = new AskTurnView { AskedAt = DateTimeOffset.Now, Question = "Add the plumber receipt" };
        turn.Files.Add("receipt.pdf");
        turn.Reply = new AssistantReply("Here are two changes.", [new AssistantLookup("read_uploaded_file", "receipt.pdf")], false, "Claude", [applied, pending]);
        await history.SaveAsync(turn);
        var unanswered = new AskTurnView { AskedAt = DateTimeOffset.Now, Question = "And the other one?", Error = "429" };
        await history.SaveAsync(unanswered);

        var restarted = new AskHistory(dbFactory);
        await restarted.EnsureLoadedAsync();

        Assert.Equal(["Add the plumber receipt", "And the other one?"], restarted.Turns.Select(t => t.Question));
        var back = restarted.Turns[0];
        Assert.Equal(["receipt.pdf"], back.Files);
        Assert.Equal("Here are two changes.", back.Reply!.Text);
        Assert.Equal("Claude", back.Reply.Provider);
        Assert.Single(back.Reply.Lookups);
        // A proposal still pending at the restart can't be applied any more.
        Assert.Equal([ProposalState.Applied, ProposalState.Expired], back.Reply.Changes.Select(c => c.State));
        Assert.Equal("Expense #9 added.", back.Reply.Changes[0].Result);
        Assert.Equal("429", restarted.Turns[1].Error);
        Assert.False(restarted.Conversation.IsEmpty);
    }

    [Fact]
    public async Task Clear_forgets_everything()
    {
        var history = new AskHistory(dbFactory);
        await history.EnsureLoadedAsync();
        var turn = new AskTurnView { AskedAt = DateTimeOffset.Now, Question = "How much rent came in?" };
        turn.Reply = new AssistantReply("$14,400.", [], false, "Claude");
        await history.SaveAsync(turn);
        history.Turns.Add(turn);
        history.Conversation.Remember(turn.Question, "$14,400.");

        await history.ClearAsync();

        Assert.Empty(history.Turns);
        Assert.True(history.Conversation.IsEmpty);
        var restarted = new AskHistory(dbFactory);
        await restarted.EnsureLoadedAsync();
        Assert.Empty(restarted.Turns);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
