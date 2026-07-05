using Sorcerer.Llm.Diagnostics;
using Xunit;

namespace Sorcerer.Tests;

[Collection("LlmTrace")]
public sealed class LlmTraceTests
{
    [Fact]
    public void BeginRecordsPromptImmediatelyAndEndFillsResponse()
    {
        LlmTrace.Clear();
        var revisionBefore = LlmTrace.Revision;

        var id = LlmTrace.Begin("wild", "test-model", "system text", "user text");

        // The prompt is visible the moment the call is dispatched, before any response exists.
        var pending = LlmTrace.Snapshot().Single(entry => entry.Id == id);
        Assert.False(pending.Completed);
        Assert.Null(pending.Response);
        Assert.Equal("wild", pending.Purpose);
        Assert.Equal("system text", pending.SystemPrompt);
        Assert.Equal("user text", pending.UserPrompt);
        Assert.True(LlmTrace.Revision > revisionBefore);

        LlmTrace.End(id, "the model reply", null);

        var completed = LlmTrace.Snapshot().Single(entry => entry.Id == id);
        Assert.True(completed.Completed);
        Assert.Equal("the model reply", completed.Response);
        Assert.Null(completed.Error);
    }

    [Fact]
    public void EndWithErrorIsRecordedAndSecondEndIsIgnored()
    {
        LlmTrace.Clear();
        var id = LlmTrace.Begin("dialogue", "m", "s", "u");

        LlmTrace.End(id, "raw", "boom");
        var afterFirst = LlmTrace.Snapshot().Single(entry => entry.Id == id);
        LlmTrace.End(id, "later", null); // ignored: already completed
        var afterSecond = LlmTrace.Snapshot().Single(entry => entry.Id == id);

        Assert.Equal("boom", afterFirst.Error);
        Assert.Equal("boom", afterSecond.Error);
        Assert.Equal("raw", afterSecond.Response);
    }

    [Fact]
    public void LogIsBoundedAndClearEmptiesIt()
    {
        LlmTrace.Clear();
        for (var i = 0; i < 400; i++)
        {
            var id = LlmTrace.Begin("bg", "m", "s", $"u{i}");
            LlmTrace.End(id, "r", null);
        }

        Assert.True(LlmTrace.Snapshot().Count <= 250);

        LlmTrace.Clear();
        Assert.Empty(LlmTrace.Snapshot());
    }
}
