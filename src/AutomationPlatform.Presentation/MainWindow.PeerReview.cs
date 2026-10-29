using System.Diagnostics;
using System.Text.Json;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private readonly HashSet<string> _pendingPeerSubmissionPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _pendingPeerSubmissionQueue = new();
    private readonly HashSet<string> _queuedPendingPeerSubmissionPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _submittedPeerReviewKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeerReviewCommentPlan> _peerReviewCommentPlans = new(StringComparer.Ordinal);
    private sealed record PeerReviewCommentPlan(string ContextFingerprint, string[] Comments);
    private Uri? _pendingPeerGradeReloadUri;
    private string? _pendingPeerGradeReloadJob;
    private string? _pendingPeerGradeReloadUser;
    private ulong? _pendingPeerGradeReloadNavigationId;
    private int _pendingPeerGradeRecoveryGeneration;

    private void TrackPendingPeerSubmission(string path)
    {
        string normalized = path.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized)) return;
        _pendingPeerSubmissionPaths.Add(normalized);
        if (_queuedPendingPeerSubmissionPaths.Add(normalized))
            _pendingPeerSubmissionQueue.Enqueue(normalized);
    }

    private void ClearPendingPeerSubmission(string path)
    {
        string normalized = path.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(normalized))
            _pendingPeerSubmissionPaths.Remove(normalized);
        // Queue entries are removed lazily when they reach the head. Keeping the
        // membership marker prevents a path that is cleared and re-added before
        // then from being queued twice.
    }

    private void ResetPendingPeerSubmissions()
    {
        _pendingPeerSubmissionPaths.Clear();
        _pendingPeerSubmissionQueue.Clear();
        _queuedPendingPeerSubmissionPaths.Clear();
    }

    private void EnsurePendingPeerSubmissionQueue()
    {
        // Defensive re-seeding also covers jobs restored from older in-memory
        // state where only the set was populated.
        foreach (string path in _pendingPeerSubmissionPaths)
            if (_queuedPendingPeerSubmissionPaths.Add(path))
                _pendingPeerSubmissionQueue.Enqueue(path);
    }

    private void ClearPendingPeerGradeReload()
    {
        _pendingPeerGradeReloadUri = null;
        _pendingPeerGradeReloadJob = null;
        _pendingPeerGradeReloadUser = null;
        _pendingPeerGradeReloadNavigationId = null;
    }

    private void ReloadPendingPeerGrade(Uri submission)
    {
        _pendingPeerGradeReloadUri = submission;
        _pendingPeerGradeReloadJob = _centralWorkerClient.CurrentJob?.Id;
        _pendingPeerGradeReloadUser = _courseraIdentity?.UserId;
        _pendingPeerGradeReloadNavigationId = null;
        try { MainWebView.Reload(); }
        catch { ClearPendingPeerGradeReload(); }
    }

    private void NavigatePendingPeerGrade(Uri destination)
    {
        _pendingPeerGradeReloadUri = destination;
        _pendingPeerGradeReloadJob = _centralWorkerClient.CurrentJob?.Id;
        _pendingPeerGradeReloadUser = _courseraIdentity?.UserId;
        _pendingPeerGradeReloadNavigationId = null;
        try
        {
            if (string.Equals(MainWebView.Source.AbsolutePath.TrimEnd('/'), destination.AbsolutePath.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
                MainWebView.Reload();
            else
                MainWebView.Source = destination;
        }
        catch { ClearPendingPeerGradeReload(); }
    }

    private void BindPendingPeerGradeReloadNavigation(string? target, ulong navigationId)
    {
        if (_pendingPeerGradeReloadUri is not { } expected) return;
        if (_pendingPeerGradeReloadJob != _centralWorkerClient.CurrentJob?.Id ||
            _pendingPeerGradeReloadUser != _courseraIdentity?.UserId ||
            !Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri) ||
            !IsPendingPeerNavigationTarget(expected, targetUri))
        {
            ClearPendingPeerGradeReload();
            return;
        }
        _pendingPeerGradeReloadNavigationId ??= navigationId;
    }

    private bool ConsumeOwnedPendingPeerGradeReload(ulong? navigationId, bool navigationSucceeded)
    {
        Uri? expected = _pendingPeerGradeReloadUri;
        string? job = _pendingPeerGradeReloadJob, user = _pendingPeerGradeReloadUser;
        if (expected == null || navigationId == null || _pendingPeerGradeReloadNavigationId != navigationId)
            return false;
        bool owned = job == _centralWorkerClient.CurrentJob?.Id &&
            user == _courseraIdentity?.UserId;
        ClearPendingPeerGradeReload();
        // A successful owned navigation is now handled by the normal document
        // dispatcher. Cancel its navigation watchdog before subsequent lessons
        // start, so a late retry can never pull the Worker back to course home.
        if (owned && navigationSucceeded) _pendingPeerGradeRecoveryGeneration++;
        return owned;
    }

    private static bool IsPendingPeerNavigationTarget(Uri expected, Uri actual)
    {
        if (!expected.IsAbsoluteUri || !actual.IsAbsoluteUri ||
            !string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase) ||
            expected.Port != actual.Port)
            return false;

        string? expectedAssignment = PeerReviewAutomation.AssignmentPath(expected);
        if (expectedAssignment != null)
            return string.Equals(expectedAssignment, PeerReviewAutomation.AssignmentPath(actual),
                StringComparison.OrdinalIgnoreCase);

        return string.Equals(expected.AbsolutePath.TrimEnd('/'), actual.AbsolutePath.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool PeerReviewCanAct(Uri expected) => !_workerClosing && !_coursePauseInProgress &&
        !_courseJobCompletionReported && !IsInteractiveBrowseSession && IsCourseraUri(MainWebView.Source) &&
        PeerReviewAutomation.AssignmentPath(expected) is { } path &&
        string.Equals(path, PeerReviewAutomation.AssignmentPath(MainWebView.Source), StringComparison.OrdinalIgnoreCase);

    private async Task<PeerReviewPageState> ProbePeerReviewAsync(Uri expected, string action = "probe", int? budget = null,
        IReadOnlyList<string>? reviewComments = null, string? reviewContext = null)
    {
        if (!PeerReviewCanAct(expected)) return new() { State = "STOPPED" };
        string raw = await MainWebView.ExecuteScriptAsync(
            PeerReviewAutomation.BuildProbe(expected, action, budget, reviewComments, reviewContext));
        return JsonSerializer.Deserialize<PeerReviewPageState>(DecodeWebViewString(raw)) ?? new();
    }

    private async Task<PeerReviewCommentPlan?> GeneratePeerReviewCommentsAsync(Uri expected, PeerReviewPageState page)
    {
        if (string.IsNullOrWhiteSpace(page.ReviewKey) || string.IsNullOrWhiteSpace(page.ContextFingerprint) ||
            page.CommentPrompts.Length == 0 || page.EmptyCommentCount <= 0)
        {
            await PausePeerReviewAsync("Chưa đọc đủ ngữ cảnh và các ô nhận xét để AI viết feedback.");
            return null;
        }
        if (_peerReviewCommentPlans.TryGetValue(page.ReviewKey, out PeerReviewCommentPlan? cached) &&
            cached.ContextFingerprint == page.ContextFingerprint && cached.Comments.Length == page.CommentPrompts.Length)
            return cached;

        string reviewKey = page.ReviewKey, context = page.ContextFingerprint;
        string? job = _centralWorkerClient.CurrentJob?.Id, user = _courseraIdentity?.UserId;
        bool Current() => PeerReviewCanAct(expected) && job == _centralWorkerClient.CurrentJob?.Id &&
            user == _courseraIdentity?.UserId;
        string title = string.IsNullOrWhiteSpace(page.AssignmentTitle) ? "Peer-reviewed assignment" : page.AssignmentTitle;
        string prompt;
        try
        {
            prompt = PeerReviewCommentContract.BuildUserPrompt(title, page.SubmissionText, page.RubricText,
                page.CommentPrompts, context);
        }
        catch (ArgumentException exception)
        {
            await PausePeerReviewAsync("Ngữ cảnh review không hợp lệ để gửi cho AI. " + exception.Message);
            return null;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var progress = new Progress<string>(message =>
            _viewModel.StatusText = "🤖 Đang viết nhận xét riêng theo bài và rubric. " + message);
        PeerReviewCommentValidation validation = new(null, "INVALID_AI_RESPONSE", "AI chưa trả lời.");
        for (int attempt = 1; attempt <= 2 && Current(); attempt++)
        {
            _viewModel.StatusText = $"🤖 AI đang đọc bài và viết feedback tự nhiên ({attempt}/2)...";
            AiCompletionResult result;
            try
            {
                result = await _aiCompletionService.CompleteAsync(PeerReviewCommentContract.SystemPrompt,
                    prompt, 0.35, progress, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!Current()) return null;
                await PausePeerReviewAsync("AI viết feedback quá thời gian chờ.");
                return null;
            }
            if (!Current()) return null;
            if (!result.Success)
            {
                await PausePeerReviewAsync("AI chưa tạo được feedback: " + result.UserMessage);
                return null;
            }
            validation = PeerReviewCommentContract.Parse(result.Content, page.CommentPrompts);
            if (validation.Comments is { Length: > 0 }) break;
            prompt += "\nThe previous response failed strict validation: " + validation.Detail +
                " Return a corrected JSON object only; keep the exact slot count.";
        }
        if (validation.Comments is not { Length: > 0 } comments)
        {
            await PausePeerReviewAsync("AI trả feedback chưa đúng cấu trúc sau 2 lần: " + validation.Detail);
            return null;
        }

        PeerReviewPageState current = await ProbePeerReviewAsync(expected);
        if (!Current()) return null;
        if (current.ReviewKey != reviewKey || current.ContextFingerprint != context ||
            current.CommentPrompts.Length != comments.Length)
        {
            await PausePeerReviewAsync("Bài review đã đổi trong lúc AI đang viết; chưa điền hoặc gửi nội dung cũ.");
            return null;
        }
        var plan = new PeerReviewCommentPlan(context, comments);
        _peerReviewCommentPlans[reviewKey] = plan;
        return plan;
    }

    private async Task PausePeerReviewAsync(string detail)
    {
        _viewModel.StatusText = "⏸️ Chưa hoàn tất Review Your Peers. " + detail;
        await PauseCourseJobAsync(_viewModel.StatusText,
            _viewModel.StatusText + " Các đánh giá đã gửi được giữ nguyên; không tự gửi lại.",
            "PEER_REVIEW_ACTION_REQUIRED");
    }

    private async Task ContinueSubmittedPeerToReviewsAsync(Uri submissionUri)
    {
        if (!PeerReviewCanAct(submissionUri)) return;
        string? path = PeerReviewAutomation.AssignmentPath(submissionUri);
        if (path is null) return;
        TrackPendingPeerSubmission(path);
        _pendingGradedResultPaths.Add(path);
        _courseHasPendingGradedResults = true;
        // Read the actual review link. Do not click Start new attempt or re-upload an accepted PDF.
        string script = """
            (function(expected) {
                const visible = e => e.getClientRects().length && !e.closest('[hidden],[aria-hidden="true"]');
                const links = Array.from(document.querySelectorAll('a[href]')).filter(visible).map(e => {
                    try { return new URL(e.href, location.href); } catch (_) { return null; }
                }).filter(u => u && u.origin === location.origin &&
                    u.pathname.startsWith(expected + '/') && /\/(give-feedback|reviews?|review-next)(\/|$)/.test(u.pathname.slice(expected.length)));
                return links.length ? links[0].href : '';
            })(__PATH__);
            """;
        string candidate = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(
            script.Replace("__PATH__", JsonSerializer.Serialize(path), StringComparison.Ordinal)));
        Uri? review = Uri.TryCreate(candidate, UriKind.Absolute, out var linked) &&
            PeerReviewAutomation.AssignmentPath(linked) == path && CourseAutomationPolicy.IsPeerReviewPath(linked.AbsolutePath)
            ? linked : PeerReviewAutomation.ReviewHome(submissionUri);
        if (review is null) { await PausePeerReviewAsync("Không tìm thấy đường dẫn review của bài này."); return; }
        _viewModel.StatusText = "➡️ PDF đã nộp; đang mở Review Your Peers, giữ nguyên bài nộp.";
        if (!await NavigatePeerPageAsync(submissionUri, review)) return;
        // Keep the existing peer guard during SPA navigation; handle this destination here.
        await AutoReviewPeersAsync(review);
    }

    private async Task<bool> NavigatePeerPageAsync(Uri expected, Uri destination)
    {
        if (!PeerReviewCanAct(expected)) return false;
        if (MainWebView.Source.AbsoluteUri == destination.AbsoluteUri) MainWebView.Reload();
        else MainWebView.Source = destination;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250);
            if (!PeerReviewCanAct(expected)) return false;
            if (string.Equals(MainWebView.Source.AbsolutePath.TrimEnd('/'), destination.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            { await Task.Delay(500); return PeerReviewCanAct(expected); }
        }
        if (CourseAutomationPolicy.IsPeerReviewPath(destination.AbsolutePath) && PeerReviewCanAct(expected) &&
            !CourseAutomationPolicy.IsPeerReviewPath(MainWebView.Source.AbsolutePath))
        {
            var redirected = await ProbePeerReviewAsync(expected);
            if (redirected.State == "OWN_SUBMISSION")
                return PeerReviewCanAct(expected); // Let the result handler inspect this page; never re-submit.
        }
        await PausePeerReviewAsync("Coursera chưa chuyển đến trang cần xử lý.");
        return false;
    }

    private async Task AutoReviewPeersAsync(Uri peerActivityUri)
    {
        Uri? reviewHome = PeerReviewAutomation.ReviewHome(peerActivityUri);
        if (reviewHome is null) return;
        int? remaining = null, initialBudget = null;
        int sent = 0, idle = 0, dialogWaits = 0;
        bool awaitingSubmission = false, awaitingCounter = false;
        int? beforeSubmit = null;
        var timeout = Stopwatch.StartNew();
        TimeSpan readinessGraceUntil = TimeSpan.FromSeconds(30);
        TimeSpan? formWorkStarted = null;
        while (PeerReviewCanAct(peerActivityUri) && timeout.Elapsed < TimeSpan.FromMinutes(8))
        {
            var page = await ProbePeerReviewAsync(peerActivityUri, budget: remaining);
            if (page.State == "STOPPED") return;
            if (page.State == "OWN_SUBMISSION")
            {
                await InspectOwnSubmissionAfterReviewAsync(peerActivityUri, page.ReviewsCompleted);
                return;
            }
            if (page.State == "STALE_DOCUMENT")
            {
                if (++idle > 30) { await PausePeerReviewAsync($"Trang hiện tại không khớp đường dẫn review ({page.Detail})."); return; }
                await Task.Delay(500);
                continue;
            }
            if (page.Remaining is >= 0)
            {
                remaining = page.Remaining;
                initialBudget ??= remaining;
            }
            if (page.State == "REVIEWS_COMPLETE")
            {
                await FinishPeerReviewsAsync(peerActivityUri);
                return;
            }
            if (page.State == "NO_REVIEWS_AVAILABLE")
            {
                await PausePeerReviewAsync($"Coursera chưa có bài để đánh giá{(remaining.HasValue ? $"; còn thiếu {remaining} bài" : "")}.");
                return;
            }
            if (page.State == "BLOCKING_DIALOG")
            {
                if (++dialogWaits <= 12)
                {
                    _viewModel.StatusText = "⏳ Đang chờ popup trên trang review đóng lại...";
                    await Task.Delay(500);
                    continue;
                }
                await PausePeerReviewAsync("Có popup thực sự đang mở trên trang. " + page.Detail);
                return;
            }
            dialogWaits = 0;
            if (page.State == "ACTION_LIMIT")
            { await PausePeerReviewAsync("Đã tới giới hạn thao tác an toàn của trang review."); return; }
            if (!awaitingSubmission && page.State is ("AMBIGUOUS_FORM" or "UNSUPPORTED_RUBRIC"))
            {
                // Start Reviewing is an SPA transition: rubric groups and Submit
                // can appear in separate renders. A partial first frame is not failure.
                if (timeout.Elapsed < readinessGraceUntil)
                {
                    _viewModel.StatusText = "⏳ Đang chờ biểu mẫu review tải đủ tiêu chí và nút gửi...";
                    await Task.Delay(500);
                    continue;
                }
                await PausePeerReviewAsync($"Biểu mẫu review chưa sẵn sàng sau thời gian chờ ({page.State}). {page.Detail}");
                return;
            }

            if (awaitingSubmission)
            {
                if (page.SubmissionConfirmed || (page.Remaining.HasValue && page.Remaining < beforeSubmit))
                {
                    awaitingSubmission = false;
                    awaitingCounter = true;
                    idle = 0;
                    if (!await NavigatePeerPageAsync(peerActivityUri, reviewHome)) return;
                    readinessGraceUntil = timeout.Elapsed + TimeSpan.FromSeconds(30);
                    formWorkStarted = null;
                    continue;
                }
                if (++idle > 80) { await PausePeerReviewAsync("Đã bấm gửi nhưng chưa thấy xác nhận; dừng để tránh gửi trùng."); return; }
                await Task.Delay(500);
                continue;
            }
            if (awaitingCounter)
            {
                if (page.Remaining.HasValue && page.Remaining < beforeSubmit) { awaitingCounter = false; idle = 0; }
                else
                {
                    if (++idle > 40) { await PausePeerReviewAsync("Review đã gửi nhưng bộ đếm chưa giảm; cần kiểm tra lại trước khi chấm thêm."); return; }
                    await Task.Delay(500);
                    continue;
                }
            }
            if (remaining is null)
            {
                // A direct link to an individual review may omit the top-level remaining counter.
                if (MainWebView.Source.AbsolutePath.TrimEnd('/') != reviewHome.AbsolutePath.TrimEnd('/'))
                { if (!await NavigatePeerPageAsync(peerActivityUri, reviewHome)) return; continue; }
                if (++idle > 30) { await PausePeerReviewAsync("Chưa đọc được dòng Reviews / left to complete, nên chưa tự chấm thêm."); return; }
                await Task.Delay(500);
                continue;
            }
            if (remaining <= 0 || sent >= initialBudget)
            { await PausePeerReviewAsync("Đã tới giới hạn số review cần làm, nhưng chưa xác nhận đủ từ Coursera."); return; }

            if (page.State == "INCOMPLETE_FORM")
            { await PausePeerReviewAsync("Còn trường chưa được xác nhận trong review. " + page.Detail); return; }
            PeerReviewCommentPlan? commentPlan = null;
            if (page.State == "FORM_READY" && page.EmptyCommentCount > 0)
            {
                bool generated = !_peerReviewCommentPlans.TryGetValue(page.ReviewKey, out commentPlan) ||
                    commentPlan.ContextFingerprint != page.ContextFingerprint ||
                    commentPlan.Comments.Length != page.CommentPrompts.Length;
                if (generated)
                {
                    commentPlan = await GeneratePeerReviewCommentsAsync(peerActivityUri, page);
                    if (commentPlan is null) return;
                    // AgentRouter may legitimately take longer than the form's own
                    // stability window. Start that window only after the plan exists.
                    formWorkStarted = timeout.Elapsed;
                }
            }
            if (page.State is "FORM_READY" or "SUBMIT_READY") formWorkStarted ??= timeout.Elapsed;
            if (formWorkStarted.HasValue && timeout.Elapsed - formWorkStarted.Value > TimeSpan.FromSeconds(60))
            { await PausePeerReviewAsync("Form chưa giữ ổn định điểm/nhận xét hoặc nút gửi sau 60 giây; chưa gửi review này."); return; }

            _viewModel.StatusText = $"📝 Review Your Peers: còn {remaining} bài cần đánh giá.";
            string? action = page.State switch
            {
                "START_READY" => "start", "NEXT_READY" => "next",
                "FORM_READY" => "fill", "SUBMIT_READY" => "submit", _ => null
            };
            if (action == "submit")
            {
                if (string.IsNullOrWhiteSpace(page.ReviewKey) || _submittedPeerReviewKeys.Contains(page.ReviewKey))
                { await PausePeerReviewAsync("Không xác định được review mới hoặc review này đã bấm gửi trong lượt chạy."); return; }
                var submitted = await ProbePeerReviewAsync(peerActivityUri, "submit", remaining);
                if (submitted.State == "SUBMIT_CLICKED")
                {
                    _submittedPeerReviewKeys.Add(page.ReviewKey);
                    _peerReviewCommentPlans.Remove(page.ReviewKey);
                    sent++; beforeSubmit = remaining; awaitingSubmission = true; idle = 0;
                    _viewModel.StatusText = $"📤 Đã gửi review; đang chờ Coursera cập nhật số bài còn lại ({remaining}).";
                }
                else if (submitted.State == "INCOMPLETE_FORM")
                { await PausePeerReviewAsync("Còn trường chưa hợp lệ trong review. " + submitted.Detail); return; }
                else if (submitted.State == "FORM_FILLING")
                {
                    idle = 0;
                    _viewModel.StatusText = "⏳ Đang chờ trang bật nút Submit Review...";
                }
                else idle++;
            }
            else if (action is not null)
            {
                var acted = action == "fill" && commentPlan is not null
                    ? await ProbePeerReviewAsync(peerActivityUri, action, remaining,
                        commentPlan.Comments, commentPlan.ContextFingerprint)
                    : await ProbePeerReviewAsync(peerActivityUri, action, remaining);
                if (acted.State == "INCOMPLETE_FORM")
                { await PausePeerReviewAsync("Không điền được đầy đủ review. " + acted.Detail); return; }
                if (acted.State is "AI_COMMENTS_REQUIRED" or "STALE_REVIEW_CONTEXT")
                {
                    if (!string.IsNullOrWhiteSpace(page.ReviewKey)) _peerReviewCommentPlans.Remove(page.ReviewKey);
                    formWorkStarted = null;
                    idle = 0;
                    await Task.Delay(300);
                    continue;
                }
                if (acted.State is "START_CLICKED" or "NEXT_CLICKED")
                {
                    readinessGraceUntil = timeout.Elapsed + TimeSpan.FromSeconds(30);
                    formWorkStarted = null;
                    _viewModel.StatusText = "⏳ Đã mở review; đang chờ biểu mẫu tải và ổn định...";
                }
                else if (acted.State == "FORM_FILLING")
                    _viewModel.StatusText = "⏳ Đang chọn từng tiêu chí và chờ trang lưu điểm/nhận xét...";
                idle = acted.State is "START_CLICKED" or "NEXT_CLICKED" or "FORM_FILLED" or "FORM_FILLING" ? 0 : idle + 1;
            }
            else idle++;
            if (idle > 50) { await PausePeerReviewAsync("Trang review chưa sẵn sàng hoặc không có bước tiếp theo rõ ràng."); return; }
            await Task.Delay(500);
        }
        if (PeerReviewCanAct(peerActivityUri)) await PausePeerReviewAsync("Hết thời gian chờ review; giữ lại tiến độ đã gửi.");
    }

    private static bool IsPeerGradeWaitingState(string state) => state is
        "SUBMITTED_PENDING_GRADE" or "SUBMITTED_CONFIRMATION_PENDING" or "ALREADY_SUBMITTED" or
        "WAIT" or "WAIT_FOR_SUBMISSION_FILE" or "RESULT_READ_FAILED" or "GRADE_REFRESH_CLICKED" or "GRADE_REFRESH_WAIT" or
        "GRADE_REFRESH_UNAVAILABLE" or "GRADE_REFRESH_AMBIGUOUS";

    private bool HasPeerGradeWaitingEvidence(Uri submission, string state) =>
        state is "SUBMITTED_PENDING_GRADE" or "SUBMITTED_CONFIRMATION_PENDING" or "ALREADY_SUBMITTED" ||
        PendingPeerReceiptFor(submission) != null && IsPeerGradeWaitingState(state);

    private async Task WaitForPendingPeerGradeAsync(Uri submission, bool reviewsCompleted,
        int maxPolls = 120, TimeSpan? pollDelay = null, int refreshEvery = 4)
    {
        string? path = PeerReviewAutomation.AssignmentPath(submission);
        if (path is null) return;

        string? expectedJob = _centralWorkerClient.CurrentJob?.Id;
        string? expectedUser = _courseraIdentity?.UserId;
        _peerPdfRunJob = expectedJob;
        _peerPdfRunUser = expectedUser;
        TrackPendingPeerSubmission(path);
        _pendingGradedResultPaths.Add(path);
        _courseHasPendingGradedResults = true;
        TimeSpan delay = pollDelay ?? TimeSpan.FromSeconds(2);
        refreshEvery = Math.Max(1, refreshEvery);

        bool Current() => expectedJob == _centralWorkerClient.CurrentJob?.Id &&
            expectedUser == _courseraIdentity?.UserId && PeerReviewCanAct(submission);

        for (int attempt = 1; attempt <= Math.Max(1, maxPolls) && Current(); attempt++)
        {
            PeerPdfPageState page;
            try
            {
                // No expected filename is supplied here: Coursera deliberately hides
                // it while AI grading is pending. The accepted-submission banner is
                // the guard that prevents any upload or Start-new-attempt action.
                page = await ProbePeerPdfAsync(submission, "inspect", PendingPeerReceiptFor(submission));
            }
            catch (Exception exception) when (exception is TimeoutException or JsonException or InvalidOperationException or
                System.Runtime.InteropServices.COMException)
            {
                if (!Current()) return;
                page = new() { State = "RESULT_READ_FAILED", Detail = exception.Message };
            }

            if (!Current()) return;
            if (page.State == "SUBMITTED_PASSED")
            {
                ClearPendingPeerReceipt(submission);
                ReportExistingPeerSubmission(page, submission);
                if (TryGetCourseHomeUri(submission, out Uri? passedHome) && passedHome != null && Current())
                    MainWebView.Source = passedHome;
                return;
            }
            if (page.State == "SUBMITTED_FAILED")
            {
                ClearPendingPeerReceipt(submission);
                await RevisePeerAfterReviewsAsync(submission, reviewsCompleted);
                return;
            }
            if (page.State == "SUBMITTED_PENDING_REVIEWS")
            {
                if (!reviewsCompleted)
                {
                    await ContinueSubmittedPeerToReviewsAsync(submission);
                    return;
                }
            }
            if (page.State is "STOPPED" or "STALE_DOCUMENT") return;

            _viewModel.StatusText = IsPeerGradeWaitingState(page.State)
                ? $"⏳ Bài đã nộp, đang chờ Coursera AI chấm; tự làm mới để lấy kết quả ({attempt}/{maxPolls})..."
                : $"⏳ Đang chờ trang kết quả bài đã nộp ổn định ({page.State}, {attempt}/{maxPolls})...";

            if (attempt % refreshEvery == 0 && Current())
            {
                bool refreshedByButton = false;
                try
                {
                    PeerPdfPageState refreshed = await ProbePeerPdfAsync(
                        submission, "refreshGrade", PendingPeerReceiptFor(submission));
                    if (!Current()) return;
                    if (refreshed.State == "SUBMITTED_PASSED")
                    {
                        ClearPendingPeerReceipt(submission);
                        ReportExistingPeerSubmission(refreshed, submission);
                        if (TryGetCourseHomeUri(submission, out Uri? refreshedHome) && refreshedHome != null && Current())
                            MainWebView.Source = refreshedHome;
                        return;
                    }
                    if (refreshed.State == "SUBMITTED_FAILED")
                    {
                        ClearPendingPeerReceipt(submission);
                        await RevisePeerAfterReviewsAsync(submission, reviewsCompleted);
                        return;
                    }
                    if (refreshed.State == "SUBMITTED_PENDING_REVIEWS")
                    {
                        if (!reviewsCompleted)
                        {
                            await ContinueSubmittedPeerToReviewsAsync(submission);
                            return;
                        }
                    }
                    refreshedByButton = refreshed.State == "GRADE_REFRESH_CLICKED";
                }
                catch (Exception exception) when (exception is TimeoutException or JsonException or InvalidOperationException or
                    System.Runtime.InteropServices.COMException)
                {
                    // A transient DOM/script failure is handled by the safe reload below.
                }
                if (!refreshedByButton && Current()) ReloadPendingPeerGrade(submission);
            }

            if (delay > TimeSpan.Zero) await Task.Delay(delay);
        }

        if (!Current()) return;
        _viewModel.StatusText =
            "⏳ Coursera vẫn đang chấm bài. Worker giữ phiên, chuyển sang nội dung khác và sẽ tự quay lại kiểm tra.";
        if (TryGetCourseHomeUri(submission, out Uri? home) && home != null)
        {
            int recoveryGeneration = ++_pendingPeerGradeRecoveryGeneration;
            NavigatePendingPeerGrade(home);
            _ = RecoverPendingCourseHomeNavigationAsync(home, path, recoveryGeneration,
                expectedJob, expectedUser);
        }
    }

    private Task<bool> ReopenPendingPeerSubmissionAsync()
    {
        if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported ||
            IsInteractiveBrowseSession || _centralWorkerClient.CurrentJob == null)
            return Task.FromResult(false);

        EnsurePendingPeerSubmissionQueue();
        int candidatesToInspect = _pendingPeerSubmissionQueue.Count;
        for (int index = 0; index < candidatesToInspect; index++)
        {
            string candidate = _pendingPeerSubmissionQueue.Dequeue();
            _queuedPendingPeerSubmissionPaths.Remove(candidate);
            if (!_pendingPeerSubmissionPaths.Contains(candidate)) continue;

            string normalized = candidate.TrimEnd('/');
            Uri validation = new("https://www.coursera.org" + normalized);
            if (!string.Equals(PeerReviewAutomation.AssignmentPath(validation), normalized, StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingPeerSubmission(candidate);
                continue;
            }

            // Put a still-pending assignment at the tail before navigating. A
            // later course sweep therefore checks B after A instead of always
            // reopening the HashSet's first path.
            TrackPendingPeerSubmission(normalized);

            string origin = IsCourseraUri(MainWebView.Source)
                ? MainWebView.Source.GetLeftPart(UriPartial.Authority)
                : "https://www.coursera.org";
            Uri destination = new(origin + normalized + "/submit");
            _viewModel.StatusText = "⏳ Các bài khác đã xong; đang quay lại bài Peer-graded chờ AI chấm...";
            int recoveryGeneration = ++_pendingPeerGradeRecoveryGeneration;
            NavigatePendingPeerGrade(destination);
            _ = RecoverPendingPeerGradeNavigationAsync(destination, normalized, recoveryGeneration,
                _centralWorkerClient.CurrentJob?.Id, _courseraIdentity?.UserId);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    private async Task RecoverPendingPeerGradeNavigationAsync(Uri destination, string canonicalPath,
        int generation, string? expectedJob, string? expectedUser)
    {
        bool Current() => generation == _pendingPeerGradeRecoveryGeneration && !_workerClosing &&
            !_coursePauseInProgress && !_courseJobCompletionReported && !IsInteractiveBrowseSession &&
            expectedJob == _centralWorkerClient.CurrentJob?.Id && expectedUser == _courseraIdentity?.UserId &&
            _pendingPeerSubmissionPaths.Contains(canonicalPath);
        try
        {
            while (Current())
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                if (!Current()) return;

                _viewModel.StatusText =
                    "⏳ Chưa mở lại được bài đang chờ chấm; Worker vẫn giữ phiên và đang thử lại...";
                NavigatePendingPeerGrade(destination);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or
            System.Runtime.InteropServices.COMException)
        { /* The Worker/window changed while this fire-and-forget recovery was yielding. */ }
    }

    private async Task RecoverPendingCourseHomeNavigationAsync(Uri destination, string canonicalPath,
        int generation, string? expectedJob, string? expectedUser)
    {
        bool Current() => generation == _pendingPeerGradeRecoveryGeneration && !_workerClosing &&
            !_coursePauseInProgress && !_courseJobCompletionReported && !IsInteractiveBrowseSession &&
            expectedJob == _centralWorkerClient.CurrentJob?.Id && expectedUser == _courseraIdentity?.UserId &&
            _pendingPeerSubmissionPaths.Contains(canonicalPath);
        try
        {
            while (Current())
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                if (!Current()) return;
                _viewModel.StatusText =
                    "⏳ Chưa quay lại được danh sách khóa học; Worker giữ phiên và đang thử lại...";
                NavigatePendingPeerGrade(destination);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or
            System.Runtime.InteropServices.COMException)
        { /* The Worker/window changed while this fire-and-forget recovery was yielding. */ }
    }

    private async Task InspectOwnSubmissionAfterReviewAsync(Uri expected, bool reviewsCompleted)
    {
        Uri submission = MainWebView.Source;
        string? user = _courseraIdentity?.UserId, job = _centralWorkerClient.CurrentJob?.Id;
        bool Current() => PeerReviewCanAct(expected) && MainWebView.Source == submission &&
            user == _courseraIdentity?.UserId && job == _centralWorkerClient.CurrentJob?.Id;
        PeerPdfPageState page = new();
        try
        {
            for (int pass = 0; pass < 12 && Current(); pass++)
            {
                // Read-only result inspection. Do not open an editor, upload, change
                // grading mode, start another attempt, or fill a peer review here.
                string raw = await MainWebView.ExecuteScriptAsync(PeerPdfAutomation.BuildProbe(submission, "inspect"))
                    .WaitAsync(TimeSpan.FromSeconds(3));
                if (!Current()) return;
                page = JsonSerializer.Deserialize<PeerPdfPageState>(DecodeWebViewString(raw)) ?? new();
                if (PeerPdfWasSubmitted(page.State)) break;
                await Task.Delay(500);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or JsonException or InvalidOperationException)
        {
            if (!Current()) return;
            page = new() { State = "RESULT_READ_FAILED" };
        }
        if (!Current()) return;
        string? path = PeerReviewAutomation.AssignmentPath(submission);
        if (reviewsCompleted && path != null)
            foreach (string suffix in new[] { "review", "reviews", "review-next", "give-feedback" })
                _verifiedPassedPeerPaths.Add(path + "/" + suffix);
        string reviewStatus = reviewsCompleted ? "Review Your Peers đã hoàn thành. " : "Đã chuyển về My submission. ";
        if (page.State == "SUBMITTED_FAILED")
        {
            await RevisePeerAfterReviewsAsync(submission, reviewsCompleted);
            return;
        }
        if (reviewsCompleted && page.State == "SUBMITTED_PASSED")
        {
            ReportExistingPeerSubmission(page, submission);
            if (TryGetCourseHomeUri(submission, out Uri? home) && home != null) MainWebView.Source = home;
            return;
        }
        if (reviewsCompleted || IsPeerGradeWaitingState(page.State))
        {
            _viewModel.StatusText = "⏳ " + reviewStatus + "Đang chờ AI chấm và tự làm mới kết quả...";
            await WaitForPendingPeerGradeAsync(submission, reviewsCompleted);
            return;
        }

        // A redirect before required reviews are complete remains a real blocker;
        // do not infer completion or submit anything from an unknown page.
        await PauseCourseJobAsync("⏸️ " + reviewStatus + "Cần kiểm tra trạng thái bài nộp.",
            $"Trạng thái: {page.State}. Không tự tạo lần nộp mới hoặc gửi thêm review.",
            "PEER_REVIEW_ACTION_REQUIRED");
    }

    private async Task FinishPeerReviewsAsync(Uri current)
    {
        string? path = PeerReviewAutomation.AssignmentPath(current);
        if (path is null || !PeerReviewCanAct(current)) return;
        foreach (string suffix in new[] { "review", "reviews", "review-next", "give-feedback" })
            _verifiedPassedPeerPaths.Add(path + "/" + suffix);
        _viewModel.StatusText = "✅ Đủ số bài review Coursera yêu cầu. Đang kiểm tra điểm bài nộp...";
        var submission = new Uri(current.GetLeftPart(UriPartial.Authority) + path);
        if (!await NavigatePeerPageAsync(current, submission)) return;
        _peerPdfRunJob = _centralWorkerClient.CurrentJob?.Id;
        _peerPdfRunUser = _courseraIdentity?.UserId;
        PeerPdfPageState page = new();
        for (int attempt = 0; attempt < 24 && PeerReviewCanAct(current); attempt++)
        {
            page = await ProbePeerPdfAsync(submission, "open");
            if (page.State is "SUBMITTED_PASSED" or "SUBMITTED_FAILED") break;
            await Task.Delay(500);
        }
        if (!PeerReviewCanAct(current)) return;
        if (page.State == "SUBMITTED_FAILED")
        {
            await RevisePeerAfterReviewsAsync(submission, true);
            return;
        }
        if (page.State == "SUBMITTED_PASSED") ReportExistingPeerSubmission(page, submission);
        else
        {
            await WaitForPendingPeerGradeAsync(submission, true);
            return;
        }
        if (TryGetCourseHomeUri(submission, out Uri? home) && home != null)
        {
            _viewModel.StatusText = page.State == "SUBMITTED_PASSED"
                ? "✅ Review đã đủ, bài nộp đã đạt. Đang tiếp tục khóa..."
                : "⏳ Review đã đủ; bài nộp còn chờ điểm. Đang xử lý nội dung khác...";
            MainWebView.Source = home;
        }
    }
}
