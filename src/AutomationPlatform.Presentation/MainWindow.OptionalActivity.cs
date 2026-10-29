using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private bool _isHandlingOptionalCourseActivity;
    private static readonly Lazy<string> OptionalCourseActivityScript = new(() =>
    {
        using Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream(
            "Acose.OptionalCourseActivityProbe.js") ?? throw new InvalidOperationException("Missing optional activity probe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private sealed record OptionalCourseActivityState(string State, string Kind);

    private static bool ShouldProbeOptionalCourseActivity(Uri? uri)
    {
        if (!IsCourseraUri(uri) || !TryGetCourseSlug(uri, out _)) return false;
        string[] segments = uri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int learn = Array.FindIndex(segments, part => part.Equals("learn", StringComparison.OrdinalIgnoreCase));
        if (learn < 0 || segments.Length <= learn + 3) return false;
        return segments[learn + 2].ToLowerInvariant() is not
            ("home" or "lecture" or "supplement" or "discussionprompt" or "peer" or
             "quiz" or "exam" or "assignment-submission" or "gradedlti");
    }

    private async Task<OptionalCourseActivityState> ProbeOptionalCourseActivityAsync(Uri uri, string action = "probe")
    {
        if (!IsSameCourseraDocument(uri)) return new("STALE_DOCUMENT", "");
        string script = OptionalCourseActivityScript.Value.Replace("__OPTIONAL_ACTIVITY_ARGUMENTS__",
            JsonSerializer.Serialize(new { expectedUrl = uri.AbsoluteUri, action }), StringComparison.Ordinal);
        return JsonSerializer.Deserialize<OptionalCourseActivityState>(DecodeWebViewString(
            await MainWebView.ExecuteScriptAsync(script))) ?? new("INVALID_RESULT", "");
    }

    private async Task<bool> TrySkipOptionalCourseActivityAsync()
    {
        Uri? activityUri = MainWebView.Source;
        if (!ShouldProbeOptionalCourseActivity(activityUri)) return false;
        // SourceChanged and NavigationCompleted can both dispatch the same SPA page.
        // The owner handles a consecutive optional page after releasing this guard.
        if (_isHandlingOptionalCourseActivity) return true;
        _isHandlingOptionalCourseActivity = true;
        string? job = _centralWorkerClient.CurrentJob?.Id;
        string? user = _courseraIdentity?.UserId;
        bool Current() => !_workerClosing && !_coursePauseInProgress && !_courseJobCompletionReported && !IsInteractiveBrowseSession &&
            _centralWorkerClient.CurrentJob?.Id == job && _courseraIdentity?.UserId == user && IsSameCourseraDocument(activityUri);
        try
        {
            OptionalCourseActivityState probe = new("NOT_OPTIONAL", "");
            // A history update can precede the new page's metadata and Next button.
            for (int attempt = 0; attempt < 8 && Current(); attempt++)
            {
                probe = await ProbeOptionalCourseActivityAsync(activityUri!);
                if (probe.State == "OPTIONAL_READY") break;
                await Task.Delay(350);
            }
            if (!Current()) return true;
            if (probe.State != "OPTIONAL_READY") return false;

            _courseHasSkippedLaunchAppItems = true;
            _skippedLaunchAppItemPaths.Add(activityUri!.AbsolutePath.TrimEnd('/'));
            _viewModel.StatusText = $"⏭️ {probe.Kind} không bắt buộc. Đang chuyển bài tiếp theo...";
            for (int attempt = 0; attempt < 3 && Current(); attempt++)
            {
                probe = await ProbeOptionalCourseActivityAsync(activityUri, "next");
                if (probe.State == "NEXT_CLICKED" &&
                    await WaitForAppItemNavigationAsync(activityUri, TimeSpan.FromSeconds(5))) return true;
                if (probe.State is "NOT_OPTIONAL" or "STALE_DOCUMENT") return true;
                await Task.Delay(800);
            }
            if (Current() && TryGetCourseHomeUri(activityUri, out Uri? courseHome))
            {
                _viewModel.StatusText = "⏭️ Đã bỏ qua hoạt động không bắt buộc; đang tìm bài kế tiếp trong danh sách.";
                MainWebView.Source = courseHome;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (Current()) _viewModel.StatusText = "⏳ Chưa chuyển được hoạt động không bắt buộc: " + ex.Message;
            return true;
        }
        finally
        {
            _isHandlingOptionalCourseActivity = false;
            if (!_workerClosing && !_coursePauseInProgress && !_courseJobCompletionReported && !IsInteractiveBrowseSession &&
                _centralWorkerClient.CurrentJob?.Id == job && _courseraIdentity?.UserId == user &&
                !IsSameCourseraDocument(activityUri) && ShouldProbeOptionalCourseActivity(MainWebView.Source))
                _ = TrySkipOptionalCourseActivityAsync();
        }
    }
}
