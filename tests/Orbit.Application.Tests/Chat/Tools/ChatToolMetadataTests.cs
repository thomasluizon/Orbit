using System.Text.Json;
using MediatR;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class ChatToolMetadataTests
{
    [Fact]
    public void ToolMetadata_ExposesExpectedNamesDescriptionsAndSchemas()
    {
        var mediator = Substitute.For<MediatR.IMediator>();
        var userDateService = Substitute.For<IUserDateService>();
        var payGateService = Substitute.For<IPayGateService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var goalCompletionService = Substitute.For<IGoalCompletionService>();
        var goalProgressReadSyncer = Substitute.For<Orbit.Application.Goals.Services.IGoalProgressReadSyncer>();

        var assignTagsTool = new AssignTagsTool(Repo<Habit>(), Repo<Tag>(), unitOfWork);
        var bulkUpdateHabitEmojisTool = new BulkUpdateHabitEmojisTool(
            Repo<Habit>(), Substitute.For<IHabitEmojiInferenceService>(), unitOfWork,
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var bulkUpdateHabitsTool = new BulkUpdateHabitsTool(mediator);
        var bulkRescheduleHabitsTool = new BulkRescheduleHabitsTool(mediator);
        var bulkLogHabitsTool = new BulkLogHabitsTool(Substitute.For<IMediator>(), Repo<Habit>(), userDateService);
        var bulkSkipHabitsTool = new BulkSkipHabitsTool(Substitute.For<IMediator>(), Repo<Habit>(), userDateService);
        var createGoalTool = new CreateGoalTool(Repo<Goal>(), unitOfWork);
        var createHabitTool = new CreateHabitTool(Repo<Habit>(), Repo<Tag>(), Repo<Goal>(), userDateService, payGateService, unitOfWork);
        var createSubHabitTool = new CreateSubHabitTool(mediator);
        var deleteGoalTool = new DeleteGoalTool(Repo<Goal>(), unitOfWork);
        var deleteHabitTool = new DeleteHabitTool(Repo<Habit>());
        var duplicateHabitTool = new DuplicateHabitTool(mediator);
        var getDailySummaryTool = new GetDailySummaryTool(mediator, userDateService);
        var getRetrospectiveTool = new GetRetrospectiveTool(mediator, userDateService);
        var getHabitMetricsTool = new GetHabitMetricsTool(mediator);
        var goalReviewTool = new GoalReviewTool(Repo<Goal>(), payGateService, userDateService, goalProgressReadSyncer);
        var linkHabitsTool = new LinkHabitsToGoalTool(Substitute.For<IMediator>(), Repo<Goal>());
        var logHabitTool = new LogHabitTool(Substitute.For<IMediator>(), Repo<Habit>(), userDateService);
        var moveHabitTool = new MoveHabitTool(mediator, Repo<Habit>());
        var queryGoalsTool = new QueryGoalsTool(Repo<Goal>(), userDateService, goalProgressReadSyncer);
        var queryHabitsTool = new QueryHabitsTool(Repo<Habit>(), Repo<User>());
        var skipHabitTool = new SkipHabitTool(Repo<Habit>(), Repo<HabitLog>(), userDateService);
        var suggestBreakdownTool = new SuggestBreakdownTool();
        var updateGoalProgressTool = new UpdateGoalProgressTool(
            Repo<Goal>(), Repo<GoalProgressLog>(), goalCompletionService, unitOfWork);
        var updateGoalStatusTool = new UpdateGoalStatusTool(Repo<Goal>(), goalCompletionService, unitOfWork);
        var updateGoalTool = new UpdateGoalTool(Repo<Goal>(), unitOfWork);
        var updateHabitTool = new UpdateHabitTool(
            Repo<Habit>(), userDateService, unitOfWork, Substitute.For<IPayGateService>());
        var listTagsTool = new ListTagsTool(mediator);
        var createTagTool = new CreateTagTool(mediator);
        var updateTagTool = new UpdateTagTool(mediator);
        var deleteTagTool = new DeleteTagTool(mediator);
        var reorderGoalsTool = new ReorderGoalsTool(mediator);
        var getReferralCodeTool = new GetReferralCodeTool(mediator);

        AssertTool(assignTagsTool, "assign_tags", "tag", "tag_names");
        JsonSerializer.Serialize(assignTagsTool.GetParameterSchema()).Should().Contain("tag_ids");
        AssertTool(listTagsTool, "list_tags", "tag", "type", expectReadOnly: true);
        AssertTool(createTagTool, "create_tag", "tag", "color");
        AssertTool(updateTagTool, "update_tag", "tag", "tag_id");
        AssertTool(deleteTagTool, "delete_tag", "tag", "tag_id");
        AssertTool(reorderGoalsTool, "reorder_goals", "position", "goal_id");
        AssertTool(getReferralCodeTool, "get_referral_code", "referral", "type");
        AssertTool(bulkUpdateHabitEmojisTool, "bulk_update_habit_emojis", "emojis", "infer_from_title");
        AssertTool(bulkUpdateHabitsTool, "bulk_update_habits", "complete", "updates");
        AssertTool(bulkRescheduleHabitsTool, "bulk_reschedule_habits", "reschedule", "due_date");
        AssertTool(bulkLogHabitsTool, "bulk_log_habits", "multiple", "habit_ids");
        AssertTool(bulkSkipHabitsTool, "bulk_skip_habits", "multiple", "habit_ids");
        AssertTool(createGoalTool, "create_goal", "goal", "goal_type");
        AssertTool(createHabitTool, "create_habit", "habit", "checklist_items");
        AssertTool(createSubHabitTool, "create_sub_habit", "sub-habit", "parent_habit_id");
        AssertTool(deleteGoalTool, "delete_goal", "goal", "goal_id");
        AssertTool(deleteHabitTool, "delete_habit", "habit", "habit_id");
        AssertTool(duplicateHabitTool, "duplicate_habit", "duplicate", "habit_id");
        AssertTool(getDailySummaryTool, "get_daily_summary", "summary", "date_from", expectReadOnly: true);
        AssertTool(getRetrospectiveTool, "get_retrospective", "retrospective", "period", expectReadOnly: true);
        AssertTool(getHabitMetricsTool, "get_habit_metrics", "metrics", "habit_id", expectReadOnly: true);
        AssertTool(goalReviewTool, "review_goals", "review", "properties", expectReadOnly: true);
        AssertTool(linkHabitsTool, "link_habits_to_goal", "Link", "habit_ids");
        AssertTool(logHabitTool, "log_habit", "habit", "date");
        AssertTool(moveHabitTool, "move_habit", "parent", "new_parent_id");
        AssertTool(queryGoalsTool, "query_goals", "goals", "include_linked_habits", expectReadOnly: true);
        AssertTool(queryHabitsTool, "query_habits", "habits", "include_metrics", expectReadOnly: true);
        AssertTool(skipHabitTool, "skip_habit", "Skip", "date");
        AssertTool(suggestBreakdownTool, "suggest_breakdown", "Suggest", "suggested_sub_habits");
        AssertTool(updateGoalProgressTool, "update_goal_progress", "goal", "current_value");
        AssertTool(updateGoalStatusTool, "update_goal_status", "goal", "status");
        AssertTool(updateGoalTool, "update_goal", "goal", "target_value");
        AssertTool(updateHabitTool, "update_habit", "habit", "frequency_unit");
    }

    private static void AssertTool(Orbit.Application.Chat.Tools.IAiTool tool, string expectedName, string descriptionFragment, string schemaFragment, bool expectReadOnly = false)
    {
        tool.Name.Should().Be(expectedName);
        tool.Description.Should().NotBeNullOrWhiteSpace();
        tool.Description.ToLowerInvariant().Should().Contain(descriptionFragment.ToLowerInvariant(),
            $"tool '{expectedName}' description should mention '{descriptionFragment}'");
        tool.IsReadOnly.Should().Be(expectReadOnly);
        var schema = JsonSerializer.Serialize(tool.GetParameterSchema());
        schema.Should().Contain("\"type\"");
        schema.Should().Contain(schemaFragment,
            $"tool '{expectedName}' parameter schema should include '{schemaFragment}'");
    }

    private static IGenericRepository<T> Repo<T>()
        where T : Entity
    {
        return Substitute.For<IGenericRepository<T>>();
    }
}
