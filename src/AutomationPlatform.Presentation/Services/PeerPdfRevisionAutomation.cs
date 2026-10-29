using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

public static class PeerPdfRevisionAutomation
{
    private static readonly string Probe = LoadProbe();

    public static bool IsReceiptForFile(PeerPdfRevisionPageState receipt, string expectedFileName) =>
        !string.IsNullOrWhiteSpace(expectedFileName) && receipt.State is "FAILED_READY" or "NOT_FAILED" &&
        string.Equals(receipt.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase);

    public static string BuildProbe(Uri expected, string action = "inspect", string? expectedFileName = null,
        string? expectedSnapshotToken = null, string? expectedDialogToken = null) => Probe.Replace("__PEER_REVISION_ARGUMENTS__", JsonSerializer.Serialize(new
        {
            expectedUrl = expected.AbsoluteUri, action, expectedFileName, expectedSnapshotToken, expectedDialogToken
        }), StringComparison.Ordinal);

    private static string LoadProbe()
    {
        using Stream stream = typeof(PeerPdfRevisionAutomation).Assembly.GetManifestResourceStream("Acose.PeerPdfRevisionProbe.js")
            ?? throw new InvalidOperationException("Missing Peer PDF revision browser probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class PeerPdfRevisionPageState
{
    public string State { get; set; } = "WAIT";
    public string Detail { get; set; } = "";
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Rubric { get; set; } = "";
    public string Feedback { get; set; } = "";
    public string SnapshotToken { get; set; } = "";
    public string DialogToken { get; set; } = "";
    public bool CanStartAttempt { get; set; }
    public double? EarnedPoints { get; set; }
    public double? PassingPoints { get; set; }
    public double? TotalPoints { get; set; }
}
