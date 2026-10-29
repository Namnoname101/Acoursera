using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

public static class PeerReviewAutomation
{
    private static readonly string Probe = LoadProbe();
    public static string BuildProbe(Uri expected, string action = "probe", int? remainingBudget = null,
        IReadOnlyList<string>? reviewComments = null, string? reviewContext = null) =>
        Probe.Replace("__PEER_REVIEW_ARGUMENTS__", JsonSerializer.Serialize(new
        {
            expectedUrl = expected.AbsoluteUri, action, remainingBudget, reviewComments, reviewContext
        }), StringComparison.Ordinal);

    public static string? AssignmentPath(Uri? uri)
    {
        if (uri is null || uri.Scheme != "https" ||
            !(uri.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("www.coursera.org", StringComparison.OrdinalIgnoreCase))) return null;
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 5 && parts[0] == "learn" && parts[2] == "peer"
            ? "/" + string.Join('/', parts.Take(5)) : null;
    }

    public static Uri? ReviewHome(Uri uri)
    {
        string? path = AssignmentPath(uri);
        if (path is null) return null;
        string[] suffix = uri.AbsolutePath[path.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        string action = suffix.FirstOrDefault() is "review" or "reviews" or "give-feedback"
            ? suffix[0] : "give-feedback";
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + path + "/" + action);
    }

    private static string LoadProbe()
    {
        using Stream stream = typeof(PeerReviewAutomation).Assembly.GetManifestResourceStream("Acose.PeerReviewProbe.js")
            ?? throw new InvalidOperationException("Missing Peer Review browser probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class PeerReviewPageState
{
    public string State { get; set; } = "WAIT";
    public string Detail { get; set; } = "";
    public string ReviewKey { get; set; } = "";
    public int? Remaining { get; set; }
    public int? Completed { get; set; }
    public int? Required { get; set; }
    public int GroupCount { get; set; }
    public int FilledComments { get; set; }
    public int EmptyCommentCount { get; set; }
    public bool SubmissionConfirmed { get; set; }
    public bool ReviewsCompleted { get; set; }
    public string AssignmentTitle { get; set; } = "";
    public string SubmissionText { get; set; } = "";
    public string RubricText { get; set; } = "";
    public string ContextFingerprint { get; set; } = "";
    public string[] CommentPrompts { get; set; } = [];
}
