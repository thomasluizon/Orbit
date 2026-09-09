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
            /**
             * Duplicates only. Contiguity is a property of the user's SCHEDULE, not of the calendar, so
             * it is enforced in UserStreakService where the scheduled occurrences are known. Asserting
             * calendar-consecutiveness at this boundary rejected every valid weekly and every-N-day gap.
             */
            .Must(dates => dates.Distinct().Count() == dates.Count)
            .WithMessage("Select each date once.");
    }
}
