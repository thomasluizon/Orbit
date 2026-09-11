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

    private static AiToolCall Call(string name, string id) =>
        new(name, id, JsonDocument.Parse("{}").RootElement.Clone());
}
