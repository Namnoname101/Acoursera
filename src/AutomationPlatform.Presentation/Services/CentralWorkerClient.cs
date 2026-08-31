using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutomationPlatform.Presentation.Services;

public sealed class CentralWorkerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkerLaunchOptions _options;
    private readonly HttpClient _httpClient;

    public WorkerJob? CurrentJob { get; private set; }
    public DirectLoginAttempt? CurrentDirectLoginAttempt { get; private set; }

    public CentralWorkerClient(WorkerLaunchOptions options)
    {
        _options = options;
        _httpClient = new HttpClient
        {
            BaseAddress = options.ServerUrl,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<WorkerJob> ClaimAsync(CancellationToken cancellationToken = default)
    {
        EnsureCourseJob();
        ApiEnvelope<WorkerJob> response = await SendAsync<WorkerJob>(
            HttpMethod.Post,
            $"api/worker/jobs/{Uri.EscapeDataString(_options.JobId)}/claim",
            new { },
            cancellationToken);
        CurrentJob = response.Data ?? throw new InvalidOperationException("Worker claim returned no job.");
        if (!string.Equals(CurrentJob.DeviceId, _options.DeviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claimed job does not belong to the requested device.");
        }
        return CurrentJob;
    }

    public async Task<WorkerJob?> ClaimNextBatchJobAsync(
        CancellationToken cancellationToken = default)
    {
        WorkerJob currentJob = RequireCurrentJob();
        if (string.IsNullOrWhiteSpace(currentJob.BatchId)) return null;
        ApiEnvelope<WorkerJob> response = await SendAsync<WorkerJob>(
            HttpMethod.Post,
            $"api/worker/jobs/batches/{Uri.EscapeDataString(currentJob.BatchId)}/next",
            new { deviceId = currentJob.DeviceId },
            cancellationToken);
        if (response.Data == null) return null;
        if (!string.Equals(response.Data.DeviceId, currentJob.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(response.Data.BatchId, currentJob.BatchId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Batch continuation returned a job outside the active account batch.");
        }
        CurrentJob = response.Data;
        return CurrentJob;
    }

    public async Task<DirectLoginAttempt> ClaimDirectLoginAttemptAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDirectLogin();
        ApiEnvelope<DirectLoginAttempt> response = await SendWithTransientRetryAsync<DirectLoginAttempt>(
            HttpMethod.Post,
            $"api/worker/direct-login-attempts/{Uri.EscapeDataString(_options.DirectLoginAttemptId)}/claim",
            new { },
            cancellationToken,
            maxAttempts: 4);
        CurrentDirectLoginAttempt = response.Data
            ?? throw new InvalidOperationException("Direct login claim returned no attempt.");
        if (!string.Equals(
                CurrentDirectLoginAttempt.Id,
                _options.DirectLoginAttemptId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claimed direct login attempt does not match the requested attempt.");
        }
        return CurrentDirectLoginAttempt;
    }

    public async Task<DirectLoginCredentials> ConsumeDirectLoginCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        DirectLoginAttempt attempt = RequireCurrentDirectLoginAttempt();
        ApiEnvelope<DirectLoginCredentials> response = await SendWithTransientRetryAsync<DirectLoginCredentials>(
            HttpMethod.Post,
            $"api/worker/direct-login-attempts/{Uri.EscapeDataString(attempt.Id)}/credentials/consume",
            new { },
            cancellationToken,
            maxAttempts: 4);
        DirectLoginCredentials credentials = response.Data
            ?? throw new InvalidOperationException("Direct login credential response contained no data.");
        if (string.IsNullOrWhiteSpace(credentials.LeaseId) ||
            string.IsNullOrWhiteSpace(credentials.GoogleEmail) ||
            string.IsNullOrEmpty(credentials.GooglePassword))
        {
            credentials.Clear();
            throw new InvalidOperationException("Direct login credentials were incomplete.");
        }
        return credentials;
    }

    public async Task<bool> AcknowledgeDirectLoginCredentialsAsync(
        string leaseId,
        CancellationToken cancellationToken = default)
    {
        DirectLoginAttempt attempt = RequireCurrentDirectLoginAttempt();
        if (string.IsNullOrWhiteSpace(leaseId))
        {
            throw new InvalidOperationException("Direct login credential lease id is required.");
        }
        try
        {
            await SendWithTransientRetryAsync<JsonElement>(
                HttpMethod.Post,
                $"api/worker/direct-login-attempts/{Uri.EscapeDataString(attempt.Id)}/credentials/ack",
                new { leaseId },
                cancellationToken,
                maxAttempts: 4);
            return true;
        }
        catch (Exception exception) when (IsTransientFailure(exception, cancellationToken))
        {
            // Password submission must keep progressing. The backend retains the worker-bound
            // lease until ACK/terminal cleanup, so a short network outage is safe to retry later.
            return false;
        }
    }

    public async Task<DirectLoginAttempt> GetDirectLoginAttemptAsync(
        CancellationToken cancellationToken = default)
    {
        DirectLoginAttempt attempt = RequireCurrentDirectLoginAttempt();
        ApiEnvelope<DirectLoginAttempt> response = await GetWithTransientRetryAsync<DirectLoginAttempt>(
            $"api/worker/direct-login-attempts/{Uri.EscapeDataString(attempt.Id)}",
            cancellationToken,
            maxAttempts: 3);
        CurrentDirectLoginAttempt = response.Data
            ?? throw new InvalidOperationException("Direct login status returned no attempt.");
        return CurrentDirectLoginAttempt;
    }

    public async Task<bool> ReportDirectLoginStatusAsync(
        string status,
        string activity,
        string? challengeNumber = null,
        string? manualActionReason = null,
        string? errorCode = null,
        string? errorMessageSafe = null,
        CancellationToken cancellationToken = default)
    {
        DirectLoginAttempt attempt = RequireCurrentDirectLoginAttempt();
        ApiEnvelope<DirectLoginAttempt> response;
        try
        {
            response = await SendWithTransientRetryAsync<DirectLoginAttempt>(
                HttpMethod.Patch,
                $"api/worker/direct-login-attempts/{Uri.EscapeDataString(attempt.Id)}/status",
                new
                {
                    status,
                    activity,
                    challengeNumber,
                    manualActionReason,
                    errorCode,
                    errorMessageSafe,
                },
                cancellationToken,
                maxAttempts: 4);
        }
        catch (Exception exception) when (IsTransientFailure(exception, cancellationToken))
        {
            return false;
        }
        if (response.Data != null)
        {
            CurrentDirectLoginAttempt = response.Data;
        }
        else
        {
            attempt.Status = status;
            attempt.Activity = activity;
            attempt.ChallengeNumber = challengeNumber;
        }
        return true;
    }

    public async Task<DirectLoginAttempt> CompleteDirectLoginAsync(
        IReadOnlyCollection<VaultCookie> cookies,
        string? courseraUserId,
        string? courseraUserName,
        CancellationToken cancellationToken = default)
    {
        DirectLoginAttempt attempt = RequireCurrentDirectLoginAttempt();
        ApiEnvelope<DirectLoginAttempt> response = await SendWithTransientRetryAsync<DirectLoginAttempt>(
            HttpMethod.Post,
            $"api/worker/direct-login-attempts/{Uri.EscapeDataString(attempt.Id)}/complete",
            new { cookies, courseraUserId, courseraUserName },
            cancellationToken,
            maxAttempts: 5,
            attemptTimeout: TimeSpan.FromSeconds(10));
        CurrentDirectLoginAttempt = response.Data
            ?? throw new InvalidOperationException("Direct login completion returned no attempt.");
        return CurrentDirectLoginAttempt;
    }

    public async Task<SessionLease> LeaseSessionAsync(CancellationToken cancellationToken = default)
    {
        WorkerJob job = RequireCurrentJob();
        ApiEnvelope<SessionLease> response = await SendAsync<SessionLease>(
            HttpMethod.Post,
            $"api/worker/jobs/{Uri.EscapeDataString(job.Id)}/session/lease",
            new { },
            cancellationToken);
        return response.Data ?? throw new InvalidOperationException("Session lease returned no data.");
    }

    public async Task<WorkerJob> HeartbeatAsync(
        string status,
        string currentActivity,
        int? progress = null,
        int? currentModule = null,
        int? totalModules = null,
        CancellationToken cancellationToken = default)
    {
        WorkerJob job = RequireCurrentJob();
        ApiEnvelope<WorkerJob> response = await SendAsync<WorkerJob>(
            HttpMethod.Post,
            $"api/worker/jobs/{Uri.EscapeDataString(job.Id)}/heartbeat",
            new
            {
                status,
                currentActivity,
                progress,
                currentModule,
                totalModules,
                agentVersion = typeof(CentralWorkerClient).Assembly.GetName().Version?.ToString() ?? "3.3.2",
            },
            cancellationToken);
        CurrentJob = response.Data
            ?? throw new InvalidOperationException("Worker heartbeat returned no job state.");
        return CurrentJob;
    }

    public async Task<WorkerJob> PauseAsync(
        string currentActivity,
        string manualActionReason,
        string errorCode,
        int? progress = null,
        int? currentModule = null,
        int? totalModules = null,
        CancellationToken cancellationToken = default)
    {
        WorkerJob job = RequireCurrentJob();
        string safeActivity = string.IsNullOrWhiteSpace(currentActivity)
            ? "Worker paused for manual action."
            : currentActivity[..Math.Min(currentActivity.Length, 500)];
        string safeReason = string.IsNullOrWhiteSpace(manualActionReason)
            ? "Cần thao tác thủ công trên profile Coursera."
            : manualActionReason[..Math.Min(manualActionReason.Length, 500)];
        ApiEnvelope<WorkerJob> response = await SendAsync<WorkerJob>(
            HttpMethod.Post,
            $"api/worker/jobs/{Uri.EscapeDataString(job.Id)}/heartbeat",
            new
            {
                status = "waiting_user",
                currentActivity = safeActivity,
                manualActionReason = safeReason,
                errorCode,
                errorMessageSafe = safeReason,
                progress,
                currentModule,
                totalModules,
                agentVersion = typeof(CentralWorkerClient).Assembly.GetName().Version?.ToString() ?? "3.3.2",
            },
            cancellationToken);
        CurrentJob = response.Data
            ?? throw new InvalidOperationException("Worker pause acknowledgement returned no job state.");
        return CurrentJob;
    }

    public Task ReportIdentityAsync(
        string userId,
        string userName,
        CancellationToken cancellationToken = default)
    {
        WorkerJob job = RequireCurrentJob();
        return SendWithoutResultAsync(
            HttpMethod.Patch,
            $"api/worker/jobs/{Uri.EscapeDataString(job.Id)}/identity",
            new { courseraUserId = userId, courseraUserName = userName },
            cancellationToken);
    }

    public Task CloseInteractiveSessionAsync(CancellationToken cancellationToken = default)
    {
        WorkerJob job = RequireCurrentJob();
        return SendWithoutResultAsync(
            HttpMethod.Post,
            $"api/worker/jobs/{Uri.EscapeDataString(job.Id)}/close",
            new { },
            cancellationToken);
    }

    public Task FailAsync(string message, CancellationToken cancellationToken = default)
    {
        WorkerJob job = RequireCurrentJob();
        string safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Worker stopped because of an unrecoverable error."
            : message[..Math.Min(message.Length, 500)];
        return SendWithoutResultAsync(
            HttpMethod.Post,
            $"api/worker/jobs/{Uri.EscapeDataString(job.Id)}/heartbeat",
            new
            {
                status = "failed",
                currentActivity = safeMessage,
                errorCode = "WORKER_AI_OR_AUTOMATION_FAILED",
                errorMessageSafe = safeMessage,
                agentVersion = typeof(CentralWorkerClient).Assembly.GetName().Version?.ToString() ?? "3.3.2",
            },
            cancellationToken);
    }

    private async Task SendWithoutResultAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        await SendAsync<JsonElement>(method, path, body, cancellationToken);
    }

    private async Task<ApiEnvelope<T>> SendWithTransientRetryAsync<T>(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken,
        int maxAttempts,
        TimeSpan? attemptTimeout = null)
    {
        TimeSpan timeout = attemptTimeout ?? TimeSpan.FromSeconds(8);
        for (int attempt = 1; ; attempt++)
        {
            using var attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptLifetime.CancelAfter(timeout);
            try
            {
                return await SendAsync<T>(method, path, body, attemptLifetime.Token);
            }
            catch (Exception exception) when (
                attempt < maxAttempts && IsTransientFailure(exception, cancellationToken))
            {
                await Task.Delay(TransientRetryDelay(attempt), cancellationToken);
            }
        }
    }

    private async Task<ApiEnvelope<T>> GetWithTransientRetryAsync<T>(
        string path,
        CancellationToken cancellationToken,
        int maxAttempts)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptLifetime.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                return await GetAsync<T>(path, attemptLifetime.Token);
            }
            catch (Exception exception) when (
                attempt < maxAttempts && IsTransientFailure(exception, cancellationToken))
            {
                await Task.Delay(TransientRetryDelay(attempt), cancellationToken);
            }
        }
    }

    private static TimeSpan TransientRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(2000, 250 * (1 << Math.Min(attempt - 1, 3))));

    private static bool IsTransientFailure(
        Exception exception,
        CancellationToken callerCancellationToken)
    {
        if (callerCancellationToken.IsCancellationRequested)
        {
            return false;
        }
        return exception is HttpRequestException or TaskCanceledException ||
               exception is CentralWorkerHttpException httpException &&
               (httpException.StatusCode == HttpStatusCode.RequestTimeout ||
                (int)httpException.StatusCode == 429 ||
                (int)httpException.StatusCode >= 500);
    }

    private async Task<ApiEnvelope<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("x-worker-key", _options.WorkerKey);
        request.Headers.TryAddWithoutValidation("x-worker-id", _options.WorkerId);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string safeMessage = "Central worker request failed.";
            try
            {
                ApiErrorEnvelope? error = JsonSerializer.Deserialize<ApiErrorEnvelope>(json, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Error?.Message)) safeMessage = error.Error.Message;
            }
            catch { }
            throw new CentralWorkerHttpException(response.StatusCode, safeMessage);
        }
        return JsonSerializer.Deserialize<ApiEnvelope<T>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Central worker returned invalid JSON.");
    }

    private async Task<ApiEnvelope<T>> GetAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("x-worker-key", _options.WorkerKey);
        request.Headers.TryAddWithoutValidation("x-worker-id", _options.WorkerId);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string safeMessage = "Central worker request failed.";
            try
            {
                ApiErrorEnvelope? error = JsonSerializer.Deserialize<ApiErrorEnvelope>(json, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Error?.Message)) safeMessage = error.Error.Message;
            }
            catch { }
            throw new CentralWorkerHttpException(response.StatusCode, safeMessage);
        }
        return JsonSerializer.Deserialize<ApiEnvelope<T>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Central worker returned invalid JSON.");
    }

    private WorkerJob RequireCurrentJob() =>
        CurrentJob ?? throw new InvalidOperationException("Worker has not claimed a job.");

    private DirectLoginAttempt RequireCurrentDirectLoginAttempt() =>
        CurrentDirectLoginAttempt
        ?? throw new InvalidOperationException("Worker has not claimed a direct login attempt.");

    private void EnsureEnabled()
    {
        if (!_options.Enabled) throw new InvalidOperationException("Application is not running in worker mode.");
    }

    private void EnsureCourseJob()
    {
        EnsureEnabled();
        if (!_options.IsCourseJob)
        {
            throw new InvalidOperationException("Application is not running a course job.");
        }
    }

    private void EnsureDirectLogin()
    {
        EnsureEnabled();
        if (!_options.IsDirectLogin)
        {
            throw new InvalidOperationException("Application is not running a direct login attempt.");
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class CentralWorkerHttpException : InvalidOperationException
    {
        public CentralWorkerHttpException(HttpStatusCode statusCode, string safeMessage)
            : base($"{safeMessage} ({(int)statusCode})")
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}

public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

public sealed class ApiErrorEnvelope
{
    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }
}

public sealed class ApiError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class WorkerJob
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("courseraUserName")]
    public string? CourseraUserName { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "course";

    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; set; } = "https://www.coursera.org/";

    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    [JsonPropertyName("skipGradedAppItems")]
    public bool SkipGradedAppItems { get; set; } = true;

    [JsonPropertyName("skipPracticeAppItems")]
    public bool SkipPracticeAppItems { get; set; } = true;

    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    [JsonPropertyName("currentModule")]
    public int? CurrentModule { get; set; }

    [JsonPropertyName("totalModules")]
    public int? TotalModules { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("pauseRequested")]
    public bool PauseRequested { get; set; }

    [JsonPropertyName("pauseRequestedReason")]
    public string? PauseRequestedReason { get; set; }
}

