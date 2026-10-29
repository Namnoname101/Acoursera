using System.Text;

namespace AutomationPlatform.Presentation.Services;

/// <summary>
/// Human-readable form metadata derived from the same AI draft as the uploaded PDF.
/// The physical PDF name remains an internal upload receipt and is deliberately not
/// used as a learner-facing title or caption.
/// </summary>
public sealed record PeerPdfSubmissionMetadata(string ProjectTitle, string FileTitle, string Caption)
{
    private const int MaximumTitleCharacters = 200;
    private const int MaximumCaptionCharacters = 800;

    public static PeerPdfSubmissionMetadata FromDraft(PeerPdfDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        string title = Limit(Normalize(draft.Title), MaximumTitleCharacters);
        string description = draft.Sections?
            .SelectMany(section => section?.Paragraphs ?? [])
            .Select(Normalize)
            .FirstOrDefault(paragraph => paragraph.Length > 0) ?? "";
        description = Limit(description, MaximumCaptionCharacters);
        if (description.Length == 0) description = title;

        return new(title, title, description);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString().Trim();
    }

    private static string Limit(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        int end = value.LastIndexOf(' ', maximum - 1, maximum);
        if (end < maximum * 2 / 3) end = maximum - 1;
        return value[..end].TrimEnd() + "…";
    }
}
