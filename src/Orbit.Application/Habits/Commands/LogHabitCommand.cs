using System.Data.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Challenges.Services;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Commands;

public record LinkedGoalUpdate(Guid GoalId, string Title, decimal NewProgress, decimal TargetValue);

public record LogHabitResponse(
    Guid LogId,
    bool IsFirstCompletionToday,
    int CurrentStreak,
    IReadOnlyList<LinkedGoalUpdate>? LinkedGoalUpdates = null,
    int? XpEarned = null,
    IReadOnlyList<string>? NewAchievementIds = null);

public record LogHabitCommand(
    Guid UserId,
    Guid HabitId,
    DateOnly? Date = null) : IRequest<Result<LogHabitResponse>>, IIdempotentCommand;

/// <summary>
/// Groups repository dependencies for habit logging to reduce constructor parameter count (S107).
/// </summary>
public record LogHabitRepositories(
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<HabitLog> HabitLogRepository,
    IGenericRepository<User> UserRepository);

/// <summary>
/// Groups supporting services for habit logging to reduce constructor parameter count (S107).
/// </summary>
public record LogHabitServices(
    IUserDateService UserDateService,
    IUserStreakService UserStreakService,
    IGamificationService GamificationService,
    IGoalCompletionService GoalCompletionService,
    IChallengeProgressService ChallengeProgressService,
    IMediator Mediator,
    IPayGateService PayGate);