public sealed class SessionLease
{
    [JsonPropertyName("cookies")]
    public List<VaultCookie> Cookies { get; set; } = [];

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class DirectLoginAttempt
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("activity")]
    public string Activity { get; set; } = string.Empty;

    [JsonPropertyName("challengeNumber")]
    public string? ChallengeNumber { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("course")]
    public DirectLoginCourse? Course { get; set; }
}

public sealed class DirectLoginCourse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("canonicalUrl")]
    public string CanonicalUrl { get; set; } = string.Empty;
}

public sealed class DirectLoginCredentials : IDisposable
{
    [JsonPropertyName("leaseId")]
    public string LeaseId { get; set; } = string.Empty;

    [JsonPropertyName("googleEmail")]
    public string GoogleEmail { get; set; } = string.Empty;

    [JsonPropertyName("googlePassword")]
    public string GooglePassword { get; set; } = string.Empty;

    public void Clear()
    {
        LeaseId = string.Empty;
        GoogleEmail = string.Empty;
        GooglePassword = string.Empty;
    }

    public void Dispose() => Clear();
}

public sealed class VaultCookie
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("secure")]
    public bool Secure { get; set; }

    [JsonPropertyName("httpOnly")]
    public bool HttpOnly { get; set; }

    [JsonPropertyName("sameSite")]
    public string? SameSite { get; set; }

    [JsonPropertyName("expires")]
    public double? Expires { get; set; }
}
