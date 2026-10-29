using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

internal static class QuizOpeningAutomation
{
    private static readonly string Probe = ReadProbe();

    internal static string BuildProbe(Uri expected, bool allowPageActions, bool feedbackRead) => Probe
        .Replace("__EXPECTED_URL__", JsonSerializer.Serialize(expected.AbsoluteUri), StringComparison.Ordinal)
        .Replace("__ALLOW_PAGE_ACTIONS__", allowPageActions ? "true" : "false", StringComparison.Ordinal)
        .Replace("__FEEDBACK_READ__", feedbackRead ? "true" : "false", StringComparison.Ordinal);

    private static string ReadProbe()
    {
        using var stream = typeof(QuizOpeningAutomation).Assembly.GetManifestResourceStream("Acose.QuizOpeningProbe.js")
            ?? throw new InvalidOperationException("Missing quiz opening probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
