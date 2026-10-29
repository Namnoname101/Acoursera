using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private QuizFeedbackMemory? _quizLearningMemory;
    private string? _quizLearningScope;

    private string CurrentQuizLearningScope(Uri uri) =>
        (_courseraIdentity?.UserId ?? throw new InvalidDataException("Chưa xác minh tài khoản để học feedback.")) +
        "|" + QuizFeedbackMemory.QuizKey(uri);

    private QuizFeedbackMemory EnsureQuizLearningMemory(Uri uri)
    {
        string scope = CurrentQuizLearningScope(uri);
        if (_quizLearningMemory != null && scope == _quizLearningScope) return _quizLearningMemory;
        string profile = _workerLaunchOptions.Enabled ? _workerLaunchOptions.ProfilePath :
            MainWebView.CoreWebView2.Environment.UserDataFolder;
        string account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_courseraIdentity!.UserId)));
        var memory = new QuizFeedbackMemory(Path.Combine(profile, "AcoseQuizAccounts", account), uri);
        _quizLearningScope = scope;
        return _quizLearningMemory = memory;
    }

    private bool IsCurrentQuizLearningScope()
    {
        if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported || IsInteractiveBrowseSession ||
            MainWebView.Source == null || _quizLearningMemory == null) return false;
        try { return CurrentQuizLearningScope(MainWebView.Source) == _quizLearningScope; }
        catch (ArgumentException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private static QuizLearningQuestion LearningQuestion(QuizQuestion question) =>
        new(question.Question, question.Kind, question.Options.Select(option => option.Text).ToArray());

    private string QuizFeedbackHints(QuizQuestion question) =>
        _quizLearningMemory?.BuildHints(LearningQuestion(question)) ?? "";

    private bool ValidateLearnedQuizAnswer(QuizQuestion question, IReadOnlyList<string> answers, out string error)
    {
        if (!IsCurrentQuizLearningScope())
        {
            error = "Trang hoặc tài khoản đã thay đổi; không áp dụng feedback khác bài.";
            return false;
        }
        return _quizLearningMemory!.Validate(LearningQuestion(question), answers, out error);
    }

    private async Task<bool> LearnCurrentQuizFeedbackAsync()
    {
        Uri? uri = MainWebView.Source;
        string? user = _courseraIdentity?.UserId;
        string? job = _centralWorkerClient.CurrentJob?.Id;
        bool StillCurrent() => uri != null && IsSameCourseraDocument(uri) &&
            user == _courseraIdentity?.UserId && job == _centralWorkerClient.CurrentJob?.Id &&
            !_workerClosing && !_coursePauseInProgress && !_courseJobCompletionReported && !IsInteractiveBrowseSession;
        try
        {
            if (uri == null || !StillCurrent()) return false;
            var memory = EnsureQuizLearningMemory(uri);
            string? previous = null;
            string lastReadFailure = "Chưa xác nhận được dữ liệu feedback.";
            // Wait for a stable graded snapshot. An empty/half-hydrated result is
            // not evidence that the previous answer should be tried again.
            for (int pass = 0; pass < 12; pass++)
            {
                if (!StillCurrent()) return false;
                string json = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(
                    QuizFeedbackAutomation.BuildProbe(uri)).WaitAsync(TimeSpan.FromSeconds(10)));
                if (!StillCurrent()) return false;
                var observations = JsonSerializer.Deserialize<List<QuizFeedbackObservation>>(json);
                var failedQuestions = observations?.Where(item => item.IsFailedQuestion).ToArray() ?? [];
                int missingAnswers = failedQuestions.Count(item => item.SelectedAnswers is not { Count: > 0 });
                if (observations is not { Count: > 0 })
                    lastReadFailure = "Không đọc được câu hỏi từ trang feedback (NO_OBSERVATIONS).";
                else if (failedQuestions.Length == 0)
                    lastReadFailure = $"Đã đọc {observations.Count} câu nhưng chưa nhận diện được câu sai (NO_FAILED_QUESTIONS).";
                else if (missingAnswers > 0)
                    lastReadFailure = $"Đã đọc {observations.Count} câu; {missingAnswers} câu sai chưa có dữ liệu đáp án đã chọn (MISSING_SELECTED_ANSWERS).";
                else if (json != previous)
                    lastReadFailure = $"Đã đọc {observations.Count} câu, {failedQuestions.Length} câu sai; chờ dữ liệu ổn định (SNAPSHOT_UNSTABLE).";
                else
                {
                    memory.Merge(observations);
                    _viewModel.StatusText = $"🧠 Đã lưu feedback của {observations.Count} câu; giữ các lỗi cũ và kiểm tra đáp án trước lần nộp tiếp theo...";
                    return true;
                }
                _viewModel.StatusText = $"📚 Đang đọc feedback ({pass + 1}/12): {lastReadFailure}";
                previous = json;
                await Task.Delay(500);
            }
            _viewModel.StatusText = $"⚠️ {lastReadFailure} Giữ trang kết quả; chưa tự làm lại.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException or TimeoutException)
        {
            if (StillCurrent()) _viewModel.StatusText = "⚠️ Chưa lưu được feedback câu sai. Giữ trang kết quả; chưa tự làm lại.";
        }
        return false;
    }
}
