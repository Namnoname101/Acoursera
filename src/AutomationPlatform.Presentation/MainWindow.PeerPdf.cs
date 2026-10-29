using System.IO;
using System.Text.Json;
using AutomationPlatform.Presentation.Services;
using Microsoft.Web.WebView2.Core;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private string _peerPdfActionReason = "Kiểm tra bài nộp trong My submission rồi chạy lại khóa.";
    private string _peerPdfLastState = "START";
    private string? _peerPdfRunJob;
    private string? _peerPdfRunUser;
    private sealed record PendingPeerReceipt(string FileName, string? JobId, string? UserId);
    private readonly Dictionary<string, PendingPeerReceipt> _pendingPeerReceipts =
        new(StringComparer.OrdinalIgnoreCase);

    private void RememberPendingPeerReceipt(Uri peerUri, string fileName)
    {
        string? path = PeerReviewAutomation.AssignmentPath(peerUri);
        if (path != null)
            _pendingPeerReceipts[path] = new(fileName, _centralWorkerClient.CurrentJob?.Id, _courseraIdentity?.UserId);
    }

    private string? PendingPeerReceiptFor(Uri peerUri)
    {
        string? path = PeerReviewAutomation.AssignmentPath(peerUri);
        return path != null && _pendingPeerReceipts.TryGetValue(path, out PendingPeerReceipt? receipt) &&
            receipt.JobId == _centralWorkerClient.CurrentJob?.Id && receipt.UserId == _courseraIdentity?.UserId
                ? receipt.FileName
                : null;
    }

    private void ClearPendingPeerReceipt(Uri peerUri)
    {
        string? path = PeerReviewAutomation.AssignmentPath(peerUri);
        if (path != null) _pendingPeerReceipts.Remove(path);
    }

    private async Task<PeerPdfPageState> ProbePeerPdfAsync(Uri peerUri, string action = "inspect", string? fileName = null,
        PeerPdfSubmissionMetadata? metadata = null)
    {
        if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported) return new() { State = "STOPPED" };
        if (_peerPdfRunJob != _centralWorkerClient.CurrentJob?.Id || _peerPdfRunUser != _courseraIdentity?.UserId)
            return new() { State = "STOPPED" };
        if (!IsSamePeerAssignmentActivity(peerUri)) return new() { State = "STALE_DOCUMENT" };
        string raw = await MainWebView.ExecuteScriptAsync(PeerPdfAutomation.BuildProbe(peerUri, action, fileName, metadata))
            .WaitAsync(TimeSpan.FromSeconds(10));
        if (!PeerPdfJobCanAct(peerUri)) return new() { State = "STOPPED" };
        PeerPdfPageState page = JsonSerializer.Deserialize<PeerPdfPageState>(DecodeWebViewString(raw)) ?? new();
        if (PeerPdfRequiresExactFileReceipt(page.State) && !string.IsNullOrEmpty(fileName))
        {
            // An old failed result can remain mounted during submission/navigation. It is
            // not acceptance of the new PDF and must never unlock a further revision.
            var receipt = await ProbePeerPdfRevisionAsync(peerUri);
            if (!PeerPdfRevisionAutomation.IsReceiptForFile(receipt, fileName))
                page = new() { State = "WAIT_FOR_SUBMISSION_FILE", Detail = "Đang chờ kết quả gắn với đúng PDF vừa tải lên." };
        }
        _peerPdfLastState = page.State;
        return page;
    }

    private static bool PeerPdfWasSubmitted(string state) =>
        state == "ALREADY_SUBMITTED" || state.StartsWith("SUBMITTED_", StringComparison.Ordinal);

    private static bool PeerPdfRequiresExactFileReceipt(string state) =>
        PeerPdfWasSubmitted(state) &&
        state is not ("SUBMITTED_PENDING_GRADE" or "SUBMITTED_PENDING_REVIEWS" or "SUBMITTED_CONFIRMATION_PENDING");

    private bool ReportExistingPeerSubmission(PeerPdfPageState page, Uri peerUri, PeerAiPdfCache? aiCache = null)
    {
        try { aiCache?.ConfirmSubmission(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { /* A pending/corrupt entry still prevents an uncertain retry if journal confirmation fails. */ }
        _viewModel.StatusText = page.State switch
        {
            "SUBMITTED_PASSED" => "✅ Bài Peer-graded đã nộp và đạt yêu cầu.",
            "SUBMITTED_PENDING_REVIEWS" => "➡️ Bài đã nộp; đang chuyển sang hoàn tất số review Coursera yêu cầu.",
            "SUBMITTED_PENDING_GRADE" => "⏳ Bài đã nộp; đang chờ Coursera AI chấm điểm.",
            "SUBMITTED_CONFIRMATION_PENDING" => "⏳ Đã bấm nộp PDF; đang tự kiểm tra xác nhận và chờ chấm.",
            "SUBMITTED_FAILED" => "⏸️ Bài đã có kết quả chưa đạt. " + page.Detail,
            _ => "⏳ Bài đã nộp; đang chờ kết quả/xác nhận đạt từ Coursera."
        };
        _peerPdfActionReason = page.State == "SUBMITTED_PENDING_REVIEWS"
            ? "PDF đã nộp được giữ nguyên; tiếp tục Review Your Peers theo bộ đếm của Coursera."
            : "Mở My submission để kiểm tra kết quả. Chỉ tạo lần nộp mới khi bạn muốn sửa bài.";
        string? canonicalPath = PeerReviewAutomation.AssignmentPath(peerUri);
        if (page.State is "SUBMITTED_PASSED" or "SUBMITTED_FAILED") ClearPendingPeerReceipt(peerUri);
        if (page.State != "SUBMITTED_PASSED") return false;
        if (canonicalPath != null)
        {
            ClearPendingPeerSubmission(canonicalPath);
            _pendingGradedResultPaths.Remove(canonicalPath);
            _courseHasPendingGradedResults = _pendingGradedResultPaths.Count > 0;
        }
        string[] path = peerUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int peerIndex = Array.FindIndex(path, segment => segment.Equals("peer", StringComparison.OrdinalIgnoreCase));
        if (peerIndex >= 0) _verifiedPassedPeerPaths.Add("/" + string.Join('/', path.Take(peerIndex + 3)));
        return true;
    }

    private IEnumerable<string> PeerPdfLibraryRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("ACOSE_PREPARED_PDF_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) yield return Path.GetFullPath(configured);
        string engineRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? installRoot = Path.GetDirectoryName(engineRoot);
        if (installRoot != null && File.Exists(Path.Combine(installRoot, ".acose-customer-manager-install")))
            yield return Path.Combine(installRoot, "PreparedPdfs");
        yield return Path.Combine(engineRoot, "PreparedPdfs");
        yield return Path.Combine(GetDownloadsDirectory(), "Acoursera PDFs");
    }

    private async Task<bool> HandlePreparedPeerPdfAsync(Uri peerUri)
    {
        _peerPdfActionReason = "Kiểm tra My submission rồi chạy lại khóa.";
        _peerPdfLastState = "START";
        _peerPdfRunJob = _centralWorkerClient.CurrentJob?.Id;
        _peerPdfRunUser = _courseraIdentity?.UserId;
        var interruptedRevision = await ProbePeerPdfRevisionAsync(peerUri);
        if (interruptedRevision.State is "ATTEMPT_CONFIRM_READY" or "ATTEMPT_CONFIRM_WAIT")
            return await ReviseFailedPeerPdfAsync(peerUri);
        if ((interruptedRevision.State is "FORM_READY" or "FORM_RETAINS_FILE") &&
            (Environment.GetEnvironmentVariable("ACOSE_PEER_PDF_MODE") ?? "ai").Trim().Equals("ai", StringComparison.OrdinalIgnoreCase))
        {
            // A Worker may have stopped immediately after Continue. Route an
            // already-open retry form back to the saved revision only when its
            // durable Start-new-attempt intent exists for this account/assignment.
            bool resumeRevision;
            using (PeerAiPdfSession? pendingSession = OpenPeerAiPdfSession(peerUri))
            {
                PeerAiPdfEntry? pending = pendingSession?.Cache.Load();
                resumeRevision = pending is { State: "ready", RevisionCount: > 0, AttemptStartPending: true };
            }
            if (resumeRevision) return await ReviseFailedPeerPdfAsync(peerUri);
        }
        await DismissAnyGlobalPopupsAsync();
        PeerPdfPageState page = new();
        for (int attempt = 1; attempt <= 24; attempt++)
        {
            page = await ProbePeerPdfAsync(peerUri, "open");
            if (page.State == "SUBMITTED_FAILED") return await ReviseFailedPeerPdfAsync(peerUri);
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri);
            if (page.State is "FILE_INPUT_READY" or "ADD_FILE_READY" or "UPLOAD_DIALOG_READY" or "EXISTING_UPLOAD") break;
            if (page.State is "STOPPED" or "STALE_DOCUMENT" or "AMBIGUOUS_UPLOAD" or "NO_PDF_FORM") break;
            _viewModel.StatusText = $"⏳ Đang mở My submission ({attempt}/24)...";
            await Task.Delay(500);
        }
        if (page.State is not ("FILE_INPUT_READY" or "ADD_FILE_READY" or "UPLOAD_DIALOG_READY" or "EXISTING_UPLOAD"))
        {
            _viewModel.StatusText = $"⏸️ Chưa xác định được một ô nộp PDF ({page.State}).";
            return false;
        }

        string mode = (Environment.GetEnvironmentVariable("ACOSE_PEER_PDF_MODE") ?? "ai").Trim().ToLowerInvariant();
        if (mode is not ("ai" or "prepared"))
        {
            _viewModel.StatusText = "⏸️ Cấu hình xử lý PDF không hợp lệ.";
            _peerPdfActionReason = "ACOSE_PEER_PDF_MODE chỉ nhận ai hoặc prepared.";
            return false;
        }
        using PeerAiPdfSession? aiSession = mode == "ai" ? OpenPeerAiPdfSession(peerUri) : null;
        string? pdfPath;
        if (mode == "ai")
        {
            if (aiSession == null) return false;
            pdfPath = await ResolveAiPeerPdfAsync(peerUri, aiSession.Cache);
            if (pdfPath == null) return false;
        }
        else
        {
            PreparedPdfResolution pdf = PreparedPdfLibrary.Find(page.Title, peerUri, PeerPdfLibraryRoots());
            pdfPath = pdf.Path;
            if (pdfPath == null)
            {
                string name = GetPeerAssignmentSlug(peerUri) ?? page.Title;
                _viewModel.StatusText = pdf.Status == "AMBIGUOUS"
                    ? $"⏸️ Có nhiều PDF cùng khớp bài {name}; cần chọn lại file trong kho."
                    : $"⏸️ Chưa có PDF soạn sẵn cho bài {name}.";
                _peerPdfActionReason = $"Thư mục: {pdf.ExpectedDirectory}. Đặt một PDF tên {name}.pdf (hoặc đúng tên bài), rồi chạy lại khóa.";
                return false;
            }
        }
        bool passed = await UploadAndSubmitPeerPdfAsync(peerUri, pdfPath, aiSession?.Cache);
        if (!passed && _peerPdfLastState == "SUBMITTED_FAILED" && aiSession != null)
        {
            aiSession.Dispose(); // Release the initial transaction before acquiring the revision lease.
            return await ReviseFailedPeerPdfAsync(peerUri);
        }
        return passed;
    }

    // Shared by initial submission and a journaled revision; neither path may bypass upload verification.
    private async Task<bool> UploadAndSubmitPeerPdfAsync(Uri peerUri, string pdfPath, PeerAiPdfCache? aiCache)
    {
        if (!PeerPdfJobCanAct(peerUri)) return false;
        PeerPdfPageState page = new();
        string fileName = Path.GetFileName(pdfPath);
        PeerPdfSubmissionMetadata? metadata = aiCache?.Load() is { } entry
            ? PeerPdfSubmissionMetadata.FromDraft(entry.Draft)
            : null;
        await ProbePeerPdfAsync(peerUri, "prepareTitle", metadata: metadata);
        _viewModel.StatusText = $"📤 Đang tải lên {fileName}...";
        PeerPdfPageState beforeUpload = await ProbePeerPdfAsync(peerUri, "inspect", fileName);
        if (PeerPdfWasSubmitted(beforeUpload.State)) return ReportExistingPeerSubmission(beforeUpload, peerUri, aiCache);
        if (beforeUpload.State == "STALE_DOCUMENT") return false;
        if (beforeUpload.State == "DIFFERENT_UPLOAD")
        {
            _viewModel.StatusText = "⏸️ My submission đang có một PDF khác; không tự ghi đè hoặc nộp nhầm bản.";
            _peerPdfActionReason = "Kiểm tra tệp đang đính kèm trước khi chạy lại. PDF vừa tạo vẫn được giữ trong GeneratedPdfs.";
            return false;
        }
        if (beforeUpload.State is not ("UPLOADED" or "UPLOADING" or "FILE_SELECTED"))
        {
            if (!await AttachPreparedPeerPdfAsync(peerUri, pdfPath, beforeUpload.State))
            {
                _viewModel.StatusText = $"⏸️ Chưa gắn được PDF vào Upload File ({_peerPdfLastState}).";
                return false;
            }
        }

        bool uploaded = false;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            page = await ProbePeerPdfAsync(peerUri, "verifyUpload", fileName);
            if (page.State == "UPLOADED") { uploaded = true; break; }
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri, aiCache);
            if (page.State is "STOPPED" or "UPLOAD_FAILED" or "STALE_DOCUMENT") break;
            _viewModel.StatusText = $"⏳ Đang chờ Coursera nhận {fileName} ({attempt + 1}/120)...";
            await Task.Delay(500);
        }
        if (!uploaded)
        {
            _viewModel.StatusText = $"⏸️ Chưa xác nhận upload PDF ({page.State}). {page.Detail}";
            return false;
        }

        await ProbePeerPdfAsync(peerUri, "prepareTitle", metadata: metadata);
        if (!IsSamePeerAssignmentActivity(peerUri)) return false;
        _viewModel.StatusText = "📤 PDF đã tải lên. Đang nộp bài...";
        string submit = "WAIT";
        for (int attempt = 0; attempt < 48; attempt++)
        {
            // Separate readiness from the only submit gesture for both AI and
            // prepared-PDF modes. This gives an exact point at which a lost
            // WebView response must be treated as an uncertain accepted click.
            page = await ProbePeerPdfAsync(peerUri, "prepareSubmit", fileName);
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri, aiCache);
            if (page.State == "SUBMIT_READY")
            {
                if (!PeerPdfJobCanAct(peerUri)) return false;
                aiCache?.BeginSubmission(); // Durable intent BEFORE the sole submit click.
                RememberPendingPeerReceipt(peerUri, fileName);
                _peerPdfLastState = "SUBMITTED_CONFIRMATION_PENDING";
                page = await ProbePeerPdfAsync(peerUri, "submit", fileName);
                submit = page.State;
                break; // Never automatically click a second time after an uncertain result.
            }
            submit = page.State;
            if (submit is "SUBMIT_CLICKED" or "SUBMIT_AMBIGUOUS" or "AMBIGUOUS_UPLOAD" or "UPLOAD_FAILED" or "STALE_DOCUMENT" or "STOPPED") break;
            await Task.Delay(250);
        }
        if (submit != "SUBMIT_CLICKED")
        {
            page = await ProbePeerPdfAsync(peerUri, fileName: fileName);
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri, aiCache);
            _viewModel.StatusText = $"⏸️ PDF đã tải lên nhưng chưa nộp được ({submit}).";
            return false;
        }
        // A click alone is not acceptance. Coursera may submit directly or show one confirmation dialog.
        bool confirmationClicked = false;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            page = await ProbePeerPdfAsync(peerUri, fileName: fileName);
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri, aiCache);
            if (page.State is "STALE_DOCUMENT" or "STOPPED") break;
            if (!confirmationClicked)
            {
                if (!IsSamePeerAssignmentActivity(peerUri)) return false;
                string confirmation = await ConfirmOwnedCourseraSubmissionAsync();
                confirmationClicked = confirmation == "CLICKED";
            }
            await Task.Delay(500);
        }
        // The submit click is journaled and must never be repeated just because
        // Coursera's result banner is late. Keep the Worker alive and let the
        // guarded pending-grade loop refresh this same submission.
        _peerPdfLastState = "SUBMITTED_CONFIRMATION_PENDING";
        _viewModel.StatusText = "⏳ Đã bấm nộp PDF; đang chờ Coursera xác nhận và chấm điểm.";
        _peerPdfActionReason = "Worker sẽ tự làm mới đúng bài nộp; không tạo lần nộp trùng.";
        return false;
    }

    private bool PeerPdfJobCanAct(Uri peerUri) =>
        !_workerClosing && !_coursePauseInProgress && !_courseJobCompletionReported && !IsInteractiveBrowseSession &&
        _peerPdfRunJob == _centralWorkerClient.CurrentJob?.Id && _peerPdfRunUser == _courseraIdentity?.UserId &&
        IsSamePeerAssignmentActivity(peerUri);

    private async Task<bool> SetPeerPdfInputAsync(Uri peerUri, string filePath, int? backendNodeId = null)
    {
        if (!PeerPdfJobCanAct(peerUri)) return false;
        var core = MainWebView.CoreWebView2;
        int? nodeId = null;
        if (backendNodeId == null)
        {
            using var document = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}"));
            int root = document.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            using var target = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",
                JsonSerializer.Serialize(new { nodeId = root, selector = "input[type=file][data-acose-peer-upload=true]" })));
            nodeId = target.RootElement.GetProperty("nodeId").GetInt32();
            if (nodeId <= 0) return false;
        }
        if (!PeerPdfJobCanAct(peerUri)) return false;
        var arguments = new Dictionary<string, object> { ["files"] = new[] { Path.GetFullPath(filePath) } };
        if (backendNodeId.HasValue) arguments["backendNodeId"] = backendNodeId.Value;
        else arguments["nodeId"] = nodeId!.Value;
        await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(arguments));
        return true;
    }

    private async Task<PeerPdfPageState> ExecutePeerPdfGestureAsync(Uri peerUri, string action, string fileName)
    {
        if (!PeerPdfJobCanAct(peerUri)) return new() { State = "STOPPED" };
        using var result = JsonDocument.Parse(await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = PeerPdfAutomation.BuildProbe(peerUri, action, fileName), userGesture = true, returnByValue = true })));
        PeerPdfPageState page = result.RootElement.GetProperty("result").TryGetProperty("value", out var value)
            ? JsonSerializer.Deserialize<PeerPdfPageState>(value.GetString() ?? "{}") ?? new()
            : new() { State = "UPLOAD_SCRIPT_FAILED" };
        _peerPdfLastState = page.State;
        return page;
    }

    private async Task<bool> AttachPreparedPeerPdfAsync(Uri peerUri, string filePath, string state)
    {
        if (!PeerPdfJobCanAct(peerUri)) return false;
        if (state == "FILE_INPUT_READY") return await SetPeerPdfInputAsync(peerUri, filePath);
        if (state is not ("ADD_FILE_READY" or "UPLOAD_DIALOG_READY")) return false;
        var core = MainWebView.CoreWebView2;
        string fileName = Path.GetFileName(filePath);
        var receiver = core.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened");
        var chosenNode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChooser(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try
            {
                using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (data.RootElement.TryGetProperty("backendNodeId", out var node) && node.TryGetInt32(out int id) && id > 0)
                    chosenNode.TrySetResult(id);
            }
            catch (JsonException) { chosenNode.TrySetResult(0); }
        }
        receiver.DevToolsProtocolEventReceived += OnChooser;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}");
            bool openedAddFile = false, clickedBrowse = false;
            for (int attempt = 0; attempt < 80 && PeerPdfJobCanAct(peerUri); attempt++)
            {
                if (chosenNode.Task.IsCompleted)
                {
                    int node = await chosenNode.Task;
                    return node > 0 && await SetPeerPdfInputAsync(peerUri, filePath, node);
                }
                if (state == "FILE_INPUT_READY") return await SetPeerPdfInputAsync(peerUri, filePath);
                if (state is "UPLOADING" or "FILE_SELECTED" or "UPLOADED") return true;
                if (state is "STOPPED" or "STALE_DOCUMENT" or "AMBIGUOUS_UPLOAD" or "BLOCKING_DIALOG" or "UPLOAD_FAILED" or "UPLOAD_SCRIPT_FAILED") return false;
                if (state == "ADD_FILE_READY" && !openedAddFile)
                {
                    openedAddFile = true;
                    state = (await ExecutePeerPdfGestureAsync(peerUri, "upload", fileName)).State;
                    continue;
                }
                if (state == "UPLOAD_DIALOG_READY" && !clickedBrowse)
                {
                    clickedBrowse = true;
                    state = (await ExecutePeerPdfGestureAsync(peerUri, "chooseFile", fileName)).State;
                    continue;
                }
                _viewModel.StatusText = $"📂 Đang chọn {fileName} trong Upload File/browse files...";
                await Task.Delay(250);
                state = (await ProbePeerPdfAsync(peerUri, "inspect", fileName)).State;
            }
            return false;
        }
        finally
        {
            receiver.DevToolsProtocolEventReceived -= OnChooser;
            try { await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":false}"); }
            catch { /* The page may have closed while uploading. */ }
        }
    }
}
