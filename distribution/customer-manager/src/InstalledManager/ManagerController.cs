using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Acose.CustomerManager.Runtime;

namespace Acose.CustomerManager;

public sealed class ManagerController : ObservableObject
{
    private readonly ManagerStore _store;
    private readonly string _dataDirectory;
    public ActivitySkipSettings ActivitySkips { get; private set; } = new();

    public void SetActivitySkips(bool skipPdf, bool skipReview)
    {
        if (_preview) return;
        var settings = new ActivitySkipSettings(skipPdf, skipReview);
        settings.Save(_dataDirectory);
        ActivitySkips = settings;
        StatusMessage = "Đã lưu dạng bài bỏ qua. Áp dụng cho các khóa mở sau khi lưu; dừng và chạy lại khóa đang mở để dùng lựa chọn mới.";
    }
    private readonly WorkerBridge _bridge = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ActiveRun> _active = [];
    private readonly bool _preview;
    private ManagerData _data = new();
    private CustomerOrder? _selectedOrder;
    private string _searchText = "";
    private string _statusMessage = "Đang mở sổ khách hàng…";
    private bool _isBusy;
    private bool _closing;
    private bool _pumping;
    private int _ticks;
    private sealed record ActiveRun(SavedRun Saved, Process Process, WorkerJobRequest Request);