public partial class LogHabitCommandHandler(
    LogHabitRepositories repos,
    LogHabitServices services,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<LogHabitCommandHandler> logger) : IRequestHandler<LogHabitCommand, Result<LogHabitResponse>>
{
    private const int MaxLogAttempts = 3;

    public async Task<Result<LogHabitResponse>> Handle(LogHabitCommand request, CancellationToken cancellationToken)
    {
        var today = await services.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var habit = await LoadLoggableHabitAsync(request.HabitId, today, cancellationToken);

        if (habit is null)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotOwned);

        var targetDate = request.Date ?? today;
        var weekStartDay = await services.UserDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var dueDateResolution = await HabitDueDateResolutionLoader.LoadAsync(
            repos.HabitLogRepository,
            [habit],
            today.AddDays(-AppConstants.MaxRangeDays),
            cancellationToken);

        var dateValidation = ValidateTargetDate(
            habit,
            targetDate,
            today,
            weekStartDay,
            dueDateResolution.Contains(habit.Id));
        if (dateValidation.IsFailure)
            return dateValidation.PropagateError<LogHabitResponse>();

        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);

        var existingLog = habit.Logs.FirstOrDefault(l => l.Date == targetDate && l.Value > 0);
        if (existingLog is not null && !habit.IsFlexible && !habit.IsBadHabit)
        {
            return await HabitCeilingLock.ExecuteEntryAsync<Habit, LogHabitResponse>(
                unitOfWork,
                request.UserId,
                services.PayGate,
                ct => PrepareUnlogAsync(request, today, ct),
                HabitLiveRootEntry.FromUnlog,
                (lockedHabit, ct) => HandleUnlogAsync(lockedHabit, targetDate, today, ct),
                cancellationToken);
        }

        return await HandleLogAsync(habit, request, targetDate, today, weekStartDay, user, cancellationToken);
    }

    private async Task<Result<Habit>> PrepareUnlogAsync(
        LogHabitCommand request,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var habit = await LoadLoggableHabitAsync(request.HabitId, today, cancellationToken);
        if (habit is null)
            return Result.Failure<Habit>(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure<Habit>(ErrorMessages.HabitNotOwned);

        return Result.Success(habit);
    }

    private static Result ValidateTargetDate(
        Habit habit,
        DateOnly targetDate,
        DateOnly today,
        int weekStartDay,
        bool dueDateResolved)
    {
        if (targetDate > today && habit.FrequencyUnit is not null)
            return Result.Failure(ErrorMessages.CannotLogFutureDate);

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return Result.Failure(ErrorMessages.BeyondOverdueWindow);

        if (habit.FrequencyUnit is not null
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate, weekStartDay))
        {
            var isOverdue = !habit.IsFlexible
                && targetDate == today
                && HabitScheduleService.HasMissedPastOccurrence(
                    habit,
                    today,
                    weekStartDay,
                    dueDateResolved);
            if (!isOverdue)
                return Result.Failure(ErrorMessages.NotScheduledOnDate);
        }

        return Result.Success();
    }

    private async Task<Result<LogHabitResponse>> HandleUnlogAsync(
        Habit habit, DateOnly targetDate, DateOnly today, CancellationToken cancellationToken)
    {
        HabitLog unlogEntity;
        LinkedGoalSyncResult goalSync;
        var attempt = 1;
        while (true)
        {
            var unlogResult = habit.Unlog(targetDate);
            if (unlogResult.IsFailure)
                return unlogResult.PropagateError<LogHabitResponse>();
            unlogEntity = unlogResult.Value;

            var goalIds = habit.Goals.Select(g => g.Id).ToList();

            try
            {
                var updates = await services.GoalCompletionService.SyncDerivedGoalsAsync(
                    habit.UserId,
                    goalIds,
                    today,
                    cancellationToken: cancellationToken);
                goalSync = ToLinkedGoalSyncResult(updates);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxLogAttempts)
            {
                unitOfWork.ResetTracking();
                var reloaded = await LoadLoggableHabitAsync(habit.Id, today, cancellationToken);
                if (reloaded is null)
                    return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotFound);
                habit = reloaded;
            }

            attempt++;
        }

        var unlogId = unlogEntity.Id;
        var userId = habit.UserId;
        UserStreakState? streakState = null;
        await ConcurrencyRetry.SaveWithRetryAsync(
            unitOfWork,
            async ct => streakState = await services.UserStreakService.RecalculateAsync(
                userId, awardFreezeIfEligible: false, ct),
            cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, userId, today);

        return Result.Success(new LogHabitResponse(
            unlogId,
            IsFirstCompletionToday: false,
            CurrentStreak: streakState?.CurrentStreak ?? 0,
            LinkedGoalUpdates: goalSync.Updates));
    }

    /// <summary>The rows a log persists, carried out of the lock so the derived work below can run outside it.</summary>
    internal sealed record PersistedLog(Guid LogId, bool IsFirstCompletionToday, LinkedGoalSyncResult GoalSync);

    private async Task<Result<LogHabitResponse>> HandleLogAsync(
        Habit habit, LogHabitCommand request, DateOnly targetDate, DateOnly today, int weekStartDay,
        User? user, CancellationToken cancellationToken)
    {
        Result<PersistedLog> persisted;
        try
        {
            /**
             * The log row and the due-date advance are both inputs a streak repair reads, so they
             * commit inside HabitCeilingLock. Only the persist is locked: the streak, gamification,
             * challenge and referral work below is derived and writes the token-protected user row.
             */
            persisted = await HabitCeilingLock.ExecuteAsync(
                unitOfWork,
                request.UserId,
                transactionToken => PersistLogAsync(
                    habit, request, targetDate, today, weekStartDay, user, transactionToken),
                cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            /**
             * The completion already exists. The catch sits OUTSIDE the transaction because a failed
             * statement aborts the whole block, and the reply below has to read rows.
             */
            unitOfWork.ResetTracking();
            return await BuildAlreadyLoggedResultAsync(habit, targetDate, today, cancellationToken);
        }

        if (persisted.IsFailure)
            return persisted.PropagateError<LogHabitResponse>();

        var logId = persisted.Value.LogId;
        var isFirstCompletionToday = persisted.Value.IsFirstCompletionToday;
        var goalSync = persisted.Value.GoalSync;
        var streakState = await services.UserStreakService.RecalculateAsync(request.UserId, cancellationToken: cancellationToken);
        var gamificationResult = await ProcessGamificationSafeAsync(request.UserId, request.HabitId, cancellationToken);
        await ProcessChallengeProgressSafeAsync(request.UserId, request.HabitId, cancellationToken);
        await ProcessOnboardingChecklistSafeAsync(request.UserId, OnboardingChecklistSignal.HabitLogged, cancellationToken);

        if (gamificationResult is null)
            await PersistStreakRecalcAsync(request.UserId, cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        await CheckReferralCompletionSafeAsync(request.UserId, cancellationToken);

        return Result.Success(new LogHabitResponse(
            logId,
            isFirstCompletionToday,
            CurrentStreak: streakState?.CurrentStreak ?? 0,
            LinkedGoalUpdates: goalSync.Updates,
            XpEarned: gamificationResult?.XpEarned,
            NewAchievementIds: gamificationResult?.NewAchievementIds));
    }

    private async Task<Result<PersistedLog>> PersistLogAsync(
        Habit habit, LogHabitCommand request, DateOnly targetDate, DateOnly today, int weekStartDay,
        User? user, CancellationToken cancellationToken)
    {
        var isFirstCompletionToday = user is not null
            && !await repos.HabitRepository.AnyAsync(
                h => h.UserId == request.UserId && h.Logs.Any(l => l.Date == targetDate && l.Value > 0),
                cancellationToken);

        var shouldAdvanceDueDate = targetDate >= today;
        var attempt = 1;
        while (true)
        {
            var logResult = habit.Log(
                targetDate,
                advanceDueDate: shouldAdvanceDueDate,
                weekStartDay: weekStartDay);
            if (logResult.IsFailure)
                return logResult.PropagateError<PersistedLog>();
            var logEntity = logResult.Value;

            await repos.HabitLogRepository.AddAsync(logEntity, cancellationToken);

            var goalIds = habit.Goals.Select(g => g.Id).ToList();

            try
            {
                var updates = await services.GoalCompletionService.SyncDerivedGoalsAsync(
                    request.UserId,
                    goalIds,
                    today,
                    cancellationToken: cancellationToken);
                return Result.Success(new PersistedLog(
                    logEntity.Id, isFirstCompletionToday, ToLinkedGoalSyncResult(updates)));
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxLogAttempts)
            {
                unitOfWork.ResetTracking();
                var reloaded = await LoadLoggableHabitAsync(request.HabitId, today, cancellationToken);
                if (reloaded is null)
                    return Result.Failure<PersistedLog>(ErrorMessages.HabitNotFound);
                habit = reloaded;
            }

            attempt++;
        }
    }

    private Task<Habit?> LoadLoggableHabitAsync(Guid habitId, DateOnly today, CancellationToken cancellationToken)
    {
        var loggableWindowStart = today.AddDays(-AppConstants.MaxRangeDays);
        return repos.HabitRepository.FindOneTrackedAsync(
            h => h.Id == habitId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= loggableWindowStart))
                  .Include(h => h.Goals)
                  .AsSplitQuery(),
            cancellationToken);
    }

    private async Task PersistStreakRecalcAsync(Guid userId, CancellationToken cancellationToken)
    {
        var attempt = 1;
        while (true)
        {
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxLogAttempts)
            {
                unitOfWork.ResetTracking();
                await services.UserStreakService.RecalculateAsync(userId, cancellationToken: cancellationToken);
            }

            attempt++;
        }
    }

    private async Task<Result<LogHabitResponse>> BuildAlreadyLoggedResultAsync(
        Habit habit, DateOnly targetDate, DateOnly today, CancellationToken cancellationToken)
    {
        var existingLogs = await repos.HabitLogRepository.FindAsync(
            l => l.HabitId == habit.Id && l.Date == targetDate && l.Value > 0, cancellationToken);
        var winningLog = existingLogs.OrderByDescending(l => l.Id).First();

        var users = await repos.UserRepository.FindAsync(u => u.Id == habit.UserId, cancellationToken);
        var currentStreak = users.SingleOrDefault()?.CurrentStreak ?? 0;

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId, today);

        return Result.Success(new LogHabitResponse(
            winningLog.Id,
            IsFirstCompletionToday: false,
            CurrentStreak: currentStreak));
    }

    private const string PostgresUniqueViolationSqlState = "23505";

    private static bool IsUniqueViolation(Exception exception)
    {
        return exception switch
        {
            DbUpdateException dbUpdateException => dbUpdateException.InnerException is not null
                && IsUniqueViolation(dbUpdateException.InnerException),
            DbException dbException => dbException.SqlState == PostgresUniqueViolationSqlState,
            _ => false
        };
    }

    private async Task<HabitLogGamificationResult?> ProcessGamificationSafeAsync(
        Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        try
        {
            return await services.GamificationService.ProcessHabitLogged(userId, habitId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationHabitLogFailed(logger, ex, habitId);
            return null;
        }
    }

    private async Task ProcessChallengeProgressSafeAsync(
        Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        try
        {
            await services.ChallengeProgressService.EvaluateOnHabitLoggedAsync(userId, habitId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogChallengeProgressFailed(logger, ex, habitId);
        }
    }

    private async Task ProcessOnboardingChecklistSafeAsync(
        Guid userId, OnboardingChecklistSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            await services.GamificationService.ProcessOnboardingChecklistAsync(userId, signal, cancellationToken);
        }
        catch (Exception ex)
        {
            LogOnboardingChecklistFailed(logger, ex, userId);
        }
    }

    private async Task CheckReferralCompletionSafeAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await services.Mediator.Send(new CheckReferralCompletionCommand(userId), cancellationToken);
        }
        catch (Exception ex)
        {
            LogReferralCompletionCheckFailed(logger, ex, userId);
        }
    }

    private static LinkedGoalSyncResult ToLinkedGoalSyncResult(IReadOnlyList<GoalCompletionUpdate> updates) =>
        updates.Count == 0
            ? new LinkedGoalSyncResult(null)
            : new LinkedGoalSyncResult(updates.Select(update => new LinkedGoalUpdate(
                update.GoalId,
                update.Title,
                update.CurrentValue,
                update.TargetValue)).ToList());

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for habit {HabitId}")]
    private static partial void LogGamificationHabitLogFailed(ILogger logger, Exception ex, Guid habitId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Referral completion check failed for user {UserId}")]
    private static partial void LogReferralCompletionCheckFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Onboarding checklist processing failed for user {UserId}")]
    private static partial void LogOnboardingChecklistFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Challenge progress processing failed for habit {HabitId}")]
    private static partial void LogChallengeProgressFailed(ILogger logger, Exception ex, Guid habitId);
}

internal record LinkedGoalSyncResult(IReadOnlyList<LinkedGoalUpdate>? Updates);
