using System.IO;
using System.Security.Cryptography;
using System.Text;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private DeferredQuizQueue? _deferredQuizQueue;
    private string? _deferredQuizScope;
    private string? _deferredQuizReviewKey;

    private DeferredQuizQueue DeferredQuizzes(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            !(uri.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.coursera.org", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Trang hiện tại không phải khóa học Coursera đã xác minh.");
        string user = _courseraIdentity?.UserId ?? throw new InvalidDataException("Chưa xác minh tài khoản cho danh sách bài cần làm lại.");
        if (!TryGetCourseSlug(uri, out string course)) throw new InvalidDataException("Chưa xác định khóa học.");
        string scope = user + "|" + (_centralWorkerClient.CurrentJob?.Id ?? "local") + "|" + course.ToLowerInvariant();
        if (_deferredQuizQueue != null && scope == _deferredQuizScope) return _deferredQuizQueue;
        string profile = _workerLaunchOptions.Enabled ? _workerLaunchOptions.ProfilePath :
            MainWebView.CoreWebView2.Environment.UserDataFolder;
        string account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)));
        var queue = new DeferredQuizQueue(Path.Combine(profile, "AcoseQuizAccounts", account), uri);
        queue.BeginNewPass();
        _deferredQuizReviewKey = null;
        _deferredQuizScope = scope;
        return _deferredQuizQueue = queue;
    }

    private void ResetDeferredQuizPass()
    {
        _deferredQuizQueue = null;
        _deferredQuizScope = null;
        _deferredQuizReviewKey = null;
    }

    private IReadOnlyList<string> DeferredQuizPaths() => MainWebView.Source is { } uri
        ? DeferredQuizzes(uri).PendingPaths : [];

    private async Task DeferCurrentQuizAsync(string reason)
    {
        Uri? uri = MainWebView.Source;
        if (uri == null || _workerClosing || _coursePauseInProgress || _courseJobCompletionReported || IsInteractiveBrowseSession) return;
        try
        {
            var queue = DeferredQuizzes(uri);
            queue.Defer(uri, reason);
            _deferredQuizReviewKey = null;
            _hasExtractedFeedbackThisSession = false;
            _viewModel.StatusText = $"⏭️ Đã lưu bài chưa đạt ({queue.PendingCount} bài cần quay lại). Học các bài còn lại trước...";
            if (TryGetCourseHomeUri(uri, out Uri? home) && home != null) MainWebView.Source = home;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            _viewModel.StatusText = "⚠️ Chưa lưu được bài cần quay lại. Giữ trang hiện tại; chưa bỏ qua bài.";
        }
        await Task.CompletedTask;
    }

    private bool SkipDeferredQuizDuringFirstPass(Uri uri)
    {
        var queue = DeferredQuizzes(uri);
        if (!queue.Contains(uri) || _deferredQuizReviewKey == DeferredQuizQueue.ActivityKey(uri)) return false;
        _viewModel.StatusText = "⏭️ Bài này đã ghi vào danh sách làm lại cuối khóa; đang tìm bài tiếp theo...";
        if (TryGetCourseHomeUri(uri, out Uri? home) && home != null) MainWebView.Source = home;
        return true;
    }

    private bool ResolvePassedDeferredQuiz(Uri uri)
    {
        var queue = DeferredQuizzes(uri);
        if (!queue.Contains(uri)) return false;
        // Only the verified PASSED route calls this. A submitted/pending grade
        // must not remove a deferred assessment or report course completion.
        queue.Resolve(uri);
        _deferredQuizReviewKey = null;
        if (TryGetCourseHomeUri(uri, out Uri? home) && home != null) MainWebView.Source = home;
        return true;
    }

    private async Task<bool> ResumeDeferredQuizzesAfterSweepAsync()
    {
        if (MainWebView.Source is not { } uri || _workerClosing || _coursePauseInProgress ||
            _courseJobCompletionReported || IsInteractiveBrowseSession) return false;
        var queue = DeferredQuizzes(uri);
        if (queue.PendingCount == 0) return false;
        Uri? next = queue.BeginNextReview();
        if (next != null)
        {
            _deferredQuizReviewKey = DeferredQuizQueue.ActivityKey(next);
            _hasExtractedFeedbackThisSession = false;
            _viewModel.StatusText = $"🔁 Đã quét hết các bài khác. Đang quay lại bài chưa đạt ({queue.PendingCount} bài còn lại)...";
            MainWebView.Source = next;
        }
        else
        {
            _viewModel.StatusText = $"⏳ Đã quay lại các bài tồn đọng; còn {queue.PendingCount} bài chưa đạt. Chưa hoàn thành khóa.";
            await PauseCourseJobAsync(_viewModel.StatusText,
                "Đã học các bài còn lại và thử lại từng bài tồn đọng. Feedback được giữ nguyên; bấm Chạy để mở một lượt mới.",
                "QUIZ_DEFERRED_REVIEW_REQUIRED");
        }
        return true;
    }
}
