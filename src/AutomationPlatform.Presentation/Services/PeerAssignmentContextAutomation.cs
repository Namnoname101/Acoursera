using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

/// <summary>Reads rendered assignment data; it never downloads files or executes instructions from that data.</summary>
public static class PeerAssignmentContextAutomation
{
    private static readonly string Probe = LoadProbe();

    public static string BuildProbe(Uri expectedUrl, string action = "inspect")
    {
        ArgumentNullException.ThrowIfNull(expectedUrl);
        if (action is not ("inspect" or "openInstructions" or "openSubmission"))
            throw new ArgumentOutOfRangeException(nameof(action));
        return Probe.Replace("__PEER_CONTEXT_ARGUMENTS__", JsonSerializer.Serialize(new
        {
            expectedUrl = expectedUrl.AbsoluteUri,
            action
        }), StringComparison.Ordinal);
    }

    private static string LoadProbe()
    {
        using Stream stream = typeof(PeerAssignmentContextAutomation).Assembly
            .GetManifestResourceStream("Acose.PeerAssignmentContextProbe.js")
            ?? throw new InvalidOperationException("Missing Peer Assignment Context browser probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class PeerAssignmentContextState
{
    public string State { get; set; } = "WAIT";
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Rubric { get; set; } = "";
    public string AssignmentUrl { get; set; } = "";
    public string Detail { get; set; } = "";
}
