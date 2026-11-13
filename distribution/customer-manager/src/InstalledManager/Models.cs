using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Acose.CustomerManager;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    public void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum CourseState { Ready, Queued, Starting, Running, Stopping, Paused, NeedsLogin, Completed, Failed, Interrupted }

public sealed class CourseItem : ObservableObject
{
    private string _url = "";
    private int? _progress;
    private CourseState _state;
    private string _activity = "";
    private int? _currentModule;
    private int? _totalModules;
    private bool? _workerWindowShown;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get => _url; set => Set(ref _url, value); }
    public int? Progress { get => _progress; set { if (Set(ref _progress, value.HasValue ? Math.Clamp(value.Value, 0, 100) : null)) Refresh(); } }
    public CourseState State { get => _state; set { if (Set(ref _state, value)) Refresh(); } }
    public string Activity { get => _activity; set => Set(ref _activity, value); }
    public int? CurrentModule { get => _currentModule; set { if (Set(ref _currentModule, value)) Refresh(); } }
    public int? TotalModules { get => _totalModules; set { if (Set(ref _totalModules, value)) Refresh(); } }
    public DateTimeOffset? LastUpdated { get; set; }
    [JsonIgnore] public bool IsRunning => State is CourseState.Starting or CourseState.Running or CourseState.Stopping;
    [JsonIgnore] public bool CanStart => !IsRunning && State != CourseState.Queued;
    [JsonIgnore] public bool CanStop => IsRunning || State == CourseState.Queued;
    [JsonIgnore] public bool CanEdit => !CanStop;
    [JsonIgnore] public bool CanToggleWorkerWindow => _workerWindowShown.HasValue;
    [JsonIgnore] public string WorkerWindowButtonText => _workerWindowShown == true ? "Ẩn" : "Hiện";
    [JsonIgnore] public string WorkerWindowToolTip => _workerWindowShown switch
    {
        true => "Ẩn cửa sổ WebView2; khóa học vẫn tiếp tục chạy.",
        false => "Hiện lại cửa sổ WebView2 đang chạy, không khởi động thêm Worker.",
        _ => "Chỉ dùng khi cửa sổ Worker của khóa này đang chạy."
    };
    public void SetWorkerWindowShown(bool? shown)
    {
        if (_workerWindowShown == shown) return;
        _workerWindowShown = shown;
        Notify(nameof(CanToggleWorkerWindow)); Notify(nameof(WorkerWindowButtonText)); Notify(nameof(WorkerWindowToolTip));
    }
    [JsonIgnore] public string ProgressText => Progress.HasValue ? $"{Progress}%" : "Chưa có tiến độ";
    [JsonIgnore] public string StatusText => State switch
    {
        CourseState.Queued => "Đang chờ lượt", CourseState.Starting => "Đang mở khóa",
        CourseState.Running => "Đang chạy", CourseState.Stopping => "Đang dừng",
        CourseState.Paused => "Đã dừng", CourseState.NeedsLogin => "Cần đăng nhập",
        CourseState.Completed => "Xong auto", CourseState.Failed => "Cần kiểm tra",
        CourseState.Interrupted => "Bị gián đoạn", _ => "Sẵn sàng"
    };
    public void Refresh()
    {
        Notify(nameof(IsRunning)); Notify(nameof(CanStart)); Notify(nameof(CanStop));
        Notify(nameof(CanEdit)); Notify(nameof(ProgressText)); Notify(nameof(StatusText));
    }
}

public sealed class CustomerOrder : ObservableObject
{
    private string _name = "";
    private string _sessionNote = "";
    private bool _sessionBusy;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string ProfilePath { get; set; } = "";
    public string Source { get; set; } = "Thủ công";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public ObservableCollection<CourseItem> Courses { get; set; } = [];
    public List<string> ObservedExternalUrls { get; set; } = [];
    // Optional website provenance keeps version-1 local data readable after an upgrade.
    // Identity is a stable device ID; customer labels are never used for deduplication.
    public string? WebsiteDeviceId { get; set; }
    public string? WebsiteAttemptId { get; set; }
    public DateTimeOffset? WebsiteCompletedAtUtc { get; set; }
    public string? CourseraUserId { get; set; }
    [JsonIgnore] public string SessionNote { get => _sessionNote; set => Set(ref _sessionNote, value); }
    [JsonIgnore] public bool SessionBusy { get => _sessionBusy; set => Set(ref _sessionBusy, value); }
    [JsonIgnore] public int CompletedCount => Courses.Count(x => x.State == CourseState.Completed);
    [JsonIgnore] public int CourseCount => Courses.Count;
    [JsonIgnore] public double OverallProgress => Courses.Count == 0 ? 0 : Courses.Average(x => x.Progress ?? 0);
    [JsonIgnore] public string SummaryText => $"{CompletedCount}/{Courses.Count} khóa xong auto";
    public void Refresh()
    {
        Notify(nameof(CompletedCount)); Notify(nameof(CourseCount)); Notify(nameof(OverallProgress)); Notify(nameof(SummaryText));
    }
}

public sealed class SavedRun
{
    public string JobId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string? CourseId { get; set; }
    public int ProcessId { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string Mode { get; set; } = "course";
}

public sealed class ManagerData
{
    public int Version { get; set; } = 1;
    public int MaxParallel { get; set; } = 5;
    public List<CustomerOrder> Orders { get; set; } = [];
    public List<SavedRun> Runs { get; set; } = [];
    public List<string> ImportRoots { get; set; } = [@"D:\Acose\WorkerProfiles"];
}

public static class InputRules
{
    public static string FullName(string value)
    {
        string name = Regex.Replace(value.Trim(), @"\s+", " ");
        if (name.Length is < 2 or > 120 || name.Any(char.IsControl))
            throw new InvalidOperationException("Hãy nhập đầy đủ họ và tên khách (từ 2 đến 120 ký tự).");
        return name.ToUpper(CultureInfo.GetCultureInfo("vi-VN"));
    }
    public static string CourseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? url) ||
            url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) || !url.IsDefaultPort ||
            !(url.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
              url.Host.EndsWith(".coursera.org", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Link khóa học phải dùng https:// và thuộc coursera.org.");
        return url.AbsoluteUri;
    }
    public static List<string> CourseUrls(IEnumerable<string> values) => values
        .SelectMany(x => x.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(CourseUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