    public ManagerController(string dataDirectory, string enginePath, bool preview = false)
    {
        _store = new ManagerStore(dataDirectory);
        _dataDirectory = dataDirectory;
        if (!preview) ActivitySkips = ActivitySkipSettings.Load(dataDirectory);
        EnginePath = Path.GetFullPath(enginePath);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _preview = preview;
        _bridge.Reported += report => _dispatcher.BeginInvoke(() => HandleReport(report));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) =>
        {
            try
            {
                ReapExited();
                RefreshWorkerWindows();
                if (++_ticks % 10 == 0) await RefreshAsync();
                await PumpAsync();
            }
            catch (Exception ex) { StatusMessage = ex.Message; }
        };
    }
    public ObservableCollection<CustomerOrder> Orders { get; } = [];
    public ObservableCollection<CustomerOrder> VisibleOrders { get; } = [];
    public CustomerOrder? SelectedOrder { get => _selectedOrder; set => Set(ref _selectedOrder, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) Filter(); } }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string EnginePath { get; }
    public string WebsiteProfileRoot => _data.ImportRoots.FirstOrDefault() ?? "";
    public bool HasActiveWork => _active.Count != 0 || Orders.Any(o => o.Courses.Any(c => c.State == CourseState.Queued));
    public string SummaryText => $"{Orders.Count} khách · {Orders.Sum(o => o.Courses.Count(c => c.IsRunning))} khóa đang chạy · {Orders.Sum(o => o.Courses.Count(c => c.State == CourseState.Queued))} đang chờ";
    public int MaxParallel
    {
        get => _data.MaxParallel;
        set
        {
            if (value is < 1 or > 64) throw new InvalidOperationException("Số khóa chạy đồng thời phải từ 1 đến 64.");
            _data.MaxParallel = value; Notify(); Persist();
        }
    }
    public async Task InitializeAsync()
    {
        if (_preview) { AddPreviewData(); return; }
        IsBusy = true;
        try
        {
            _data = _store.Load();
            string? recoveryWarning = _store.LastWarning;
            RecoverInterruptedRuns();
            int imported = _store.ImportExisting(_data);
            foreach (var order in _data.Orders) Orders.Add(order);
            await _bridge.StartAsync();
            _timer.Start();
            Changed(); Filter(); SelectedOrder = VisibleOrders.FirstOrDefault();
            Persist();
            StatusMessage = recoveryWarning ?? _store.LastWarning ?? (imported > 0
                ? $"Đã nhận {imported} khách từ profile trên máy. Chưa tự chạy khóa học."
                : "Sẵn sàng. Chọn khách hoặc thêm đơn mới.");
        }
        finally { IsBusy = false; }
    }
    private void RecoverInterruptedRuns()
    {
        // Only recover processes recorded by this manager, matching both executable and start time.
        foreach (var saved in _data.Runs)
        {
            try
            {
                using Process process = Process.GetProcessById(saved.ProcessId);
                if (!process.HasExited &&
                    string.Equals(saved.ExecutablePath, EnginePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(process.MainModule?.FileName, EnginePath, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs((process.StartTime.ToUniversalTime() - saved.StartTimeUtc).TotalSeconds) < 1)
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(5000);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("Một cửa sổ auto của lần chạy trước chưa đóng được. Hãy đóng cửa sổ đó rồi mở lại trình quản lý.");
            }
        }
        _data.Runs.Clear();
        foreach (var course in _data.Orders.SelectMany(o => o.Courses))
            if (course.IsRunning || course.State == CourseState.Queued)
            {
                course.State = CourseState.Interrupted;
                course.Activity = "Phiên quản lý trước đã kết thúc. Bấm Chạy để tiếp tục.";
            }
    }
    public Task RefreshAsync()
    {
        if (_preview || _closing) return Task.CompletedTask;
        int imported = _store.ImportExisting(_data);
        foreach (var order in _data.Orders.Where(o => !Orders.Contains(o))) Orders.Add(order);
        foreach (var order in Orders)
        {
            if (!order.SessionBusy && string.IsNullOrEmpty(order.SessionNote))
                order.SessionNote = order.Source == "Website"
                    ? "Dùng profile khách đã đăng nhập trên PC này. Nếu phiên hết hạn, cần đăng nhập lại."
                    : "Mở kiểm tra để xem hoặc đăng nhập tài khoản của khách.";
        }
        Changed(); Filter(); Persist();
        if (imported > 0) StatusMessage = $"Đã nhận thêm {imported} khách từ profile trên máy. Chọn khóa rồi bấm Chạy khi bạn muốn.";
        else if (_store.LastWarning != null) StatusMessage = _store.LastWarning;
        return Task.CompletedTask;
    }
    public void SetWebsiteProfileRoot(string value)
    {
        GuardPreview();
        string root = WebsiteProfileImportSettings.NormalizeRoot(value);
        List<string> previous = _data.ImportRoots;
        _data.ImportRoots = root.Length == 0 ? [] : [root];
        try { Persist(); }
        catch { _data.ImportRoots = previous; throw; }
        Notify(nameof(WebsiteProfileRoot));
        StatusMessage = root.Length == 0
            ? "Đã tắt nhận khách mới từ website; khách và profile đã nhập được giữ nguyên."
            : "Đã bật nhận profile website trên PC này. Bấm Làm mới hoặc chờ tối đa 10 giây.";
    }
    public async Task<CustomerOrder> AddOrderAsync(string name, IEnumerable<string> links)
    {
        GuardPreview();
        string fullName = InputRules.FullName(name);
        List<string> urls = InputRules.CourseUrls(links);
        var order = new CustomerOrder { Name = fullName, Source = "Thủ công" };
        order.ProfilePath = _store.AllocateProfilePath(order.Id);
        foreach (string url in urls) order.Courses.Add(new CourseItem { Url = url });
        _data.Orders.Add(order); Orders.Add(order); Persist();
        SearchText = ""; Filter(); SelectedOrder = order; Changed();
        await OpenLoginAsync(order);
        return order;
    }
    public void RenameOrder(CustomerOrder order, string name)
    {
        order.Name = InputRules.FullName(name); Persist(); Filter(); Changed();
        StatusMessage = "Đã lưu họ tên khách.";
    }
    public void AddLinks(CustomerOrder order, IEnumerable<string> links)
    {
        List<string> urls = InputRules.CourseUrls(links);
        if (urls.Count == 0) throw new InvalidOperationException("Hãy nhập ít nhất một link khóa học.");
        int added = 0;
        foreach (string url in urls)
            if (!order.Courses.Any(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase)))
            { order.Courses.Add(new CourseItem { Url = url }); added++; }
        Persist(); Changed(); StatusMessage = added > 0 ? $"Đã thêm {added} khóa cho {order.Name}." : "Các link này đã có trong đơn.";
    }
    public void EditLink(CustomerOrder order, CourseItem course, string url)
    {
        if (!course.CanEdit || HasBrowse(order, course.Url)) throw new InvalidOperationException("Hãy dừng khóa và đóng cửa sổ kiểm tra của khóa trước khi sửa link.");
        string valid = InputRules.CourseUrl(url);
        if (order.Courses.Any(c => c != course && string.Equals(c.Url, valid, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Link này đã có trong đơn.");
        if (!string.Equals(course.Url, valid, StringComparison.OrdinalIgnoreCase))
        {
            course.Url = valid; course.Progress = null; course.State = CourseState.Ready;
            course.CurrentModule = null; course.TotalModules = null; course.Activity = "";
        }
        Persist(); Changed(); StatusMessage = "Đã lưu link khóa học.";
    }
    public void RemoveLink(CustomerOrder order, CourseItem course)
    {
        if (!course.CanEdit || HasBrowse(order, course.Url)) throw new InvalidOperationException("Hãy dừng khóa và đóng cửa sổ kiểm tra trước khi xóa link.");
        order.Courses.Remove(course); Persist(); Changed(); StatusMessage = "Đã xóa link khỏi đơn.";
    }
    public Task OpenLoginAsync(CustomerOrder order) => OpenBrowseAsync(order, null, "https://www.coursera.org/?authMode=login");
    public Task OpenCourseAsync(CustomerOrder order, CourseItem course) => OpenBrowseAsync(order, course, course.Url);
    private Task OpenBrowseAsync(CustomerOrder order, CourseItem? course, string url)
    {
        GuardPreview(); ValidateEngine();
        if (HasBrowse(order, url))
        { StatusMessage = "Cửa sổ kiểm tra này đang mở; hãy chọn cửa sổ ACOSE trên thanh tác vụ."; return Task.CompletedTask; }
        var request = new WorkerJobRequest(Guid.NewGuid().ToString("N"), order.Id, order.Name, order.ProfilePath, url, "browse");
        Launch(order, course, request);
        order.SessionBusy = true;
        order.SessionNote = course == null ? "Đang mở đăng nhập. Hoàn tất đăng nhập rồi đóng cửa sổ đó." : "Đang mở kiểm tra bằng tay; cửa sổ này không tự chạy auto.";
        StatusMessage = order.SessionNote; Changed();
        return Task.CompletedTask;
    }
    private bool HasBrowse(CustomerOrder order, string url) => _active.Values.Any(r => r.Saved.OrderId == order.Id && r.Saved.Mode == "browse" && r.Request.Url == url);
    public async Task StartCourseAsync(CustomerOrder order, CourseItem course)
    {
        GuardPreview(); ValidateEngine();
        if (!course.CanStart) return;
        if (course.State == CourseState.Completed)
        { course.Progress = null; course.CurrentModule = null; course.TotalModules = null; }
        course.State = CourseState.Queued; course.Activity = "Đã xếp vào hàng đợi.";
        Persist(); Changed(); await PumpAsync();
    }
    public async Task StartOrderAsync(CustomerOrder order)
    {
        GuardPreview(); ValidateEngine();
        foreach (var course in order.Courses.Where(c => c.CanStart && c.State != CourseState.Completed))
        { course.State = CourseState.Queued; course.Activity = "Đã xếp vào hàng đợi."; }
        Persist(); Changed(); await PumpAsync();
        StatusMessage = $"Đã xếp các khóa chưa xong của {order.Name}; tối đa {MaxParallel} khóa chạy đồng thời.";
    }
    private Task PumpAsync()
    {
        if (_pumping || _closing || _preview) return Task.CompletedTask;
        if (!Orders.Any(o => o.Courses.Any(c => c.State == CourseState.Queued))) return Task.CompletedTask;
        _pumping = true;
        try
        {
            int slots = MaxParallel - _active.Values.Count(r => r.Saved.Mode == "course");
            foreach (var pair in Orders.SelectMany(o => o.Courses.Select(c => (Order: o, Course: c)))
                         .Where(p => p.Course.State == CourseState.Queued).Take(Math.Max(0, slots)).ToList())
            {
                try
                {
                    var c = pair.Course;
                    var request = new WorkerJobRequest(Guid.NewGuid().ToString("N"), pair.Order.Id, pair.Order.Name,
                        pair.Order.ProfilePath, c.Url, "course", c.Progress ?? 0, c.CurrentModule, c.TotalModules);
                    c.State = CourseState.Starting; c.Activity = "Đang mở đúng phiên đăng nhập và khóa học…";
                    Launch(pair.Order, c, request);
                }
                catch (Exception ex) { pair.Course.State = CourseState.Failed; pair.Course.Activity = ex.Message; StatusMessage = ex.Message; }
            }
            Persist(); Changed();
        }
        finally { _pumping = false; }
        return Task.CompletedTask;
    }
    private void Launch(CustomerOrder order, CourseItem? course, WorkerJobRequest request)
    {
        request = request with { SkipPdf = ActivitySkips.SkipPdf, SkipReview = ActivitySkips.SkipReview };
        _bridge.Register(request);
        Process? process = null;
        try
        {
            process = WorkerLauncher.Launch(EnginePath, _bridge, request);
            var saved = new SavedRun { JobId = request.JobId, OrderId = order.Id, CourseId = course?.Id,
                ProcessId = process.Id, StartTimeUtc = process.StartTime.ToUniversalTime(), ExecutablePath = EnginePath, Mode = request.Mode };
            _active.Add(request.JobId, new ActiveRun(saved, process, request));
            _data.Runs.Add(saved); Persist();
        }
        catch
        {
            if (process != null) { try { if (!process.HasExited) process.Kill(false); } catch { } process.Dispose(); }
            _active.Remove(request.JobId); _data.Runs.RemoveAll(r => r.JobId == request.JobId);
            _bridge.Forget(request.JobId); throw;
        }
    }
    private void RefreshWorkerWindows()
    {
        foreach (var order in Orders)
        foreach (var course in order.Courses)
        {
            var run = _active.Values.FirstOrDefault(r => r.Saved.OrderId == order.Id &&
                r.Saved.CourseId == course.Id && r.Saved.Mode == "course");
            course.SetWorkerWindowShown(run is null ? null : WorkerWindow.Read(run.Process, run.Saved)?.IsShown);
        }
    }

    public async Task ToggleWorkerWindowAsync(CustomerOrder order, CourseItem course)
    {
        GuardPreview();
        if (_closing || !Orders.Contains(order) || !order.Courses.Contains(course)) return;
        var run = _active.Values.FirstOrDefault(r => r.Saved.OrderId == order.Id &&
            r.Saved.CourseId == course.Id && r.Saved.Mode == "course");
        var window = run is null ? null : WorkerWindow.Read(run.Process, run.Saved);
        if (run is null || window is null)
        {
            course.SetWorkerWindowShown(null);
            StatusMessage = "Worker chưa mở cửa sổ hoặc đã dừng; không có cửa sổ để ẩn/hiện.";
            return;
        }
        bool show = !window.IsShown;
        if (WorkerWindow.RequestVisibility(run.Process, run.Saved, show))
        {
            // Showing a window in another process is asynchronous. Confirm actual state.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(100);
                if (!_active.ContainsKey(run.Saved.JobId)) break;
                var current = WorkerWindow.Read(run.Process, run.Saved);
                if (current?.IsShown != show) continue;
                course.SetWorkerWindowShown(show);
                StatusMessage = show ? $"Đã hiện cửa sổ Worker của {order.Name}."
                    : $"Đã ẩn cửa sổ Worker của {order.Name}; khóa học vẫn tiếp tục chạy.";
                return;
            }
        }
        RefreshWorkerWindows();
        StatusMessage = "Chưa đổi được trạng thái cửa sổ. Worker có thể đang bận hoặc đã đóng; hãy thử lại.";
    }

    public async Task StopCourseAsync(CustomerOrder order, CourseItem course)
    {
        if (course.State == CourseState.Queued) { course.State = CourseState.Paused; course.Activity = "Đã bỏ khỏi hàng đợi."; }
        var runs = _active.Values.Where(r => r.Saved.OrderId == order.Id && r.Saved.CourseId == course.Id && r.Saved.Mode == "course").ToList();
        if (runs.Count > 0) { course.State = CourseState.Stopping; Changed(); }
        foreach (var run in runs) await StopRunAsync(run);
        ReapExited(); Persist(); Changed();
    }
    public async Task StopOrderAsync(CustomerOrder order)
    {
        var tasks = order.Courses.Where(c => c.CanStop).Select(c => StopCourseAsync(order, c)).ToArray();
        await Task.WhenAll(tasks);
        StatusMessage = $"Đã dừng các khóa của {order.Name}. Cửa sổ kiểm tra có thể đóng riêng.";
    }
    private async Task StopRunAsync(ActiveRun run)
    {
        try
        {
            if (run.Process.HasExited) return;
            if (run.Saved.Mode == "course")
            {
                _bridge.RequestPause(run.Saved.JobId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await run.Process.WaitForExitAsync(timeout.Token); return; }
                catch (OperationCanceledException) { }
            }
            else
            {
                run.Process.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await run.Process.WaitForExitAsync(timeout.Token); return; }
                catch (OperationCanceledException) { }
            }
            if (!run.Process.HasExited)
            {
                // WebView2 may share its browser among multiple courses: stop only this worker.
                run.Process.Kill(entireProcessTree: false);
                await run.Process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { }
    }
    private void HandleReport(WorkerReport report)
    {
        if (!_active.TryGetValue(report.JobId, out var run)) return;
        var order = Orders.FirstOrDefault(o => o.Id == run.Saved.OrderId);
        if (order == null) return;
        if (run.Saved.Mode == "browse")
        {
            if (report.IdentityName != null) order.SessionNote = "Đã nhận danh tính Coursera. Bạn có thể đóng cửa sổ kiểm tra khi xong.";
            else if (report.Status == "needs_login") order.SessionNote = "Hãy đăng nhập trong cửa sổ Coursera vừa mở.";
            else if (!string.IsNullOrWhiteSpace(report.Activity)) order.SessionNote = report.Activity;
        }
        else if (order.Courses.FirstOrDefault(c => c.Id == run.Saved.CourseId) is { } course)
        {
            // Once terminal, late running heartbeats must not overwrite the result.
            if (course.State is CourseState.Completed or CourseState.Failed or CourseState.NeedsLogin or CourseState.Paused)
            { if (report.Status == "running") return; }
            if (report.Progress.HasValue) course.Progress = report.Progress;
            if (report.CurrentModule.HasValue) course.CurrentModule = report.CurrentModule;
            if (report.TotalModules.HasValue) course.TotalModules = report.TotalModules;
            if (!string.IsNullOrWhiteSpace(report.Activity)) course.Activity = report.Activity;
            if (!string.IsNullOrWhiteSpace(report.ManualActionReason)) course.Activity = report.ManualActionReason;
            course.State = report.Status switch
            {
                "completed" => CourseState.Completed,
                "failed" => course.State == CourseState.NeedsLogin ? CourseState.NeedsLogin : CourseState.Failed,
                "needs_login" => CourseState.NeedsLogin,
                "waiting_user" or "paused" or "cancelled" => CourseState.Paused,
                "running" => course.State == CourseState.Stopping ? CourseState.Stopping : CourseState.Running,
                _ => course.State
            };
            if (course.State == CourseState.Completed) course.Progress = 100;
            course.LastUpdated = DateTimeOffset.Now;
        }
        Changed();
        try { Persist(); } catch (Exception ex) { StatusMessage = ex.Message; }
    }
    private void ReapExited()
    {
        foreach (var run in _active.Values.ToList())
        {
            if (!run.Process.HasExited) continue;
            var order = Orders.FirstOrDefault(o => o.Id == run.Saved.OrderId);
            var course = order?.Courses.FirstOrDefault(c => c.Id == run.Saved.CourseId);
            if (run.Saved.Mode == "course" && course != null)
            {
                course.SetWorkerWindowShown(null);
                if (course.State == CourseState.Stopping)
                { course.State = CourseState.Paused; course.Activity = "Đã dừng theo yêu cầu."; }
                else if (course.IsRunning)
                { course.State = CourseState.Interrupted; course.Activity = "Cửa sổ auto đã đóng nhưng chưa báo hoàn tất. Mở kiểm tra hoặc chạy lại."; }
            }
            _active.Remove(run.Saved.JobId); _data.Runs.RemoveAll(r => r.JobId == run.Saved.JobId);
            _bridge.Forget(run.Saved.JobId); run.Process.Dispose();
            if (order != null)
            {
                order.SessionBusy = _active.Values.Any(r => r.Saved.OrderId == order.Id && r.Saved.Mode == "browse");
                if (!order.SessionBusy && run.Saved.Mode == "browse") order.SessionNote = "Đã đóng cửa sổ kiểm tra. Chọn khóa để chạy khi tài khoản đã đăng nhập.";
            }
            Persist(); Changed();
        }
    }
    public async Task ShutdownAsync()
    {
        if (_closing) return;
        _closing = true; _timer.Stop();
        foreach (var c in Orders.SelectMany(o => o.Courses).Where(c => c.State == CourseState.Queued)) c.State = CourseState.Paused;
        foreach (var c in Orders.SelectMany(o => o.Courses).Where(c => c.IsRunning)) c.State = CourseState.Stopping;
        await Task.WhenAll(_active.Values.ToList().Select(StopRunAsync));
        ReapExited(); Persist(); await _bridge.DisposeAsync();
    }
    private void ValidateEngine()
    {
        if (_closing) throw new InvalidOperationException("Trình quản lý đang đóng.");
        if (!File.Exists(EnginePath)) throw new InvalidOperationException("Thiếu bộ tự động hóa trong thư mục Engine. Hãy giữ nguyên toàn bộ thư mục công cụ.");
    }
    private void GuardPreview()
    { if (_preview) throw new InvalidOperationException("Đây là dữ liệu minh họa; bản xem trước không chạy tài khoản thật."); }
    private void Persist() { if (!_preview) _store.Save(_data); }
    private void Changed()
    {
        foreach (var order in Orders) order.Refresh();
        Notify(nameof(SummaryText)); Notify(nameof(HasActiveWork));
    }
    private void Filter()
    {
        string query = SearchText.Trim();
        var matches = Orders.Where(o => query.Length == 0 || o.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            o.Courses.Any(c => c.Url.Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(o => o.Name).ToList();
        if (VisibleOrders.SequenceEqual(matches)) return;
        var selected = SelectedOrder;
        VisibleOrders.Clear(); foreach (var order in matches) VisibleOrders.Add(order);
        SelectedOrder = selected != null && matches.Contains(selected) ? selected : matches.FirstOrDefault();
    }
    private void AddPreviewData()
    {
        var first = new CustomerOrder { Name = "NGUYỄN MINH ANH", Source = "Đã có trên máy", SessionNote = "Phiên đăng nhập của khách được giữ riêng trên máy." };
        first.Courses.Add(new CourseItem { Url = "https://www.coursera.org/learn/python", State = CourseState.Completed, Progress = 100, Activity = "Phần tự động đã hoàn tất. Mở kiểm tra để xem kết quả." });
        first.Courses.Add(new CourseItem { Url = "https://www.coursera.org/learn/learning-how-to-learn", State = CourseState.Running, Progress = 64, CurrentModule = 3, TotalModules = 4, Activity = "Đang xử lý nội dung trong Module 3." });
        first.Courses.Add(new CourseItem { Url = "https://www.coursera.org/learn/speak-english-professionally", State = CourseState.Ready });
        var second = new CustomerOrder { Name = "TRẦN HOÀNG NAM", Source = "Thủ công" };
        second.Courses.Add(new CourseItem { Url = "https://www.coursera.org/learn/excel-basics-data-analysis-ibm", State = CourseState.Running, Progress = 32 });
        var third = new CustomerOrder { Name = "LÊ THẢO NGUYÊN", Source = "Thủ công" };
        third.Courses.Add(new CourseItem { Url = "https://www.coursera.org/learn/english-for-career-development", State = CourseState.NeedsLogin });
        foreach (var order in new[] { first, second, third }) { _data.Orders.Add(order); Orders.Add(order); }
        Filter(); SelectedOrder = first; Changed(); StatusMessage = "BẢN XEM TRƯỚC · Dữ liệu minh họa, không kết nối tài khoản thật.";
    }
}
