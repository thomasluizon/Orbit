namespace Orbit.Domain.Common;

/// <summary>
/// Error catalog for failures raised by Domain entity guards.
/// Application-level errors live in Orbit.Application.Common.ErrorMessages.
/// </summary>
public static class DomainErrors
{
    public static readonly AppError UserIdRequired = new("USER_ID_REQUIRED", "User ID is required.");
    public static readonly AppError TokenHashRequired = new("TOKEN_HASH_REQUIRED", "Token hash is required.");
    public static readonly AppError SessionNotActive = new("SESSION_NOT_ACTIVE", "Session is no longer active.");

    public static readonly AppError NameRequired = new("NAME_REQUIRED", "Name is required");
    public static readonly AppError InvalidHandle = new("INVALID_HANDLE", "Handle must be 3-20 characters using only letters, numbers, or underscores.");
    public static readonly AppError CannotFriendSelf = new("CANNOT_FRIEND_SELF", "You cannot send a friend request to yourself.");
    public static readonly AppError FriendshipNotPending = new("FRIENDSHIP_NOT_PENDING", "This friend request is no longer pending.");
    public static readonly AppError CannotCheerSelf = new("CANNOT_CHEER_SELF", "You cannot cheer yourself.");
    public static readonly AppError CheerNoteTooLong = new("CHEER_NOTE_TOO_LONG", "Cheer note must be at most {0} characters.");
    public static readonly AppError CannotPairSelf = new("CANNOT_PAIR_SELF", "You cannot pair with yourself.");
    public static readonly AppError PairNotPending = new("PAIR_NOT_PENDING", "This accountability invite is no longer pending.");
    public static readonly AppError AccountabilityNoteTooLong = new("ACCOUNTABILITY_NOTE_TOO_LONG", "Check-in note must be at most {0} characters.");
    public static readonly AppError CannotBlockSelf = new("CANNOT_BLOCK_SELF", "You cannot block yourself.");
    public static readonly AppError CannotReportSelf = new("CANNOT_REPORT_SELF", "You cannot report yourself.");
    public static readonly AppError ReportDetailsTooLong = new("REPORT_DETAILS_TOO_LONG", "Report details must be at most {0} characters.");
    public static readonly AppError NameTooLong = new("NAME_TOO_LONG", "Name must be at most {0} characters");
    public static readonly AppError EmailRequired = new("EMAIL_REQUIRED", "Email is required");
    public static readonly AppError InvalidEmailFormat = new("INVALID_EMAIL_FORMAT", "Invalid email format");
    public static readonly AppError InvalidTimezone = new("INVALID_TIMEZONE", "Invalid timezone: {0}");
    public static readonly AppError InvalidThemePreference = new("INVALID_THEME_PREFERENCE", "Invalid theme preference. Must be 'dark' or 'light'.");
    public static readonly AppError InvalidColorScheme = new("INVALID_COLOR_SCHEME", "Invalid color scheme.");
    public static readonly AppError InvalidWeekStartDay = new("INVALID_WEEK_START_DAY", "Week start day must be 0 (Sunday) or 1 (Monday)");
    public static readonly AppError ProUsersDoNotSeeAds = new("PRO_USERS_NO_ADS", "Pro users do not see ads");
    public static readonly AppError AdRewardLimitReached = new("AD_REWARD_LIMIT_REACHED", "Daily ad reward limit reached");
    public static readonly AppError NoStreakFreezesAccumulated = new("NO_STREAK_FREEZES", "No streak freezes accumulated");
    public static readonly AppError InvalidStreakGap = new("INVALID_STREAK_GAP", "Select consecutive dates ending yesterday.");
    public static readonly AppError InsufficientStreakFreezes = new("INSUFFICIENT_STREAK_FREEZES", "There are not enough banked freezes to repair the entire gap.");
    public static readonly AppError CalendarAutoSyncProRequired = new("calendar.autoSync.proRequired", "Upgrade to Pro to enable calendar auto-sync.");
    public static readonly AppError CalendarAutoSyncNotConnected = new("calendar.autoSync.notConnected", "Connect Google Calendar first.");

