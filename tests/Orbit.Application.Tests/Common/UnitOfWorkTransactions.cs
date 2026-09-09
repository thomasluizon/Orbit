using NSubstitute;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

/// <summary>
/// Makes a substituted <see cref="IUnitOfWork"/> run a transaction body inline instead of returning
/// null. A handler that opens a transaction, directly or through <c>HabitCeilingLock</c>, does
/// nothing at all without this, so the stub is a prerequisite of the test rather than a detail of it.
///
/// The overloads are per return type because NSubstitute matches the closed generic method.
/// </summary>
internal static class UnitOfWorkTransactions
{
    public static IUnitOfWork PassThroughTransactions(this IUnitOfWork unitOfWork)
    {
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        return unitOfWork;
    }

    public static IUnitOfWork PassThroughTransactions<T>(this IUnitOfWork unitOfWork)
    {
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<T>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<T>>>(0)(call.ArgAt<CancellationToken>(1)));
        return unitOfWork;
    }
}
