using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Orbit.Infrastructure.Services.Prompts.Sections.Static;

namespace Orbit.Infrastructure.Tests.Services;

public class CoreIdentitySectionTests
{
    [Fact]
    public void Order_Is100()
    {
        new CoreIdentitySection().Order.Should().Be(100);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new CoreIdentitySection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_ContainsOrbitIdentity()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new CoreIdentitySection().Build(ctx);

        result.Should().Contain("Orbit AI");
        result.Should().Contain("Habit and Goal Tracking Assistant");
    }

    [Fact]
    public void Build_SoftenedGuidance_RoutesDestructiveAndAmbiguousThroughCards()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new CoreIdentitySection().Build(ctx);

        result.Should().Contain("Act directly");
        result.Should().Contain("confirmation card");
        result.Should().Contain("clarification card");
        result.Should().Contain("quick-action chips");
    }

    [Fact]
    public void Build_NoLongerActsWithoutConfirmationOnDestructiveTools()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new CoreIdentitySection().Build(ctx);

        result.Should().NotContain("without asking for unnecessary confirmation");
    }
}

public class EncouragingToneSectionTests
{
    [Fact]
    public void Order_Is150()
    {
        new EncouragingToneSection().Order.Should().Be(150);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new EncouragingToneSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_ContainsWarmEncouragingTone()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new EncouragingToneSection().Build(ctx);

        result.Should().Contain("warm");
        result.Should().Contain("streaks");
        result.Should().Contain("non-judgmental");
        result.Should().Contain("saccharine");
    }

    [Fact]
    public void Build_ContainsNoEmOrEnDashes()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new EncouragingToneSection().Build(ctx);

        result.Should().NotContain("—");
        result.Should().NotContain("–");
    }
}

public class GlobalRulesSectionTests
{
    [Fact]
    public void Order_Is200()
    {
        new GlobalRulesSection().Order.Should().Be(200);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new GlobalRulesSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_ContainsCoreRules()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("Core Rules");
        result.Should().Contain("DUPLICATE PREVENTION");
        result.Should().Contain("ACTION-ORIENTED");
    }

    [Fact]
    public void Build_ActionOrientedRule_AllowsOneClarifyingQuestion()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("ONE targeted question");
        result.Should().Contain("Structuring Strategy");
    }

    [Fact]
    public void Build_ContainsHabitEmojiRule()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("HABIT EMOJIS");
        result.Should().Contain("make all habit emojis sensible");
        result.Should().Contain("bulk_update_habit_emojis");
        result.Should().Contain("Do not call update_habit once per habit");
    }

    [Fact]
    public void Build_ContainsNoSubstitutionRule()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("NO HABIT SUBSTITUTION FOR LOG / SKIP");
        result.Should().Contain("log_habit");
        result.Should().Contain("bulk_log_habits");
        result.Should().Contain("skip_habit");
        result.Should().Contain("bulk_skip_habits");
        result.Should().Contain("no more, no fewer");
        result.Should().Contain("Indirect references");
    }

    [Fact]
    public void Build_ContainsDescribeFeaturePointer()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("describe_feature");
    }

    [Fact]
    public void Build_ContainsExhaustiveListingRules()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("list EVERY matching entry");
        result.Should().Contain("your list must match them");
        result.Should().Contain("Conciseness applies to prose, never to lists");
    }

    [Fact]
    public void Build_RequiresBulkCallsAndHonestCompletionClaims()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("bulk_update_habits");
        result.Should().Contain("bulk_reschedule_habits");
        result.Should().Contain("partial=false");
        result.Should().Contain("applied_count equals total_matched");
        result.Should().Contain("Never loop update_habit");
    }
}

public class StructuringStrategySectionTests
{
    [Fact]
    public void Order_Is250()
    {
        new StructuringStrategySection().Order.Should().Be(250);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new StructuringStrategySection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_ExplainsChecklistVsSubHabits()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new StructuringStrategySection().Build(ctx);

        result.Should().Contain("Structuring Strategy");
        result.Should().Contain("checklist_items");
        result.Should().Contain("sub_habits");
    }

