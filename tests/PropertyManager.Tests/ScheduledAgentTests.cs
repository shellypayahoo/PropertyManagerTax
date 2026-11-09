using Microsoft.Extensions.Logging.Abstractions;
using PropertyManager.Web.Agents;

namespace PropertyManager.Tests;

public sealed class ScheduledAgentTests
{
    private sealed class TestAgent() : ScheduledAgent(TimeProvider.System, NullLogger.Instance)
    {
        public TimeSpan Every { get; set; } = TimeSpan.FromHours(3);
        public List<bool> Runs { get; } = [];
        public Func<bool, string?> Work { get; set; } = full => $"ran {(full ? "full" : "quick")}";
        public TaskCompletionSource Ran { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override TimeSpan Interval => Every;

        public void Now(bool full) => Wake(full);

        public async Task WaitForRunAsync()
        {
            await Ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Let the agent record LastRunAt after the work returns.
            for (var i = 0; i < 100 && IsRunning; i++)
            {
                await Task.Delay(10);
            }
        }

        protected override Task<string?> WorkAsync(bool full, CancellationToken ct)
        {
            Runs.Add(full);
            try
            {
                return Task.FromResult(Work(full));
            }
            finally
            {
                Ran.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task Runs_when_woken_and_schedules_the_next_run_from_the_setting()
    {
        var agent = new TestAgent();
        await agent.StartAsync(CancellationToken.None);
        try
        {
            agent.Now(full: false);
            await agent.WaitForRunAsync();

            Assert.Equal([false], agent.Runs);
            Assert.Equal("ran quick", agent.LastResult);
            Assert.Equal(agent.LastRunAt!.Value + TimeSpan.FromHours(3), agent.NextRunAt);

            // A new interval from Settings moves the next run at once.
            agent.Every = TimeSpan.FromHours(12);
            Assert.Equal(agent.LastRunAt!.Value + TimeSpan.FromHours(12), agent.NextRunAt);

            agent.Now(full: true);
            await agent.WaitForRunAsync();
            Assert.Equal([false, true], agent.Runs);
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failed_run_is_reported_and_the_next_one_still_happens()
    {
        var agent = new TestAgent { Work = _ => throw new HttpRequestException("network down") };
        await agent.StartAsync(CancellationToken.None);
        try
        {
            agent.Now(full: true);
            await agent.WaitForRunAsync();
            Assert.Equal("Failed: network down", agent.LastResult);

            agent.Work = _ => "fine";
            agent.Now(full: true);
            await agent.WaitForRunAsync();
            Assert.Equal("fine", agent.LastResult);
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }
    }
}