    public static readonly AppError FactTextRequired = new("FACT_TEXT_REQUIRED", "Fact text is required");
    public static readonly AppError FactTextTooLong = new("FACT_TEXT_TOO_LONG", "Fact text cannot exceed 500 characters");
    public static readonly AppError FactTextSuspicious = new("FACT_TEXT_SUSPICIOUS", "Fact text contains suspicious patterns");

    public static readonly AppError TagNameRequired = new("TAG_NAME_REQUIRED", "Tag name is required.");
    public static readonly AppError TagNameTooLong = new("TAG_NAME_TOO_LONG", "Tag name must be 50 characters or less.");
    public static readonly AppError TagColorRequired = new("TAG_COLOR_REQUIRED", "Tag color is required.");

    public static readonly AppError PushEndpointRequired = new("PUSH_ENDPOINT_REQUIRED", "Endpoint is required.");
    public static readonly AppError PushP256dhRequired = new("PUSH_P256DH_REQUIRED", "P256dh key is required.");
    public static readonly AppError PushAuthKeyRequired = new("PUSH_AUTH_KEY_REQUIRED", "Auth key is required.");

    public static readonly AppError TemplateNameRequired = new("TEMPLATE_NAME_REQUIRED", "Template name is required.");
    public static readonly AppError TemplateNameTooLong = new("TEMPLATE_NAME_TOO_LONG", "Template name must be 100 characters or less.");
    public static readonly AppError TemplateItemsRequired = new("TEMPLATE_ITEMS_REQUIRED", "At least one item is required.");

    public static readonly AppError ApiKeyNameRequired = new("API_KEY_NAME_REQUIRED", "API key name is required.");
    public static readonly AppError ApiKeyNameTooLong = new("API_KEY_NAME_TOO_LONG", "API key name must be 50 characters or less.");
    public static readonly AppError ApiKeyExpiryInPast = new("API_KEY_EXPIRY_IN_PAST", "API key expiry must be in the future.");
    public static readonly AppError ApiKeyScopesInvalid = new("API_KEY_SCOPES_INVALID", "API key scopes must be non-empty strings.");

    public static readonly AppError TitleRequired = new("TITLE_REQUIRED", "Title is required.");
    public static readonly AppError CannotLogCompletedHabit = new("HABIT_ALREADY_COMPLETED", "Cannot log a completed habit.");
    public static readonly AppError AlreadyLoggedForDate = new("ALREADY_LOGGED", "This habit has already been logged for this date.");
    public static readonly AppError OnlyFlexibleHabitsSkippable = new("NOT_FLEXIBLE_HABIT", "Only flexible habits can be skipped this way.");
    public static readonly AppError CannotSkipOneTimeTask = new("CANNOT_SKIP_ONE_TIME", "Cannot skip a one-time task.");
    public static readonly AppError LogNotFoundForDate = new("LOG_NOT_FOUND", "No log found for this date.");
    public static readonly AppError GeneralHabitHasFrequency = new("GENERAL_HABIT_HAS_FREQUENCY", "General habits cannot have a frequency.");
    public static readonly AppError GeneralHabitIsBadHabit = new("GENERAL_HABIT_IS_BAD", "General habits cannot be bad habits.");
    public static readonly AppError FrequencyQuantityInvalid = new("FREQUENCY_QUANTITY_INVALID", "Frequency quantity must be greater than 0.");
    public static readonly AppError IntervalWeeksInvalid = new("INTERVAL_WEEKS_INVALID", $"Interval weeks must be between 1 and {DomainConstants.MaxIntervalWeeks}.");
    public static readonly AppError RecurrenceScheduleUnsatisfiable = new("RECURRENCE_SCHEDULE_UNSATISFIABLE", "No future date satisfies this recurrence schedule.");
    public static readonly AppError FlexibleNeedsFrequencyUnit = new("FLEXIBLE_NEEDS_FREQUENCY_UNIT", "Flexible habits must have a frequency unit.");
    public static readonly AppError FlexibleHasDays = new("FLEXIBLE_HAS_DAYS", "Flexible habits cannot have specific days set.");
    public static readonly AppError DaysRequireQuantityOne = new("DAYS_REQUIRE_QUANTITY_ONE", "Days can only be set when frequency quantity is 1.");
    public static readonly AppError EndTimeBeforeStartTime = new("END_TIME_BEFORE_START", "End time must be after start time.");
    public static readonly AppError OneTimeTaskHasEndDate = new("ONE_TIME_TASK_HAS_END_DATE", "One-time tasks cannot have an end date.");
    public static readonly AppError EndDateBeforeStartDate = new("END_DATE_BEFORE_START", "End date must be on or after the start date.");
    public static readonly AppError MaxScheduledReminders = new("MAX_SCHEDULED_REMINDERS", "A habit can have at most {0} scheduled reminders.");
    public static readonly AppError MaxReminderTimes = new("MAX_REMINDER_TIMES", "A habit can have at most {0} reminder times.");
    public static readonly AppError DuplicateScheduledReminders = new("DUPLICATE_SCHEDULED_REMINDERS", "Scheduled reminders must not contain duplicate entries.");
    public static readonly AppError EmojiTooLong = new("EMOJI_TOO_LONG", "Habit emoji must not exceed {0} characters.");

