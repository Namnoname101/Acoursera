using System.Diagnostics;

namespace AutomationPlatform.Presentation.Services;

/// <summary>Keeps the paper stage visible while provider messages and request timers change.</summary>
public sealed class PeerResearchProgress(IProgress<string>? target) : IProgress<string>
{
    private readonly object _gate = new();
    private string _stage = "Đang chuẩn bị bài nghiên cứu";
    private long _version;

    public void Report(string value)
    {
        lock (_gate) { _stage = value.TrimEnd('.', '…'); _version++; target?.Report(value); }
    }

    public async Task<AiCompletionResult> RequestAsync(
        Func<IProgress<string>, Task<AiCompletionResult>> request, int attempt, CancellationToken ct,
        TimeSpan? heartbeatInterval = null)
    {
        var timer = Stopwatch.StartNew();
        string provider = "Đang chờ phản hồi AI";
        long version;
        bool active = true;
        lock (_gate) version = _version;
        void Emit(string? detail = null)
        {
            lock (_gate)
            {
                if (!active || version != _version || ct.IsCancellationRequested) return;
                if (detail != null) provider = detail.TrimEnd('.', '…');
                target?.Report($"{_stage} · lần {attempt + 1}/3 · {timer.Elapsed.TotalSeconds:F0}s · {provider}");
            }
        }
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task TickAsync()
        {
            using var periodic = new PeriodicTimer(heartbeatInterval ?? TimeSpan.FromSeconds(2));
            try { while (await periodic.WaitForNextTickAsync(heartbeat.Token).ConfigureAwait(false)) Emit(); }
            catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { }
        }
        ct.ThrowIfCancellationRequested();
        Emit();
        Task ticking = TickAsync();
        try { return await request(new ImmediateProgress(detail => Emit(detail))); }
        finally
        {
            lock (_gate) active = false;
            heartbeat.Cancel();
            await ticking;
        }
    }

    private sealed class ImmediateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
