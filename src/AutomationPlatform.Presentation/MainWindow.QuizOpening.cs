using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private bool IsSameCourseraHostAndPath(Uri? expectedUri)
    {
        Uri? currentUri = MainWebView.Source;
        return IsCourseraUri(expectedUri) &&
               IsCourseraUri(currentUri) &&
               Uri.Compare(
                   expectedUri!,
                   currentUri!,
                   UriComponents.SchemeAndServer | UriComponents.Path,
                   UriFormat.SafeUnescaped,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }

    private async Task<string> WaitForQuizOpeningAsync(Uri expected, bool allowPageActions)
    {
        int readyPolls = 0;
        string state = "WAITING_QUESTIONS";
        for (int pass = 0; pass < 60; pass++)
        {
            if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported ||
                IsInteractiveBrowseSession || !IsSameCourseraHostAndPath(expected)) return "STALE";
            await DismissAnyGlobalPopupsAsync(maxPasses: 1);
            if (!IsSameCourseraHostAndPath(expected)) return "STALE";
            try
            {
                state = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(
                    QuizOpeningAutomation.BuildProbe(expected, allowPageActions, _hasExtractedFeedbackThisSession)));
            }
            catch
            {
                state = "PROBE_FAILED";
            }
            if (!IsSameCourseraHostAndPath(expected)) return "STALE";
            if (state is "FEEDBACK" or "STALE") return state;
            readyPolls = state == "READY" ? readyPolls + 1 : 0;
            if (readyPolls >= 2) return "READY";
            _viewModel.StatusText = state switch
            {
                "CLICKED_CONTINUE" => "▶️ Đã bấm Continue; đang chờ popup đóng và đề thi mới tải xong...",
                "CLICKED_START" => "▶️ Đã bấm Start/Retry; đang chờ xác nhận lần thi mới...",
                "WAITING_DIALOG" or "WAITING_CONTINUE" => "⏳ Đang chờ xử lý popup trước khi đọc câu hỏi...",
                _ => "⏳ Đang chờ câu hỏi của lần thi mới sẵn sàng..."
            };
            await Task.Delay(500);
        }
        _viewModel.StatusText = $"⚠️ Chưa mở được lần thi mới ({state}); giữ trang để kiểm tra popup, chưa đọc câu hỏi.";
        return "TIMEOUT";
    }

    private async Task<bool> LoadQuizQuestionsWhenReadyAsync(Uri expected)
    {
        for (int step = 0; step < 8; step++)
        {
            if (await WaitForQuizOpeningAsync(expected, allowPageActions: false) != "READY") return false;
            _viewModel.StatusText = "⏳ Đang cuộn trang để tải danh sách câu hỏi của lần thi mới...";
            await MainWebView.ExecuteScriptAsync("window.scrollBy(0, 1000);");
        }
        if (await WaitForQuizOpeningAsync(expected, allowPageActions: false) != "READY") return false;
        await MainWebView.ExecuteScriptAsync("window.scrollTo(0, 0);");
        return true;
    }
}
