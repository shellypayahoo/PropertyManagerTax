using Microsoft.Extensions.AI;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public class SmallFixesTests
{
    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/heic", true)]
    [InlineData("video/quicktime", true)]
    [InlineData("audio/mp4", true)]
    [InlineData("application/pdf", true)]
    [InlineData("Application/PDF; charset=binary", true)]
    [InlineData("text/html", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("application/xhtml+xml", false)]
    [InlineData("text/javascript", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData(null, false)]
    public void Only_pictures_video_audio_and_PDFs_open_in_the_browser(string? contentType, bool opens) =>
        Assert.Equal(opens, SafeFiles.OpensInBrowser(contentType));

    [Fact]
    public void A_short_conversation_is_sent_whole()
    {
        var history = Exchanges(5);
        Assert.Equal(history, AssistantService.Recent(history));
    }

    [Fact]
    public void A_long_conversation_sends_only_its_recent_part_starting_at_a_question()
    {
        var history = Exchanges(30);
        // A change note between exchanges makes the cut land on an answer; it must move on to the next question.
        history.Insert(history.Count - AssistantService.MaxRememberedMessages + 1, new ChatMessage(ChatRole.Assistant, "stray answer"));

        var recent = AssistantService.Recent(history).ToList();

        Assert.True(recent.Count <= AssistantService.MaxRememberedMessages);
        Assert.Equal(ChatRole.User, recent[0].Role);
        Assert.Equal("answer 30", recent[^1].Text);
        Assert.DoesNotContain(recent, m => m.Text == "question 1");
    }

    private static List<ChatMessage> Exchanges(int count) =>
        Enumerable.Range(1, count)
            .SelectMany(i => new[] { new ChatMessage(ChatRole.User, $"question {i}"), new ChatMessage(ChatRole.Assistant, $"answer {i}") })
            .ToList();
}
