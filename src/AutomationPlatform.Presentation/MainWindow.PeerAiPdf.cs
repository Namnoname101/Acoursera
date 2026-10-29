using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private sealed record PeerAiPdfSession(PeerAiPdfCache Cache, FileStream Lease) : IDisposable
    {
        public void Dispose() => Lease.Dispose();
    }

    private string PeerAiPdfRoot()
    {
        string engine = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? install = Path.GetDirectoryName(engine);
        return Path.Combine(install != null && File.Exists(Path.Combine(install, ".acose-customer-manager-install"))
            ? install : engine, "GeneratedPdfs");
    }

    private PeerAiPdfSession? OpenPeerAiPdfSession(Uri peerUri)
    {
        // User ID protects against switching Coursera accounts within one browser profile.
        if (string.IsNullOrWhiteSpace(_courseraIdentity?.UserId))
        {
            _viewModel.StatusText = "⏸️ Chưa xác định được tài khoản Coursera để tạo PDF riêng.";
            _peerPdfActionReason = "Đăng nhập và đọc lại danh tính Coursera trước khi tạo bài; không dùng chung PDF giữa các tài khoản.";
            return null;
        }
        var cache = new PeerAiPdfCache(PeerAiPdfRoot(), _courseraIdentity.UserId, peerUri);
        FileStream? lease = cache.TryAcquireLease();
        if (lease == null)
        {
            _viewModel.StatusText = "⏸️ Bài PDF này đang được một Worker khác xử lý cho cùng tài khoản.";
            _peerPdfActionReason = "Chờ Worker đang chạy xong; không chạy hai tiến trình cho cùng bài và tài khoản.";
            return null;
        }
        return new PeerAiPdfSession(cache, lease);
    }

    private async Task<PeerAssignmentContextState> ProbePeerAssignmentContextAsync(Uri peerUri, string action)
    {
        if (!PeerPdfJobCanAct(peerUri)) return new() { State = "STOPPED" };
        string raw = await MainWebView.ExecuteScriptAsync(PeerAssignmentContextAutomation.BuildProbe(peerUri, action))
            .WaitAsync(TimeSpan.FromSeconds(10));
        return JsonSerializer.Deserialize<PeerAssignmentContextState>(DecodeWebViewString(raw)) ?? new();
    }

    private async Task<string?> ResolveAiPeerPdfAsync(Uri peerUri, PeerAiPdfCache cache)
    {
        // Long research papers use several bounded AI calls rather than one truncated response.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        string? expectedJob = _centralWorkerClient.CurrentJob?.Id;
        string? expectedUser = _courseraIdentity?.UserId;
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        monitor.Tick += (_, _) =>
        {
            if (!PeerPdfJobCanAct(peerUri) || expectedJob != _centralWorkerClient.CurrentJob?.Id || expectedUser != _courseraIdentity?.UserId)
                cancellation.Cancel();
        };
        monitor.Start();
        CancellationToken ct = cancellation.Token;
        try
        {
            PeerAiPdfEntry? saved = cache.Load();
            if (saved?.State is "pending" or "confirmed")
                throw new InvalidDataException("Đã ghi nhận lần nộp trước nhưng trang chưa xác nhận. Kiểm tra My submission; không tự tạo hoặc nộp lại.");

            _viewModel.StatusText = "📖 Đang đọc Instructions và tiêu chí chấm cho bài PDF...";
            PeerAssignmentContextState context = new();
            string? previousContext = null;
            bool stable = false;
            for (int i = 0; i < 40; i++)
            {
                ct.ThrowIfCancellationRequested();
                context = await ProbePeerAssignmentContextAsync(peerUri, "openInstructions");
                if (context.State == "CONTEXT_READY")
                {
                    string snapshot = context.Instructions + "\n" + context.Rubric;
                    if (snapshot == previousContext) { stable = true; break; }
                    previousContext = snapshot;
                }
                else
                {
                    // A loading transition between two reads is not a second stable snapshot.
                    previousContext = null;
                    if (context.State is not ("WAIT" or "INSTRUCTIONS_CLICKED" or "CONTEXT_NOT_READY")) break;
                }
                _viewModel.StatusText = $"⏳ Đang mở Instructions và chờ đề bài tải xong ({i + 1}/40)...";
                await Task.Delay(500, ct);
            }
            if (!stable)
            {
                _viewModel.StatusText = $"⏸️ Chưa đọc đủ đề bài PDF ({context.State}).";
                _peerPdfActionReason = context.Detail.Length > 0 ? context.Detail : "Mở Instructions để kiểm tra nội dung đề và tài liệu bắt buộc.";
                return null;
            }
            string profile = _workerLaunchOptions.Enabled ? _workerLaunchOptions.ProfilePath : MainWebView.CoreWebView2.Environment.UserDataFolder;
            string factsPath = Path.Combine(profile, "AcoseLearnerContexts", cache.ProfileKey + ".txt");
            string facts = "";
            PeerAiPdfCache.EnsureOrdinaryPath(factsPath);
            if (File.Exists(factsPath))
            {
                if (new FileInfo(factsPath).Length > 24_000) throw new InvalidDataException("Tệp dữ liệu người học vượt quá 24 KB.");
                facts = await File.ReadAllTextAsync(factsPath, ct);
                if (facts.Length > 16_000) throw new InvalidDataException("Tệp dữ liệu người học vượt quá 16.000 ký tự.");
            }
            string contextHash = PeerAiPdfCache.Hash(context.Title + "\n" + context.Instructions + "\n" + context.Rubric);
            string factsHash = PeerAiPdfCache.Hash(facts);
            bool research = PeerResearchWriter.IsApplicable(context.Title, context.Instructions, context.Rubric);
            PeerResearchLength? researchLength = research
                ? PeerResearchWriter.GetRequiredLength(context.Title, context.Instructions, context.Rubric)
                : null;
            bool useResearchFallback = false;
            string researchCatalogReason = "";
            var researchFallbackIssues = new List<string>();
            var researchFallbackWriter = new PeerResearchFallbackWriter(
                (system, input, temperature, report, token) =>
                    _aiCompletionService.CompleteAsync(system, input, temperature, report, token));
            if (saved != null && (saved.ContextHash != contextHash || saved.FactsHash != factsHash))
                throw new InvalidDataException("Đề bài hoặc dữ liệu người học đã đổi so với PDF đang lưu. Kiểm tra bản cũ trước khi tạo lại.");

            for (int generation = 0; saved == null && generation < 3; generation++)
            {
                ct.ThrowIfCancellationRequested();
                _viewModel.StatusText = generation == 0
                    ? "🤖 Đang soạn bài PDF riêng theo đề và tiêu chí chấm..."
                    : "🤖 Đang điều chỉnh cách triển khai để tạo bản PDF hợp lệ...";
                var progress = new Progress<string>(message =>
                {
                    if (!ct.IsCancellationRequested && PeerPdfJobCanAct(peerUri)) _viewModel.StatusText = message;
                });
                PeerPdfDraftValidation validation;
                if (research && !useResearchFallback)
                {
                    var checkpoint = new PeerResearchCheckpoint(cache, contextHash, factsHash, generation);
                    using var evidenceService = new PeerResearchEvidenceService();
                    var writer = new PeerResearchWriter(
                        (system, input, temperature, report, token) => checkpoint.CompleteAsync(system, input, temperature,
                            report, token, (s, p, t, r, c) => _aiCompletionService.CompleteAsync(s, p, t, r, c)),
                        (selection, token) => checkpoint.EvidenceAsync(selection, token, evidenceService.FetchAsync),
                        checkpoint.RejectReply);
                    validation = await writer.GenerateAsync(context.Title, context.Instructions, context.Rubric, facts,
                        PeerAiPdfCache.Hash(cache.ProfileKey + contextHash + generation), progress, ct);
                    if (validation.Draft == null && validation.Status == "UNSUPPORTED_RESEARCH")
                    {
                        useResearchFallback = true;
                        researchCatalogReason = validation.Detail;
                        _viewModel.StatusText = "🧠 Kho số liệu chuyên dụng chưa phủ đề này; AI đang tự soạn bản nghiên cứu tốt nhất...";
                        validation = await researchFallbackWriter.GenerateAsync(context.Title, context.Instructions,
                            context.Rubric, facts,
                            PeerAiPdfCache.Hash(cache.ProfileKey + contextHash + generation + ":research-fallback"),
                            researchLength!, researchCatalogReason, researchFallbackIssues, progress, ct);
                    }
                }
                else if (research)
                {
                    _viewModel.StatusText = "🧠 AI đang tiếp tục hoàn thiện bản nghiên cứu fallback...";
                    validation = await researchFallbackWriter.GenerateAsync(context.Title, context.Instructions,
                        context.Rubric, facts,
                        PeerAiPdfCache.Hash(cache.ProfileKey + contextHash + generation + ":research-fallback"),
                        researchLength!, researchCatalogReason, researchFallbackIssues, progress, ct);
                }
                else
                {
                    AiCompletionResult result = await _aiCompletionService.CompleteAsync(PeerPdfDraftContract.SystemPrompt,
                        PeerPdfDraftContract.BuildUserPrompt(context.Title, context.Instructions, context.Rubric, facts,
                            Guid.NewGuid().ToString("N"), []), 0.8, progress, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!result.Success)
                    {
                        _viewModel.StatusText = "⏸️ AI chưa tạo được bài PDF.";
                        _peerPdfActionReason = result.UserMessage;
                        return null;
                    }
                    validation = PeerPdfDraftContract.Parse(result.Content);
                }
                ct.ThrowIfCancellationRequested();
                if (validation.Draft == null)
                {
                    if (useResearchFallback && generation < 2 && validation.Status is
                        ("NEEDS_INFORMATION" or "UNSUPPORTED" or "INVALID_AI_RESPONSE" or "RESEARCH_FALLBACK_LENGTH"))
                    {
                        researchFallbackIssues.Add(validation.Status + ": " +
                            validation.Detail[..Math.Min(1_500, validation.Detail.Length)]);
                        continue;
                    }
                    // Autonomous generic assignments may use explicitly disclosed illustrative
                    // personal scenarios; do not immediately bounce a preference question to the user.
                    if (!research && validation.Status == "NEEDS_INFORMATION" && generation < 2) continue;
                    _viewModel.StatusText = $"⏸️ Chưa thể tạo PDF để nộp ({validation.Status}).";
                    _peerPdfActionReason = validation.Detail;
                    return null;
                }
                string reservation = "BUSY";
                for (int wait = 0; reservation == "BUSY" && wait < 50; wait++)
                {
                    ct.ThrowIfCancellationRequested();
                    reservation = cache.TryReserveDraft(contextHash, factsHash, validation.Draft);
                    if (reservation == "BUSY") await Task.Delay(200, ct);
                }
                if (reservation == "BUSY") throw new IOException("Kho bài PDF đang bận. Chạy lại sau ít giây.");
                if (reservation == "TOO_SIMILAR") continue;
                saved = cache.Load();
            }
            if (saved == null)
            {
                _viewModel.StatusText = "⏸️ AI chưa tạo được bản đủ khác với các bài đã lưu sau 3 lần thử.";
                _peerPdfActionReason = "Bổ sung bối cảnh riêng của người học, rồi chạy lại; không dùng lại bài của tài khoản khác.";
                return null;
            }
            if (saved.State == "draft")
            {
                _viewModel.StatusText = "📄 Đang xuất nội dung thành PDF...";
                string output = cache.PdfPath(saved);
                if (!File.Exists(output))
                    await PeerPdfDocumentRenderer.RenderAsync(MainWebView.CoreWebView2.Environment,
                        new WindowInteropHelper(this).Handle, output, saved.Draft.Title, saved.Draft.Sections, ct,
                        researchLength?.MinimumPages, researchLength?.MaximumPages);
                ct.ThrowIfCancellationRequested();
                cache.CommitPdf();
            }
            string pdf = cache.GetVerifiedPdf(contextHash, factsHash);
            _viewModel.StatusText = "📄 PDF đã sẵn sàng; đang quay lại My submission...";
            for (int i = 0; i < 30; i++)
            {
                ct.ThrowIfCancellationRequested();
                await ProbePeerAssignmentContextAsync(peerUri, "openSubmission");
                PeerPdfPageState page = await ProbePeerPdfAsync(peerUri, "inspect", Path.GetFileName(pdf));
                if (page.State is "FILE_INPUT_READY" or "ADD_FILE_READY" or "EXISTING_UPLOAD" or "UPLOADED" or "UPLOADING" or "FILE_SELECTED" or "DIFFERENT_UPLOAD")
                    return pdf;
                if (PeerPdfWasSubmitted(page.State)) return pdf; // The caller reports this without another upload.
                if (page.State is "STALE_DOCUMENT" or "STOPPED" or "AMBIGUOUS_UPLOAD") break;
                await Task.Delay(500, ct);
            }
            _viewModel.StatusText = "⏸️ PDF đã tạo nhưng chưa mở lại được My submission.";
            _peerPdfActionReason = "PDF được giữ nguyên trong GeneratedPdfs; chạy lại sẽ dùng đúng bản này.";
            return null;
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "⏸️ Đã dừng tạo PDF hoặc hết thời gian chờ.";
            _peerPdfActionReason = "Bản nháp đã lưu (nếu có) được giữ nguyên; không tiếp tục upload khi Worker đã dừng hoặc đổi bài.";
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or TimeoutException or ArgumentException or System.Net.Http.HttpRequestException or System.Runtime.InteropServices.COMException)
        {
            _viewModel.StatusText = "⏸️ Chưa hoàn tất chuẩn bị PDF bằng AI.";
            _peerPdfActionReason = exception.Message;
            return null;
        }
        finally { monitor.Stop(); }
    }
}
