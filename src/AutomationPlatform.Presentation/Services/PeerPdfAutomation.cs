using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

public static class PeerPdfAutomation
{
    private static readonly string Probe = LoadProbe();

    public static string BuildProbe(Uri expected, string action = "inspect", string? expectedFileName = null,
        PeerPdfSubmissionMetadata? metadata = null) =>
        Probe.Replace("__PEER_ARGUMENTS__", JsonSerializer.Serialize(new
        {
            expectedUrl = expected.AbsoluteUri,
            action,
            expectedFileName,
            projectTitle = metadata?.ProjectTitle,
            fileTitle = metadata?.FileTitle,
            caption = metadata?.Caption
        }), StringComparison.Ordinal);

    private static string LoadProbe()
    {
        using Stream stream = typeof(PeerPdfAutomation).Assembly.GetManifestResourceStream("Acose.PeerPdfProbe.js")
            ?? throw new InvalidOperationException("Missing Peer PDF browser probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class PeerPdfPageState
{
    public string State { get; set; } = "WAIT";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}
