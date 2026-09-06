using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class StreakGapRepairTests
{
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _logs = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<StreakFreeze> _freezes = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _dateService = Substitute.For<IUserDateService>();
    private readonly User _user = User.Create("Test", "test@example.com").Value;
    private readonly List<StreakFreeze> _persistedFreezes = [];
    private readonly UserStreakService _service;
    private readonly DateOnly _today = new(2026, 9, 6);
    private Habit _habit;

    public StreakGapRepairTests()
    {
        _service = new(new(_users, _habits, _logs, _freezes), _dateService,
            Substitute.For<IFriendFeedEventEmitter>());
        _users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(_user);
        _users.FindAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns([_user]);
        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_today);
        _freezes.FindAsync(Arg.Any<Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => _persistedFreezes.Where(call.Arg<Expression<Func<StreakFreeze, bool>>>().Compile()).ToList());
        _habit = SetHistory(_today, 2);
    }

    [Theory]
    [InlineData(2, true, 0)]
    [InlineData(1, false, 1)]
    public async Task TwoDayGap_RealEvaluationAndHandler_SpendEntireCostOrNothing(int bank, bool succeeds, int remaining)
    {
        _user.SetStreakState(bank * 7, bank * 7, _today.AddDays(-3));
        _user.AwardStreakFreezeIfEligible();
        _user.SetStreakState(0, bank * 7, null);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var sender = Substitute.For<ISender>();
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        sender.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new StreakInfoResponse(3, bank * 7, _today.AddDays(-1), 2, 1, 3,
                false, [_today.AddDays(-2), _today.AddDays(-1)], remaining, 3, 4, remaining, true, false, null, 0)));
        var staged = new List<StreakFreeze>();
        _freezes.AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>())
            .Returns(call => { staged.Add(call.Arg<StreakFreeze>()); return Task.CompletedTask; });
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            staged.Count.Should().Be(2);
            _persistedFreezes.AddRange(staged);
            return Task.FromResult(3);
        });
        var handler = new RepairStreakGapCommandHandler(_users, _freezes, _dateService, _service,
            flags, unitOfWork, sender, NullLogger<RepairStreakGapCommandHandler>.Instance);

        var result = await handler.Handle(new(_user.Id, [_today.AddDays(-2), _today.AddDays(-1)]), CancellationToken.None);

        result.IsSuccess.Should().Be(succeeds);
        _user.StreakFreezesAccumulated.Should().Be(remaining);
        if (succeeds)
        {
            _user.CurrentStreak.Should().Be(3);
            var recalculated = await _service.CalculateAsync(_user.Id);
            recalculated!.CurrentStreak.Should().Be(3);
            recalculated.LastActiveDate.Should().Be(_today.AddDays(-1));
            await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        }
        else
        {
            result.ErrorCode.Should().Be(DomainErrors.InsufficientStreakFreezes.Code);
            _persistedFreezes.Should().BeEmpty();
            staged.Should().BeEmpty();
            await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(-3, -1)]
    [InlineData(-1, -1)]
    [InlineData(-3, -2)]
    [InlineData(-1, 0)]
    public async Task InvalidSelection_IsUnavailable(int first, int last)
    {
        var result = await _service.EvaluateGapRepairAsync(_user.Id, _today,
            [_today.AddDays(first), _today.AddDays(last)]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task OnlySuffixOfGap_IsUnavailable()
    {
        var result = await _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-1)]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SelectionContainsCompletedDay_IsUnavailable()
    {
        _habit.Log(_today.AddDays(-2), advanceDueDate: false);

        (await Evaluate()).Should().BeNull();
    }

    [Fact]
    public async Task SelectionContainsFrozenDay_IsUnavailable()
    {
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, _today.AddDays(-1)));

        (await Evaluate()).Should().BeNull();
    }

    [Fact]
    public async Task SelectionContainsUnscheduledDay_IsUnavailable()
    {
        _habit = SetHistory(_today, 2, frequencyQuantity: 2);

        (await Evaluate()).Should().BeNull();
    }

    [Fact]
    public async Task GapExceedsMonthlyAllowance_IsUnavailable()
    {
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 9, 1)));
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 9, 2)));

        (await Evaluate()).Should().BeNull();
    }

    [Fact]
    public async Task GapCrossesMonthBoundary_ChecksEachMonthsAllowance()
    {
        var today = new DateOnly(2026, 9, 2);
        SetHistory(today, 2);
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 8, 20)));
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 8, 21)));

        var result = await _service.EvaluateGapRepairAsync(_user.Id, today, [today.AddDays(-2), today.AddDays(-1)]);

        result!.CurrentStreak.Should().Be(3);
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 8, 22)));
        (await _service.EvaluateGapRepairAsync(_user.Id, today, [today.AddDays(-2), today.AddDays(-1)])).Should().BeNull();
    }

    [Fact]
    public async Task CompletionToday_RestoresPriorStreakAndCountsToday()
    {
        _habit.Log(_today, advanceDueDate: false);

        var result = await Evaluate();

        result!.CurrentStreak.Should().Be(4);
        result.LastActiveDate.Should().Be(_today);
    }

    [Fact]
    public async Task NoPriorStreak_IsUnavailable()
    {
        _logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>()).Returns([]);

        (await Evaluate()).Should().BeNull();
    }

    [Theory]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("America/Los_Angeles")]
    public async Task GapUsesPassedLocalTodayAndUserTimezone(string timezone)
    {
        _user.SetTimeZone(timezone);

        var result = await Evaluate();

        result!.LastActiveDate.Should().Be(_today.AddDays(-1));
    }

    private Task<UserStreakState?> Evaluate() =>
        _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-2), _today.AddDays(-1)]);

    private Habit SetHistory(DateOnly today, int gapLength, int frequencyQuantity = 1)
    {
        var createdOn = today.AddDays(-gapLength - 3);
        var habit = Habit.Create(new HabitCreateParams(_user.Id, "Run", FrequencyUnit.Day, frequencyQuantity,
            DueDate: createdOn)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit,
            createdOn.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        for (var offset = 0; offset < 3; offset++)
            habit.Log(createdOn.AddDays(offset), advanceDueDate: false);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        _logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habit.Logs.Where(call.Arg<Expression<Func<HabitLog, bool>>>().Compile()).ToList());
        return habit;
    }
}
