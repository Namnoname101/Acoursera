using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation.Services;

/// <summary>Per-assignment, per-learner intermediate research. Never changes submission journal state.</summary>
public sealed class PeerResearchCheckpoint
{
    private const int MaximumArchiveDirectories = 128;
    private readonly string _directory;
    public PeerResearchCheckpoint(PeerAiPdfCache cache, string contextHash, string factsHash, int generation)
    {
        if (generation is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(generation));
        _directory = Path.Combine(Path.GetDirectoryName(cache.EntryPath)!, "research-work", cache.ProfileKey,
            PeerAiPdfCache.Hash("research-v1:" + contextHash + ":" + factsHash + ":" + generation));
        PeerAiPdfCache.EnsureOrdinaryPath(_directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task<AiCompletionResult> CompleteAsync(string system, string input, double temperature, IProgress<string>? progress,
        CancellationToken ct, Func<string, string, double, IProgress<string>?, CancellationToken, Task<AiCompletionResult>> complete)
    {
        ct.ThrowIfCancellationRequested();
        string key = PeerAiPdfCache.Hash(system + "\n" + input);
        string path = Path.Combine(_directory, "reply-" + key + ".json");
        var cached = await ReadAsync<AiCompletionResult>(path, ct);
        var rejected = await ReadAsync<RejectedReply>(RejectionPath(key), ct);
        ValidateRejection(rejected, key);
        if (IsUsable(cached))
        {
            // A durable rejection is written first. Finish its move if the process stopped between
            // recording the rejection and archiving the old response.
            if (rejected?.ReplyHash == ReplyHash(cached!)) ArchiveReply(path);
            else { ct.ThrowIfCancellationRequested(); return cached!; }
        }
        else if (cached != null)
            throw new InvalidDataException("Research checkpoint does not contain a successful reply.");

        if (rejected == null)
        {
            cached = await RecoverArchivedReplyAsync(key, ct) ?? await RecoverLegacySectionAsync(system, input, ct);
            if (cached != null)
            {
                await WriteAsync(path, cached, ct);
                ct.ThrowIfCancellationRequested();
                return cached;
            }
        }
        ct.ThrowIfCancellationRequested();
        var result = await complete(system, input, temperature, progress, ct);
        ct.ThrowIfCancellationRequested();
        if (result.Success && result.Content.Length is > 0 and <= 120000)
            await WriteAsync(path, result, ct);
        return result;
    }

    public async Task<PeerResearchEvidence> EvidenceAsync(PeerResearchSelection selection, CancellationToken ct,
        Func<PeerResearchSelection, CancellationToken, Task<PeerResearchEvidence>> fetch)
    {
        ct.ThrowIfCancellationRequested();
        string path = Path.Combine(_directory, "evidence-" + PeerAiPdfCache.Hash(JsonSerializer.Serialize(selection)) + ".json");
        var result = await ReadAsync<PeerResearchEvidence>(path, ct);
        if (result != null) { ct.ThrowIfCancellationRequested(); return result; }
        result = await fetch(selection, ct);
        ct.ThrowIfCancellationRequested();
        await WriteAsync(path, result, ct);
        return result;
    }

    /// <summary>Reject only this failed stage; evidence and other completed stages remain reusable.</summary>
    public void RejectReply(string system, string input)
    {
        string key = PeerAiPdfCache.Hash(system + "\n" + input);
        string path = Path.Combine(_directory, "reply-" + key + ".json");
        PeerAiPdfCache.EnsureOrdinaryPath(path);
        AiCompletionResult? cached = null;
        if (File.Exists(path))
        {
            CheckRecordSize(path);
            cached = ParseRecord<AiCompletionResult>(File.ReadAllText(path));
        }
        // Keep a tombstone even when the live reply is absent: an older, invalid reply must not
        // return from a legacy whole-run archive on the next start.
        WriteRejection(RejectionPath(key), new RejectedReply(1, key, cached == null ? null : ReplyHash(cached)));
        if (File.Exists(path)) ArchiveReply(path);
    }

    private string RejectionPath(string key) => Path.Combine(_directory, "rejected-reply-" + key + ".json");

    private static bool IsUsable(AiCompletionResult? result) => result is { Success: true, Content.Length: > 0 and <= 120000 };
    private static string ReplyHash(AiCompletionResult result) => PeerAiPdfCache.Hash(JsonSerializer.Serialize(result));

    private static void ValidateRejection(RejectedReply? marker, string key)
    {
        if (marker != null && (marker.Version != 1 || marker.Key != key ||
            (marker.ReplyHash != null && !System.Text.RegularExpressions.Regex.IsMatch(marker.ReplyHash, "^[A-F0-9]{64}$"))))
            throw new InvalidDataException("Invalid research reply rejection checkpoint.");
    }

    private async Task<AiCompletionResult?> RecoverArchivedReplyAsync(string key, CancellationToken ct)
    {
        foreach (string archive in GetArchives(ct))
        {
            // Never search a sibling learner, assignment, context or generation directory.
            var reply = await ReadAsync<AiCompletionResult>(Path.Combine(archive, "reply-" + key + ".json"), ct);
            if (reply == null) continue;
            if (!IsUsable(reply)) throw new InvalidDataException("Archived research checkpoint does not contain a successful reply.");
            return reply;
        }
        return null;
    }

    private string[] GetArchives(CancellationToken ct)
    {
        PeerAiPdfCache.EnsureOrdinaryPath(_directory);
        var archives = Directory.EnumerateDirectories(_directory, "rejected-*", SearchOption.TopDirectoryOnly)
            .Take(MaximumArchiveDirectories + 1).ToArray();
        if (archives.Length > MaximumArchiveDirectories)
            throw new InvalidDataException("Research reply archive exceeds its recovery limit.");
        foreach (string archive in archives)
        {
            ct.ThrowIfCancellationRequested();
            PeerAiPdfCache.EnsureOrdinaryPath(archive);
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(archive), "^rejected-[a-fA-F0-9]{32}$"))
                throw new InvalidDataException("Unexpected research reply archive path.");
        }
        return archives.OrderByDescending(Directory.GetLastWriteTimeUtc).ThenByDescending(p => p, StringComparer.Ordinal).ToArray();
    }

