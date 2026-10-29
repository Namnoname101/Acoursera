using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomationPlatform.Presentation.Services;

/// <summary>Per-learner durable draft and submission intent. Never contains login credentials.</summary>
public sealed class PeerAiPdfCache
{
    public const int MaximumRevisions = 3;
    private const int MaxEntryBytes = 400_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 12 };
    private readonly string _directory;
    private readonly string _entryPath;
    private readonly string _assignmentKey;
    private readonly string _profileKey;

    public PeerAiPdfCache(string root, string learnerIdentity, Uri assignment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(learnerIdentity);
        _assignmentKey = AssignmentKey(assignment);
        _profileKey = Hash(learnerIdentity);
        _directory = Path.Combine(Path.GetFullPath(root), _assignmentKey);
        EnsureOrdinaryPath(_directory);
        Directory.CreateDirectory(_directory);
        _entryPath = Path.Combine(_directory, _profileKey + ".json");
    }

    public string EntryPath => _entryPath;
    public string ProfileKey => _profileKey;
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string AssignmentKey(Uri assignment)
    {
        if (!assignment.IsAbsoluteUri || assignment.Scheme != "https" || !assignment.IsDefaultPort ||
            assignment.UserInfo.Length != 0 ||
            !(assignment.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
              assignment.Host.Equals("www.coursera.org", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Expected a trusted Coursera assignment URL.");
        Match match = Regex.Match(assignment.AbsolutePath,
            @"^/learn/([a-zA-Z0-9_-]+)/peer/([a-zA-Z0-9_-]+)/([a-zA-Z0-9_-]+)(?:/(?i:instructions|submit|submission|my-submission))?/?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ArgumentException("Expected a peer assignment, not a peer review URL.");
        return Hash(match.Groups[1].Value.ToLowerInvariant() + "/peer/" + match.Groups[2].Value);
    }

    // Held throughout this learner's generation/upload transaction, including across async waits.
    public FileStream? TryAcquireLease() => TryLock(Path.Combine(_directory, _profileKey + ".lock"));

    public PeerAiPdfEntry? Load()
    {
        if (!File.Exists(_entryPath)) return null;
        PeerAiPdfEntry entry = ReadEntry(_entryPath);
        if (entry.ProfileKey != _profileKey || entry.AssignmentKey != _assignmentKey)
            throw new InvalidDataException("PDF cache belongs to a different learner or assignment.");
        return entry;
    }

    public string PdfPath(PeerAiPdfEntry entry) =>
        Path.Combine(_directory, $"assignment-{_profileKey[..12]}-{entry.DraftHash[..16]}.pdf");

    // No other learner's prose is sent to the AI. Similarity checking stays entirely on this PC.
    public string TryReserveDraft(string contextHash, string factsHash, PeerPdfDraft draft)
    {
        using FileStream? gate = TryLock(Path.Combine(_directory, "catalog.lock"));
        if (gate is null) return "BUSY";
        if (Load() is not null) return "EXISTS";
        var files = Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly).Take(5001).ToArray();
        if (files.Length > 5000) throw new InvalidDataException("PDF draft catalog needs archiving before more drafts can be generated.");
        bool similar = false;
        foreach (string path in files)
        {
            PeerAiPdfEntry other = ReadEntry(path);
            if (other.AssignmentKey != _assignmentKey)
                throw new InvalidDataException("Unexpected assignment in PDF draft catalog.");
            similar |= TooSimilar(draft, other.Draft);
        }
        if (similar) return "TOO_SIMILAR";
        Write(new PeerAiPdfEntry(1, _assignmentKey, _profileKey, contextHash, factsHash,
            DraftHash(draft), "", "draft", draft));
        return "RESERVED";
    }

    /// <summary>
    /// Checks a positively identified, failed server submission against this learner's saved PDF.
    /// The caller holds TryAcquireLease for the entire inspect/generate/upload transaction.
    /// RESUME means the revision is already reserved; never generate another revision for that failure.
    /// </summary>
    public string InspectRevision(string failedPdfName, string feedbackHash)
    {
        if (!IsProfilePdfName(failedPdfName, _profileKey) || !IsHash(feedbackHash)) return "INVALID_FAILURE";
        PeerAiPdfEntry? entry = Load();
        if (entry is null) return "NO_DRAFT";

        if (entry.State is "draft" or "ready")
        {
            if (entry.RevisionCount == 0) return "NOT_SUBMITTED";
            if (!string.Equals(entry.ParentPdfName, failedPdfName, StringComparison.OrdinalIgnoreCase))
                return "PDF_MISMATCH";
            if (!string.Equals(entry.FeedbackHash, feedbackHash, StringComparison.OrdinalIgnoreCase))
                return "FAILURE_MISMATCH";
            if (entry.AttemptStartPending) return "ATTEMPT_UNCERTAIN";
            if (entry.State == "ready") _ = GetVerifiedPdf(entry.ContextHash, entry.FactsHash);
            return "RESUME";
        }

        // A pending new revision may not be replaced while the old parent's failed result is visible.
        // Only a failed result explicitly naming the pending/confirmed current artifact is sufficient.
        if (!string.Equals(Path.GetFileName(PdfPath(entry)), failedPdfName, StringComparison.OrdinalIgnoreCase))
            return "PDF_MISMATCH";
        if (entry.RevisionCount >= MaximumRevisions) return "LIMIT_REACHED";
        string failureKey = FailureKey(failedPdfName, feedbackHash);
        if (entry.RevisionFailureKeys?.Contains(failureKey, StringComparer.OrdinalIgnoreCase) == true)
            return "FAILURE_ALREADY_USED";
        string currentPdf = PdfPath(entry);
        ValidatePdf(currentPdf);
        if (entry.PdfHash != FileHash(currentPdf))
            throw new InvalidDataException("Bản PDF đã nộp bị thay đổi trong kho; không tự sửa hoặc nộp nhầm bản.");
        return "CAN_REVISE";
    }

    /// <summary>Archives the prior journal, retains its PDF, and reserves at most three revisions.</summary>
    public string TryReserveRevision(string failedPdfName, string feedbackHash, PeerPdfDraft draft,
        string? newContextHash = null, string? newFactsHash = null)
    {
        if (newContextHash is not null && !IsHash(newContextHash))
            throw new ArgumentException("Invalid revised assignment context hash.", nameof(newContextHash));
        if (newFactsHash is not null && !IsHash(newFactsHash))
            throw new ArgumentException("Invalid revised learner facts hash.", nameof(newFactsHash));
        using FileStream? gate = TryLock(Path.Combine(_directory, "catalog.lock"));
        if (gate is null) return "BUSY";
        string state = InspectRevision(failedPdfName, feedbackHash);
        if (state != "CAN_REVISE") return state;
        PeerAiPdfEntry previous = Load()!;
        string draftHash = DraftHash(draft);
        if (draftHash == previous.DraftHash ||
            previous.PreviousDraftHashes?.Contains(draftHash, StringComparer.OrdinalIgnoreCase) == true ||
            NormalizeProse(DraftText(draft)) == NormalizeProse(DraftText(previous.Draft)))
            return "UNCHANGED_DRAFT";

        string[] files = Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly).Take(5001).ToArray();
        if (files.Length > 5000) throw new InvalidDataException("PDF draft catalog needs archiving before more drafts can be generated.");
        foreach (string path in files)
        {
            PeerAiPdfEntry other = ReadEntry(path);
            if (other.AssignmentKey != _assignmentKey)
                throw new InvalidDataException("Unexpected assignment in PDF draft catalog.");
            if (other.ProfileKey != _profileKey && TooSimilar(draft, other.Draft)) return "TOO_SIMILAR";
        }

        var revision = previous with
        {
            Version = 2,
            ContextHash = newContextHash ?? previous.ContextHash,
            FactsHash = newFactsHash ?? previous.FactsHash,
            Draft = draft,
            DraftHash = draftHash,
            PdfHash = "",
            State = "draft",
            RevisionCount = previous.RevisionCount + 1,
            ParentPdfName = Path.GetFileName(PdfPath(previous)),
            FeedbackHash = feedbackHash.ToUpperInvariant(),
            PreviousDraftHashes = [.. previous.PreviousDraftHashes ?? [], previous.DraftHash],
            RevisionFailureKeys = [.. previous.RevisionFailureKeys ?? [], FailureKey(failedPdfName, feedbackHash)],
            AttemptStartPending = false,
            AttemptConfirmationPending = false
        };
        ValidateEntry(revision);
        Archive(previous);
        Write(revision);
        return "RESERVED";
    }

    /// <summary>Persists intent before the sole Start new attempt click; uncertain clicks are not replayed.</summary>
    public void BeginRevisionAttempt(string failedPdfName, string feedbackHash)
    {
        if (InspectRevision(failedPdfName, feedbackHash) != "RESUME")
            throw new InvalidDataException("Không thể mở lại lần nộp PDF khi kết quả hoặc ý định lần trước chưa rõ.");
        PeerAiPdfEntry entry = Load()!;
        if (entry.State != "ready") throw new InvalidDataException("PDF sửa lại chưa xuất xong.");
        _ = GetVerifiedPdf(entry.ContextHash, entry.FactsHash);
        Write(entry with { AttemptStartPending = true });
    }

    /// <summary>
    /// Persists intent before confirming the known new-attempt dialog. The caller must first
    /// verify that exact dialog on the same assignment while holding this learner's lease.
    /// A confirmation with an uncertain outcome is never replayed, including after a restart.
    /// </summary>
    public void BeginRevisionConfirmation(string expectedParentFileName)
    {
        PeerAiPdfEntry entry = Load() ?? throw new InvalidDataException("Missing revised PDF draft.");
        if (entry.State != "ready" || entry.RevisionCount == 0 || !entry.AttemptStartPending ||
            entry.AttemptConfirmationPending || !IsProfilePdfName(expectedParentFileName, _profileKey) ||
            !string.Equals(entry.ParentPdfName, expectedParentFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Không thể xác nhận lần nộp PDF khi bản cũ hoặc ý định lần trước chưa rõ.");
        _ = GetVerifiedPdf(entry.ContextHash, entry.FactsHash);
        Write(entry with { AttemptConfirmationPending = true });
    }

    /// <summary>
    /// Re-arms only a dialog-opening click, never a destructive confirmation or submission.
    /// The caller must observe the exact old failed artifact/feedback and no dialog after a
    /// restart, under this learner's lease. The same ready revision and budget are retained.
    /// </summary>
    public void ReopenRevisionAttempt(string failedFileName, string feedbackHash)
    {
        PeerAiPdfEntry entry = Load() ?? throw new InvalidDataException("Missing revised PDF draft.");
        if (entry.State != "ready" || entry.RevisionCount == 0 || !entry.AttemptStartPending ||
            entry.AttemptConfirmationPending || !IsProfilePdfName(failedFileName, _profileKey) ||
            !IsHash(feedbackHash) ||
            !string.Equals(entry.ParentPdfName, failedFileName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.FeedbackHash, feedbackHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Không thể mở lại hộp thoại khi kết quả hoặc xác nhận lần trước chưa rõ.");
        _ = GetVerifiedPdf(entry.ContextHash, entry.FactsHash);
        Write(entry with { AttemptStartPending = false });
    }

    private static string FailureKey(string pdfName, string feedbackHash) =>
        Hash(pdfName.ToUpperInvariant() + "\n" + feedbackHash.ToUpperInvariant());

    private static string NormalizeProse(string text) =>
        Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"\s+", " ").Trim();

    private static bool IsProfilePdfName(string? name, string profileKey) =>
        name is not null && Regex.IsMatch(name,
            @"\Aassignment-" + Regex.Escape(profileKey[..12]) + @"-[0-9a-f]{16}\.pdf\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private void Archive(PeerAiPdfEntry entry)
    {
        string directory = Path.Combine(_directory, "history", _profileKey);
        EnsureOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"revision-{entry.RevisionCount}-{entry.DraftHash}.json");
        EnsureOrdinaryPath(path);
        // A crash after archiving but before replacing the current entry is harmless and retryable.
        if (File.Exists(path))
        {
            if (FileHash(path) != FileHash(_entryPath))
                throw new InvalidDataException("Lịch sử PDF không khớp bản hiện tại; giữ nguyên để kiểm tra.");
            return;
        }
        string staging = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(_entryPath, staging, false);
            using (var stream = new FileStream(staging, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(true);
            File.Move(staging, path, false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public void CommitPdf()
    {
        PeerAiPdfEntry entry = Load() ?? throw new InvalidDataException("Missing reserved PDF draft.");
        if (entry.State != "draft") throw new InvalidDataException("PDF draft is no longer editable.");
        string path = PdfPath(entry);
        ValidatePdf(path);
        Write(entry with { State = "ready", PdfHash = FileHash(path) });
    }

    public string GetVerifiedPdf(string contextHash, string factsHash)
    {
        PeerAiPdfEntry entry = Load() ?? throw new InvalidDataException("Missing PDF draft.");
        if (entry.ContextHash != contextHash || entry.FactsHash != factsHash)
            throw new InvalidDataException("Đề bài hoặc dữ liệu người học đã đổi. Cần kiểm tra bản PDF đang lưu trước khi tạo lại.");
        if (entry.State is "pending" or "confirmed")
            throw new InvalidDataException("Đã ghi nhận một lần nộp PDF. Kiểm tra My submission trước; không tự nộp lại khi kết quả chưa rõ.");
        if (entry.State != "ready") throw new InvalidDataException("PDF chưa xuất xong.");
        string path = PdfPath(entry);
        ValidatePdf(path);
        if (entry.PdfHash != FileHash(path)) throw new InvalidDataException("PDF đã bị thay đổi sau khi tạo. Dừng để tránh nộp nhầm bản.");
        return path;
    }

    public void BeginSubmission()
    {
        PeerAiPdfEntry entry = Load() ?? throw new InvalidDataException("Missing PDF draft.");
        _ = GetVerifiedPdf(entry.ContextHash, entry.FactsHash);
        Write(entry with { State = "pending" });
    }

    public void ConfirmSubmission()
    {
        PeerAiPdfEntry? entry = Load();
        if (entry?.State == "pending") Write(entry with { State = "confirmed" });
    }

    public static bool TooSimilar(PeerPdfDraft first, PeerPdfDraft second)
    {
        HashSet<string> a = Shingles(DraftText(first)), b = Shingles(DraftText(second));
        if (a.Count == 0 || b.Count == 0) return true;
        int overlap = a.Count(b.Contains);
        // Five-word containment also catches copying most of a shorter document into a longer one.
        return overlap / (double)Math.Min(a.Count, b.Count) >= 0.55;
    }

    public static string DraftText(PeerPdfDraft draft) => string.Join("\n", draft.Sections.SelectMany(s => s.Paragraphs));
    private static string DraftHash(PeerPdfDraft draft) => Hash(JsonSerializer.Serialize(draft, JsonOptions));
    private static HashSet<string> Shingles(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        string[] words = Regex.Matches(normalized, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant).Select(m => m.Value).ToArray();
        var shingles = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i <= words.Length - 5; i++) shingles.Add(string.Join(' ', words, i, 5));
        return shingles;
    }

    private PeerAiPdfEntry ReadEntry(string path)
    {
        EnsureOrdinaryPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxEntryBytes) throw new InvalidDataException("Invalid PDF cache size.");
        PeerAiPdfEntry? entry = JsonSerializer.Deserialize<PeerAiPdfEntry>(stream, JsonOptions);
        ValidateEntry(entry);
        return entry!;
    }

    private static void ValidateEntry(PeerAiPdfEntry? entry)
    {
        if (entry is null || entry.Version is not (1 or 2) ||
            !IsHash(entry.AssignmentKey) || !IsHash(entry.ProfileKey) || !IsHash(entry.ContextHash) ||
            !IsHash(entry.FactsHash) || !IsHash(entry.DraftHash) ||
            entry.State is not ("draft" or "ready" or "pending" or "confirmed") ||
            (entry.State != "draft" && !IsHash(entry.PdfHash)) || entry.Draft?.Sections is not { Length: > 0 and <= 40 } ||
            string.IsNullOrWhiteSpace(entry.Draft.Title) || entry.Draft.Sections.Any(s => s is null ||
                s.Paragraphs is not { Length: > 0 and <= 80 } || s.Paragraphs.Any(string.IsNullOrWhiteSpace)) ||
            entry.DraftHash != DraftHash(entry.Draft) || !ValidRevisionMetadata(entry))
            throw new InvalidDataException("PDF cache is corrupt; it will not be overwritten automatically.");
    }

    private static bool ValidRevisionMetadata(PeerAiPdfEntry entry)
    {
        if (entry.Version == 1)
            return entry.RevisionCount == 0 && string.IsNullOrEmpty(entry.ParentPdfName) &&
                string.IsNullOrEmpty(entry.FeedbackHash) && !entry.AttemptStartPending &&
                !entry.AttemptConfirmationPending &&
                (entry.PreviousDraftHashes?.Length ?? 0) == 0 && (entry.RevisionFailureKeys?.Length ?? 0) == 0;
        return entry.RevisionCount is >= 1 and <= MaximumRevisions &&
            IsProfilePdfName(entry.ParentPdfName, entry.ProfileKey) && IsHash(entry.FeedbackHash) &&
            entry.PreviousDraftHashes?.Length == entry.RevisionCount &&
            entry.RevisionFailureKeys?.Length == entry.RevisionCount &&
            entry.PreviousDraftHashes.All(IsHash) && entry.RevisionFailureKeys.All(IsHash) &&
            entry.PreviousDraftHashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == entry.RevisionCount &&
            !entry.PreviousDraftHashes.Contains(entry.DraftHash, StringComparer.OrdinalIgnoreCase) &&
            entry.RevisionFailureKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == entry.RevisionCount &&
            entry.RevisionFailureKeys[^1] == FailureKey(entry.ParentPdfName, entry.FeedbackHash) &&
            string.Equals(entry.ParentPdfName,
                $"assignment-{entry.ProfileKey[..12]}-{entry.PreviousDraftHashes[^1][..16]}.pdf",
                StringComparison.OrdinalIgnoreCase) &&
            (!entry.AttemptStartPending || entry.State != "draft") &&
            (!entry.AttemptConfirmationPending || entry.AttemptStartPending);
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private void Write(PeerAiPdfEntry entry)
    {
        EnsureOrdinaryPath(_entryPath);
        string staging = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, entry, JsonOptions);
                if (stream.Length > MaxEntryBytes) throw new InvalidDataException("PDF cache exceeds its safe size limit.");
                stream.Flush(true);
            }
            File.Move(staging, _entryPath, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static FileStream? TryLock(string path)
    {
        EnsureOrdinaryPath(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void ValidatePdf(string path)
    {
        EnsureOrdinaryPath(path);
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[5];
        if (stream.Length is < 100 or > 20_000_000 || stream.Read(signature) != 5 || !signature.SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("Generated PDF is missing, too large, or invalid.");
        stream.Seek(-Math.Min(1024, stream.Length), SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        if (!reader.ReadToEnd().Contains("%%EOF", StringComparison.Ordinal))
            throw new InvalidDataException("Generated PDF did not finish writing.");
    }

    public static void EnsureOrdinaryPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current != null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing a linked PDF data path.");
            current = Path.GetDirectoryName(current);
        }
    }
}

public sealed record PeerAiPdfEntry(int Version, string AssignmentKey, string ProfileKey,
    string ContextHash, string FactsHash, string DraftHash, string PdfHash, string State, PeerPdfDraft Draft,
    int RevisionCount = 0, string ParentPdfName = "", string FeedbackHash = "",
    string[]? PreviousDraftHashes = null, string[]? RevisionFailureKeys = null, bool AttemptStartPending = false,
    bool AttemptConfirmationPending = false);
