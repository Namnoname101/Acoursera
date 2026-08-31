using System.IO;

namespace AutomationPlatform.Presentation.Services;

public sealed class WorkerLaunchOptions
{
    public bool Enabled { get; private init; }
    public bool IsCourseJob => Enabled && !string.IsNullOrWhiteSpace(JobId);
    public bool IsDirectLogin => Enabled && !string.IsNullOrWhiteSpace(DirectLoginAttemptId);
    public bool IsInteractiveProfile =>
        IsCourseJob && string.Equals(JobModeHint, "browse", StringComparison.OrdinalIgnoreCase);
    public bool IsCourseAutomation => IsCourseJob && !IsInteractiveProfile;
    public Uri? ServerUrl { get; private init; }
    public string WorkerId { get; private init; } = string.Empty;
    public string JobId { get; private init; } = string.Empty;
    public string DeviceId { get; private init; } = string.Empty;
    public string DirectLoginAttemptId { get; private init; } = string.Empty;
    public string JobModeHint { get; private init; } = string.Empty;
    public string ProfilePath { get; private init; } = string.Empty;
    internal string WorkerKey { get; private init; } = string.Empty;

    public static WorkerLaunchOptions FromArgs(IEnumerable<string> arguments)
    {
        string[] args = arguments.ToArray();
        if (!args.Contains("--worker-mode", StringComparer.OrdinalIgnoreCase))
        {
            return new WorkerLaunchOptions();
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--worker-mode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                values[args[index]] = args[index + 1];
                index++;
            }
        }

        string Required(string name)
        {
            if (!values.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Worker launch argument {name} is required.");
            }
            return value.Trim();
        }

        string serverText = Required("--server-url");
        if (!Uri.TryCreate(serverText, UriKind.Absolute, out Uri? serverUrl) ||
            (serverUrl.Scheme != Uri.UriSchemeHttps && serverUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Worker server URL must use HTTP or HTTPS.");
        }

        string jobId = values.TryGetValue("--job-id", out string? configuredJobId)
            ? configuredJobId.Trim()
            : string.Empty;
        string directLoginAttemptId = values.TryGetValue(
            "--direct-login-attempt-id",
            out string? configuredAttemptId)
            ? configuredAttemptId.Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(jobId) == string.IsNullOrWhiteSpace(directLoginAttemptId))
        {
            throw new InvalidOperationException(
                "Worker launch requires exactly one of --job-id or --direct-login-attempt-id.");
        }

        bool isDirectLogin = !string.IsNullOrWhiteSpace(directLoginAttemptId);
        string jobModeHint = values.TryGetValue("--job-mode", out string? configuredJobMode)
            ? configuredJobMode.Trim().ToLowerInvariant()
            : (isDirectLogin ? string.Empty : "course");
        if (!isDirectLogin && jobModeHint is not ("course" or "browse"))
        {
            throw new InvalidOperationException("Worker job mode must be course or browse.");
        }
        if (isDirectLogin && serverUrl.Scheme != Uri.UriSchemeHttps && !serverUrl.IsLoopback)
        {
            throw new InvalidOperationException(
                "Direct login credentials may only be consumed over HTTPS (HTTP is allowed only on loopback)." );
        }

        string workerKey = Environment.GetEnvironmentVariable("ACOSE_WORKER_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workerKey))
        {
            throw new InvalidOperationException("ACOSE_WORKER_KEY is required in worker mode.");
        }

        string deviceId = isDirectLogin ? string.Empty : Required("--device-id");
        string profileKey = isDirectLogin ? directLoginAttemptId : deviceId;
        string profilePath = values.TryGetValue("--profile-path", out string? configuredProfile)
            ? configuredProfile
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Acose", isDirectLogin ? "DirectLoginProfiles" : "WorkerProfiles",
                SanitizePathSegment(profileKey));

        return new WorkerLaunchOptions
        {
            Enabled = true,
            ServerUrl = serverUrl,
            WorkerId = Required("--worker-id"),
            JobId = jobId,
            DeviceId = deviceId,
            DirectLoginAttemptId = directLoginAttemptId,
            JobModeHint = jobModeHint,
            ProfilePath = Path.GetFullPath(profilePath),
            WorkerKey = workerKey,
        };
    }

    private static string SanitizePathSegment(string value) =>
        string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    public override string ToString()
    {
        if (!Enabled) return "Interactive mode";
        return IsDirectLogin
            ? $"Worker {WorkerId} / direct login {DirectLoginAttemptId}"
            : $"Worker {WorkerId} / {JobModeHint} job {JobId} / device {DeviceId}";
    }
}
