using System.Windows;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private void OnApplyActivitySkipsClick(object sender, RoutedEventArgs e)
    {
        bool skipPdf = SkipPeerPdfCheckBox.IsChecked == true;
        bool skipReview = SkipPeerReviewCheckBox.IsChecked == true;
        if (skipPdf == ShouldSkipPeerPdfItems && skipReview == ShouldSkipPeerReviewItems)
        {
            _viewModel.StatusText = "Tùy chọn bỏ qua đã được áp dụng cho cửa sổ này.";
            return;
        }

        ShouldSkipPeerPdfItems = skipPdf;
        ShouldSkipPeerReviewItems = skipReview;
        // A previous sweep may have excluded activities that are now enabled again.
        _automationCompletedModuleNumbers.Clear();
        _viewModel.StatusText = $"Đã áp dụng: PDF/Peer-graded {(skipPdf ? "bỏ qua" : "xử lý")}; " +
            $"đánh giá chéo {(skipReview ? "bỏ qua" : "xử lý")}. Áp dụng trong cửa sổ hiện tại.";

        if (MainWebView.CoreWebView2 == null || _directLoginActive || IsInteractiveBrowseSession ||
            _workerClosing || _coursePauseInProgress || _courseJobCompletionReported) return;
        if (TrySkipConfiguredPeerActivity()) return;
        // Re-scan a course outline if options changed while it was visible.
        if (TryGetCourseHomeUri(MainWebView.Source, out Uri? home) && home != null &&
            IsSameCourseraDocument(home)) MainWebView.Reload();
    }

    private bool IsConfiguredPeerActivitySkipped(Uri? activity)
    {
        if (!IsCourseraUri(activity) ||
            PeerReviewAutomation.AssignmentPath(activity!) == null) return false;
        return CourseAutomationPolicy.IsPeerReviewPath(activity!.AbsolutePath)
            ? ShouldSkipPeerReviewItems : ShouldSkipPeerPdfItems;
    }

    private bool TrySkipConfiguredPeerActivity()
    {
        Uri? activity = MainWebView.Source;
        if (_directLoginActive || IsInteractiveBrowseSession || _workerClosing ||
            _coursePauseInProgress || _courseJobCompletionReported ||
            !IsConfiguredPeerActivitySkipped(activity)) return false;
        return SkipPeerActivityToOutline(activity!,
            CourseAutomationPolicy.IsPeerReviewPath(activity!.AbsolutePath)
                ? "đánh giá chéo" : "bài nộp PDF/Peer-graded");
    }

    private bool SkipPeerActivityToOutline(Uri activity, string kind)
    {
        if (!TryGetCourseHomeUri(activity, out Uri? home) || home == null) return false;
        _courseHasSkippedPeerItems = true;
        _viewModel.StatusText = $"⏭️ Đã bỏ qua {kind} theo lựa chọn; đang tìm bài tiếp theo. Bài bỏ qua chưa được tính đạt trên Coursera.";
        MainWebView.Source = home;
        return true;
    }
}
