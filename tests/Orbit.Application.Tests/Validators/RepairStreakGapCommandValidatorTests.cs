using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Validators;

namespace Orbit.Application.Tests.Validators;

public class RepairStreakGapCommandValidatorTests
{
    private readonly RepairStreakGapCommandValidator _validator = new();
    private static readonly DateOnly Yesterday = new(2026, 9, 5);

    [Fact]
    public void ConsecutiveDatesInAnyOrder_AreValid()
    {
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), [Yesterday, Yesterday.AddDays(-1)]))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void DuplicateDates_AreInvalid()
    {
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), [Yesterday, Yesterday]))
            .ShouldHaveValidationErrorFor(command => command.Dates);
    }

    /// <summary>
    /// Calendar sparseness is NOT a request-boundary error. A weekly gap's dates are seven days apart,
    /// and rejecting them here made those gaps unrepairable before the schedule was consulted.
    /// UserStreakService decides contiguity against the real scheduled occurrences.
    /// </summary>
    [Fact]
    public void CalendarSparseDates_AreValidHere()
    {
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), [Yesterday, Yesterday.AddDays(-7)]))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void MissingInputs_AreInvalid()
    {
        _validator.TestValidate(new RepairStreakGapCommand(Guid.Empty, []))
            .ShouldHaveValidationErrorFor(command => command.UserId);
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), []))
            .ShouldHaveValidationErrorFor(command => command.Dates);
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), null!))
            .ShouldHaveValidationErrorFor(command => command.Dates);
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), [DateOnly.MinValue]))
            .ShouldHaveValidationErrorFor(command => command.Dates);
    }

    [Fact]
    public void DatesBeyondHistoryWindow_AreInvalid()
    {
        var dates = Enumerable.Range(0, AppConstants.MaxStreakLookbackDays).Select(offset => Yesterday.AddDays(-offset)).ToArray();
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), dates))
            .ShouldHaveValidationErrorFor(command => command.Dates);
    }
}
