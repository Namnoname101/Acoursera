using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

internal static class QuizFeedbackAutomation
{
    private static readonly string Probe = ReadProbe();

    internal static string BuildProbe(Uri expected)
    {
        if (!expected.IsAbsoluteUri || expected.Scheme != Uri.UriSchemeHttps ||
            !expected.IsDefaultPort || expected.UserInfo.Length != 0 ||
            !(expected.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
              expected.Host.Equals("www.coursera.org", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Feedback must belong to a trusted Coursera document.", nameof(expected));
        return Probe.Replace("__EXPECTED_URL__", JsonSerializer.Serialize(expected.AbsoluteUri), StringComparison.Ordinal);
    }

    private static string ReadProbe()
    {
        using var stream = typeof(QuizFeedbackAutomation).Assembly.GetManifestResourceStream("Acose.QuizFeedbackProbe.js")
            ?? throw new InvalidOperationException("Missing quiz feedback probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
