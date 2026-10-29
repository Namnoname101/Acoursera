using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AutomationPlatform.Presentation.Services;

public sealed record PreparedPdfResolution(string? Path, string Status, string ExpectedDirectory);

public static class PreparedPdfLibrary
{
    public static PreparedPdfResolution Find(string title, Uri peerUri, IEnumerable<string> roots)
    {
        string[] parts = peerUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int courseIndex = Array.FindIndex(parts, p => p.Equals("learn", StringComparison.OrdinalIgnoreCase));
        int peerIndex = Array.FindIndex(parts, p => p.Equals("peer", StringComparison.OrdinalIgnoreCase));
        string course = courseIndex >= 0 && courseIndex + 1 < parts.Length ? Uri.UnescapeDataString(parts[courseIndex + 1]) : "";
        string assignment = peerIndex >= 0 && peerIndex + 2 < parts.Length ? Uri.UnescapeDataString(parts[peerIndex + 2]) : "";
        string id = peerIndex >= 0 && peerIndex + 1 < parts.Length ? Uri.UnescapeDataString(parts[peerIndex + 1]) : "";
        if (!Regex.IsMatch(course, @"^[a-zA-Z0-9_-]+$") || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(assignment))
            return new(null, "MISSING", "");

        string[] directories = roots.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => System.IO.Path.GetFullPath(System.IO.Path.Combine(p, course)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var keys = Keys(title, assignment, id);
        var candidates = new List<string>();
        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            string[] matches = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(p => System.IO.Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                .Where(p => Keys(System.IO.Path.GetFileNameWithoutExtension(p)).Overlaps(keys))
                .Where(IsPdf).ToArray();
            candidates.AddRange(matches);
        }
        if (candidates.Count > 1) return new(null, "AMBIGUOUS", directories.FirstOrDefault() ?? "");
        if (candidates.Count == 1) return new(candidates[0], "FOUND", System.IO.Path.GetDirectoryName(candidates[0])!);
        return new(null, "MISSING", directories.FirstOrDefault() ?? "");
    }

    private static HashSet<string> Keys(params string[] values)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            var letters = new StringBuilder();
            foreach (char c in value.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    letters.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c == 'đ' || c == 'Đ' ? 'd' : c) : ' ');
            string normalized = Regex.Replace(letters.ToString(), @"\s+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s+coursera$", "");
            if (normalized.Length == 0) continue;
            keys.Add(normalized);
            keys.Add(Regex.Replace(normalized, @"^(peer graded assignment|graded peer assignment|graded assignment|peer reviewed assignment|peer assignment|assignment)\s+", ""));
        }
        keys.Remove("");
        return keys;
    }

    private static bool IsPdf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5];
            return stream.Read(header) == 5 && header.SequenceEqual("%PDF-"u8);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
