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

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void DuplicatesOrNonContiguousDates_AreInvalid(int offset)
    {
        _validator.TestValidate(new RepairStreakGapCommand(Guid.NewGuid(), [Yesterday, Yesterday.AddDays(offset)]))
            .ShouldHaveValidationErrorFor(command => command.Dates);
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
