using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acose.CustomerManager.Runtime;

public sealed record WorkerJobRequest(
    string JobId, string CustomerId, string Name, string ProfilePath, string Url, string Mode,
    int Progress = 0, int? CurrentModule = null, int? TotalModules = null,
    bool SkipPdf = false, bool SkipReview = false);

public sealed record WorkerReport(
    string JobId, string Status, int? Progress = null, int? CurrentModule = null,
    int? TotalModules = null, string? Activity = null, string? ManualActionReason = null,
    string? IdentityName = null);

/// <summary>Local implementation of the existing worker protocol; never forwards sessions to a server.</summary>
public sealed class WorkerBridge : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly byte[] _secretBytes = RandomNumberGenerator.GetBytes(32);
    private WebApplication? _application;
    private Uri? _baseUri;
    private bool _disposed;

    public event Action<WorkerReport>? Reported;
    public string Secret => Convert.ToHexString(_secretBytes);
    public Uri BaseUri => _baseUri ?? throw new InvalidOperationException("Bộ điều khiển chưa khởi động.");

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is not null) return;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(WorkerBridge).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 16 * 1024;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Listen(IPAddress.Loopback, 0);
            });
            WebApplication application = builder.Build();
            application.MapMethods("/api/worker/jobs/{jobId}/{operation}", ["POST", "PATCH"],
                (Func<HttpContext, Task<IResult>>)HandleJobAsync);
            application.MapPost("/api/worker/jobs/{jobId}/session/lease",
                (Func<HttpContext, Task<IResult>>)HandleLeaseAsync);
            try
            {
                await application.StartAsync();
                var addresses = application.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses;
                string address = addresses?.Single()
                    ?? throw new InvalidOperationException("Không xác định được địa chỉ điều khiển trên máy.");
                _baseUri = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
                _application = application;
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Register(WorkerJobRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRequest(request);
        if (!_jobs.TryAdd(request.JobId, new JobState(request)))
            throw new InvalidOperationException("Khóa học này đã được đăng ký chạy.");
    }

    public void Forget(string jobId) => _jobs.TryRemove(jobId, out _);

    public void RequestPause(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out JobState? job)) return;
        lock (job.Sync)
        {
            job.PauseRequested = true;
        }
    }

    internal static string WorkerIdFor(string jobId) =>
        "manager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId)))[..24].ToLowerInvariant();

    internal static void ValidateRequest(WorkerJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.JobId) || request.JobId.Length > 160 ||
            request.JobId.Any(char.IsControl) || string.IsNullOrWhiteSpace(request.CustomerId))
            throw new ArgumentException("Thiếu mã đơn hoặc mã khóa học.");
        if (request.Mode is not ("course" or "browse"))
            throw new ArgumentException("Chế độ mở khóa học không hợp lệ.");
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".coursera.org", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Link phải là địa chỉ HTTPS của Coursera.");
        if (string.IsNullOrWhiteSpace(request.ProfilePath) || !Path.IsPathFullyQualified(request.ProfilePath))
            throw new ArgumentException("Thư mục phiên đăng nhập phải là đường dẫn đầy đủ.");
        if (request.Progress is < 0 or > 100 || request.CurrentModule is < 0 || request.TotalModules is < 0)
            throw new ArgumentException("Thông tin tiến độ không hợp lệ.");
    }

    private async Task<IResult> HandleJobAsync(HttpContext context)
    {
        if (!TryAuthorize(context, out JobState? job, out IResult? error)) return error!;
        string operation = context.Request.RouteValues["operation"]?.ToString() ?? "";
        if ((operation == "identity" && context.Request.Method != "PATCH") ||
            (operation != "identity" && context.Request.Method != "POST"))
            return Failure(405, "Unsupported worker request method.");
        if (operation is not ("claim" or "heartbeat" or "identity" or "close"))
            return Failure(404, "Unknown worker operation.");
        try
        {
            using JsonDocument payload = await ReadPayloadAsync(context);
            if (operation == "claim")
            {
                lock (job!.Sync)
                {
                    if (job.Claimed) return Failure(409, "The local job has already been claimed.");
                    job.Claimed = true;
                    job.Status = "running";
                    return Envelope(Snapshot(job));
                }
            }

            WorkerReport report;
            object response;
            lock (job!.Sync)
            {
                if (!job.Claimed) return Failure(409, "The local job has not been claimed.");
                if (operation == "heartbeat")
                {
                    string status = ReadText(payload.RootElement, "status", 40) ?? "";
                    if (status is not ("running" or "completed" or "failed" or "waiting_user"))
                        return Failure(400, "Unknown worker status.");
                    string? activity = ReadText(payload.RootElement, "currentActivity", 500);
                    string? reason = ReadText(payload.RootElement, "manualActionReason", 500);
                    int? progress = ReadNumber(payload.RootElement, "progress", 0, 100);
                    int? currentModule = ReadNumber(payload.RootElement, "currentModule", 0, int.MaxValue);
                    int? totalModules = ReadNumber(payload.RootElement, "totalModules", 0, int.MaxValue);
                    // A stale heartbeat must not undo a terminal result already acknowledged.
                    if (job.Status is not ("completed" or "failed" or "waiting_user" or "closed" or "needs_login"))
                    {
                        job.Status = job.NeedsLogin && status == "failed" ? "needs_login" : status;
                        job.Activity = job.NeedsLogin && status == "failed" ? LoginRequiredMessage : activity;
                        job.ManualActionReason = reason;
                        if (progress.HasValue) job.Progress = Math.Max(job.Progress, progress.Value);
                        if (currentModule.HasValue) job.CurrentModule = currentModule;
                        if (totalModules.HasValue) job.TotalModules = totalModules;
                    }
                    report = Report(job);
                }
                else if (operation == "identity")
                {
                    string? identityName = ReadText(payload.RootElement, "courseraUserName", 300);
                    // Identity is reported as evidence; the manager decides whether to apply it.
                    job.NeedsLogin = false;
                    report = Report(job) with { IdentityName = identityName };
                }
                else
                {
                    if (job.Request.Mode != "browse") return Failure(409, "Only a manual profile can be closed here.");
                    job.Status = "closed";
                    job.Activity = "Đã đóng cửa sổ kiểm tra.";
                    report = Report(job);
                }
                response = Snapshot(job);
            }
            Publish(report);
            return Envelope(response);
        }
        catch (JsonException)
        {
            return Failure(400, "Invalid worker JSON.");
        }
        catch (BadHttpRequestException exception)
        {
            return Failure(exception.StatusCode, "Worker request exceeded the allowed size.");
        }
        catch (ArgumentException)
        {
            return Failure(400, "Invalid worker payload.");
        }
    }

    private async Task<IResult> HandleLeaseAsync(HttpContext context)
    {
        if (!TryAuthorize(context, out JobState? job, out IResult? error)) return error!;
        try
        {
            using JsonDocument payload = await ReadPayloadAsync(context);
            WorkerReport report;
            lock (job!.Sync)
            {
                if (!job.Claimed) return Failure(409, "The local job has not been claimed.");
                job.NeedsLogin = true;
                job.Activity = LoginRequiredMessage;
                report = Report(job) with { Status = "needs_login", ManualActionReason = LoginRequiredMessage };
            }
            Publish(report);
            // An empty cookie lease cannot recover a course: the existing worker rejects it.
            // Browse mode already tolerates this response and lets the customer sign in normally.
            return Failure(409, LoginRequiredMessage);
        }
        catch (JsonException)
        {
            return Failure(400, "Invalid worker JSON.");
        }
        catch (BadHttpRequestException exception)
        {
            return Failure(exception.StatusCode, "Worker request exceeded the allowed size.");
        }
    }

    private bool TryAuthorize(HttpContext context, out JobState? job, out IResult? error)
    {
        job = null;
        string suppliedKey = context.Request.Headers["x-worker-key"].ToString();
        bool keyValid = suppliedKey.Length == 64 && suppliedKey.All(Uri.IsHexDigit) &&
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(suppliedKey), _secretBytes);
        if (!keyValid || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
        {
            error = Failure(401, "Worker authentication required.");
            return false;
        }
        string jobId = context.Request.RouteValues["jobId"]?.ToString() ?? "";
        if (!_jobs.TryGetValue(jobId, out job))
        {
            error = Failure(404, "Unknown local job.");
            return false;
        }
        if (!string.Equals(context.Request.Headers["x-worker-id"].ToString(), WorkerIdFor(jobId), StringComparison.Ordinal))
        {
            job = null;
            error = Failure(403, "The worker does not own this local job.");
            return false;
        }
        error = null;
        return true;
    }

    private static async Task<JsonDocument> ReadPayloadAsync(HttpContext context)
    {
        if (context.Request.ContentLength == 0) return JsonDocument.Parse("{}");
        JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body,
            new JsonDocumentOptions { MaxDepth = 12 }, context.RequestAborted);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException("An object payload is required.");
        }
        return document;
    }

    private static string? ReadText(JsonElement element, string name, int limit)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name);
        string? text = value.GetString();
        if (text?.Length > limit) throw new ArgumentException(name);
        return text;
    }

    private static int? ReadNumber(JsonElement element, string name, int minimum, int maximum)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) ||
            number < minimum || number > maximum) throw new ArgumentException(name);
        return number;
    }

    private static object Snapshot(JobState job) => new
    {
        id = job.Request.JobId,
        deviceId = job.Request.CustomerId,
        courseraUserName = job.Request.Name,
        mode = job.Request.Mode,
        targetUrl = job.Request.Url,
        batchId = (string?)null,
        skipGradedAppItems = true,
        skipPracticeAppItems = true,
        progress = job.Progress,
        currentModule = job.CurrentModule,
        totalModules = job.TotalModules,
        status = job.Status,
        pauseRequested = job.PauseRequested,
        pauseRequestedReason = job.PauseRequested ? "Đã dừng theo yêu cầu trong trình quản lý." : null,
    };

    private static WorkerReport Report(JobState job) => new(job.Request.JobId, job.Status,
        job.Progress, job.CurrentModule, job.TotalModules, job.Activity, job.ManualActionReason);

    private void Publish(WorkerReport report)
    {
        if (Reported is not { } handlers) return;
        foreach (Action<WorkerReport> handler in handlers.GetInvocationList())
        {
            try { handler(report); }
            catch { /* A UI observer cannot prevent acknowledgement of the worker's result. */ }
        }
    }

    private static IResult Envelope(object? data) => Results.Json(new { data });
    private static IResult Failure(int status, string message) =>
        Results.Json(new { error = new { message } }, statusCode: status);

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_application is not null)
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _application.StopAsync(shutdown.Token);
                await _application.DisposeAsync();
                _application = null;
            }
            _baseUri = null;
            _jobs.Clear();
            CryptographicOperations.ZeroMemory(_secretBytes);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private const string LoginRequiredMessage = "Phiên Coursera chưa đăng nhập hoặc đã hết hạn. Mở kiểm tra để đăng nhập lại rồi chạy khóa học.";

    private sealed class JobState(WorkerJobRequest request)
    {
        public object Sync { get; } = new();
        public WorkerJobRequest Request { get; } = request;
        public bool Claimed { get; set; }
        public bool PauseRequested { get; set; }
        public bool NeedsLogin { get; set; }
        public string Status { get; set; } = "queued";
        public int Progress { get; set; } = request.Progress;
        public int? CurrentModule { get; set; } = request.CurrentModule;
        public int? TotalModules { get; set; } = request.TotalModules;
        public string? Activity { get; set; }
        public string? ManualActionReason { get; set; }
    }
}
