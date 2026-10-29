using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private async Task<PeerPdfRevisionPageState> ProbePeerPdfRevisionAsync(Uri peerUri,
        string action = "inspect", string? fileName = null, string? snapshot = null, string? dialogToken = null)
    {
        if (!PeerPdfJobCanAct(peerUri)) return new() { State = "STOPPED" };
        string raw = await MainWebView.ExecuteScriptAsync(
            PeerPdfRevisionAutomation.BuildProbe(peerUri, action, fileName, snapshot, dialogToken)).WaitAsync(TimeSpan.FromSeconds(10));
        if (!PeerPdfJobCanAct(peerUri)) return new() { State = "STOPPED" };
        return JsonSerializer.Deserialize<PeerPdfRevisionPageState>(DecodeWebViewString(raw)) ?? new();
    }

    private async Task<PeerPdfRevisionPageState> ReadStablePeerFailureAsync(Uri peerUri, CancellationToken ct)
    {
        string? previous = null;
        PeerPdfRevisionPageState page = new();
        for (int i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            page = await ProbePeerPdfRevisionAsync(peerUri);
            if (page.State == "FAILED_READY" && page.SnapshotToken.Length > 0)
            {
                if (page.SnapshotToken == previous) return page;
                previous = page.SnapshotToken;
            }
            else
            {
                previous = null;
                if (page.State is not ("WAIT" or "CONTEXT_INCOMPLETE")) return page;
            }
            await Task.Delay(500, ct);
        }
        return new() { State = "FEEDBACK_NOT_STABLE", Detail = page.Detail };
    }

    private async Task<PeerAssignmentContextState> ReadRevisionInstructionsAsync(Uri peerUri, CancellationToken ct)
    {
        string? previous = null;
        for (int i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            var context = await ProbePeerAssignmentContextAsync(peerUri, "openInstructions");
            if (context.State == "CONTEXT_READY")
            {
                string key = context.Title + "\n" + context.Instructions + "\n" + context.Rubric;
                if (key == previous) return context;
                previous = key;
            }
            else
            {
                previous = null;
                if (context.State is not ("WAIT" or "INSTRUCTIONS_CLICKED" or "CONTEXT_NOT_READY"))
                    throw new InvalidDataException("Chưa đọc lại được đề bài để sửa PDF: " + context.State);
            }
            await Task.Delay(500, ct);
        }
        throw new InvalidDataException("Đề bài chưa ổn định để sửa PDF; bản đã nộp vẫn được giữ nguyên.");
    }

    private async Task<bool> ReviseFailedPeerPdfAsync(Uri peerUri)
    {
        _peerPdfRunJob = _centralWorkerClient.CurrentJob?.Id;
        _peerPdfRunUser = _courseraIdentity?.UserId;
        _peerPdfLastState = "SUBMITTED_FAILED";
        if (!PeerPdfJobCanAct(peerUri)) return false;
        if (!(Environment.GetEnvironmentVariable("ACOSE_PEER_PDF_MODE") ?? "ai").Trim().Equals("ai", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.StatusText = "⏸️ PDF chưa đạt; chế độ kho PDF soạn sẵn không tự viết lại nội dung.";
            _peerPdfActionReason = "Chọn chế độ AI để sửa bản AI đã lưu, hoặc thay PDF soạn sẵn bằng bản đã sửa.";
            return false;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        monitor.Tick += (_, _) => { if (!PeerPdfJobCanAct(peerUri)) cancellation.Cancel(); };
        monitor.Start();
        CancellationToken ct = cancellation.Token;
        try
        {
            using PeerAiPdfSession? session = OpenPeerAiPdfSession(peerUri);
            if (session is null) return false;
            var cache = session.Cache;
            for (int cycle = 0; cycle <= PeerAiPdfCache.MaximumRevisions; cycle++)
            {
                ct.ThrowIfCancellationRequested();
                // A stopped older Worker may have left the known confirmation open.
                // Resume the already-rendered revision; do not generate another draft.
                var interrupted = await ProbePeerPdfRevisionAsync(peerUri);
                if (interrupted.State is "ATTEMPT_CONFIRM_READY" or "ATTEMPT_CONFIRM_WAIT" or "FORM_READY" or "FORM_RETAINS_FILE")
                {
                    PeerAiPdfEntry? pending = cache.Load();
                    if (pending is not { State: "ready", RevisionCount: > 0, AttemptStartPending: true } ||
                        ((interrupted.State is "ATTEMPT_CONFIRM_READY" or "ATTEMPT_CONFIRM_WAIT") &&
                         !string.Equals(pending.ParentPdfName, interrupted.FileName, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Trang mở lần nộp mới không khớp một bản PDF sửa đã sẵn sàng.");
                    string pendingPdf = cache.GetVerifiedPdf(pending.ContextHash, pending.FactsHash);
                    await WaitForPeerRevisionFormAsync(peerUri, cache, pending.ParentPdfName, pendingPdf, ct);
                    if (await UploadAndAwaitPeerRevisionResultAsync(peerUri, pendingPdf, cache, ct)) return true;
                    if (_peerPdfLastState != "SUBMITTED_FAILED") return false;
                    continue;
                }
                _viewModel.StatusText = "📖 Bài PDF chưa đạt; đang đọc đề, điểm và feedback của bản đã nộp...";
                var failure = await ReadStablePeerFailureAsync(peerUri, ct);
                if (failure.State != "FAILED_READY")
                    throw new InvalidDataException($"Chưa đọc đủ kết quả để sửa PDF ({failure.State}). {failure.Detail}");
                string feedbackHash = PeerAiPdfCache.Hash(failure.SnapshotToken);
                string eligibility = cache.InspectRevision(failure.FileName, feedbackHash);
                if (eligibility == "ATTEMPT_UNCERTAIN" && cache.Load()?.AttemptConfirmationPending == false)
                {
                    // No Continue or submit intent was ever issued. A stable, exact old
                    // result proves the interrupted confirmation did not create a new submission.
                    cache.ReopenRevisionAttempt(failure.FileName, feedbackHash);
                    eligibility = cache.InspectRevision(failure.FileName, feedbackHash);
                }
                if (eligibility == "LIMIT_REACHED")
                {
                    _viewModel.StatusText = "⏸️ PDF vẫn chưa đạt sau tối đa 3 lần sửa và nộp lại.";
                    _peerPdfActionReason = "Đã giữ toàn bộ bản PDF và lịch sử; xem feedback mới nhất để kiểm tra phần còn thiếu.";
                    return false;
                }
                if (eligibility is not ("CAN_REVISE" or "RESUME"))
                    throw new InvalidDataException(eligibility switch
                    {
                        "NO_DRAFT" => "Không có bản nháp AI của PDF đã nộp trong máy này; chưa thể sửa đúng bản.",
                        "ATTEMPT_UNCERTAIN" => "Đã yêu cầu mở lần nộp mới nhưng trang vẫn hiện kết quả cũ. Mở My submission kiểm tra; không tự bấm lặp.",
                        _ => $"PDF/kết quả hiện tại chưa khớp bản AI đã lưu ({eligibility}); không tự sửa nhầm bài."
                    });

                PeerAiPdfEntry saved = cache.Load()!;
                _viewModel.StatusText = "📖 Đang đối chiếu Instructions với câu hỏi thực tế trong My submission...";
                var context = await ReadRevisionInstructionsAsync(peerUri, ct);
                string contextHash = PeerAiPdfCache.Hash(context.Title + "\n" + context.Instructions + "\n" + context.Rubric);
                PeerResearchLength? researchLength = PeerResearchWriter.IsApplicable(context.Title, context.Instructions, context.Rubric)
                    ? PeerResearchWriter.GetRequiredLength(context.Title, context.Instructions, context.Rubric)
                    : null;
                string profile = _workerLaunchOptions.Enabled ? _workerLaunchOptions.ProfilePath : MainWebView.CoreWebView2.Environment.UserDataFolder;
                string factsPath = Path.Combine(profile, "AcoseLearnerContexts", cache.ProfileKey + ".txt");
                PeerAiPdfCache.EnsureOrdinaryPath(factsPath);
                string facts = "";
                if (File.Exists(factsPath))
                {
                    if (new FileInfo(factsPath).Length > 24_000) throw new InvalidDataException("Tệp dữ liệu người học vượt 24 KB.");
                    facts = await File.ReadAllTextAsync(factsPath, ct);
                }
                if (facts.Length > 16_000) throw new InvalidDataException("Tệp dữ liệu người học vượt 16.000 ký tự.");
                string factsHash = PeerAiPdfCache.Hash(facts);
                if (eligibility == "RESUME" && (saved.ContextHash != contextHash || saved.FactsHash != factsHash))
                    throw new InvalidDataException("Đề/dữ liệu đã đổi sau khi lưu bản sửa. Giữ lại bản sửa để kiểm tra trước khi nộp.");

                if (eligibility == "CAN_REVISE")
                {
                    var errors = new List<string>();
                    bool reserved = false;
                    for (int generation = 0; generation < 3; generation++)
                    {
                        ct.ThrowIfCancellationRequested();
                        int number = saved.RevisionCount + 1;
                        var progress = new Progress<string>(message =>
                        {
                            if (!ct.IsCancellationRequested && PeerPdfJobCanAct(peerUri))
                                _viewModel.StatusText = $"✍️ Sửa PDF lần {number}/3: {message}";
                        });
                        string revisionSystemPrompt = PeerPdfRevisionDraftContract.SystemPrompt +
                            (researchLength == null ? "" : "\n\n" + PeerResearchFallbackWriter.BuildGuidance(researchLength));
                        var result = await _aiCompletionService.CompleteAsync(revisionSystemPrompt,
                            PeerPdfRevisionDraftContract.BuildUserPrompt(failure.Title, context.Instructions, failure.Rubric,
                                failure.Prompt, failure.Feedback, facts, saved.Draft,
                                PeerAiPdfCache.Hash(cache.ProfileKey + feedbackHash + generation), errors.ToArray()), 0.4, progress, ct);
                        ct.ThrowIfCancellationRequested();
                        if (!result.Success) throw new InvalidDataException(result.UserMessage);
                        var validation = PeerPdfDraftContract.Parse(result.Content);
                        if (researchLength != null)
                            validation = PeerResearchFallbackWriter.ValidateLength(validation, researchLength);
                        if (validation.Draft is null)
                        {
                            if (generation < 2 && (validation.Status == "NEEDS_INFORMATION" ||
                                (researchLength != null && validation.Status is ("UNSUPPORTED" or "RESEARCH_FALLBACK_LENGTH"))))
                            {
                                errors.Add("No additional personal information will be supplied. Use a clearly labeled illustrative scenario for absent personal details; never invent real evidence. Previous response: " + validation.Detail[..Math.Min(1500, validation.Detail.Length)]);
                                continue;
                            }
                            if (validation.Status != "INVALID_AI_RESPONSE")
                                throw new InvalidDataException(validation.Detail);
                            errors.Add(validation.Detail);
                            continue;
                        }
                        string reservation = "BUSY";
                        for (int wait = 0; reservation == "BUSY" && wait < 50; wait++)
                        {
                            ct.ThrowIfCancellationRequested();
                            reservation = cache.TryReserveRevision(failure.FileName, feedbackHash, validation.Draft, contextHash, factsHash);
                            if (reservation == "BUSY") await Task.Delay(200, ct);
                        }
                        if (reservation is "UNCHANGED_DRAFT" or "TOO_SIMILAR")
                        {
                            errors.Add(reservation == "UNCHANGED_DRAFT" ? "The revision must meaningfully change the failed draft, not just its title."
                                : "Use a more distinctive substantive approach, without fabricating learner facts.");
                            continue;
                        }
                        if (reservation != "RESERVED") throw new InvalidDataException("Chưa lưu được bản sửa PDF: " + reservation);
                        reserved = true;
                        break;
                    }
                    if (!reserved) throw new InvalidDataException("AI chưa tạo được bản sửa hợp lệ sau 3 lần kiểm tra. " + errors.LastOrDefault());
                    saved = cache.Load()!;
                }

                if (saved.State == "draft")
                {
                    _viewModel.StatusText = $"📄 Đang xuất bản PDF sửa lần {saved.RevisionCount}/3...";
                    string output = cache.PdfPath(saved);
                    if (!File.Exists(output))
                        await PeerPdfDocumentRenderer.RenderAsync(MainWebView.CoreWebView2.Environment, new WindowInteropHelper(this).Handle,
                            output, saved.Draft.Title, saved.Draft.Sections, ct,
                            researchLength?.MinimumPages, researchLength?.MaximumPages);
                    ct.ThrowIfCancellationRequested();
                    cache.CommitPdf();
                }
                string pdf = cache.GetVerifiedPdf(contextHash, factsHash);
                _viewModel.StatusText = "📄 Bản sửa đã lưu; đang mở lại My submission để nộp...";
                PeerPdfRevisionPageState current = new();
                for (int i = 0; i < 30; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await ProbePeerAssignmentContextAsync(peerUri, "openSubmission");
                    current = await ProbePeerPdfRevisionAsync(peerUri);
                    if (current.State == "FAILED_READY") break;
                    if (current.State is not ("WAIT" or "CONTEXT_INCOMPLETE"))
                        throw new InvalidDataException("Trạng thái bài đã đổi khi AI đang sửa: " + current.State);
                    await Task.Delay(500, ct);
                }
                if (current.State != "FAILED_READY" || current.SnapshotToken != failure.SnapshotToken)
                    throw new InvalidDataException("Kết quả/feedback đã đổi khi AI đang sửa; giữ bản sửa, chưa tạo lần nộp mới.");
                if (!current.CanStartAttempt)
                    throw new InvalidDataException("Coursera chưa bật Start new attempt. Bản sửa đã lưu; chạy lại khi hết thời gian chờ.");
                ct.ThrowIfCancellationRequested();
                if (!PeerPdfJobCanAct(peerUri)) return false;
                cache.BeginRevisionAttempt(failure.FileName, feedbackHash); // Durable intent before the only attempt click.
                var opened = await ProbePeerPdfRevisionAsync(peerUri, "startAttempt", failure.FileName, failure.SnapshotToken);
                if (opened.State != "NEW_ATTEMPT_CLICKED")
                    throw new InvalidDataException("Chưa xác nhận mở lần nộp mới: " + opened.State + ". Không tự bấm lại.");
                await WaitForPeerRevisionFormAsync(peerUri, cache, failure.FileName, pdf, ct);
                if (await UploadAndAwaitPeerRevisionResultAsync(peerUri, pdf, cache, ct)) return true;
                if (_peerPdfLastState != "SUBMITTED_FAILED") return false;
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "⏸️ Đã dừng sửa PDF hoặc hết thời gian chờ.";
            _peerPdfActionReason = "Bản nháp và ý định nộp đã lưu; không tiếp tục khi Worker dừng hoặc đổi bài/tài khoản.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or ArgumentException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            _viewModel.StatusText = "⏸️ Chưa hoàn tất sửa/nộp lại PDF.";
            _peerPdfActionReason = exception.Message;
            return false;
        }
        finally { monitor.Stop(); }
    }

    private async Task WaitForPeerRevisionFormAsync(Uri peerUri, PeerAiPdfCache cache,
        string previousFileName, string pdf, CancellationToken ct)
    {
        bool removedPrevious = false, confirmedThisRun = false;
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            var current = await ProbePeerPdfRevisionAsync(peerUri);
            if (current.State == "FORM_READY") return;
            if (current.State is "ATTEMPT_CONFIRM_READY" or "ATTEMPT_CONFIRM_WAIT")
            {
                PeerAiPdfEntry? entry = cache.Load();
                if (entry is not { State: "ready", RevisionCount: > 0, AttemptStartPending: true } ||
                    !string.Equals(entry.ParentPdfName, previousFileName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.FileName, previousFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(current.DialogToken))
                    throw new InvalidDataException("Popup không khớp bản PDF sửa đã lưu; chưa bấm Continue.");
                if (entry.AttemptConfirmationPending && !confirmedThisRun)
                    throw new InvalidDataException("Lần trước đã yêu cầu Continue nhưng chưa rõ kết quả; không bấm lặp để tránh xóa nhầm lần nộp.");
                if (current.State == "ATTEMPT_CONFIRM_READY" && !confirmedThisRun)
                {
                    if (!PeerPdfJobCanAct(peerUri)) throw new OperationCanceledException(ct);
                    ct.ThrowIfCancellationRequested();
                    // Continue clears the previous server submission/grade. Verify the new
                    // PDF and journal intent BEFORE dispatch; never retry an uncertain click.
                    string verified = cache.GetVerifiedPdf(entry.ContextHash, entry.FactsHash);
                    if (!string.Equals(verified, pdf, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Bản PDF sửa đã đổi trước khi xác nhận lần nộp mới.");
                    cache.BeginRevisionConfirmation(previousFileName);
                    var confirmation = await ProbePeerPdfRevisionAsync(peerUri, "confirmAttempt",
                        previousFileName, dialogToken: current.DialogToken);
                    if (confirmation.State != "ATTEMPT_CONFIRM_CLICKED")
                        throw new InvalidDataException("Chưa xác nhận được Continue (" + confirmation.State + "); không bấm lại.");
                    confirmedThisRun = true;
                }
                _viewModel.StatusText = confirmedThisRun
                    ? "⏳ Đã bấm Continue; đang chờ form để tải PDF đã sửa..."
                    : "⏳ Đã nhận diện popup mở lần nộp mới; chờ nút Continue sẵn sàng...";
                await Task.Delay(500, ct);
                continue;
            }
            if (current.State == "FORM_RETAINS_FILE")
            {
                if (string.Equals(current.FileName, Path.GetFileName(pdf), StringComparison.OrdinalIgnoreCase)) return;
                if (!removedPrevious && string.Equals(current.FileName, previousFileName, StringComparison.OrdinalIgnoreCase))
                {
                    var replaced = await ProbePeerPdfRevisionAsync(peerUri, "removePreviousFile", previousFileName);
                    if (replaced.State != "PREVIOUS_FILE_REMOVED")
                        throw new InvalidDataException("Chưa thay được tệp cũ trong form mới: " + replaced.State);
                    removedPrevious = true;
                    await Task.Delay(500, ct);
                    continue;
                }
            }
            if (current.State is "FORM_RETAINS_FILE" or "BLOCKING_DIALOG" or "STOPPED" or "STALE_DOCUMENT")
                throw new InvalidDataException($"Lần nộp mới cần kiểm tra ({current.State}). {current.Detail}");
            _viewModel.StatusText = $"⏳ Đang chờ form nộp PDF mới ({i + 1}/60)...";
            await Task.Delay(500, ct);
        }
        throw new InvalidDataException("Chưa thấy form nộp mới sau 30 giây; không lặp Start new attempt hoặc Continue.");
    }

    private async Task<bool> UploadAndAwaitPeerRevisionResultAsync(Uri peerUri, string pdf, PeerAiPdfCache cache, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (await UploadAndSubmitPeerPdfAsync(peerUri, pdf, cache)) return true;
        if ((!PeerPdfWasSubmitted(_peerPdfLastState) && _peerPdfLastState != "SUBMITTED_CONFIRMATION_PENDING") ||
            _peerPdfLastState is "SUBMITTED_FAILED" or "SUBMITTED_PENDING_REVIEWS") return false;
        int revision = cache.Load()?.RevisionCount ?? 0;
        // A click alone and stale old results are not evidence of new grading.
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbePeerPdfAsync(peerUri, fileName: Path.GetFileName(pdf));
            if (result.State == "SUBMITTED_PASSED") return ReportExistingPeerSubmission(result, peerUri, cache);
            if (result.State == "SUBMITTED_FAILED") return false;
            if (result.State is "STOPPED" or "STALE_DOCUMENT") return false;
            if (result.State == "SUBMITTED_PENDING_REVIEWS") return ReportExistingPeerSubmission(result, peerUri, cache);
            if ((i + 1) % 8 == 0 && PeerPdfJobCanAct(peerUri))
            {
                var refreshed = await ProbePeerPdfAsync(peerUri, "refreshGrade", Path.GetFileName(pdf));
                if (refreshed.State == "SUBMITTED_PASSED") return ReportExistingPeerSubmission(refreshed, peerUri, cache);
                if (refreshed.State == "SUBMITTED_FAILED") return false;
                if (refreshed.State == "SUBMITTED_PENDING_REVIEWS") return ReportExistingPeerSubmission(refreshed, peerUri, cache);
                if (refreshed.State != "GRADE_REFRESH_CLICKED" && PeerPdfJobCanAct(peerUri))
                    ReloadPendingPeerGrade(peerUri);
            }
            _viewModel.StatusText = $"⏳ Đã nộp PDF sửa lần {revision}/3; chờ Coursera chấm ({i + 1}/60s)...";
            await Task.Delay(1000, ct);
        }
        _viewModel.StatusText = "⏳ PDF sửa đã nộp; Coursera chưa trả kết quả mới. Không nộp thêm bản trùng.";
        _peerPdfActionReason = "Chờ Coursera chấm; khi chạy lại, chỉ sửa tiếp nếu bản mới được xác nhận chưa đạt.";
        return false;
    }

    private async Task RevisePeerAfterReviewsAsync(Uri submission, bool reviewsCompleted)
    {
        bool passed = await ReviseFailedPeerPdfAsync(submission);
        if (!PeerPdfJobCanAct(submission)) return;
        if (passed)
        {
            if (TryGetCourseHomeUri(submission, out Uri? home) && home != null) MainWebView.Source = home;
            return;
        }
        if (_peerPdfLastState == "SUBMITTED_PENDING_REVIEWS")
        {
            if (reviewsCompleted)
                await WaitForPendingPeerGradeAsync(submission, reviewsCompleted: true);
            else
                await ContinueSubmittedPeerToReviewsAsync(submission);
            return;
        }
        if (HasPeerGradeWaitingEvidence(submission, _peerPdfLastState))
        {
            await WaitForPendingPeerGradeAsync(submission, reviewsCompleted);
            return;
        }
        await PauseCourseJobAsync(_viewModel.StatusText, _viewModel.StatusText + " " + _peerPdfActionReason,
            "PEER_ASSIGNMENT_USER_WORK_REQUIRED");
    }
}
