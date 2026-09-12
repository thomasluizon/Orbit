using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class BulkToolRepeatGuardTests
{
    [Fact]
    public void FindRedirects_AtThreshold_RedirectsEveryRepeatedSingleEntityCall()
    {
        var calls = Enumerable.Range(1, BulkToolRepeatGuard.Threshold)
            .Select(index => Call("update_habit", $"call_{index}"))
            .ToList();

        var redirects = BulkToolRepeatGuard.FindRedirects(calls);

        redirects.Should().HaveCount(BulkToolRepeatGuard.Threshold);
        redirects.Values.Should().OnlyContain(value => value == "bulk_update_habits");
    }

    [Fact]
    public void FindRedirects_DistinctSingleEntityOperations_DoesNotFire()
    {
        var calls = new[]
        {
            Call("update_habit", "call_1"),
            Call("log_habit", "call_2"),
            Call("skip_habit", "call_3")
        };

        BulkToolRepeatGuard.FindRedirects(calls).Should().BeEmpty();
    }

    [Fact]
    public void FindRedirects_HeterogeneousUpdates_DoesNotFire()
    {
        var calls = new[]
        {
            Call("update_habit", "call_1", """{"habit_id":"00000000-0000-0000-0000-000000000001","title":"First"}"""),
            Call("update_habit", "call_2", """{"habit_id":"00000000-0000-0000-0000-000000000002","title":"Second"}"""),
            Call("update_habit", "call_3", """{"habit_id":"00000000-0000-0000-0000-000000000003","title":"Third"}""")
        };

        BulkToolRepeatGuard.FindRedirects(calls).Should().BeEmpty();
    }

    [Fact]
    public void FindRedirects_EquivalentUpdatesWithDifferentHabitIds_RedirectsAll()
    {
        var calls = new[]
        {
            Call("update_habit", "call_1", """{"habit_id":"00000000-0000-0000-0000-000000000001","description":"Shared"}"""),
            Call("update_habit", "call_2", """{"description":"Shared","habit_id":"00000000-0000-0000-0000-000000000002"}"""),
            Call("update_habit", "call_3", """{"habit_id":"00000000-0000-0000-0000-000000000003","description":"Shared"}""")
        };

        var redirects = BulkToolRepeatGuard.FindRedirects(calls);

        redirects.Should().HaveCount(3);
        redirects.Values.Should().OnlyContain(value => value == "bulk_update_habits");
    }

    private static AiToolCall Call(string name, string id) =>
        new(name, id, JsonDocument.Parse("{}").RootElement.Clone());

    private static AiToolCall Call(string name, string id, string args) =>
        new(name, id, JsonDocument.Parse(args).RootElement.Clone());
}
