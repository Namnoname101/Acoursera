using System.Diagnostics;
using System.Text.Json;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private async Task<bool> OpenAndLoadQuizFeedbackAsync()
    {
        Uri? expected = MainWebView.Source;
        string? user = _courseraIdentity?.UserId, job = _centralWorkerClient.CurrentJob?.Id;
        string key;
        try { key = QuizFeedbackMemory.QuizKey(expected!); }
        catch (Exception e) when (e is ArgumentException or NullReferenceException) { return false; }
        bool Current()
        {
            if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported || IsInteractiveBrowseSession ||
                user != _courseraIdentity?.UserId || job != _centralWorkerClient.CurrentJob?.Id || MainWebView.Source == null) return false;
            try { return QuizFeedbackMemory.QuizKey(MainWebView.Source) == key; }
            catch (ArgumentException) { return false; }
        }
        async Task<QuizFeedbackOpeningState> Probe(string action = "inspect")
        {
            if (!Current()) return new() { State = "STALE" };
            string raw = await MainWebView.ExecuteScriptAsync(QuizFeedbackOpeningAutomation.BuildProbe(expected!, action))
                .WaitAsync(TimeSpan.FromSeconds(3));
            return Current() ? JsonSerializer.Deserialize<QuizFeedbackOpeningState>(DecodeWebViewString(raw)) ?? new() : new() { State = "STALE" };
        }

        string state = "WAITING_FEEDBACK";
        try
        {
            var watch = Stopwatch.StartNew();
            int clicks = 0, readyPolls = 0;
            TimeSpan lastClick = TimeSpan.MinValue;
            string previousSignature = "";
            bool opened = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(35) && Current())
            {
                QuizFeedbackOpeningState page = await Probe();
                state = page.State;
                if (state == "STALE") return false;
                readyPolls = state == "READY" && page.QuestionCount > 0
                    ? (previousSignature == page.Signature ? readyPolls + 1 : 1) : 0;
                previousSignature = page.Signature;
                if (readyPolls >= 2) { opened = true; break; }
                if (state == "CLICK_READY" && clicks < 2 && (clicks == 0 || watch.Elapsed - lastClick >= TimeSpan.FromSeconds(6)))
                {
                    page = await Probe("click");
                    state = page.State;
                    if (state == "CLICK_SENT")
                    {
                        clicks++;
                        lastClick = watch.Elapsed;
                    }
                }
                if (!Current()) return false;
                _viewModel.StatusText = clicks == 0
                    ? $"📖 Đang tìm View feedback ({state})..."
                    : $"📖 Đã gửi click View feedback; đang chờ chi tiết câu hỏi ({watch.Elapsed.TotalSeconds:0}/35s, {state})...";
                await Task.Delay(500);
            }
            if (!Current()) return false;
            if (!opened)
            {
                _viewModel.StatusText = $"⚠️ Chưa mở được chi tiết View feedback ({state}). Giữ trang; chưa đọc feedback hoặc làm lại.";
                return false;
            }

            // Coursera's fullscreen feedback uses an inner scrolling container.
            // Wait at its end twice so lazily rendered questions can settle.
            int stableEnds = 0;
            for (int step = 0; step < 30 && Current(); step++)
            {
                QuizFeedbackOpeningState page = await Probe("scroll");
                if (page.State != "READY")
                {
                    if (Current()) _viewModel.StatusText = "⚠️ Trang feedback đã thay đổi; chưa bắt đầu làm lại.";
                    return false;
                }
                stableEnds = page.AtEnd && previousSignature == page.Signature ? stableEnds + 1 : 0;
                previousSignature = page.Signature;
                if (stableEnds >= 2) return Current();
                _viewModel.StatusText = $"📚 Đã mở feedback; đang tải danh sách câu hỏi ({page.QuestionCount} câu, lượt {step + 1}/30)...";
                await Task.Delay(350);
            }
            if (Current()) _viewModel.StatusText = "⚠️ Chưa tải hết danh sách feedback. Giữ trang; chưa tự làm lại.";
        }
        catch (Exception e) when (e is TimeoutException or JsonException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (Current()) _viewModel.StatusText = $"⚠️ Mở View feedback bị gián đoạn ({state}). Giữ trang; chưa tự làm lại.";
        }
        return false;
    }
}
