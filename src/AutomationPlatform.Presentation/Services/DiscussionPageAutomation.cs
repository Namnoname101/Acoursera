using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomationPlatform.Presentation.Services;

internal static class DiscussionPageAutomation
{
    private static readonly string Probe = ReadProbe();
    internal static string BuildProbe(Uri expected, string reply, string action = "inspect",
        string? expectedTitle = null, string? expectedPrompt = null)
    {
        var urlJson = JsonSerializer.Serialize(expected.AbsoluteUri);
        var replyJson = JsonSerializer.Serialize(reply);
        var actionJson = JsonSerializer.Serialize(action);
        var contextJson = expectedTitle is not null && expectedPrompt is not null
            ? JsonSerializer.Serialize(new { Title = expectedTitle, Prompt = expectedPrompt }) : "null";
        // Substitute template tokens once. User/AI text may itself contain a token;
        // never interpret or rewrite those characters inside serialized JSON.
        return Regex.Replace(Probe, "__(?:EXPECTED_URL|EXPECTED_REPLY|EXPECTED_CONTEXT|ACTION)__", match => match.Value switch
        {
            "__EXPECTED_URL__" => urlJson,
            "__EXPECTED_REPLY__" => replyJson,
            "__EXPECTED_CONTEXT__" => contextJson,
            "__ACTION__" => actionJson,
            _ => throw new InvalidOperationException("Unknown discussion probe token.")
        });
    }

    private static string ReadProbe()
    {
        using var stream = typeof(DiscussionPageAutomation).Assembly.GetManifestResourceStream("Acose.DiscussionPageProbe.js")
            ?? throw new InvalidOperationException("Missing discussion page probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