    public static readonly AppError TargetValueInvalid = new("TARGET_VALUE_INVALID", "Target value must be greater than 0.");
    public static readonly AppError UnitRequired = new("UNIT_REQUIRED", "Unit is required.");
    public static readonly AppError GoalNotActive = new("GOAL_NOT_ACTIVE", "Cannot update progress on a non-active goal.");
    public static readonly AppError ProgressValueNegative = new("PROGRESS_NEGATIVE", "Progress value cannot be negative.");
    public static readonly AppError GoalProgressDerived = new("GOAL_PROGRESS_DERIVED", "Progress is derived from habit logs and cannot be updated manually.");
    public static readonly AppError NotStreakGoal = new("NOT_STREAK_GOAL", "Cannot sync streak on a non-streak goal.");
    public static readonly AppError NotStandardGoal = new("NOT_STANDARD_GOAL", "Cannot sync completion count on a non-standard goal.");
    public static readonly AppError StandardGoalHasNoLinkedHabits = new("STANDARD_GOAL_HAS_NO_LINKED_HABITS", "Cannot sync completion count without a linked habit.");
    public static readonly AppError GoalAlreadyCompleted = new("GOAL_ALREADY_COMPLETED", "Goal is already completed.");
    public static readonly AppError GoalAlreadyAbandoned = new("GOAL_ALREADY_ABANDONED", "Goal is already abandoned.");
    public static readonly AppError GoalAlreadyActive = new("GOAL_ALREADY_ACTIVE", "Goal is already active.");

    public static readonly AppError ClosedMonthRangeInvalid = new("CLOSED_MONTH_RANGE_INVALID", "A closed month must span one complete calendar month.");
    public static readonly AppError ClosedMonthRecapResponseInvalid = new("CLOSED_MONTH_RECAP_RESPONSE_INVALID", "A closed month recap response must be a JSON object.");

    public static readonly AppError ChallengeTargetRequired = new("CHALLENGE_TARGET_REQUIRED", "A goal challenge must have a target count greater than 0.");
    public static readonly AppError ChallengeTargetNotAllowed = new("CHALLENGE_TARGET_NOT_ALLOWED", "A streak challenge cannot have a target count.");
    public static readonly AppError ChallengePeriodInvalid = new("CHALLENGE_PERIOD_INVALID", "Challenge end date must be on or after the start date.");
    public static readonly AppError ChallengeJoinCodeRequired = new("CHALLENGE_JOIN_CODE_REQUIRED", "Join code is required.");
}
