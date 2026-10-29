using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

internal sealed class QuizFeedbackOpeningState
{
    public string State { get; set; } = "INVALID";
    public string Signature { get; set; } = "";
    public int QuestionCount { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool AtEnd { get; set; }
}

internal static class QuizFeedbackOpeningAutomation
{
    private static readonly string Probe = ReadProbe();
    internal static string BuildProbe(Uri expected, string action = "inspect")
    {
        if (!expected.IsAbsoluteUri || expected.Scheme != Uri.UriSchemeHttps || !expected.IsDefaultPort || expected.UserInfo.Length != 0 ||
            !(expected.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) || expected.Host.Equals("www.coursera.org", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Feedback opening requires a trusted course URL.");
        if (action is not ("inspect" or "prepareClick" or "click" or "scroll" or "top")) throw new ArgumentException("Unknown feedback action.");
        return Probe.Replace("__ACTION__", JsonSerializer.Serialize(action), StringComparison.Ordinal)
            .Replace("__EXPECTED_URL__", JsonSerializer.Serialize(expected.AbsoluteUri), StringComparison.Ordinal);
    }
    private static string ReadProbe()
    {
        using var stream = typeof(QuizFeedbackOpeningAutomation).Assembly.GetManifestResourceStream("Acose.QuizFeedbackOpeningProbe.js")
            ?? throw new InvalidOperationException("Missing feedback opening probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
