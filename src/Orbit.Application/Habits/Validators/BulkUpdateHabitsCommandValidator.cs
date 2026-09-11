using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public sealed class BulkUpdateHabitsCommandValidator : AbstractValidator<BulkUpdateHabitsCommand>
{
    public BulkUpdateHabitsCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Filter)
            .Must(filter => filter.HasSelector)
            .WithMessage("A bulk habit filter is required.");
        RuleFor(command => command.Changes)
            .Must(changes => changes.HasAnyChange)
            .WithMessage("At least one habit change is required.");
        RuleFor(command => command.Changes.Title)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxHabitTitleLength)
            .When(command => command.Changes.HasTitle);
        RuleFor(command => command.Changes.Description)
            .MaximumLength(AppConstants.MaxHabitDescriptionLength)
            .When(command => command.Changes.HasDescription);
        RuleFor(command => command.Changes.Emoji)
            .MaximumLength(AppConstants.MaxHabitEmojiLength)
            .When(command => command.Changes.HasEmoji);
        RuleFor(command => command.Changes.FrequencyQuantity)
            .GreaterThan(0)
            .When(command => command.Changes.HasFrequencyQuantity && command.Changes.FrequencyQuantity.HasValue);
        RuleFor(command => command.Changes.IntervalWeeks)
            .InclusiveBetween(1, AppConstants.MaxIntervalWeeks)
            .When(command => command.Changes.HasIntervalWeeks && command.Changes.IntervalWeeks.HasValue);
        SharedHabitRules.AddReminderTimesRules(RuleFor(command => command.Changes.ReminderTimes));
        SharedHabitRules.AddChecklistItemRules(RuleFor(command => command.Changes.ChecklistItems));
        SharedHabitRules.AddScheduledReminderRules(RuleFor(command => command.Changes.ScheduledReminders));
    }
}
