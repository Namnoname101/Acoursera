using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private sealed record DiscussionProbe(string State, bool HasDraft = false, string Title = "", string Prompt = "");
    private string? _discussionRunUser;
    private Uri? _discussionReplyUri;
    private string? _discussionReplyUser;
    private DiscussionReplyDraft? _discussionReplyDraft;
    private bool _discussionHandlerRequested;
    private Uri? _discussionRecoveryUri;
    private string? _discussionRecoveryJob;
    private DispatcherTimer? _discussionRecoveryTimer;
    private bool _discussionRecoveryChecking;
    private readonly HashSet<string> _discussionBacktrackedPaths = new(StringComparer.Ordinal);

    private bool IsCurrentDiscussion(Uri uri, string? jobId) => !_workerClosing && !_coursePauseInProgress &&
        !_courseJobCompletionReported && !IsInteractiveBrowseSession &&
        !string.IsNullOrWhiteSpace(_discussionRunUser) && _discussionRunUser == _courseraIdentity?.UserId &&
        string.Equals(_centralWorkerClient.CurrentJob?.Id, jobId, StringComparison.Ordinal) &&
        IsSameCourseraDocument(uri) && uri.AbsolutePath.Contains("/discussionPrompt/", StringComparison.OrdinalIgnoreCase);

    private string DiscussionAccountDirectory()
    {
        string profile = _workerLaunchOptions.Enabled ? _workerLaunchOptions.ProfilePath : MainWebView.CoreWebView2.Environment.UserDataFolder;
        string identity = _courseraIdentity?.UserId ?? throw new InvalidOperationException("Chưa xác minh được tài khoản Coursera.");
        return Path.Combine(profile, "AcoseDiscussionAccounts",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    private DiscussionSubmissionJournal DiscussionJournal() => new(DiscussionAccountDirectory());

    private string OwnedDiscussionReply(Uri uri) => _discussionReplyUri?.AbsoluteUri == uri.AbsoluteUri &&
        _discussionReplyUser == _courseraIdentity?.UserId ? _discussionReplyDraft?.Reply ?? "" : "";

    private async Task<DiscussionProbe> ProbeDiscussionAsync(Uri uri, string action = "inspect", DiscussionProbe? expectedContext = null)
    {
        string raw = await MainWebView.ExecuteScriptAsync(DiscussionPageAutomation.BuildProbe(uri, OwnedDiscussionReply(uri), action,
                expectedContext?.Title, expectedContext?.Prompt))
            .WaitAsync(TimeSpan.FromSeconds(10));
        // Null/invalid JS is a retryable read failure, never proof that a response was submitted.
        return JsonSerializer.Deserialize<DiscussionProbe>(DecodeWebViewString(raw)) ?? new("NOT_READY");
    }

    private async Task HandleDiscussionAsync()
    {
        _discussionHandlerRequested = true;
        if (_isHandlingDiscussion) return;
        _isHandlingDiscussion = true;
        try
        {
            while (_discussionHandlerRequested)
            {
                _discussionHandlerRequested = false;
                Uri? uri = MainWebView.Source;
                string? jobId = _centralWorkerClient.CurrentJob?.Id;
                _discussionRunUser = _courseraIdentity?.UserId;
                if (uri == null || !IsCurrentDiscussion(uri, jobId)) return;
                if (_discussionRecoveryUri != null && IsSameCourseraDocument(_discussionRecoveryUri)) return;
                ClearDiscussionRecovery();
                try { await ProcessDiscussionAsync(uri, jobId); }
                catch (Exception exception)
                {
                    if (IsCurrentDiscussion(uri, jobId)) HoldDiscussionForRecovery(uri, jobId,
                        exception is IOException or InvalidDataException or TimeoutException
                            ? "Chưa hoàn tất phản hồi thảo luận: " + exception.Message
                            : "Chưa đọc được trạng thái bài thảo luận. Giữ phiên để kiểm tra lại; chưa xác nhận gửi thành công.");
                }
            }
        }
        finally { _isHandlingDiscussion = false; }
    }

    private async Task ProcessDiscussionAsync(Uri uri, string? jobId)
    {
        const int maxRetries = 3;
        var journal = DiscussionJournal();
        var store = new DiscussionReplyStore(DiscussionAccountDirectory());
        using FileStream? lease = store.TryAcquireLease(uri);
        if (lease == null)
        {
            HoldDiscussionForRecovery(uri, jobId, "Một Worker khác đang xử lý phản hồi cho cùng bài và tài khoản. Chờ Worker đó hoàn tất.");
            return;
        }
        _discussionReplyUri = uri;
        _discussionReplyUser = _discussionRunUser;
        _discussionReplyDraft = store.Load(uri);
        bool isOptional = await IsOptionalDiscussionAsync();
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (!IsCurrentDiscussion(uri, jobId)) return;
            _viewModel.StatusText = isOptional
                ? $"📝 Bài thảo luận tùy chọn; đang đăng phản hồi (lần {attempt}/{maxRetries})..."
                : $"📝 Đang đăng phản hồi thảo luận (lần {attempt}/{maxRetries})...";
            await DismissAnyGlobalPopupsAsync();
            if (!IsCurrentDiscussion(uri, jobId)) return;
            if (!await TryProcessDiscussionAttemptAsync(uri, jobId, journal, store)) return;
            if (!IsCurrentDiscussion(uri, jobId)) return;
            if (attempt < maxRetries)
            {
                _viewModel.StatusText = $"⚠️ Chưa đăng được phản hồi; đang tải lại để thử lại ({attempt}/{maxRetries})...";
                MainWebView.CoreWebView2.Reload();
                await Task.Delay(4000);
                // The next pass dismisses known popups and checks confirmation
                // before focusing an editor or trying to submit anything.
            }
        }

        if (!IsCurrentDiscussion(uri, jobId)) return;
        if (_discussionBacktrackedPaths.Add((jobId ?? "interactive") + "|" + uri.AbsolutePath))
        {
            _viewModel.StatusText = "⚠️ Đã thử 3 lần không thành công; đang quay lại bài trước...";
            await ReturnToPreviousDiscussionItemAsync(uri, jobId);
            if (!IsCurrentDiscussion(uri, jobId)) return;
        }
        HoldDiscussionForRecovery(uri, jobId,
            "Bài thảo luận vẫn chưa sẵn sàng sau 3 lần thử. Giữ phiên để kiểm tra lại, tránh lặp giữa hai bài.");
    }

    // True permits another attempt only when no draft or submission can be lost.
    private async Task<bool> TryProcessDiscussionAttemptAsync(
        Uri uri, string? jobId, DiscussionSubmissionJournal journal, DiscussionReplyStore store)
    {
        bool pending = journal.GetState(uri) != DiscussionSubmissionState.None;
        bool inserted = false;
        bool sawDraft = false;
        string lastState = "NOT_READY";
        // The loop waits for actual readiness rather than relying on one fixed 3-second delay.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (!IsCurrentDiscussion(uri, jobId)) return false;
            DiscussionProbe probe;
            try { probe = await ProbeDiscussionAsync(uri); }
            catch { probe = new("NOT_READY", sawDraft); }
            if (!IsCurrentDiscussion(uri, jobId)) return false;
            lastState = probe.State;
            sawDraft |= probe.HasDraft;
            if (probe.State == "STALE") return false;
            if (probe.State == "CONFIRMED")
            {
                journal.MarkConfirmed(uri);
                await AdvanceConfirmedDiscussionAsync(uri, jobId);
                return false;
            }
            if (pending)
            {
                _viewModel.StatusText = "⏳ Đang kiểm tra phản hồi đã gửi; chưa gửi lại để tránh trùng bài...";
            }
            else if (probe.State is "EXISTING_DRAFT" or "AMBIGUOUS" or "ACCESS_REQUIRED")
            {
                HoldDiscussionForRecovery(uri, jobId, DescribeDiscussionRecovery(probe.State));
                return false;
            }
            else if (probe.State == "EMPTY" && !inserted)
            {
                if (!await EnsureDiscussionReplyAsync(uri, jobId, store, probe)) return false;
                if (!IsCurrentDiscussion(uri, jobId)) return false;
                if (string.IsNullOrWhiteSpace(OwnedDiscussionReply(uri))) return false;
                var beforeInput = await ProbeDiscussionAsync(uri);
                if (!IsCurrentDiscussion(uri, jobId)) return false;
                if (beforeInput.State != "EMPTY") continue;
                if (!DiscussionDraftMatches(beforeInput))
                {
                    HoldDiscussionForRecovery(uri, jobId, DescribeDiscussionRecovery("CONTEXT_CHANGED"));
                    return false;
                }
                // Focus and recheck the exact editor immediately before Chromium input.
                var focus = await ProbeDiscussionAsync(uri, "focus", beforeInput);
                if (!IsCurrentDiscussion(uri, jobId)) return false;
                if (focus.State == "FOCUSED" && DiscussionDraftMatches(focus))
                {
                    var input = await ProbeDiscussionAsync(uri, "can-insert", focus);
                    if (!IsCurrentDiscussion(uri, jobId)) return false;
                    if (input.State == "FOCUSED" && DiscussionDraftMatches(input))
                    {
                        inserted = true; // An uncertain CDP result must not cause repeated typing.
                        await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText",
                            JsonSerializer.Serialize(new { text = OwnedDiscussionReply(uri) })).WaitAsync(TimeSpan.FromSeconds(10));
                        _viewModel.StatusText = "⌨️ Đã nhập phản hồi; đang kiểm tra nội dung và chờ nút gửi sẵn sàng...";
                    }
                }
            }
            else if (probe.State == "READY")
            {
                if (!DiscussionDraftMatches(probe))
                {
                    HoldDiscussionForRecovery(uri, jobId, "Đề thảo luận đã đổi hoặc chưa đọc đủ. Giữ bản nháp; chưa bấm Reply.");
                    return false;
                }
                await Task.Delay(500);
                if (!IsCurrentDiscussion(uri, jobId)) return false;
                var stable = await ProbeDiscussionAsync(uri);
                if (!IsCurrentDiscussion(uri, jobId)) return false;
                if (stable.State == "CONFIRMED") continue;
                if (stable.State == "READY" && DiscussionDraftMatches(stable))
                {
                    // Persist before the network action. A crash/restart must not post again.
                    if (journal.TryBegin(uri))
                    {
                        pending = true;
                        if (!IsCurrentDiscussion(uri, jobId)) return false;
                        var submission = await ProbeDiscussionAsync(uri, "submit", stable);
                        if (!IsCurrentDiscussion(uri, jobId)) return false;
                        if (submission.State is not ("SUBMIT_ATTEMPTED" or "SUBMIT_PENDING" or "CONFIRMED"))
                        {
                            HoldDiscussionForRecovery(uri, jobId, DescribeDiscussionRecovery(submission.State));
                            return false;
                        }
                        _viewModel.StatusText = "⏳ Đã yêu cầu gửi phản hồi; đang chờ Coursera xác nhận...";
                        attempt = 0; // one bounded confirmation window after the sole submission
                    }
                    else pending = true;
                }
            }
            if (attempt < 29) await Task.Delay(1000);
        }
        if (!IsCurrentDiscussion(uri, jobId)) return false;
        // A clicked submission may have reached Coursera even if confirmation
        // is delayed. Keep checking it instead of posting a duplicate.
        if (!pending && !inserted && !sawDraft && (lastState is "NOT_READY" or "EMPTY")) return true;
        HoldDiscussionForRecovery(uri, jobId, pending
            ? "Chưa thấy Coursera xác nhận phản hồi. Giữ cửa sổ và kiểm tra tiếp; không gửi lặp."
            : DescribeDiscussionRecovery(lastState));
        return false;
    }

    private bool DiscussionDraftMatches(DiscussionProbe probe) => _discussionReplyDraft != null &&
        probe.Title.Length > 0 && probe.Prompt.Length > 0 && _discussionReplyDraft.ContextHash ==
        DiscussionReplyContract.ContextHash(probe.Title, probe.Prompt);

    private async Task<bool> EnsureDiscussionReplyAsync(Uri uri, string? jobId, DiscussionReplyStore store, DiscussionProbe first)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        string? user = _courseraIdentity?.UserId;
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        monitor.Tick += (_, _) => { if (!IsCurrentDiscussion(uri, jobId) || user != _courseraIdentity?.UserId) cancellation.Cancel(); };
        monitor.Start();
        try
        {
            DiscussionProbe context = first;
            bool stable = false;
            for (int read = 0; read < 20; read++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await Task.Delay(400, cancellation.Token);
                var next = await ProbeDiscussionAsync(uri);
                if (!IsCurrentDiscussion(uri, jobId)) return false;
                if (next.State == "STALE") return false;
                if (next.State == "CONFIRMED")
                {
                    DiscussionJournal().MarkConfirmed(uri);
                    await AdvanceConfirmedDiscussionAsync(uri, jobId);
                    return false;
                }
                if (next.State is "EXISTING_DRAFT" or "AMBIGUOUS" or "ACCESS_REQUIRED")
                {
                    HoldDiscussionForRecovery(uri, jobId, DescribeDiscussionRecovery(next.State));
                    return false;
                }
                if (next.State == "READY" && DiscussionDraftMatches(next)) return true;
                if (next.State == "EMPTY" && context.State == "EMPTY" && next.Title.Length > 0 &&
                    next.Prompt.Length > 0 && next.Title == context.Title && next.Prompt == context.Prompt)
                { context = next; stable = true; break; }
                context = next;
                _viewModel.StatusText = "📖 Đang chờ nội dung yêu cầu thảo luận tải đủ...";
            }
            if (!stable)
            {
                HoldDiscussionForRecovery(uri, jobId, "Chưa đọc đủ yêu cầu thảo luận để AI viết phản hồi. Giữ trang để kiểm tra lại.");
                return false;
            }
            string contextHash = DiscussionReplyContract.ContextHash(context.Title, context.Prompt);
            if (_discussionReplyDraft != null)
            {
                if (_discussionReplyDraft.ContextHash != contextHash) throw new InvalidDataException("Yêu cầu đã thay đổi so với phản hồi đang lưu. Không tự ghi đè bản cũ.");
                return true;
            }
            var progress = new Progress<string>(message =>
            {
                if (!cancellation.IsCancellationRequested && IsCurrentDiscussion(uri, jobId)) _viewModel.StatusText = message;
            });
            var stages = new PeerResearchProgress(progress);
            stages.Report("🤖 Đang soạn phản hồi theo nội dung bài thảo luận...");
            string request = DiscussionReplyContract.BuildPrompt(context.Title, context.Prompt);
            string? reply = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var result = await stages.RequestAsync(report => _aiCompletionService.CompleteAsync(
                    DiscussionReplyContract.SystemPrompt, request, .7, report, cancellation.Token), attempt, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!result.Success) throw new IOException(result.UserMessage);
                try { reply = DiscussionReplyContract.Parse(result.Content); break; }
                catch (InvalidDataException) when (attempt == 0)
                { request += "\nReturn only the complete requested JSON object with a valid plain-text reply, no extra properties."; }
            }
            if (reply == null) throw new InvalidDataException("AI chưa trả về phản hồi hợp lệ.");
            if (!IsCurrentDiscussion(uri, jobId) || user != _courseraIdentity?.UserId) return false;
            var current = await ProbeDiscussionAsync(uri);
            if (!IsCurrentDiscussion(uri, jobId)) return false;
            if (current.State == "CONFIRMED")
            {
                DiscussionJournal().MarkConfirmed(uri);
                await AdvanceConfirmedDiscussionAsync(uri, jobId);
                return false;
            }
            if (current.State != "EMPTY" || current.Title != context.Title || current.Prompt != context.Prompt)
            {
                HoldDiscussionForRecovery(uri, jobId, "Trang hoặc nội dung ô trả lời đã thay đổi khi AI đang soạn. Giữ nguyên nội dung hiện tại.");
                return false;
            }
            _discussionReplyDraft = store.Save(uri, contextHash, reply);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentDiscussion(uri, jobId)) HoldDiscussionForRecovery(uri, jobId, "Đã dừng hoặc hết thời gian soạn phản hồi AI. Chưa bấm Reply.");
            return false;
        }
        finally { monitor.Stop(); }
    }

    private async Task ReturnToPreviousDiscussionItemAsync(Uri uri, string? jobId)
    {
        if (!IsCurrentDiscussion(uri, jobId)) return;
        // The probe rechecks the document, draft and submission immediately
        // before navigating, including content hydrated after the final poll.
        var probe = await ProbeDiscussionAsync(uri, "previous");
        if (!IsCurrentDiscussion(uri, jobId)) return;
        if (probe.State == "CONFIRMED")
        {
            DiscussionJournal().MarkConfirmed(uri);
            await AdvanceConfirmedDiscussionAsync(uri, jobId);
            return;
        }
        if (probe.State == "PREVIOUS_CLICKED")
        {
            await WaitForAppItemNavigationAsync(uri, TimeSpan.FromSeconds(3));
        }
        else if (probe.State == "NO_PREVIOUS" && MainWebView.CoreWebView2.CanGoBack)
        {
            string history = await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}");
            if (!IsCurrentDiscussion(uri, jobId)) return;
            using var document = JsonDocument.Parse(history);
            int index = document.RootElement.GetProperty("currentIndex").GetInt32();
            var entries = document.RootElement.GetProperty("entries");
            if (index <= 0 || index >= entries.GetArrayLength()) return;
            var previous = entries[index - 1];
            if (!Uri.TryCreate(previous.GetProperty("url").GetString(), UriKind.Absolute, out var previousUri) ||
                !TryGetCourseSlug(uri, out var course) || !TryGetCourseSlug(previousUri, out var previousCourse) ||
                !string.Equals(course, previousCourse, StringComparison.OrdinalIgnoreCase) ||
                previousUri.AbsolutePath == uri.AbsolutePath) return;

            // Recheck after reading history; hydration may have produced a draft.
            probe = await ProbeDiscussionAsync(uri, "previous");
            if (!IsCurrentDiscussion(uri, jobId)) return;
            if (probe.State == "CONFIRMED")
            {
                DiscussionJournal().MarkConfirmed(uri);
                await AdvanceConfirmedDiscussionAsync(uri, jobId);
                return;
            }
            if (probe.State == "NO_PREVIOUS")
            {
                await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.navigateToHistoryEntry",
                    JsonSerializer.Serialize(new { entryId = previous.GetProperty("id").GetInt32() }));
            }
            else if (probe.State != "PREVIOUS_CLICKED") return;
            await WaitForAppItemNavigationAsync(uri, TimeSpan.FromSeconds(3));
        }
    }

    private async Task AdvanceConfirmedDiscussionAsync(Uri uri, string? jobId)
    {
        if (!IsCurrentDiscussion(uri, jobId)) return;
        _courseHasSubmittedDiscussionItems = true;
        _submittedDiscussionPaths.Add(uri.AbsolutePath);
        ClearDiscussionRecovery();
        DiscussionProbe probe = await ProbeDiscussionAsync(uri, "advance");
        if (!IsCurrentDiscussion(uri, jobId)) return;
        if (probe.State == "ADVANCED")
        {
            _viewModel.StatusText = "✅ Coursera đã xác nhận phản hồi; đang chuyển bài tiếp theo...";
            if (await WaitForAppItemNavigationAsync(uri, TimeSpan.FromSeconds(3))) return;
        }
        if (IsCurrentDiscussion(uri, jobId) && TryGetCourseHomeUri(uri, out Uri? home) && home != null)
        {
            _viewModel.StatusText = "✅ Coursera đã xác nhận phản hồi; đang quét bài tiếp theo...";
            MainWebView.Source = home;
        }
    }

    private static string DescribeDiscussionRecovery(string state) => state switch
    {
        "EXISTING_DRAFT" => "Bài có nội dung đang nhập. Giữ nguyên nội dung để bạn kiểm tra và gửi trên trang.",
        "AMBIGUOUS" => "Có nhiều ô hoặc nút phản hồi. Giữ trang để kiểm tra đúng biểu mẫu.",
        "ACCESS_REQUIRED" => "Bài yêu cầu kiểm tra đăng nhập hoặc quyền truy cập. Phiên vẫn được giữ mở.",
        "BLOCKED_DIALOG" => "Một hộp thoại đang che bài thảo luận. Giữ trang để bạn kiểm tra.",
        "SUBMIT_NOT_READY" => "Nội dung đã nhập nhưng nút gửi chưa sẵn sàng. Giữ nguyên bản nháp để kiểm tra lại.",
        "CONTEXT_CHANGED" => "Yêu cầu thảo luận đã thay đổi. Giữ nguyên bản nháp; chưa bấm Reply.",
        _ => "Bài thảo luận chưa tải đủ sau các lần thử. Giữ cửa sổ; bấm Kiểm tra lại bài thảo luận khi trang sẵn sàng."
    };

    private void HoldDiscussionForRecovery(Uri uri, string? jobId, string reason)
    {
        if (!IsCurrentDiscussion(uri, jobId)) return;
        _discussionRecoveryUri = uri;
        _discussionRecoveryJob = jobId;
        _viewModel.StatusText = "⏳ " + reason;
        DiscussionRetryButton.Visibility = Visibility.Visible;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (Left + Width < SystemParameters.VirtualScreenLeft || Top + Height < SystemParameters.VirtualScreenTop)
        { Left = 80; Top = 80; }
        // Keep the normal running heartbeat: waiting_user is terminal in the manager protocol.
        _discussionRecoveryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _discussionRecoveryTimer.Tick -= DiscussionRecoveryTimer_Tick;
        _discussionRecoveryTimer.Tick += DiscussionRecoveryTimer_Tick;
        _discussionRecoveryTimer.Start();
    }

    private async void DiscussionRecoveryTimer_Tick(object? sender, EventArgs e)
    {
        if (_discussionRecoveryChecking || _isHandlingDiscussion) return;
        if (_discussionRecoveryUri is not { } uri || !IsCurrentDiscussion(uri, _discussionRecoveryJob))
        { ClearDiscussionRecovery(); return; }
        _discussionRecoveryChecking = true;
        try
        {
            var probe = await ProbeDiscussionAsync(uri);
            if (probe.State == "CONFIRMED" && IsCurrentDiscussion(uri, _discussionRecoveryJob))
            {
                string? jobId = _discussionRecoveryJob;
                DiscussionJournal().MarkConfirmed(uri);
                await AdvanceConfirmedDiscussionAsync(uri, jobId);
            }
        }
        catch { /* A read failure keeps the draft/session and does not submit. */ }
        finally { _discussionRecoveryChecking = false; }
    }

    private void ClearDiscussionRecovery()
    {
        _discussionRecoveryTimer?.Stop();
        _discussionRecoveryUri = null;
        _discussionRecoveryJob = null;
        DiscussionRetryButton.Visibility = Visibility.Collapsed;
    }

    private async void OnRetryDiscussionClick(object sender, RoutedEventArgs e)
    {
        if (_discussionRecoveryUri is not { } uri || !IsCurrentDiscussion(uri, _discussionRecoveryJob)) return;
        ClearDiscussionRecovery();
        await HandleDiscussionAsync();
    }
}
