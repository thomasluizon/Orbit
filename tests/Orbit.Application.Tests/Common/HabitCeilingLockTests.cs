using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

public class HabitCeilingLockTests
{
    [Fact]
    public async Task ConcurrentCreateAttempts_CommitNoMoreThanConfiguredCeiling()
    {
        const int ceiling = 1000;
        var liveHabitCount = 999;
        var unitOfWork = new SerializingUnitOfWork();
        var firstReadCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCreate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptCount = 0;

        Task<Result> AttemptCreate() => HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            Guid.Empty,
            async cancellationToken =>
            {
                var observedCount = liveHabitCount;
                if (Interlocked.Increment(ref attemptCount) == 1)
                {
                    firstReadCompleted.TrySetResult(true);
                    await releaseFirstCreate.Task.WaitAsync(cancellationToken);
                }

                if (observedCount + 1 > ceiling)
                    return Result.Failure("Habit limit reached.");

                liveHabitCount = observedCount + 1;
                return Result.Success();
            },
            CancellationToken.None);

        var first = AttemptCreate();
        await firstReadCompleted.Task;
        var second = AttemptCreate();
        await unitOfWork.SecondLockRequested.Task;

        second.IsCompleted.Should().BeFalse();
        releaseFirstCreate.TrySetResult(true);
        var results = await Task.WhenAll(first, second);

        liveHabitCount.Should().Be(ceiling);
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.IsFailure);
    }

    [Fact]
    public async Task LockedCreationOverlappingCompletedRootReactivation_RefusesReactivationAtCeiling()
    {
        const int ceiling = 1000;
        var liveHabitCount = 999;
        var reactivated = false;
        var unitOfWork = new SerializingUnitOfWork();
        var payGate = Substitute.For<IPayGateService>();
        var creationGateCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        payGate.CanCreateHabits(Guid.Empty, 1, Arg.Any<CancellationToken>())
            .Returns(_ => liveHabitCount < ceiling
                ? Result.Success()
                : Result.Failure("You've reached the 1000 habit limit."));

        var creation = HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            Guid.Empty,
            async cancellationToken =>
            {
                var allowance = await payGate.CanCreateHabits(Guid.Empty, 1, cancellationToken);
                creationGateCompleted.TrySetResult(true);
                await releaseCreation.Task.WaitAsync(cancellationToken);
                if (allowance.IsFailure)
                    return allowance;

                liveHabitCount++;
                return Result.Success();
            },
            CancellationToken.None);

        await creationGateCompleted.Task;
        var reactivation = HabitCeilingLock.ExecuteEntryAsync(
            unitOfWork,
            Guid.Empty,
            payGate,
            _ => Task.FromResult(Result.Success(true)),
            entersLiveRootSet: _ => true,
            (_, _) =>
            {
                reactivated = true;
                liveHabitCount++;
                return Task.FromResult(Result.Success());
            },
            CancellationToken.None);

        await unitOfWork.SecondLockRequested.Task;
        reactivation.IsCompleted.Should().BeFalse();
        releaseCreation.TrySetResult(true);
        var creationResult = await creation;
        var reactivationResult = await reactivation;

        creationResult.IsSuccess.Should().BeTrue();
        reactivationResult.IsFailure.Should().BeTrue();
        reactivationResult.Error.Should().Be("You've reached the 1000 habit limit.");
        reactivated.Should().BeFalse();
        liveHabitCount.Should().Be(ceiling);
    }
}
