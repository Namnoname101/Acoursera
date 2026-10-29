using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomationPlatform.Presentation.Services;

/// <summary>
/// Account/course-scoped retry queue. Ordinary course scanning skips PendingPaths;
/// only after that scan ends does BeginNextReview claim each pending activity once.
/// Claims survive a crash, and only an explicit new run may call BeginNewPass.
/// </summary>
internal sealed class DeferredQuizQueue
{
    private const int Version = 1;
    private const int MaximumEntries = 2_000;
    private const int MaximumBytes = 4_000_000;
    private readonly string _directory;
    private readonly string _course;
    private readonly string _path;
    private readonly string _lockPath;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    private static readonly Regex CoursePath = new(
        @"^(?:/programs/[A-Za-z0-9_-]{1,200})?/learn/(?<course>[A-Za-z0-9][A-Za-z0-9_-]{0,199})(?:/|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ActivityPath = new(
        @"^(?:/programs/[A-Za-z0-9_-]{1,200})?/learn/(?<course>[A-Za-z0-9][A-Za-z0-9_-]{0,199})/(?<type>quiz|exam|assignment-submission)/(?<item>[A-Za-z0-9_-]{1,200})(?:/[A-Za-z0-9_-]{1,400})?(?:/(?:attempt|view-attempt|view-feedback))?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal DeferredQuizQueue(string accountDirectory, Uri courseOrActivity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDirectory);
        ValidateTrustedUrl(courseOrActivity);
        Match match = CoursePath.Match(courseOrActivity.AbsolutePath);
        if (!match.Success) throw new ArgumentException("Expected a Coursera course URL.", nameof(courseOrActivity));
        _course = match.Groups["course"].Value.ToLowerInvariant();
        _directory = Path.Combine(Path.GetFullPath(accountDirectory), ".acose-deferred-quizzes");
        EnsureOrdinaryPath(_directory);
        Directory.CreateDirectory(_directory);
        EnsureOrdinaryPath(_directory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_course)));
        _path = Path.Combine(_directory, key + ".json");
        _lockPath = Path.Combine(_directory, key + ".lock");
    }

    // Slugs, attempt suffixes, query strings and organization prefixes do not
    // identify another question set. Item IDs retain their case.
    internal static string ActivityKey(Uri activity)
    {
        ValidateTrustedUrl(activity);
        Match match = ActivityPath.Match(activity.AbsolutePath);
        if (!match.Success) throw new ArgumentException("Expected a Coursera quiz or assessment URL.", nameof(activity));
        return "/learn/" + match.Groups["course"].Value.ToLowerInvariant() + "/" +
            match.Groups["type"].Value.ToLowerInvariant() + "/" + match.Groups["item"].Value;
    }

    internal IReadOnlyList<string> PendingPaths => Read(state => state.Entries.Select(entry => entry.Path).ToArray());
    internal int PendingCount => Read(state => state.Entries.Count);
    internal bool HasReviewAttemptedThisPass => Read(state => state.Entries.Any(entry => entry.ReviewAttempted));

    internal bool Contains(Uri activity)
    {
        string path = InCourseKey(activity);
        return Read(state => state.Entries.Any(entry => entry.Path == path));
    }

    internal void Defer(Uri activity, string reason)
    {
        string path = InCourseKey(activity);
        string safeReason = string.IsNullOrWhiteSpace(reason) ? "unresolved" : reason.Trim();
        if (safeReason.Length > 1_000) safeReason = safeReason[..1_000];
        Mutate(state =>
        {
            int index = state.Entries.FindIndex(entry => entry.Path == path);
            if (index >= 0)
            {
                // In particular do NOT clear ReviewAttempted after another failure.
                state.Entries[index] = state.Entries[index] with { Reason = safeReason };
            }
            else
            {
                if (state.Entries.Count >= MaximumEntries) throw new IOException("Deferred assessment queue is full.");
                string navigationPath = activity.AbsolutePath;
                string trimmed = navigationPath.TrimEnd('/');
                const string feedbackSuffix = "/view-feedback";
                if (trimmed.EndsWith(feedbackSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // Store the observed cover route for a later return pass.
                    // Do not mistake an item actually named view-feedback for a suffix.
                    string coverPath = trimmed[..^feedbackSuffix.Length];
                    try
                    {
                        if (ActivityKey(new Uri("https://www.coursera.org" + coverPath)) == path)
                            navigationPath = coverPath;
                    }
                    catch (ArgumentException) { }
                }
                state.Entries.Add(new Entry(path, navigationPath, safeReason, false));
            }
        });
    }

    internal void Resolve(Uri activity)
    {
        string path = InCourseKey(activity);
        Mutate(state => state.Entries.RemoveAll(entry => entry.Path == path));
    }

    internal Uri? BeginNextReview()
    {
        Uri? result = null;
        Mutate(state =>
        {
            int index = state.Entries.FindIndex(entry => !entry.ReviewAttempted);
            if (index < 0) return;
            Entry entry = state.Entries[index];
            // Persist the claim before navigation so a crash or second worker
            // cannot repeatedly pick the first failed assessment.
            state.Entries[index] = entry with { ReviewAttempted = true };
            result = new Uri("https://www.coursera.org" + entry.NavigationPath);
        });
        return result;
    }

    internal void BeginNewPass() => Mutate(state =>
    {
        for (int index = 0; index < state.Entries.Count; index++)
            state.Entries[index] = state.Entries[index] with { ReviewAttempted = false };
    });

    private string InCourseKey(Uri activity)
    {
        string key = ActivityKey(activity);
        if (!key.StartsWith("/learn/" + _course + "/", StringComparison.Ordinal))
            throw new ArgumentException("The assessment belongs to a different course.", nameof(activity));
        return key;
    }

    private T Read<T>(Func<State, T> read)
    {
        using FileStream lease = AcquireLease();
        return read(Load());
    }

    private void Mutate(Action<State> change)
    {
        using FileStream lease = AcquireLease();
        State state = Load();
        change(state);
        Save(state);
    }

    private FileStream AcquireLease()
    {
        EnsureOrdinaryPath(_lockPath);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Never delete this file: another process might already hold the
                // same inode and deletion would break the mutual exclusion.
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && attempt < 40)
            {
                Thread.Sleep(25);
            }
        }
    }

    private State Load()
    {
        EnsureOrdinaryPath(_path);
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Deferred assessment queue has an invalid size.");
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (!HasProperties(root, "version", "course", "entries"))
                throw new InvalidDataException("Deferred assessment queue has an invalid schema.");
            if (root.GetProperty("entries").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Deferred assessment queue entries are invalid.");
            foreach (JsonElement item in root.GetProperty("entries").EnumerateArray())
                if (!HasProperties(item, "path", "navigationPath", "reason", "reviewAttempted"))
                    throw new InvalidDataException("Deferred assessment queue entry has an invalid schema.");
            State? state = root.Deserialize<State>(JsonOptions);
            if (state is null || state.Version != Version || state.Course != _course ||
                state.Entries is null || state.Entries.Count > MaximumEntries)
                throw new InvalidDataException("Deferred assessment queue belongs to another course or version.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in state.Entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || entry.Path.Length > 650 ||
                    string.IsNullOrWhiteSpace(entry.NavigationPath) || entry.NavigationPath.Length > 1_400 ||
                    string.IsNullOrWhiteSpace(entry.Reason) || entry.Reason.Length > 1_000 || !seen.Add(entry.Path))
                    throw new InvalidDataException("Deferred assessment queue contains an invalid or duplicate entry.");
                try
                {
                    if (!Uri.TryCreate("https://www.coursera.org" + entry.Path, UriKind.Absolute, out Uri? itemUri) ||
                        InCourseKey(itemUri) != entry.Path)
                        throw new InvalidDataException("Deferred assessment queue path is not canonical.");
                    if (!Uri.TryCreate("https://www.coursera.org" + entry.NavigationPath, UriKind.Absolute, out Uri? navigationUri) ||
                        navigationUri.AbsolutePath != entry.NavigationPath ||
                        InCourseKey(navigationUri) != entry.Path || navigationUri.Query.Length != 0 || navigationUri.Fragment.Length != 0)
                        throw new InvalidDataException("Deferred assessment queue navigation path is not the same assessment.");
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("Deferred assessment queue contains a foreign or invalid path.", exception);
                }
            }
            return state;
        }
        catch (FileNotFoundException)
        {
            return new State(Version, _course, []);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Deferred assessment queue is malformed.", exception);
        }
    }

    private void Save(State state)
    {
        string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        EnsureOrdinaryPath(temporary);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            EnsureOrdinaryPath(_path);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { EnsureOrdinaryPath(temporary); File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool HasProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        string[] actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == names.Length && actual.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            actual.All(name => names.Contains(name, StringComparer.Ordinal));
    }

    private static void ValidateTrustedUrl(Uri activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!activity.IsAbsoluteUri || activity.Scheme != Uri.UriSchemeHttps || !activity.IsDefaultPort ||
            !string.IsNullOrEmpty(activity.UserInfo) ||
            !(activity.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
              activity.Host.Equals("www.coursera.org", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Expected a trusted Coursera URL.", nameof(activity));
    }

    private static void EnsureOrdinaryPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Deferred assessment storage cannot use a linked or redirected path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed record Entry(string Path, string NavigationPath, string Reason, bool ReviewAttempted);
    private sealed record State(int Version, string Course, List<Entry> Entries);
}
