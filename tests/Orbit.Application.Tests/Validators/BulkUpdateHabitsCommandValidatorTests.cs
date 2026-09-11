using FluentAssertions;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public sealed class BulkUpdateHabitsCommandValidatorTests
{
    private readonly BulkUpdateHabitsCommandValidator _validator = new();

    [Fact]
    public async Task Validate_ExplicitAllFilterAndOneChange_IsValid()
    {
        var command = new BulkUpdateHabitsCommand(
            Guid.NewGuid(),
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasDescription: true, Description: "New description"));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EmptyFilter_IsInvalid()
    {
        var command = new BulkUpdateHabitsCommand(
            Guid.NewGuid(),
            new BulkHabitFilter(false, []),
            new BulkHabitChanges(HasDescription: true, Description: "New description"));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_NoChanges_IsInvalid()
    {
        var command = new BulkUpdateHabitsCommand(
            Guid.NewGuid(),
            new BulkHabitFilter(true, []),
            new BulkHabitChanges());

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RecurringUnitWithoutPositiveQuantity_IsInvalid()
    {
        var command = new BulkUpdateHabitsCommand(
            Guid.NewGuid(),
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasFrequencyUnit: true, FrequencyUnit: FrequencyUnit.Day));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_ExplicitNullQuantityWithoutOneTimeConversion_IsInvalid()
    {
        var command = new BulkUpdateHabitsCommand(
            Guid.NewGuid(),
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasFrequencyQuantity: true, FrequencyQuantity: null));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_OneTimeConversionWithNullCadencePair_IsValid()
    {
        var command = new BulkUpdateHabitsCommand(
            Guid.NewGuid(),
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(
                HasFrequencyUnit: true,
                FrequencyUnit: null,
                HasFrequencyQuantity: true,
                FrequencyQuantity: null));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
