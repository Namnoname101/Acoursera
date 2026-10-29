using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

internal static class CourseEnrollmentAutomation
{
    private static readonly string Probe = ReadProbe();

    internal static string BuildProbe(Uri expectedDocument, bool allowEnrollmentStart) => Probe
        .Replace("__EXPECTED_URL_JSON__", JsonSerializer.Serialize(expectedDocument.AbsoluteUri), StringComparison.Ordinal)
        .Replace("__ALLOW_ENROLLMENT_START__", allowEnrollmentStart ? "true" : "false", StringComparison.Ordinal);

    private static string ReadProbe()
    {
        using Stream stream = typeof(CourseEnrollmentAutomation).Assembly.GetManifestResourceStream(
            "Acose.CourseEnrollmentProbe.js") ?? throw new InvalidOperationException("Missing enrollment probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