    [Fact]
    public void Build_TeachesClarifyingQuestionPolicy()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new StructuringStrategySection().Build(ctx);

        result.Should().Contain("Ask ONE targeted clarifying question");
        result.Should().Contain("NEVER ask more than one");
    }

    [Fact]
    public void Build_ListsAntiPatterns()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new StructuringStrategySection().Build(ctx);

        result.Should().Contain("Anti-patterns to avoid");
    }

    [Fact]
    public void Build_PreservesFlexibleWeeklyShortcut()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new StructuringStrategySection().Build(ctx);

        result.Should().Contain("NEVER ask a question");
        result.Should().Contain("X times per week");
        result.Should().Contain("is_flexible=true");
    }
}

public class ToolFailureSectionTests
{
    [Fact]
    public void Order_Is270()
    {
        new ToolFailureSection().Order.Should().Be(270);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);

        new ToolFailureSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_DefinesTerminalFailureRules()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);

        var result = new ToolFailureSection().Build(ctx);

        result.Should().Contain("Tool Failures");
        result.Should().Contain("NEVER retry a rejected tool call");
        result.Should().Contain("NEVER turn the rejection into a question");
        result.Should().Contain("user's own words and language");
        result.Should().Contain("ask the user to resolve it");
        result.Should().ContainAll("frequency_unit", "frequency_quantity", "days", "is_flexible");
    }

    [Fact]
    public void Build_PreservesClarificationForAmbiguousRequests()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);

        var result = new ToolFailureSection().Build(ctx);

        result.Should().Contain("do not replace the NeedsClarification flow");
        result.Should().Contain("genuinely ambiguous request");
    }
}

public class TodayDateSectionTests
{
    [Fact]
    public void Order_Is650()
    {
        new TodayDateSection().Order.Should().Be(650);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new TodayDateSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_WithUserToday_UsesProvidedDate()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, new DateOnly(2026, 6, 15), null);
        var result = new TodayDateSection().Build(ctx);

        result.Should().Contain("2026-06-15");
    }

    [Fact]
    public void Build_WithoutUserToday_Throws()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);

        var act = () => new TodayDateSection().Build(ctx);

        act.Should().Throw<InvalidOperationException>();
    }
}

public class HabitCountSectionTests
{
    [Fact]
    public void Order_Is800()
    {
        new HabitCountSection().Order.Should().Be(800);
    }

    [Fact]
    public void Build_ShowsCorrectCount()
    {
        var habits = new List<Habit>
        {
            Habit.Create(new HabitCreateParams(Guid.NewGuid(), "H1", null, null, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value,
            Habit.Create(new HabitCreateParams(Guid.NewGuid(), "H2", null, null, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value,
        };
        var ctx = new PromptContext(habits, new List<UserFact>(), false, null, null, null, null);
        var result = new HabitCountSection().Build(ctx);

        result.Should().Contain("2 active habits");
    }

    [Fact]
    public void Build_EmptyHabits_ShowsZero()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new HabitCountSection().Build(ctx);

        result.Should().Contain("0 active habits");
    }
}

public class ImageInstructionsSectionTests
{
    [Fact]
    public void Order_Is700()
    {
        new ImageInstructionsSection().Order.Should().Be(700);
    }

    [Fact]
    public void ShouldInclude_HasImageTrue_ReturnsTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), true, null, null, null, null);
        new ImageInstructionsSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void ShouldInclude_HasImageFalse_ReturnsFalse()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new ImageInstructionsSection().ShouldInclude(ctx).Should().BeFalse();
    }

    [Fact]
    public void Build_ContainsImageAnalysisInstructions()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), true, null, null, new DateOnly(2026, 4, 10), null);
        var result = new ImageInstructionsSection().Build(ctx);

        result.Should().Contain("Image Analysis Instructions");
        result.Should().Contain("Extract EVERYTHING visible");
        result.Should().Contain("2026-04-10");
    }

    [Fact]
    public void Build_WithoutUserToday_Throws()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), true, null, null, null, null);

        var act = () => new ImageInstructionsSection().Build(ctx);

        act.Should().Throw<InvalidOperationException>();
    }
}
