namespace AutomationPlatform.Presentation.Services;

public static class CourseAutomationPolicy
{
    private static readonly HashSet<string> UserActionBlockerCodes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "ADMIN_PAUSED",
        "COURSERA_PLUS_REQUIRED",
        "COURSERA_ACCOUNT_ID_UNAVAILABLE",
        "COURSE_ENROLLMENT_REQUIRED",
        "COURSE_ENROLLMENT_CONFIRMATION_REQUIRED",
        "COURSE_ENROLLMENT_AMBIGUOUS",
        "QUIZ_LEGAL_NAME_CONFLICT",
        "QUIZ_PROFILE_ACCOUNT_CHANGED",
        "QUIZ_DEFERRED_REVIEW_REQUIRED",
        "LAB_LEGAL_NAME_CHANGED",
        "GRADED_LAB_COMPLETION_TIMEOUT",
        "LAB_COMPLETION_TIMEOUT",
        "PEER_ASSIGNMENT_USER_WORK_REQUIRED",
        "PEER_REVIEW_USER_JUDGMENT_REQUIRED",
        "PEER_REVIEW_ACTION_REQUIRED",
    };

    public static bool RequiresUserAction(string? errorCode) =>
        !string.IsNullOrWhiteSpace(errorCode) &&
        UserActionBlockerCodes.Contains(errorCode);

    public static int ClampRunningProgress(int progress) =>
        Math.Clamp(progress, 0, 99);

    public static bool AreAllModulesEffectivelyComplete(
        IEnumerable<int>? allModules,
        IEnumerable<int>? courseraCompletedModules,
        IEnumerable<int>? automationCompletedModules)
    {
        HashSet<int> required = new(allModules ?? []);
        if (required.Count == 0)
        {
            return false;
        }

        HashSet<int> completed = new(courseraCompletedModules ?? []);
        completed.UnionWith(automationCompletedModules ?? []);
        return required.All(completed.Contains);
    }

    public static bool IsPeerReviewPath(string? absolutePath)
    {
        string[] pathSegments = (absolutePath ?? string.Empty).Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int peerIndex = Array.FindIndex(pathSegments, segment =>
            string.Equals(segment, "peer", StringComparison.OrdinalIgnoreCase));
        return peerIndex >= 0 && pathSegments.Skip(peerIndex + 3).Any(segment =>
            string.Equals(segment, "give-feedback", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "review-next", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "review", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "reviews", StringComparison.OrdinalIgnoreCase));
    }

    public static TimeSpan GetRecoveryDelay(int attempt)
    {
        int normalizedAttempt = Math.Max(1, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(5_000, 500 * normalizedAttempt));
    }
}