    private async Task<AiCompletionResult?> RecoverLegacySectionAsync(string system, string input, CancellationToken ct)
    {
        // Old versions included prior section endings in each key and discarded all replies on a
        // single word-budget failure. Reusing an earlier, now-acceptable paragraph can change every
        // following key. Recover only initial prose for the EXACT same archived plan, never an audit,
        // length adjustment, revision, or validation-repair prompt. The caller still validates prose
        // and audits the assembled paper against the current evidence before generating any PDF.
        if (!system.Contains("Write ONLY currentSection.", StringComparison.Ordinal)) return null;
        using JsonDocument? request = TryReadJson(input);
        if (request == null) return null;
        JsonElement root = request.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 8 ||
            Field(root, "assignment").ValueKind != JsonValueKind.String ||
            Field(root, "verifiedEvidence").ValueKind != JsonValueKind.String ||
            Field(root, "previousSectionEndings").ValueKind != JsonValueKind.Array) return null;
        string? identity = PlanIdentity(root, "outline");
        var current = SectionIdentity(Field(root, "currentSection"));
        if (identity == null || current == null || Field(root, "outline").EnumerateArray().Count(e => SectionIdentity(e) == current) != 1)
            return null;

        int scanned = 0;
        foreach (string archive in GetArchives(ct))
        {
            var paths = Directory.EnumerateFiles(archive, "reply-*.json", SearchOption.TopDirectoryOnly).Take(257).ToArray();
            scanned += paths.Length;
            if (scanned > 256) throw new InvalidDataException("Legacy research section archive exceeds its recovery limit.");
            var replies = new List<(string Path, AiCompletionResult Reply)>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^reply-[A-F0-9]{64}\.json$"))
                    throw new InvalidDataException("Unexpected archived research reply path.");
                string candidateKey = name.Substring(6, 64);
                var marker = await ReadAsync<RejectedReply>(RejectionPath(candidateKey), ct);
                ValidateRejection(marker, candidateKey);
                if (marker != null) continue;
                var reply = await ReadAsync<AiCompletionResult>(path, ct);
                if (!IsUsable(reply)) throw new InvalidDataException("Archived research checkpoint does not contain a successful reply.");
                replies.Add((path, reply!));
                using JsonDocument? candidate = TryReadJson(reply!.Content);
                if (candidate != null && Field(candidate.RootElement, "status").ValueKind == JsonValueKind.String &&
                    Field(candidate.RootElement, "status").GetString() == "ready")
                {
                    string? candidateIdentity = PlanIdentity(candidate.RootElement, "sections");
                    if (candidateIdentity != null) identities.Add(candidateIdentity);
                }
            }
            // Never mix sections from an archive containing different research topics or outlines.
            if (identities.Count != 1 || !identities.Contains(identity)) continue;
            foreach (var candidate in replies.OrderByDescending(r => File.GetLastWriteTimeUtc(r.Path)).ThenByDescending(r => r.Path, StringComparer.Ordinal))
            {
                using JsonDocument? section = TryReadJson(candidate.Reply.Content);
                if (section == null || section.RootElement.ValueKind != JsonValueKind.Object || section.RootElement.EnumerateObject().Count() != 2) continue;
                JsonElement heading = Field(section.RootElement, "heading"), paragraphs = Field(section.RootElement, "paragraphs");
                if (heading.ValueKind == JsonValueKind.String && heading.GetString()?.Trim() == current.Heading &&
                    paragraphs.ValueKind == JsonValueKind.Array && paragraphs.GetArrayLength() is > 0 and <= 32 &&
                    paragraphs.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String && p.GetString()!.Length is > 0 and <= 6000))
                    return candidate.Reply;
            }
        }
        return null;
    }

    private static JsonDocument? TryReadJson(string text)
    {
        if (text.Length > 120000) return null;
        string raw = text.Trim();
        if (raw.StartsWith("```json", StringComparison.OrdinalIgnoreCase) && raw.EndsWith("```", StringComparison.Ordinal)) raw = raw[7..^3].Trim();
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateJsonStrings(document.RootElement);
            return document;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            document?.Dispose();
            return null;
        }
    }

    private static void ValidateJsonStrings(JsonElement item)
    {
        // JsonDocument can defer decoding an unpaired surrogate until GetString/Name is called.
        // An unusable historical model reply is not a candidate, unlike a corrupt outer checksum.
        if (item.ValueKind == JsonValueKind.String) _ = item.GetString();
        else if (item.ValueKind == JsonValueKind.Array)
            foreach (JsonElement value in item.EnumerateArray()) ValidateJsonStrings(value);
        else if (item.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in item.EnumerateObject()) { _ = property.Name; ValidateJsonStrings(property.Value); }
    }

    private static JsonElement Field(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) return default;
        JsonElement result = default;
        int count = 0;
        foreach (JsonProperty property in item.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { count++; result = property.Value; }
        return count == 1 ? result : default;
    }

    private static string? PlanIdentity(JsonElement item, string sectionsName)
    {
        JsonElement title = Field(item, "title"), question = Field(item, "question"), language = Field(item, "language"), sections = Field(item, sectionsName);
        if (title.ValueKind != JsonValueKind.String || title.GetString()!.Trim().Length is < 1 or > 200 ||
            question.ValueKind != JsonValueKind.String || question.GetString()!.Trim().Length is < 1 or > 1200 ||
            language.ValueKind != JsonValueKind.String || language.GetString()!.Trim().Length is < 1 or > 100 ||
            sections.ValueKind != JsonValueKind.Array || sections.GetArrayLength() is < 4 or > 16) return null;
        var outline = sections.EnumerateArray().Select(SectionIdentity).ToArray();
        if (outline.Any(s => s == null) || outline.Select(s => s!.Heading).Distinct(StringComparer.Ordinal).Count() != outline.Length) return null;
        return JsonSerializer.Serialize(new { Title = title.GetString()!.Trim(), Question = question.GetString()!.Trim(), Language = language.GetString()!.Trim(), Sections = outline });
    }

    private static LegacySection? SectionIdentity(JsonElement item)
    {
        JsonElement heading = Field(item, "heading"), purpose = Field(item, "purpose"), min = Field(item, "minimumWords"), max = Field(item, "maximumWords");
        if (heading.ValueKind != JsonValueKind.String || purpose.ValueKind != JsonValueKind.String ||
            heading.GetString()!.Trim().Length is < 1 or > 200 || purpose.GetString()!.Trim().Length is < 1 or > 1500 ||
            min.ValueKind != JsonValueKind.Number || max.ValueKind != JsonValueKind.Number ||
            !min.TryGetInt32(out int minimum) || !max.TryGetInt32(out int maximum) || minimum < 100 || maximum > 1000 || maximum < minimum) return null;
        return new(heading.GetString()!.Trim(), purpose.GetString()!.Trim(), minimum, maximum);
    }

    private sealed record LegacySection(string Heading, string Purpose, int MinimumWords, int MaximumWords);

    private void ArchiveReply(string path)
    {
        PeerAiPdfCache.EnsureOrdinaryPath(path);
        string archive = Path.Combine(_directory, "rejected-" + Guid.NewGuid().ToString("N"));
        PeerAiPdfCache.EnsureOrdinaryPath(archive);
        Directory.CreateDirectory(archive);
        File.Move(path, Path.Combine(archive, Path.GetFileName(path)), overwrite: false);
    }

    private static void WriteRejection(string path, RejectedReply value)
    {
        PeerAiPdfCache.EnsureOrdinaryPath(path);
        string payload = JsonSerializer.Serialize(value);
        byte[] record = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { payload, sha256 = PeerAiPdfCache.Hash(payload) }));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(record);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed record RejectedReply(int Version, string Key, string? ReplyHash);

    public void ArchiveRejectedReplies()
    {
        // Keep evidence, completed PDF journal and user data; only retryable AI replies move aside.
        PeerAiPdfCache.EnsureOrdinaryPath(_directory);
        string archive = Path.Combine(_directory, "rejected-" + Guid.NewGuid().ToString("N"));
        string[] files = Directory.GetFiles(_directory, "reply-*.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0) return;
        Directory.CreateDirectory(archive);
        foreach (string file in files)
        {
            PeerAiPdfCache.EnsureOrdinaryPath(file);
            string name = Path.GetFileName(file);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^reply-[A-F0-9]{64}\.json$"))
                throw new InvalidDataException("Unexpected research checkpoint path.");
            File.Move(file, Path.Combine(archive, name), overwrite: false);
        }
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        PeerAiPdfCache.EnsureOrdinaryPath(path);
        if (!File.Exists(path)) return null;
        CheckRecordSize(path);
        return ParseRecord<T>(await File.ReadAllTextAsync(path, ct));
    }

    private static void CheckRecordSize(string path)
    {
        if (new FileInfo(path).Length > 1000000) throw new InvalidDataException("Research checkpoint exceeds its size limit.");
    }

    private static T ParseRecord<T>(string text) where T : class
    {
        using JsonDocument record = JsonDocument.Parse(text);
        string payload = record.RootElement.GetProperty("payload").GetString() ?? "";
        if (record.RootElement.GetProperty("sha256").GetString() != PeerAiPdfCache.Hash(payload))
            throw new InvalidDataException("Research checkpoint has changed; refusing to reuse it.");
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Invalid research checkpoint.");
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        PeerAiPdfCache.EnsureOrdinaryPath(path);
        string payload = JsonSerializer.Serialize(value);
        string record = JsonSerializer.Serialize(new { payload, sha256 = PeerAiPdfCache.Hash(payload) });
        if (record.Length > 800000) throw new InvalidDataException("Research checkpoint is too large.");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(record);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); } // Only this operation's uniquely named temp file.
    }
}
