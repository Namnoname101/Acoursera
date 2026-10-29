using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomationPlatform.Presentation.Services;

internal enum DiscussionSubmissionState
{
    None,
    Pending,
    Confirmed,
}

/// <summary>
/// Records a submission intent before the browser clicks Reply. A pending entry
/// survives restarts: an uncertain result must never become permission to repost.
/// No response text, account name, URL, cookie, or credential is stored.
/// </summary>
internal sealed class DiscussionSubmissionJournal
{
    private const int CurrentVersion = 1;
    private const int MaximumEntryBytes = 1024;
    private readonly string _directory;
    private static readonly Regex DiscussionPath = new(
        @"^(?:/programs/[^/]+)?/learn/(?<course>[^/]+)/discussionprompt/(?<item>[^/]+)(?:/[^/]+)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
    };

    internal DiscussionSubmissionJournal(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        _directory = Path.Combine(Path.GetFullPath(profileDirectory), ".acose-discussion-journal");
        // Do not hide access failures. The caller must keep the page open and
        // refrain from submitting if it cannot establish durable ownership.
        Directory.CreateDirectory(_directory);
    }

    internal DiscussionSubmissionState GetState(Uri discussion)
    {
        string key = GetKey(discussion);
        string path = GetEntryPath(key);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > MaximumEntryBytes)
                {
                    throw new InvalidDataException("Discussion submission journal entry has an invalid size.");
                }

                Entry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<Entry>(stream, JsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("Discussion submission journal entry is malformed.", exception);
                }

                if (entry?.Version != CurrentVersion ||
                    !string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Discussion submission journal entry is not valid for this item.");
                }

                return entry.State switch
                {
                    "pending" => DiscussionSubmissionState.Pending,
                    "confirmed" => DiscussionSubmissionState.Confirmed,
                    _ => throw new InvalidDataException("Discussion submission journal entry has an invalid state."),
                };
            }
            catch (FileNotFoundException)
            {
                return DiscussionSubmissionState.None;
            }
            catch (IOException exception) when (IsSharingViolation(exception) && attempt < 20)
            {
                // A different worker may still be flushing its CreateNew entry.
                // Wait briefly for the exclusive writer, never read partial JSON.
                Thread.Sleep(25);
            }
        }
    }

    internal bool IsPending(Uri discussion) =>
        GetState(discussion) == DiscussionSubmissionState.Pending;

    internal bool TryBegin(Uri discussion)
    {
        string key = GetKey(discussion);
        try
        {
            using var stream = new FileStream(
                GetEntryPath(key), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            WriteEntry(stream, key, "pending");
            return true;
        }
        catch (IOException exception) when (IsAlreadyExists(exception))
        {
            // Validate the existing entry so corruption is an explicit error,
            // not mistaken for either a new item or a successful submission.
            _ = GetState(discussion);
            return false;
        }
    }

    internal void MarkConfirmed(Uri discussion)
    {
        string key = GetKey(discussion);
        if (GetState(discussion) == DiscussionSubmissionState.Confirmed)
        {
            return;
        }

        string temporaryPath = Path.Combine(_directory, $"{key}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                WriteEntry(stream, key, "confirmed");
            }

            // Replacement is atomic within this directory. Readers observe a
            // complete pending entry or a complete confirmed entry.
            File.Move(temporaryPath, GetEntryPath(key), overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string GetEntryPath(string key) => Path.Combine(_directory, key + ".json");

    private static string GetKey(Uri discussion)
    {
        ArgumentNullException.ThrowIfNull(discussion);
        if (!discussion.IsAbsoluteUri ||
            discussion.Scheme != Uri.UriSchemeHttps || !discussion.IsDefaultPort ||
            !string.IsNullOrEmpty(discussion.UserInfo) ||
            !(discussion.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
              discussion.Host.Equals("www.coursera.org", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Expected a trusted Coursera discussion URL.", nameof(discussion));
        }

        Match match = DiscussionPath.Match(discussion.AbsolutePath);
        if (!match.Success)
        {
            throw new ArgumentException("Expected a Coursera discussion item path.", nameof(discussion));
        }

        // Organization prefixes and display slugs do not change the item.
        // Preserve item-ID case because Coursera identifiers are case-sensitive.
        string identity = match.Groups["course"].Value.ToLowerInvariant() +
            "/discussionprompt/" + match.Groups["item"].Value;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static void WriteEntry(FileStream stream, string key, string state)
    {
        JsonSerializer.Serialize(stream, new Entry(CurrentVersion, key, state), JsonOptions);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsAlreadyExists(IOException exception) =>
        (exception.HResult & 0xffff) is 80 or 183;

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) is 32 or 33;

    private sealed record Entry(int Version, string Key, string State);
}
