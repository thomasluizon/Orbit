using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;

namespace Orbit.Application.Gamification.Validators;

public class RepairStreakGapCommandValidator : AbstractValidator<RepairStreakGapCommand>
{
    public RepairStreakGapCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Dates).Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(dates => dates.Count < AppConstants.MaxStreakLookbackDays)
            .WithMessage("The gap exceeds the streak history window.")
            .Must(dates => dates.All(date => date != DateOnly.MinValue))
            .WithMessage("Each date is required.")
            .Must(dates =>
            {
                var ordered = dates.Order().ToArray();
                return ordered.Select((date, index) => date.DayNumber == ordered[0].DayNumber + index).All(valid => valid);
            })
            .WithMessage("Select consecutive dates without duplicates.");
    }
}
