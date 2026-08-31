using AutomationPlatform.Presentation.Services;
using AutomationPlatform.Presentation.ViewModels;
using System.Windows;
using System.Threading.Tasks;
using System;
using System.Text;
using System.Text.Json;

namespace AutomationPlatform.Presentation;

public partial class MainWindow : Window
{
    private enum CourseraProfileBootstrapState
    {
        Idle,
        AwaitingTarget,
        LoadingProfile,
        ReturningTarget
    }

    private sealed record CourseraIdentity(string UserId, string FullName);

    private enum DirectLoginOutcomeKind
    {
        Success,
        ManualRequired,
        Failed,
        Cancelled
    }

    private sealed record DirectLoginOutcome(
        DirectLoginOutcomeKind Kind,
        string Code,
        string Message);

    private sealed record CourseProgressSnapshot(
        int Progress,
        int CurrentModule,
        int TotalModules);

    private sealed record CourseLandingProbe(
        string State,
        string? Href,
        string? Label);

    private sealed record QuizAnswerResolution(
        bool Success,
        List<List<string>>? Answers,
        string ProviderName,
        string Message,
        bool IsAiFailure)
    {
        public static QuizAnswerResolution Completed(
            List<List<string>> answers,
            string providerName) =>
            new(true, answers, providerName, string.Empty, false);

        public static QuizAnswerResolution ValidationFailed(string message) =>
            new(false, null, string.Empty, message, false);

        public static QuizAnswerResolution AiFailed(string message) =>
            new(false, null, string.Empty, message, true);
    }

    private readonly MainViewModel _viewModel;
    private readonly AiCompletionService _aiCompletionService;
    private readonly WorkerLaunchOptions _workerLaunchOptions;
    private readonly CentralWorkerClient _centralWorkerClient;
    private List<QuizFeedbackDto> _quizFeedbackList = new List<QuizFeedbackDto>();
    private readonly System.Threading.SemaphoreSlim _courseraProfileNameLock = new(1, 1);
    private readonly System.Threading.SemaphoreSlim _popupWatchdogLock = new(1, 1);
    private System.Windows.Threading.DispatcherTimer? _popupWatchdogTimer;
    private CourseraIdentity? _courseraIdentity;
    private CourseraProfileBootstrapState _courseraProfileBootstrapState;
    private Uri? _courseraProfileReturnUri;
    private int _courseraProfileBootstrapGeneration;
    private ulong? _courseraProfileExpectedNavigationId;
    private Uri? _courseraProfilePendingNavigationUri;
    private bool _courseraProfileAcceptLoginContinuation;
    private Uri? _suppressLtiNewWindowSourceUri;
    private DateTimeOffset _suppressLtiNewWindowUntilUtc;
    private bool _suppressedLtiNewWindow;
    private readonly List<Microsoft.Web.WebView2.Core.CoreWebView2Controller> _hiddenLtiControllers = new();
    private readonly System.Threading.SemaphoreSlim _workerHeartbeatLock = new(1, 1);
    private readonly System.Threading.SemaphoreSlim _directLoginStatusReportLock = new(1, 1);
    private System.Windows.Threading.DispatcherTimer? _workerHeartbeatTimer;
    private CourseProgressSnapshot? _lastCourseProgressSnapshot;
    private bool _courseHasSkippedLaunchAppItems;
    // Coursera can label the same activity as "Ungraded App Item" in the
    // outline and "Practice App Item" on the activity page. Keep the exact
    // paths that were deliberately skipped so a later outline scan cannot
    // reopen them when labels or DOM structure differ.
    private readonly HashSet<string> _skippedLaunchAppItemPaths =
        new(StringComparer.OrdinalIgnoreCase);
    // A module that contains only completed or configured-to-skip activities
    // is complete for this automation run even if Coursera keeps its own
    // module badge at Incomplete.
    private readonly HashSet<int> _automationCompletedModuleNumbers = [];
    private bool _isScanningCourseOutline;
    private bool _courseHasSkippedPeerItems;
    // An assessment can be accepted by Coursera while its grade is still
    // pending.  Keep the canonical lesson paths for the current course job so
    // the course scanner does not reopen the same accepted submission.
    private readonly HashSet<string> _pendingGradedResultPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _courseHasPendingGradedResults;
    // A discussion can be visibly submitted before Coursera adds the sidebar
    // completion icon. Keep its canonical path out of the scanner if it has no
    // usable Next control, otherwise the course home sweep would reopen it.
    private readonly HashSet<string> _submittedDiscussionPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _courseHasSubmittedDiscussionItems;
    private bool _isHandlingCourseLanding;
    private bool _courseJobCompletionReported;
    private bool _coursePauseInProgress;
    private int _courseLandingFallbackCount;
    private bool _workerClosing;
    private bool _workerClaimPending;
    private CancellationTokenSource? _directLoginLifetime;
    private bool _directLoginActive;
    private bool _directLoginTerminal;
    private string _directLoginStatus = "claimed";
    private string? _directLoginChallengeNumber;
    private bool _directLoginStatusDirty;
    private readonly System.Threading.SemaphoreSlim _directLoginPopupLock = new(1, 1);
    private Window? _directLoginOAuthWindow;
    private Microsoft.Web.WebView2.Wpf.WebView2? _directLoginOAuthBrowser;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _directLoginOAuthWebView;
    private Uri? _directLoginOAuthExpectedRedirectUri;
    private string? _directLoginOAuthFailure;
    private TaskCompletionSource<bool>? _directLoginOAuthPopupOpened;
    private static readonly TimeSpan DirectLoginWebViewOperationTimeout = TimeSpan.FromSeconds(5);

    private bool ShouldSkipGradedAppItems =>
        _centralWorkerClient.CurrentJob?.SkipGradedAppItems ?? true;

    private bool ShouldSkipPracticeAppItems =>
        _centralWorkerClient.CurrentJob?.SkipPracticeAppItems ?? true;

    private bool IsInteractiveBrowseSession => string.Equals(
        _centralWorkerClient.CurrentJob?.Mode,
        "browse",
        StringComparison.OrdinalIgnoreCase);

    // Peer-graded submissions and reviews must be completed by the learner.
    // Treat them as an explicit, visible skip so the worker can keep processing
    // the remaining supported lessons in the course.
    private const bool ShouldSkipPeerItems = true;

    public MainWindow(
        MainViewModel viewModel,
        AiCompletionService aiCompletionService,
        WorkerLaunchOptions workerLaunchOptions,
        CentralWorkerClient centralWorkerClient)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _aiCompletionService = aiCompletionService;
        _workerLaunchOptions = workerLaunchOptions;
        _centralWorkerClient = centralWorkerClient;
        _workerClaimPending = _workerLaunchOptions.IsInteractiveProfile;
        DataContext = _viewModel;

        if (_workerLaunchOptions.IsDirectLogin || _workerLaunchOptions.IsInteractiveProfile)
        {
            // Direct-login and manual profile sessions are intentionally visible
            // on the Worker Host. Course automation continues to run hidden.
            ShowInTaskbar = true;
            ShowActivated = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = _workerLaunchOptions.IsInteractiveProfile
                ? ResizeMode.CanResize
                : ResizeMode.CanMinimize;
            Width = 1280;
            Height = 900;
        }

        MainWebView.NavigationCompleted += MainWebView_NavigationCompleted;
        MainWebView.NavigationStarting += MainWebView_NavigationStarting;
        MainWebView.SourceChanged += MainWebView_SourceChanged;

        this.Loaded += MainWindow_Loaded;
        this.Closing += MainWindow_Closing;
        this.Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Thay vì dùng ZoomFactor (chỉ scale hình ảnh nhưng không đổi CSS Viewport),
            // Ta dùng DevTools Protocol để ép cứng Viewport logic luôn là 1920px.
            MainWebView.SizeChanged += async (s, args) =>
            {
                if (!_workerLaunchOptions.IsCourseAutomation)
                {
                    return;
                }

                try
                {
                    if (MainWebView.CoreWebView2 == null) return;

                    double targetWidth = 1920.0;
                    double actualWidth = MainWebView.ActualWidth;
                    double actualHeight = MainWebView.ActualHeight;

                    if (actualWidth > 0 && actualHeight > 0)
                    {
                        double scale = actualWidth < targetWidth ? actualWidth / targetWidth : 1.0;
                        int virtualHeight = (int)(actualHeight / scale);

                        string payload = $@"{{
                            ""width"": 1920,
                            ""height"": {virtualHeight},
                            ""deviceScaleFactor"": 1,
                            ""mobile"": false,
                            ""scale"": {scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                        }}";

                        try
                        {
                            await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", payload);
                        }
                        catch { }
                    }
                }
                catch { }
            };

            // Cài đặt User-Agent chuyên dụng đã vượt qua bài test Lockdown Browser
            MainWebView.CoreWebView2InitializationCompleted += async (s, args) => {
                if (args.IsSuccess) {
                    if (_workerLaunchOptions.IsCourseAutomation)
                    {
                        MainWebView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 coursera-locking-browser/0.6.5";
                    }
                    else if (_workerLaunchOptions.IsDirectLogin)
                    {
                        // Google rejects the old lockdown-browser UA. Keep the current WebView2 UA
                        // and prevent any credential/autofill persistence in this temporary profile.
                        MainWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                        MainWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                        MainWebView.CoreWebView2.ProcessFailed += (_, _) =>
                        {
                            if (_directLoginActive && !_directLoginTerminal)
                            {
                                _directLoginOAuthFailure =
                                    "Trình duyệt đăng nhập Google đã dừng đột ngột. Vui lòng thử lại.";
                                _directLoginOAuthPopupOpened?.TrySetResult(false);
                            }
                        };
                    }
                    else if (_workerLaunchOptions.IsInteractiveProfile)
                    {
                        // Keep the durable Coursera session/profile, but never
                        // persist credentials entered during a manual repair.
                        MainWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                        MainWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                    }
                    
                    // Chỉ chặn cửa sổ mới do chính cú bấm Launch App của automation tạo ra.
                    MainWebView.CoreWebView2.NewWindowRequested += MainWebView_NewWindowRequested;
                    MainWebView.CoreWebView2.NavigationStarting += CancelCourseraLockNavigation;
                    MainWebView.CoreWebView2.LaunchingExternalUriScheme +=
                        CancelCourseraLockExternalLaunch;

                    /* =========================================================================================
                     * GIẢI THÍCH LOGIC ÉP BUỘC GIAO DIỆN DESKTOP (1920px)
                     * =========================================================================================
                     * Mục đích: Coursera dùng CSS Media Queries để ẩn Sidebar nếu cửa sổ trình duyệt nhỏ.
                     * Giải pháp: Sử dụng Chrome DevTools Protocol (CDP) lệnh `Emulation.setDeviceMetricsOverride`.
                     * 
                     * Tại sao không dùng MainWebView.ZoomFactor?
                     * - ZoomFactor chỉ phóng to/thu nhỏ hình ảnh (như Ctrl + / Ctrl -). 
                     * - Khi cửa sổ nhỏ (VD: 900px), ZoomFactor sẽ làm chữ nhỏ lại, nhưng Coursera vẫn
                     *   thấy cửa sổ 900px -> kích hoạt giao diện Mobile -> Ẩn Sidebar.
                     * 
                     * Lợi ích của CDP (setDeviceMetricsOverride):
                     * - `width`: Bắt buộc trình duyệt "tin" rằng màn hình luôn rộng 1920px (Desktop chuẩn).
                     * - `scale`: Tự động co rút toàn bộ khung hình 1920px này chui lọt vào cửa sổ WPF thực tế.
                     * - Kết quả: Sidebar luôn hiển thị đầy đủ, không bao giờ bị chuyển sang chế độ Mobile.
                     * ========================================================================================= */
                    
                    if (_workerLaunchOptions.IsCourseAutomation)
                    {
                        // Gọi ngay 1 lần lúc vừa khởi tạo xong để không cần chờ resize
                        double targetWidth = 1920.0;
                        double actualWidth = MainWebView.ActualWidth;
                        double actualHeight = MainWebView.ActualHeight;

                        if (actualWidth > 0 && actualHeight > 0)
                        {
                            double scale = actualWidth < targetWidth ? actualWidth / targetWidth : 1.0;
                            int virtualHeight = (int)(actualHeight / scale);

                            string payload = $@"{{
                                ""width"": 1920,
                                ""height"": {virtualHeight},
                                ""deviceScaleFactor"": 1,
                                ""mobile"": false,
                                ""scale"": {scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                            }}";

                            try { await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", payload); } catch { }
                        }
                    }
                }
            };
            string? workerProfilePath = null;
            var options = _workerLaunchOptions.Enabled
                ? new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions()
                : new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--remote-debugging-port=9222");
            if (_workerLaunchOptions.Enabled)
            {
                workerProfilePath = _workerLaunchOptions.ProfilePath;
                System.IO.Directory.CreateDirectory(workerProfilePath);
            }
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                null, workerProfilePath, options);
            await MainWebView.EnsureCoreWebView2Async(env);            
            if (_workerLaunchOptions.IsCourseAutomation)
            {
                // Chỉ course worker mới giả lập Lockdown Browser. Không tiêm script lạ vào
                // accounts.google.com trong phiên đăng nhập trực tiếp.
                await MainWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    Object.defineProperty(window, 'isLockdownBrowser', { get: () => true, set: () => {} });
                    Object.defineProperty(window, 'CourseraLockdownBrowser', { get: () => true, set: () => {} });
                    window.localStorage.setItem('isLockdownBrowser', 'true');
                ");
                StartPopupWatchdog();
            }
            if (_workerLaunchOptions.Enabled)
            {
                await StartCentralWorkerAsync();
            }
        }
        catch (Exception exception)
        {
            if (_workerLaunchOptions.Enabled)
            {
                // WebView/environment setup can fail before a browse worker has
                // a chance to claim its job. Do not leave the visible window in
                // the claim-pending state, where its close guard would otherwise
                // reject every attempt to exit.
                _workerClaimPending = false;
                _viewModel.StatusText = _workerLaunchOptions.IsDirectLogin
                    ? "❌ Không khởi tạo được trình đăng nhập tạm."
                    : "❌ Không khởi tạo được worker: " + exception.Message;
            }
            if (_workerLaunchOptions.IsDirectLogin)
            {
                if (_centralWorkerClient.CurrentDirectLoginAttempt != null)
                {
                    using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await _centralWorkerClient.ReportDirectLoginStatusAsync(
                            "failed",
                            "Máy chủ không khởi tạo được trình đăng nhập Google.",
                            errorCode: "DIRECT_LOGIN_INITIALIZATION_FAILED",
                            errorMessageSafe: "Máy chủ không khởi tạo được trình đăng nhập Google.",
                            cancellationToken: reportTimeout.Token);
                    }
                    catch { }
                }
                _directLoginTerminal = true;
                await Task.Delay(250);
                Close();
            }
            else if (_workerLaunchOptions.IsInteractiveProfile &&
                     _centralWorkerClient.CurrentJob == null)
            {
                _workerClosing = true;
                await Task.Delay(250);
                Close();
            }
        }

    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_workerLaunchOptions.IsInteractiveProfile &&
            _workerClaimPending &&
            !_workerClosing &&
            _centralWorkerClient.CurrentJob == null)
        {
            e.Cancel = true;
            _viewModel.StatusText = "⏳ Đang nhận profile từ trung tâm; vui lòng chờ một lát rồi đóng lại.";
            return;
        }

        // A course worker owns a claimed job until it reports a terminal result.
        // Do not let an accidental Alt+F4/title-bar close turn a healthy job into
        // "Worker window was closed by the operator". Intentional terminal paths
        // set _workerClosing before calling Close(), and direct-login windows stay
        // fully user-controllable for Google verification.
        if (!_workerLaunchOptions.Enabled ||
            _workerLaunchOptions.IsDirectLogin ||
            _workerClosing ||
            _centralWorkerClient.CurrentJob == null ||
            string.Equals(
                _centralWorkerClient.CurrentJob.Mode,
                "browse",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        _viewModel.StatusText =
            "⚠️ Worker đang xử lý job; yêu cầu đóng cửa sổ đã được bỏ qua.";
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // Preserve why the window closed before marking it as closed.  Several
        // normal terminal paths set _workerClosing and call Close() after they
        // have already reported a result to the central server.  Those paths
        // must never be overwritten below as an operator-closed failure.
        bool wasExpectedWorkerClose = _workerClosing;
        WorkerJob? unexpectedlyClosedCourseJob =
            _workerLaunchOptions.Enabled &&
            !_workerLaunchOptions.IsDirectLogin &&
            !wasExpectedWorkerClose
                ? _centralWorkerClient.CurrentJob
                : null;

        _workerClosing = true;
        _directLoginLifetime?.Cancel();
        CloseDirectLoginOAuthWindow();
        if (_workerHeartbeatTimer != null)
        {
            _workerHeartbeatTimer.Stop();
            _workerHeartbeatTimer.Tick -= WorkerHeartbeatTimer_Tick;
            _workerHeartbeatTimer = null;
        }
        if (_popupWatchdogTimer != null)
        {
            _popupWatchdogTimer.Stop();
            _popupWatchdogTimer.Tick -= PopupWatchdogTimer_Tick;
            _popupWatchdogTimer = null;
        }

        foreach (Microsoft.Web.WebView2.Core.CoreWebView2Controller controller
                 in _hiddenLtiControllers.ToArray())
        {
            try { controller.Close(); } catch { }
        }
        _hiddenLtiControllers.Clear();

        if (_workerLaunchOptions.IsDirectLogin &&
            _centralWorkerClient.CurrentDirectLoginAttempt != null &&
            !_directLoginTerminal &&
            !wasExpectedWorkerClose)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                Task.Run(() => _centralWorkerClient.ReportDirectLoginStatusAsync(
                        "failed",
                        "Trình đăng nhập trên máy chủ đã đóng trước khi hoàn tất.",
                        errorCode: "WORKER_CLOSED",
                        errorMessageSafe: "Trình đăng nhập đã đóng trước khi hoàn tất.",
                        cancellationToken: closeTimeout.Token))
                    .GetAwaiter().GetResult();
            }
            catch { }
        }
        else if (unexpectedlyClosedCourseJob != null)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                if (string.Equals(
                    unexpectedlyClosedCourseJob.Mode,
                    "browse",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Task.Run(() => _centralWorkerClient.CloseInteractiveSessionAsync(closeTimeout.Token))
                        .GetAwaiter().GetResult();
                }
                else
                {
                    Task.Run(() => _centralWorkerClient.FailAsync(
                            "Worker window was closed by the operator.",
                            closeTimeout.Token))
                        .GetAwaiter().GetResult();
                }
            }
            catch { }
        }
    }

    private async Task StartCentralWorkerAsync()
    {
        if (_workerLaunchOptions.IsDirectLogin)
        {
            await StartDirectLoginWorkerAsync();
            return;
        }

        Title = $"ACOSE Worker · {_workerLaunchOptions.DeviceId}";
        _viewModel.StatusText = "Đang nhận job từ trung tâm...";
        try
        {
            WorkerJob job = await _centralWorkerClient.ClaimAsync();
            _workerClaimPending = false;
            bool isInteractiveProfile = string.Equals(
                job.Mode,
                "browse",
                StringComparison.OrdinalIgnoreCase);
            if (isInteractiveProfile != _workerLaunchOptions.IsInteractiveProfile)
            {
                throw new InvalidOperationException("Worker job mode does not match its launch request.");
            }
            _courseHasSkippedLaunchAppItems = false;
            _skippedLaunchAppItemPaths.Clear();
            _automationCompletedModuleNumbers.Clear();
            _isScanningCourseOutline = false;
            _courseHasSkippedPeerItems = false;
            _courseHasPendingGradedResults = false;
            _pendingGradedResultPaths.Clear();
            _courseHasSubmittedDiscussionItems = false;
            _submittedDiscussionPaths.Clear();
            _courseJobCompletionReported = false;
            _coursePauseInProgress = false;
            _courseLandingFallbackCount = 0;
            // A manually repaired durable profile is the freshest source of
            // truth. Import the encrypted vault lease only when this device's
            // local profile has no usable Coursera authentication cookie.
            bool isUsingLocalProfileSession =
                await HasValidCourseraAuthCookieAsync();
            if (!isUsingLocalProfileSession && !isInteractiveProfile)
            {
                SessionLease lease = await _centralWorkerClient.LeaseSessionAsync();
                await ImportCourseraCookiesAsync(lease.Cookies);
            }
            else if (!isUsingLocalProfileSession)
            {
                // A new/expired local profile can still be recovered from the
                // encrypted vault. Manual browse remains available even when
                // that lease has expired so the operator can sign in again.
                try
                {
                    SessionLease lease = await _centralWorkerClient.LeaseSessionAsync();
                    await ImportCourseraCookiesAsync(lease.Cookies);
                }
                catch { }
            }
            await _centralWorkerClient.HeartbeatAsync(
                "running",
                isInteractiveProfile
                    ? "Đã mở profile riêng để thao tác tài khoản"
                    : (isUsingLocalProfileSession
                        ? "Đang dùng phiên mới nhất trong profile riêng"
                        : "Đã khôi phục phiên khách; đang mở khóa học"),
                job.Progress,
                job.CurrentModule,
                job.TotalModules);
            if (job.Mode != "browse" && job.TotalModules is > 0)
            {
                _lastCourseProgressSnapshot = new CourseProgressSnapshot(
                    job.Progress,
                    Math.Max(1, job.CurrentModule ?? 1),
                    job.TotalModules.Value);
            }
            StartWorkerHeartbeat();

            UrlTextBox.Text = string.IsNullOrWhiteSpace(job.TargetUrl)
                ? "https://www.coursera.org/"
                : job.TargetUrl;
            if (isInteractiveProfile)
            {
                Title = string.IsNullOrWhiteSpace(job.CourseraUserName)
                    ? "ACOSE · Profile Coursera"
                    : $"ACOSE · {job.CourseraUserName}";
                _viewModel.StatusText = "👤 Profile riêng đang mở để thao tác thủ công.";
                Show();
                Activate();
                MainWebView.Source = new Uri(UrlTextBox.Text, UriKind.Absolute);
            }
            else
            {
                _viewModel.StatusText = isUsingLocalProfileSession
                    ? "✅ Đang dùng phiên mới nhất trong profile riêng; đang mở khóa học..."
                    : "✅ Đã khôi phục đúng phiên khách; đang mở khóa học...";
                OnTestClick(this, new RoutedEventArgs());
            }
        }
        catch (Exception exception)
        {
            _workerClaimPending = false;
            _viewModel.StatusText = "❌ Không khởi động được worker: " + exception.Message;
            var startupFailureReported = false;
            if (_centralWorkerClient.CurrentJob != null)
            {
                try
                {
                    await _centralWorkerClient.FailAsync("Worker startup failed.");
                    startupFailureReported = true;
                }
                catch { }
            }

            // Once the terminal startup failure reached the server, close the
            // worker deliberately.  Otherwise the closing guard correctly keeps
            // the window open for a recoverable report retry.
            if (startupFailureReported)
            {
                _workerClosing = true;
                await Task.Delay(350);
                Close();
            }
        }
    }

    private async Task StartDirectLoginWorkerAsync()
    {
        Title = "ACOSE · Đăng nhập đơn trực tiếp";
        _directLoginActive = true;
        _directLoginTerminal = false;
        _directLoginStatusDirty = false;
        _directLoginOAuthFailure = null;
        _directLoginOAuthExpectedRedirectUri = null;
        _directLoginLifetime = new CancellationTokenSource();
        CancellationToken cancellationToken = _directLoginLifetime.Token;

        try
        {
            _viewModel.StatusText = "Đang nhận phiên đăng nhập tạm từ trung tâm...";
            DirectLoginAttempt attempt =
                await _centralWorkerClient.ClaimDirectLoginAttemptAsync(cancellationToken);

            TimeSpan remaining = attempt.ExpiresAt.HasValue
                ? attempt.ExpiresAt.Value - DateTimeOffset.UtcNow
                : TimeSpan.FromMinutes(10);
            if (remaining <= TimeSpan.Zero)
            {
                _directLoginTerminal = true;
                return;
            }
            _directLoginLifetime.CancelAfter(remaining);
            DateTimeOffset deadline = attempt.ExpiresAt ?? DateTimeOffset.UtcNow.Add(remaining);

            await SetDirectLoginStatusAsync(
                "signing_in",
                "Đang mở Coursera và chuẩn bị đăng nhập Google.",
                cancellationToken: cancellationToken);
            StartWorkerHeartbeat();

            using DirectLoginCredentials credentials =
                await _centralWorkerClient.ConsumeDirectLoginCredentialsAsync(cancellationToken);

            ResetCourseraProfileBootstrap();
            _courseraIdentity = null;
            MainWebView.CoreWebView2.Navigate("https://www.coursera.org/?authMode=login");

            DirectLoginOutcome outcome = await AutomateDirectGoogleLoginAsync(
                credentials,
                deadline,
                cancellationToken);
            credentials.Clear();

            if (outcome.Kind == DirectLoginOutcomeKind.Cancelled)
            {
                _directLoginTerminal = true;
                return;
            }
            if (outcome.Kind == DirectLoginOutcomeKind.ManualRequired)
            {
                await SetDirectLoginStatusAsync(
                    "manual_required",
                    outcome.Message,
                    manualActionReason: outcome.Code,
                    errorCode: outcome.Code,
                    errorMessageSafe: outcome.Message,
                    cancellationToken: cancellationToken);
                _directLoginTerminal = true;
                return;
            }
            if (outcome.Kind == DirectLoginOutcomeKind.Failed)
            {
                await SetDirectLoginStatusAsync(
                    "failed",
                    outcome.Message,
                    errorCode: outcome.Code,
                    errorMessageSafe: outcome.Message,
                    cancellationToken: cancellationToken);
                _directLoginTerminal = true;
                return;
            }

            await SetDirectLoginStatusAsync(
                "signing_in",
                "Google đã xác nhận; đang kiểm tra phiên Coursera.",
                cancellationToken: cancellationToken);

            CloseDirectLoginOAuthWindow();
            MainWebView.CoreWebView2.Navigate("https://www.coursera.org/account-settings");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await EnsureCourseraIdentityAsync(maxAttempts: 12, forceRefresh: true);

            IReadOnlyCollection<VaultCookie> cookies = await ExportCourseraCookiesAsync();
            CourseraIdentity? identity = _courseraIdentity;
            await _centralWorkerClient.CompleteDirectLoginAsync(
                cookies,
                identity?.UserId,
                identity?.FullName,
                cancellationToken);

            _directLoginTerminal = true;
            _directLoginStatus = "completed";
            _directLoginChallengeNumber = null;
            _directLoginStatusDirty = false;
            _viewModel.StatusText = "✅ Đăng nhập thành công; trung tâm đã tạo đơn và bắt đầu khóa học.";
        }
        catch (OperationCanceledException) when (!_workerClosing)
        {
            if (!_directLoginTerminal && _centralWorkerClient.CurrentDirectLoginAttempt != null)
            {
                using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await SetDirectLoginStatusAsync(
                        "failed",
                        "Quá thời gian chờ đăng nhập hoặc xác nhận Google.",
                        errorCode: "DIRECT_LOGIN_TIMEOUT",
                        errorMessageSafe: "Quá thời gian chờ đăng nhập hoặc xác nhận Google.",
                        cancellationToken: reportTimeout.Token);
                }
                catch { }
            }
            _directLoginTerminal = true;
        }
        catch
        {
            if (!_directLoginTerminal && _centralWorkerClient.CurrentDirectLoginAttempt != null)
            {
                using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await SetDirectLoginStatusAsync(
                        "failed",
                        "Máy chủ không hoàn tất được phiên đăng nhập Google.",
                        errorCode: "DIRECT_LOGIN_WORKER_FAILED",
                        errorMessageSafe: "Máy chủ không hoàn tất được phiên đăng nhập Google.",
                        cancellationToken: reportTimeout.Token);
                }
                catch { }
            }
            _directLoginTerminal = true;
        }
        finally
        {
            _directLoginActive = false;
            CloseDirectLoginOAuthWindow();
            try
            {
                if (MainWebView.CoreWebView2?.Profile != null)
                {
                    await MainWebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                        Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllProfile);
                }
            }
            catch { }
            try { MainWebView.CoreWebView2?.CookieManager.DeleteAllCookies(); } catch { }

            if (!_workerClosing)
            {
                await Task.Delay(800);
                Close();
            }
        }
    }

    private async Task SetDirectLoginStatusAsync(
        string status,
        string activity,
        string? challengeNumber = null,
        string? manualActionReason = null,
        string? errorCode = null,
        string? errorMessageSafe = null,
        CancellationToken cancellationToken = default)
    {
        _directLoginStatus = status;
        _directLoginChallengeNumber = challengeNumber;
        _viewModel.StatusText = activity;
        await _directLoginStatusReportLock.WaitAsync(cancellationToken);
        try
        {
            bool reported = await _centralWorkerClient.ReportDirectLoginStatusAsync(
                status,
                activity,
                challengeNumber,
                manualActionReason,
                errorCode,
                errorMessageSafe,
                cancellationToken);
            if (status == _directLoginStatus && challengeNumber == _directLoginChallengeNumber)
            {
                _directLoginStatusDirty = !reported;
            }
        }
        finally
        {
            _directLoginStatusReportLock.Release();
        }
    }

    private async Task<DirectLoginOutcome> AutomateDirectGoogleLoginAsync(
        DirectLoginCredentials credentials,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        string email = credentials.GoogleEmail;
        string password = credentials.GooglePassword;
        string credentialLeaseId = credentials.LeaseId;
        bool emailSubmitted = false;
        bool passwordSubmitted = false;
        bool credentialsAcknowledged = false;
        DateTimeOffset? emailSubmittedAt = null;
        DateTimeOffset? passwordSubmittedAt = null;
        bool identifierTransitionReloaded = false;
        bool passwordTransitionReloaded = false;
        DateTimeOffset lastCredentialAckAttempt = DateTimeOffset.MinValue;
        DateTimeOffset lastRemoteCheck = DateTimeOffset.MinValue;
        DateTimeOffset? challengePageSeenAt = null;
        DateTimeOffset? oauthClickAt = null;
        DateTimeOffset? oauthButtonWaitStartedAt = null;
        DateTimeOffset? courseraReturnAt = null;
        DateTimeOffset? lastCourseraLoginClickAt = null;
        bool oauthClickIssued = false;
        bool googleNavigationObserved = false;
        bool googlePageReady = false;
        bool googlePageLoadingReported = false;
        int courseraLoginRecoveryCount = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Always evaluate the OAuth hand-off timeout before touching WebView2
            // state. A stalled cookie query must never leave the direct-login UI at
            // "waiting for Google" until the overall 15-minute attempt expires.
            if (oauthClickIssued && oauthClickAt.HasValue && !googlePageReady &&
                DateTimeOffset.UtcNow - oauthClickAt.Value > TimeSpan.FromSeconds(30))
            {
                return new DirectLoginOutcome(
                    DirectLoginOutcomeKind.Failed,
                    googleNavigationObserved ? "GOOGLE_PAGE_NOT_READY" : "GOOGLE_OAUTH_NOT_OPENED",
                    googleNavigationObserved
                        ? "Google đã mở nhưng không tải được biểu mẫu đăng nhập."
                        : "Coursera không mở được trang đăng nhập Google.");
            }

            if (passwordSubmitted && !credentialsAcknowledged &&
                DateTimeOffset.UtcNow - lastCredentialAckAttempt >= TimeSpan.FromSeconds(3))
            {
                lastCredentialAckAttempt = DateTimeOffset.UtcNow;
                credentialsAcknowledged =
                    await _centralWorkerClient.AcknowledgeDirectLoginCredentialsAsync(
                        credentialLeaseId,
                        cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(_directLoginOAuthFailure))
            {
                return new DirectLoginOutcome(
                    DirectLoginOutcomeKind.Failed,
                    "GOOGLE_OAUTH_POPUP_FAILED",
                    _directLoginOAuthFailure);
            }

            Microsoft.Web.WebView2.Core.CoreWebView2 activeWebView = GetDirectLoginWebView();
            Uri? currentUri = Uri.TryCreate(activeWebView.Source, UriKind.Absolute, out Uri? parsedUri)
                ? parsedUri
                : null;
            string host = currentUri?.Host ?? string.Empty;
            // The direct profile starts empty, so a cookie probe before Google has
            // returned cannot possibly succeed. More importantly, WebView2 can hold
            // GetCookiesAsync while it attaches a popup. Probe only after a Google
            // navigation has actually happened and the browser is back on Coursera.
            if ((passwordSubmitted || googleNavigationObserved) &&
                IsHostOrSubdomain(host, "coursera.org") &&
                await HasValidCourseraAuthCookieAsync(cancellationToken))
            {
                if (passwordSubmitted && !credentialsAcknowledged)
                {
                    // The session is already authenticated, but order creation is intentionally
                    // blocked until the backend confirms it erased the RAM-only credentials.
                    await Task.Delay(750, cancellationToken);
                    continue;
                }
                credentials.Clear();
                credentialLeaseId = string.Empty;
                email = string.Empty;
                password = string.Empty;
                return new DirectLoginOutcome(
                    DirectLoginOutcomeKind.Success,
                    "OK",
                    "Đăng nhập Coursera thành công.");
            }

            if (oauthClickIssued && googleNavigationObserved &&
                IsHostOrSubdomain(host, "coursera.org"))
            {
                courseraReturnAt ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - courseraReturnAt.Value > TimeSpan.FromSeconds(45))
                {
                    return new DirectLoginOutcome(
                        DirectLoginOutcomeKind.Failed,
                        "COURSERA_SESSION_NOT_READY",
                        "Google đã quay lại Coursera nhưng phiên đăng nhập chưa sẵn sàng.");
                }
            }

            if (DateTimeOffset.UtcNow - lastRemoteCheck >= TimeSpan.FromSeconds(3))
            {
                lastRemoteCheck = DateTimeOffset.UtcNow;
                try
                {
                    DirectLoginAttempt remote =
                        await _centralWorkerClient.GetDirectLoginAttemptAsync(cancellationToken);
                    if (remote.Status is "cancelled" or "expired")
                    {
                        credentials.Clear();
                        email = string.Empty;
                        password = string.Empty;
                        return new DirectLoginOutcome(
                            DirectLoginOutcomeKind.Cancelled,
                            remote.Status.ToUpperInvariant(),
                            "Phiên đăng nhập đã bị hủy hoặc hết hạn.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A transient status read must not discard one-time credentials.
                }
            }

            if (IsHostOrSubdomain(host, "coursera.org"))
            {
                bool isMainCourseraView = ReferenceEquals(activeWebView, MainWebView.CoreWebView2);
                if (isMainCourseraView &&
                    (!lastCourseraLoginClickAt.HasValue ||
                     DateTimeOffset.UtcNow - lastCourseraLoginClickAt.Value > TimeSpan.FromSeconds(3)))
                {
                    string loginResult = await ClickDirectLoginControlAsync(
                        activeWebView,
                        ClickCourseraLoginButtonScript,
                        cancellationToken);
                    if (loginResult == "LOGIN_CLICKED")
                    {
                        lastCourseraLoginClickAt = DateTimeOffset.UtcNow;
                        if (oauthClickIssued && ++courseraLoginRecoveryCount > 3)
                        {
                            return new DirectLoginOutcome(
                                DirectLoginOutcomeKind.Failed,
                                "GOOGLE_OAUTH_RETURNED_TO_COURSERA",
                                "Google liên tục quay lại Coursera khi chưa đăng nhập.");
                        }

                        // Coursera can return to its public course page after an
                        // OAuth popup is dismissed or redirected. Open its login
                        // dialog again, then perform a fresh Google click instead
                        // of waiting on the previous OAuth attempt.
                        oauthClickIssued = false;
                        oauthClickAt = null;
                        oauthButtonWaitStartedAt = DateTimeOffset.UtcNow;
                        googleNavigationObserved = false;
                        googlePageReady = false;
                        googlePageLoadingReported = false;
                        courseraReturnAt = null;
                        await SetDirectLoginStatusAsync(
                            "signing_in",
                            "Coursera đang ở trang chưa đăng nhập; đang mở lại đăng nhập Google.",
                            cancellationToken: cancellationToken);
                        await Task.Delay(900, cancellationToken);
                        continue;
                    }
                }

                if (!oauthClickIssued)
                {
                    oauthButtonWaitStartedAt ??= DateTimeOffset.UtcNow;
                    var popupOpened = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _directLoginOAuthPopupOpened = popupOpened;
                    string clickResult = await ClickDirectLoginControlAsync(
                        activeWebView,
                        ClickCourseraGoogleButtonScript,
                        cancellationToken);
                    if (clickResult == "CLICKED")
                    {
                        Task completed = await Task.WhenAny(
                            popupOpened.Task,
                            Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                        bool popupWasOpened = completed == popupOpened.Task &&
                                              await popupOpened.Task;
                        Uri? mainUri = Uri.TryCreate(
                            MainWebView.CoreWebView2?.Source,
                            UriKind.Absolute,
                            out Uri? parsedMainUri)
                            ? parsedMainUri
                            : null;
                        if (popupWasOpened || IsGoogleAccountsHost(mainUri?.Host))
                        {
                            oauthClickIssued = true;
                            oauthClickAt = DateTimeOffset.UtcNow;
                            googleNavigationObserved = IsGoogleAccountsHost(mainUri?.Host);
                            googlePageReady = false;
                            googlePageLoadingReported = false;
                            courseraReturnAt = null;
                            await SetDirectLoginStatusAsync(
                                "signing_in",
                                popupWasOpened
                                    ? "Đã mở cửa sổ đăng nhập Google."
                                    : "Google đã mở trong cửa sổ đăng nhập.",
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            if (ReferenceEquals(_directLoginOAuthPopupOpened, popupOpened))
                            {
                                _directLoginOAuthPopupOpened = null;
                            }
                            await SetDirectLoginStatusAsync(
                                "signing_in",
                                "Đã bấm nút Google nhưng Coursera chưa yêu cầu cửa sổ đăng nhập; đang thử lại.",
                                cancellationToken: cancellationToken);
                            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                        }
                    }
                    else
                    {
                        if (ReferenceEquals(_directLoginOAuthPopupOpened, popupOpened))
                        {
                            _directLoginOAuthPopupOpened = null;
                        }
                        if (DateTimeOffset.UtcNow - oauthButtonWaitStartedAt.Value > TimeSpan.FromSeconds(30))
                        {
                            return new DirectLoginOutcome(
                                DirectLoginOutcomeKind.Failed,
                                "GOOGLE_SIGNIN_BUTTON_NOT_READY",
                                "Coursera không tải được nút đăng nhập bằng Google.");
                        }
                    }
                }
            }
            else if (IsGoogleAccountsHost(host))
            {
                googleNavigationObserved = true;
                GoogleLoginPageState pageState = await ReadGoogleLoginPageStateAsync(
                    activeWebView,
                    cancellationToken);
                if (pageState.Step == "wait")
                {
                    if (!googlePageLoadingReported)
                    {
                        googlePageLoadingReported = true;
                        await SetDirectLoginStatusAsync(
                            "signing_in",
                            "Google đã mở; đang tải biểu mẫu đăng nhập.",
                            cancellationToken: cancellationToken);
                    }
                }
                else
                {
                    googlePageReady = true;
                }
                switch (pageState.Step)
                {
                    case "email":
                        if (!emailSubmitted && !string.IsNullOrWhiteSpace(email) &&
                            await FillGoogleInputAndContinueAsync(
                                activeWebView,
                                "email",
                                email,
                                cancellationToken))
                        {
                            emailSubmitted = true;
                            emailSubmittedAt = DateTimeOffset.UtcNow;
                            await SetDirectLoginStatusAsync(
                                "signing_in",
                                "Đã gửi tài khoản Google; đang chờ bước mật khẩu.",
                                cancellationToken: cancellationToken);
                        }
                        else if (emailSubmitted &&
                                 !identifierTransitionReloaded &&
                                 emailSubmittedAt.HasValue &&
                                 currentUri?.AbsolutePath.Contains(
                                     "/challenge/pwd",
                                     StringComparison.OrdinalIgnoreCase) == true &&
                                 DateTimeOffset.UtcNow - emailSubmittedAt.Value >
                                     TimeSpan.FromSeconds(2))
                        {
                            // Google đôi khi đổi route trước khi hoàn tất animation sang bước
                            // mật khẩu trong WebView ẩn. Tải lại route hiện tại đúng một lần.
                            identifierTransitionReloaded = true;
                            activeWebView.Reload();
                        }
                        break;

                    case "account_chooser":
                        if (!string.IsNullOrWhiteSpace(email))
                        {
                            await SelectGoogleAccountAsync(activeWebView, email, cancellationToken);
                        }
                        break;

                    case "password":
                        if (!passwordSubmitted && !string.IsNullOrEmpty(password) &&
                            await FillGoogleInputAndContinueAsync(
                                activeWebView,
                                "password",
                                password,
                                cancellationToken))
                        {
                            passwordSubmitted = true;
                            passwordSubmittedAt = DateTimeOffset.UtcNow;
                            lastCredentialAckAttempt = DateTimeOffset.UtcNow;
                            credentialsAcknowledged =
                                await _centralWorkerClient.AcknowledgeDirectLoginCredentialsAsync(
                                    credentialLeaseId,
                                    cancellationToken);
                            credentials.Clear();
                            email = string.Empty;
                            password = string.Empty;
                            await SetDirectLoginStatusAsync(
                                "signing_in",
                                "Đã gửi mật khẩu; đang chờ Google xác minh.",
                                cancellationToken: cancellationToken);
                        }
                        else if (passwordSubmitted &&
                                 !passwordTransitionReloaded &&
                                 passwordSubmittedAt.HasValue &&
                                 (currentUri?.AbsolutePath.Contains(
                                      "/challenge/dp",
                                      StringComparison.OrdinalIgnoreCase) == true ||
                                  currentUri?.AbsolutePath.Contains(
                                      "/challenge/ipp",
                                      StringComparison.OrdinalIgnoreCase) == true) &&
                                 DateTimeOffset.UtcNow - passwordSubmittedAt.Value >
                                     TimeSpan.FromSeconds(2))
                        {
                            // Tương tự bước identifier: buộc Google dựng lại giao diện xác minh
                            // nếu route đã sang challenge nhưng form mật khẩu cũ còn hiển thị.
                            passwordTransitionReloaded = true;
                            activeWebView.Reload();
                        }
                        break;

                    case "number_match":
                        challengePageSeenAt ??= DateTimeOffset.UtcNow;
                        if (!string.IsNullOrWhiteSpace(pageState.ChallengeNumber) &&
                            (_directLoginStatus != "waiting_number" ||
                             _directLoginChallengeNumber != pageState.ChallengeNumber ||
                             _directLoginStatusDirty))
                        {
                            await SetDirectLoginStatusAsync(
                                "waiting_number",
                                "Đang chờ khách chọn đúng số trên điện thoại.",
                                pageState.ChallengeNumber,
                                cancellationToken: cancellationToken);
                        }
                        break;

                    case "push_approval":
                        challengePageSeenAt ??= DateTimeOffset.UtcNow;
                        if (_directLoginStatus != "waiting_approval" ||
                            _directLoginStatusDirty)
                        {
                            await SetDirectLoginStatusAsync(
                                "waiting_approval",
                                "Google đã gửi thông báo tới điện thoại. Đang chờ khách bấm Yes/Có.",
                                cancellationToken: cancellationToken);
                        }
                        break;

                    case "number_pending":
                        challengePageSeenAt ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - challengePageSeenAt > TimeSpan.FromSeconds(35))
                        {
                            return new DirectLoginOutcome(
                                DirectLoginOutcomeKind.ManualRequired,
                                "GOOGLE_NUMBER_NOT_READABLE",
                                "Google yêu cầu xác minh nhưng máy chủ không đọc được số.");
                        }
                        break;

                    case "challenge_pending":
                        challengePageSeenAt ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - challengePageSeenAt > TimeSpan.FromSeconds(25))
                        {
                            return new DirectLoginOutcome(
                                DirectLoginOutcomeKind.ManualRequired,
                                "GOOGLE_UNSUPPORTED_CHALLENGE",
                                "Google đang yêu cầu một bước xác minh chưa được hỗ trợ.");
                        }
                        break;

                    case "continue":
                        await ClickGoogleContinueAsync(activeWebView, cancellationToken);
                        break;

                    case "credential_error":
                        credentials.Clear();
                        email = string.Empty;
                        password = string.Empty;
                        return new DirectLoginOutcome(
                            DirectLoginOutcomeKind.Failed,
                            pageState.Code,
                            pageState.Message);

                    case "manual_required":
                        credentials.Clear();
                        email = string.Empty;
                        password = string.Empty;
                        return new DirectLoginOutcome(
                            DirectLoginOutcomeKind.ManualRequired,
                            pageState.Code,
                            pageState.Message);
                }
            }

            if (passwordSubmitted &&
                passwordSubmittedAt.HasValue &&
                IsGoogleAccountsHost(host) &&
                _directLoginStatus is not ("waiting_number" or "waiting_approval") &&
                DateTimeOffset.UtcNow - passwordSubmittedAt.Value > TimeSpan.FromMinutes(2))
            {
                return new DirectLoginOutcome(
                    DirectLoginOutcomeKind.ManualRequired,
                    "GOOGLE_UNSUPPORTED_CHALLENGE",
                    "Google đang yêu cầu một bước xác minh chưa được hỗ trợ.");
            }

            await Task.Delay(750, cancellationToken);
        }

        credentials.Clear();
        email = string.Empty;
        password = string.Empty;
        return new DirectLoginOutcome(
            DirectLoginOutcomeKind.Failed,
            "DIRECT_LOGIN_TIMEOUT",
            "Quá thời gian chờ đăng nhập Google.");
    }

    private sealed record GoogleLoginPageState(
        string Step,
        string Code,
        string Message,
        string? ChallengeNumber);

    private static async Task<string?> ExecuteDirectLoginScriptAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 webView,
        string script,
        CancellationToken cancellationToken)
    {
        try
        {
            return await webView.ExecuteScriptAsync(script).WaitAsync(
                DirectLoginWebViewOperationTimeout,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            // WebView2 can temporarily stop accepting script calls while an OAuth
            // popup is being attached or a Google document is being replaced. The
            // main state loop has a short, explicit recovery deadline for this.
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<string> ClickDirectLoginControlAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 webView,
        string locatorScript,
        CancellationToken cancellationToken)
    {
        string located = DecodeWebViewString(
            await ExecuteDirectLoginScriptAsync(webView, locatorScript, cancellationToken) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(located))
        {
            return "CLICK_FAILED";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(located);
            JsonElement root = document.RootElement;
            string status = root.TryGetProperty("status", out JsonElement statusElement)
                ? statusElement.GetString() ?? "CLICK_FAILED"
                : "CLICK_FAILED";
            if (status != "FOUND")
            {
                return status;
            }

            if (!root.TryGetProperty("x", out JsonElement xElement) ||
                !root.TryGetProperty("y", out JsonElement yElement) ||
                !xElement.TryGetDouble(out double x) ||
                !yElement.TryGetDouble(out double y) ||
                double.IsNaN(x) || double.IsNaN(y) ||
                double.IsInfinity(x) || double.IsInfinity(y))
            {
                return "CLICK_FAILED";
            }

            string pressedPayload = JsonSerializer.Serialize(new
            {
                type = "mousePressed",
                x,
                y,
                button = "left",
                clickCount = 1
            });
            string releasedPayload = JsonSerializer.Serialize(new
            {
                type = "mouseReleased",
                x,
                y,
                button = "left",
                clickCount = 1
            });
            await webView.CallDevToolsProtocolMethodAsync(
                "Input.dispatchMouseEvent",
                pressedPayload).WaitAsync(DirectLoginWebViewOperationTimeout, cancellationToken);
            await webView.CallDevToolsProtocolMethodAsync(
                "Input.dispatchMouseEvent",
                releasedPayload).WaitAsync(DirectLoginWebViewOperationTimeout, cancellationToken);
            return "CLICKED";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "CLICK_FAILED";
        }
    }

    private async Task<GoogleLoginPageState> ReadGoogleLoginPageStateAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 webView,
        CancellationToken cancellationToken)
    {
        string script = """
            (function() {
                const normalize = value => String(value || '')
                    .normalize('NFC').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                const lower = normalize(document.body?.innerText).toLocaleLowerCase();
                const visible = el => !!el && el.getClientRects().length > 0 &&
                    getComputedStyle(el).visibility !== 'hidden';
                const result = (step, code, message, challengeNumber) =>
                    JSON.stringify({ step, code, message, challengeNumber: challengeNumber || null });

                if (document.querySelector('iframe[src*="recaptcha"], div.g-recaptcha') ||
                    /captcha|prove you.re not a robot|xác minh rằng bạn không phải là robot/i.test(lower)) {
                    return result('manual_required', 'GOOGLE_CAPTCHA',
                        'Google yêu cầu CAPTCHA; cần đăng nhập thủ công.');
                }
                if (/this browser or app may not be secure|couldn.t sign you in|trình duyệt hoặc ứng dụng này có thể không an toàn/i.test(lower)) {
                    return result('manual_required', 'GOOGLE_BROWSER_BLOCKED',
                        'Google từ chối trình duyệt tự động; cần đăng nhập thủ công.');
                }
                if (/wrong password|incorrect password|mật khẩu không đúng|sai mật khẩu|couldn[’']?t find (this|your google) account|could not find (this|your google) account|không tìm thấy tài khoản google/i.test(lower)) {
                    return result('credential_error', 'GOOGLE_CREDENTIALS_REJECTED',
                        'Google từ chối tài khoản hoặc mật khẩu.');
                }
                if (visible(document.querySelector('input[type="password"], input[name="Passwd"]'))) {
                    return result('password', '', 'Đang chờ nhập mật khẩu Google.');
                }
                if (visible(document.querySelector('input[type="email"], input#identifierId'))) {
                    return result('email', '', 'Đang chờ nhập tài khoản Google.');
                }
                if (document.querySelector('[data-identifier]')) {
                    return result('account_chooser', '', 'Đang chọn tài khoản Google.');
                }
                const challengeRoute = /\/challenge\/(dp|ipp)(\/|$)/i.test(location.pathname);
                const numberContext = challengeRoute ||
                    /(match|tap|select|choose|nhấn|chọn|khớp).{0,180}(number|số|\b\d{1,3}\b).{0,180}(phone|device|điện thoại|thiết bị)/i.test(lower) ||
                    /(number|số|\b\d{1,3}\b).{0,180}(phone|device|notification|prompt|điện thoại|thiết bị|thông báo)/i.test(lower) ||
                    /(google sent|check your|kiểm tra).{0,160}(phone|device|notification|điện thoại|thiết bị|thông báo)/i.test(lower);
                if (visible(document.querySelector('[autocomplete="one-time-code"], input[name="totpPin"], input#idvPin')) ||
                    /enter (the )?(verification )?code|nhập mã xác minh|mã gồm 6 chữ số|google authenticator/i.test(lower)) {
                    return result('manual_required', 'GOOGLE_OTP_REQUIRED',
                        'Google yêu cầu mã OTP; cần đăng nhập thủ công.');
                }
                if (!numberContext && /passkey|security key|khóa truy cập|khóa bảo mật/i.test(lower)) {
                    return result('manual_required', 'GOOGLE_PASSKEY_REQUIRED',
                        'Google yêu cầu passkey hoặc khóa bảo mật; cần đăng nhập thủ công.');
                }
                if (!numberContext && /recovery email|recovery phone|email khôi phục|số điện thoại khôi phục|confirm your recovery/i.test(lower)) {
                    return result('manual_required', 'GOOGLE_RECOVERY_REQUIRED',
                        'Google yêu cầu thông tin khôi phục; cần đăng nhập thủ công.');
                }

                const approvalContext = challengeRoute &&
                    /(open the (gmail|google) app|google sent a notification|tap yes|mở ứng dụng (gmail|google)|đã gửi (một )?thông báo|bấm (yes|có)|nhấn (yes|có))/i.test(lower) &&
                    !/(select|choose|match|chọn|khớp).{0,100}(number|số)/i.test(lower);
                if (approvalContext) {
                    return result('push_approval', 'GOOGLE_PUSH_APPROVAL',
                        'Đang chờ khách bấm Yes/Có trên thông báo Google.');
                }

                if (numberContext) {
                    const inlineNumber = lower.match(
                        /(?:match|tap|select|choose|nhấn|chọn|khớp).{0,80}\b(\d{1,3})\b.{0,120}(?:phone|device|điện thoại|thiết bị)/i);
                    if (inlineNumber) {
                        return result('number_match', 'GOOGLE_NUMBER_MATCH',
                            'Đang chờ khách chọn đúng số trên điện thoại.', inlineNumber[1]);
                    }
                    const candidates = Array.from(document.querySelectorAll(
                        '[data-challengevalue],[data-challenge-value],[aria-label],div,span,p,strong,h1,h2'))
                        .filter(visible)
                        .map(el => {
                            const ownText = normalize(Array.from(el.childNodes)
                                .filter(node => node.nodeType === Node.TEXT_NODE)
                                .map(node => node.textContent).join(' '));
                            const attributeText = normalize(
                                el.getAttribute('data-challengevalue') ||
                                el.getAttribute('data-challenge-value') ||
                                el.getAttribute('aria-label'));
                            const numericText = /^\d{1,3}$/.test(ownText)
                                ? ownText
                                : (/^\d{1,3}$/.test(attributeText) ? attributeText : '');
                            if (!numericText) return null;
                            let context = '';
                            let parent = el;
                            for (let i = 0; i < 4 && parent; i++, parent = parent.parentElement) {
                                context += ' ' + normalize(parent.innerText);
                            }
                            const style = getComputedStyle(el);
                            const size = parseFloat(style.fontSize || '0');
                            let score = Math.min(size / 4, 10);
                            if (/(tap|select|choose|nhấn|chọn)/i.test(context)) score += 5;
                            if (/(number|số)/i.test(context)) score += 5;
                            if (/(phone|device|notification|prompt|điện thoại|thiết bị|thông báo)/i.test(context)) score += 4;
                            if (/(challenge|number|code)/i.test(String(el.id || '') + ' ' + String(el.className || ''))) score += 4;
                            return { value: numericText, score };
                        })
                        .filter(Boolean)
                        .sort((a, b) => b.score - a.score);
                    if (candidates.length && candidates[0].score >= 10) {
                        return result('number_match', 'GOOGLE_NUMBER_MATCH',
                            'Đang chờ khách chọn đúng số trên điện thoại.', candidates[0].value);
                    }
                    return result('number_pending', 'GOOGLE_NUMBER_MATCH',
                        'Google đang chuẩn bị số xác minh.');
                }

                const buttons = Array.from(document.querySelectorAll('button,[role="button"]')).filter(visible);
                const continueButton = buttons.find(button => {
                    const text = normalize(button.innerText || button.textContent).toLocaleLowerCase();
                    return text === 'continue' || text === 'allow' || text === 'tiếp tục' || text === 'cho phép';
                });
                if (continueButton && /coursera/i.test(lower)) {
                    return result('continue', '', 'Đang hoàn tất liên kết Google với Coursera.');
                }

                if (/verify it.s you|xác minh danh tính|choose another way|thử một cách khác/i.test(lower)) {
                    return result('challenge_pending', 'GOOGLE_CHALLENGE_LOADING',
                        'Đang chờ Google tải đầy đủ bước xác minh.');
                }
                return result('wait', '', 'Đang chờ Google tải bước tiếp theo.');
            })();
            """;

        try
        {
            string raw = DecodeWebViewString(
                await ExecuteDirectLoginScriptAsync(webView, script, cancellationToken) ?? string.Empty);
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            return new GoogleLoginPageState(
                root.TryGetProperty("step", out JsonElement step) ? step.GetString() ?? "wait" : "wait",
                root.TryGetProperty("code", out JsonElement code) ? code.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("challengeNumber", out JsonElement number) && number.ValueKind == JsonValueKind.String
                    ? number.GetString()
                    : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GoogleLoginPageState("wait", string.Empty, string.Empty, null);
        }
    }

    private async Task<bool> FillGoogleInputAndContinueAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 webView,
        string kind,
        string value,
        CancellationToken cancellationToken)
    {
        string valueJson = JsonSerializer.Serialize(value);
        string selector = kind == "password"
            ? "input[type=\"password\"],input[name=\"Passwd\"]"
            : "input[type=\"email\"],input#identifierId";
        string nextSelector = kind == "password" ? "#passwordNext" : "#identifierNext";
        string script = $$"""
            (function() {
                const input = document.querySelector({{JsonSerializer.Serialize(selector)}});
                if (!input || input.getClientRects().length === 0) return 'NO_INPUT';
                const setter = Object.getOwnPropertyDescriptor(
                    HTMLInputElement.prototype, 'value')?.set;
                if (!setter) return 'NO_SETTER';
                input.focus();
                setter.call(input, {{valueJson}});
                input.dispatchEvent(new InputEvent('input', {
                    bubbles: true, inputType: 'insertText', data: null
                }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                const nextRoot = document.querySelector({{JsonSerializer.Serialize(nextSelector)}});
                const fallbackNext = Array.from(document.querySelectorAll('button,[role="button"]')).find(button => {
                        const text = (button.innerText || button.textContent || '').trim().toLocaleLowerCase();
                        return text === 'next' || text === 'tiếp theo';
                    });
                const next = nextRoot?.matches('button,[role="button"]')
                    ? nextRoot
                    : nextRoot?.querySelector('button,[role="button"]') || fallbackNext;
                if (!next || next.getClientRects().length === 0) return 'NO_NEXT';
                next.click();
                return 'CLICKED';
            })();
            """;
        try
        {
            string result = DecodeWebViewString(
                await ExecuteDirectLoginScriptAsync(webView, script, cancellationToken) ?? string.Empty);
            return result == "CLICKED";
        }
        finally
        {
            valueJson = string.Empty;
            script = string.Empty;
        }
    }

    private async Task SelectGoogleAccountAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 webView,
        string email,
        CancellationToken cancellationToken)
    {
        string emailJson = JsonSerializer.Serialize(email);
        string script = $$"""
            (function() {
                const expected = String({{emailJson}}).trim().toLocaleLowerCase();
                const accounts = Array.from(document.querySelectorAll('[data-identifier]'));
                const match = accounts.find(element =>
                    String(element.getAttribute('data-identifier') || '').trim().toLocaleLowerCase() === expected);
                if (!match) return 'NO_MATCH';
                (match.closest('[role="link"],[role="button"]') || match).click();
                return 'CLICKED';
            })();
            """;
        try
        {
            await ExecuteDirectLoginScriptAsync(webView, script, cancellationToken);
        }
        finally
        {
            emailJson = string.Empty;
            script = string.Empty;
        }
    }

    private static async Task ClickGoogleContinueAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 webView,
        CancellationToken cancellationToken)
    {
        const string script = """
            (function() {
                const visible = el => !!el && el.getClientRects().length > 0;
                const button = Array.from(document.querySelectorAll('button,[role="button"]'))
                    .filter(visible)
                    .find(element => {
                        const text = (element.innerText || element.textContent || '')
                            .trim().toLocaleLowerCase();
                        return text === 'continue' || text === 'allow' ||
                            text === 'tiếp tục' || text === 'cho phép';
                    });
                if (!button) return 'NO_BUTTON';
                button.click();
                return 'CLICKED';
            })();
            """;
        await ExecuteDirectLoginScriptAsync(webView, script, cancellationToken);
    }

    private async Task<bool> HasValidCourseraAuthCookieAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<Microsoft.Web.WebView2.Core.CoreWebView2Cookie> cookies =
                await MainWebView.CoreWebView2.CookieManager
                    .GetCookiesAsync("https://www.coursera.org")
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return cookies.Any(cookie =>
                string.Equals(cookie.Name, "CAUTH", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(cookie.Value) &&
                IsCourseraCookieDomain(cookie.Domain) &&
                (cookie.IsSession || cookie.Expires == DateTime.MinValue ||
                 cookie.Expires.ToUniversalTime() > DateTime.UtcNow.AddSeconds(15)));
        }
        catch (TimeoutException)
        {
            // WebView2 can occasionally hold this request while an OAuth popup is
            // being attached. Treat it as "not signed in" so the main loop keeps
            // its Google-navigation timeout and recovery path alive.
            return false;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<IReadOnlyCollection<VaultCookie>> ExportCourseraCookiesAsync()
    {
        IReadOnlyList<Microsoft.Web.WebView2.Core.CoreWebView2Cookie> browserCookies =
            await MainWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.coursera.org");
        List<VaultCookie> cookies = browserCookies
            .Where(cookie => IsCourseraCookieDomain(cookie.Domain))
            .Select(cookie => new VaultCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Secure = cookie.IsSecure,
                HttpOnly = cookie.IsHttpOnly,
                SameSite = cookie.SameSite.ToString().ToLowerInvariant(),
                Expires = cookie.IsSession || cookie.Expires == DateTime.MinValue
                    ? null
                    : new DateTimeOffset(
                        DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            })
            .ToList();
        if (!cookies.Any(cookie =>
                string.Equals(cookie.Name, "CAUTH", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(cookie.Value)))
        {
            throw new InvalidOperationException("Coursera authentication cookie was not available.");
        }
        return cookies;
    }

    private static bool IsCourseraCookieDomain(string? value)
    {
        string domain = (value ?? string.Empty).Trim().TrimStart('.');
        return string.Equals(domain, "coursera.org", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith(".coursera.org", StringComparison.OrdinalIgnoreCase);
    }

    private const string ClickCourseraLoginButtonScript = """
        (function() {
            const normalize = value => String(value || '').replace(/\s+/g, ' ').trim().toLocaleLowerCase();
            const visible = element => {
                if (!element || element.getClientRects().length === 0) return false;
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' &&
                    style.display !== 'none' && style.pointerEvents !== 'none';
            };
            const candidates = Array.from(document.querySelectorAll('button,a,[role="button"]')).filter(visible);
            const login = candidates.find(element => {
                const text = normalize(element.innerText || element.textContent);
                const label = normalize(element.getAttribute('aria-label'));
                const href = normalize(element.getAttribute('href'));
                return text === 'log in' || text === 'login' || text === 'đăng nhập' ||
                    label === 'log in' || label === 'login' || label === 'đăng nhập' ||
                    (/login/.test(href) && (text === 'log in' || text === 'login'));
            });
            if (!login) return JSON.stringify({ status: 'NO_LOGIN' });
            const rect = login.getBoundingClientRect();
            return JSON.stringify({
                status: 'FOUND',
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2
            });
        })();
        """;

    private const string ClickCourseraGoogleButtonScript = """
        (function() {
            const normalize = value => String(value || '').replace(/\s+/g, ' ').trim().toLocaleLowerCase();
            const visible = element => {
                if (!element || element.getClientRects().length === 0) return false;
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' &&
                    style.display !== 'none' && style.pointerEvents !== 'none';
            };
            const isGoogleSignInLabel = value => /^(continue|sign in|log in|login|đăng nhập|tiếp tục)(\s+(with|bằng|với))?\s+google(\s+account)?$/.test(value);
            const candidates = Array.from(document.querySelectorAll('button,a,[role="button"]')).filter(visible);
            const button = candidates.find(element => {
                const text = normalize(element.innerText || element.textContent);
                const label = normalize(element.getAttribute('aria-label'));
                return isGoogleSignInLabel(text) || isGoogleSignInLabel(label);
            });
            if (!button) return JSON.stringify({ status: 'NO_BUTTON' });
            const rect = button.getBoundingClientRect();
            return JSON.stringify({
                status: 'FOUND',
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2
            });
        })();
        """;

    private async Task ImportCourseraCookiesAsync(IEnumerable<VaultCookie> cookies)
    {
        if (MainWebView.CoreWebView2 == null)
        {
            throw new InvalidOperationException("WebView2 is not initialized.");
        }

        int imported = 0;
        foreach (VaultCookie source in cookies)
        {
            string domain = source.Domain.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(source.Name) ||
                string.IsNullOrEmpty(source.Value) ||
                !(domain == "coursera.org" || domain.EndsWith(".coursera.org", StringComparison.Ordinal)))
            {
                continue;
            }

            Microsoft.Web.WebView2.Core.CoreWebView2Cookie cookie =
                MainWebView.CoreWebView2.CookieManager.CreateCookie(
                    source.Name,
                    source.Value,
                    domain,
                    string.IsNullOrWhiteSpace(source.Path) ? "/" : source.Path);
            cookie.IsSecure = source.Secure;
            cookie.IsHttpOnly = source.HttpOnly;
            cookie.SameSite = source.SameSite?.ToLowerInvariant() switch
            {
                "strict" => Microsoft.Web.WebView2.Core.CoreWebView2CookieSameSiteKind.Strict,
                "lax" => Microsoft.Web.WebView2.Core.CoreWebView2CookieSameSiteKind.Lax,
                "none" => Microsoft.Web.WebView2.Core.CoreWebView2CookieSameSiteKind.None,
                _ => Microsoft.Web.WebView2.Core.CoreWebView2CookieSameSiteKind.None,
            };
            if (source.Expires is > 0 && source.Expires < 253402300799)
            {
                cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)source.Expires.Value).UtcDateTime;
            }
            MainWebView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            imported++;
        }

        if (imported == 0)
        {
            throw new InvalidOperationException("Phiên Coursera không chứa cookie hợp lệ.");
        }
        await Task.CompletedTask;
    }

    private void StartWorkerHeartbeat()
    {
        if (_workerHeartbeatTimer != null) return;
        _workerHeartbeatTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _workerHeartbeatTimer.Tick += WorkerHeartbeatTimer_Tick;
        _workerHeartbeatTimer.Start();
    }

    private async void WorkerHeartbeatTimer_Tick(object? sender, EventArgs e)
    {
        if (_workerClosing || !_workerHeartbeatLock.Wait(0)) return;
        bool shouldPauseCourse = false;
        string pauseReason = "Đã tạm dừng theo yêu cầu từ trang theo dõi.";
        try
        {
            string activity = _viewModel.StatusText ?? "Worker đang chạy";
            if (activity.Length > 500) activity = activity[..500];
            if (_workerLaunchOptions.IsDirectLogin &&
                _centralWorkerClient.CurrentDirectLoginAttempt != null)
            {
                string status = _directLoginStatus;
                string? challengeNumber = _directLoginChallengeNumber;
                await _directLoginStatusReportLock.WaitAsync();
                try
                {
                    bool reported = await _centralWorkerClient.ReportDirectLoginStatusAsync(
                        status,
                        activity,
                        challengeNumber);
                    if (reported && status == _directLoginStatus &&
                        challengeNumber == _directLoginChallengeNumber)
                    {
                        _directLoginStatusDirty = false;
                    }
                }
                finally
                {
                    _directLoginStatusReportLock.Release();
                }
            }
            else
            {
                CourseProgressSnapshot? progressSnapshot =
                    await ReadCourseProgressSnapshotAsync();
                if (progressSnapshot != null)
                {
                    int monotonicProgress = Math.Max(
                        _lastCourseProgressSnapshot?.Progress ?? 0,
                        progressSnapshot.Progress);
                    progressSnapshot = progressSnapshot with
                    {
                        Progress = monotonicProgress,
                    };
                    _lastCourseProgressSnapshot = progressSnapshot;
                }
                else
                {
                    progressSnapshot = _lastCourseProgressSnapshot;
                }

                WorkerJob refreshedJob = await _centralWorkerClient.HeartbeatAsync(
                    "running",
                    activity,
                    progressSnapshot?.Progress,
                    progressSnapshot?.CurrentModule,
                    progressSnapshot?.TotalModules);
                if (refreshedJob.PauseRequested)
                {
                    shouldPauseCourse = true;
                    if (!string.IsNullOrWhiteSpace(refreshedJob.PauseRequestedReason))
                    {
                        pauseReason = refreshedJob.PauseRequestedReason;
                    }
                }
            }
        }
        catch
        {
            // Mất kết nối tạm thời không được làm thay đổi logic automation đang chạy.
        }
        finally
        {
            _workerHeartbeatLock.Release();
        }

        if (shouldPauseCourse && !_workerClosing)
        {
            await PauseCourseJobAsync(
                "⏸️ Worker đã tạm dừng; profile đã sẵn sàng để thao tác thủ công.",
                pauseReason,
                "ADMIN_PAUSED");
        }
    }

    private async Task<CourseProgressSnapshot?> ReadCourseProgressSnapshotAsync()
    {
        WorkerJob? job = _centralWorkerClient.CurrentJob;
        if (job == null ||
            string.Equals(job.Mode, "browse", StringComparison.OrdinalIgnoreCase) ||
            MainWebView.CoreWebView2 == null ||
            !Uri.TryCreate(MainWebView.Source?.ToString(), UriKind.Absolute, out Uri? currentUri) ||
            !IsHostOrSubdomain(currentUri.Host, "coursera.org"))
        {
            return null;
        }

        // Keep the live dashboard consistent with the lesson scanner.  Coursera
        // shows an accepted submission as incomplete until grading finishes,
        // but this Worker must not count it as another item to retry.
        string pendingResultPathsJson = JsonSerializer.Serialize(
            _pendingGradedResultPaths.ToArray());
        string submittedDiscussionPathsJson = JsonSerializer.Serialize(
            _submittedDiscussionPaths.ToArray());
        string skippedLaunchAppItemPathsJson = JsonSerializer.Serialize(
            _skippedLaunchAppItemPaths.ToArray());
        string automationCompletedModulesJson = JsonSerializer.Serialize(
            _automationCompletedModuleNumbers.ToArray());

        string script = """
            (function() {
                const skipGradedAppItems = __SKIP_GRADED__;
                const skipPracticeAppItems = __SKIP_PRACTICE__;
                const skipPeerItems = __SKIP_PEER__;
                const pendingResultPaths = new Set(__PENDING_RESULT_PATHS__);
                const submittedDiscussionPaths = new Set(__SUBMITTED_DISCUSSION_PATHS__);
                const skippedLaunchAppItemPaths = new Set(__SKIPPED_LAUNCH_APP_PATHS__);
                const automationCompletedModules = new Set(__AUTOMATION_COMPLETED_MODULES__);
                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                const normalizePath = value => {
                    const path = String(value || '').replace(/\/+$/, '');
                    return path || '/';
                };
                const unique = values => Array.from(new Set(values));
                const modules = unique(Array.from(document.querySelectorAll(
                    'a[data-testid="rc-WeekNavigationItem"], ' +
                    '[data-testid="rc-WeekNavigationItem"] a, ' +
                    'a[aria-label^="Module "], ' +
                    '[data-testid="rc-WeekNavigationItem"] button, ' +
                    'button[aria-expanded][aria-controls]'
                ))).filter(module => {
                    const label = normalize(module.getAttribute('aria-label') || module.innerText);
                    return /^Module\s+\d+\b/i.test(label);
                });
                if (!modules.length) return null;

                const moduleLabel = module =>
                    normalize(module.getAttribute('aria-label') || module.innerText);
                const moduleNumber = (module, index) => {
                    const labelMatch = moduleLabel(module).match(/^Module\s+(\d+)\b/i);
                    if (labelMatch) return Number(labelMatch[1]);
                    try {
                        const pathMatch = new URL(
                            module.getAttribute('href') || '',
                            window.location.href).pathname.match(/\/home\/(?:week|module)\/(\d+)/i);
                        if (pathMatch) return Number(pathMatch[1]);
                    } catch (_) { }
                    return index + 1;
                };
                const moduleCompleted = (module, index) =>
                    /\bComplete(?:d)?\b/i.test(moduleLabel(module)) ||
                    automationCompletedModules.has(moduleNumber(module, index));
                let currentIndex = modules.findIndex(module => {
                    const label = moduleLabel(module);
                    return module.getAttribute('aria-current') === 'page' ||
                        module.getAttribute('aria-selected') === 'true' ||
                        module.getAttribute('aria-expanded') === 'true' ||
                        /\bselected\b/i.test(label);
                });
                if (currentIndex < 0) {
                    const currentPath = normalizePath(window.location.pathname);
                    currentIndex = modules.findIndex(module => {
                        try {
                            return normalizePath(new URL(
                                module.getAttribute('href') || '',
                                window.location.href).pathname) === currentPath;
                        } catch (_) {
                            return false;
                        }
                    });
                }
                const completedModules = modules.filter(moduleCompleted).length;
                if (currentIndex < 0) {
                    currentIndex = modules.findIndex((module, index) =>
                        !moduleCompleted(module, index));
                    if (currentIndex < 0) currentIndex = modules.length - 1;
                }

                const lessons = unique(Array.from(document.querySelectorAll(
                    'a[data-click-key="open_course_home.period_page.click.item_link"], ' +
                    'li[data-testid^="WeekSingleItemDisplay"] > a[href]'
                )));
                let supportedLessons = 0;
                let completedLessons = 0;
                let skippedAppItems = 0;
                let skippedPeerItems = 0;
                let skippedPendingResults = 0;
                let skippedSubmittedDiscussions = 0;
                for (const lesson of lessons) {
                    const html = lesson.innerHTML || '';
                    const ariaLabel = lesson.getAttribute('aria-label') || '';
                    const href = lesson.getAttribute('href') || '';
                    const itemText = normalize(ariaLabel + ' ' + (lesson.innerText || ''));
                    let lessonPath = '';
                    try {
                        lessonPath = normalizePath(
                            new URL(href, window.location.href).pathname);
                    } catch (_) { }
                    if (lessonPath && skippedLaunchAppItemPaths.has(lessonPath)) {
                        skippedAppItems++;
                        continue;
                    }
                    if (lessonPath && pendingResultPaths.has(lessonPath)) {
                        skippedPendingResults++;
                        continue;
                    }
                    if (lessonPath && submittedDiscussionPaths.has(lessonPath)) {
                        skippedSubmittedDiscussions++;
                        continue;
                    }
                    const isGradedAppItem = /\bGraded App Item\b/i.test(itemText);
                    const isPracticeAppItem = /\b(?:Practice|Ungraded) App Item\b/i.test(itemText);
                    const isPeerItem = /\/peer(?:\/|$)/i.test(href) ||
                        /\bpeer[-\s]?(?:graded|review(?:ed)?)\b|\bpeer\s+(?:assessment|assignment)\b/i.test(itemText);
                    if ((skipGradedAppItems && isGradedAppItem) ||
                        (skipPracticeAppItems && isPracticeAppItem)) {
                        skippedAppItems++;
                        continue;
                    }
                    if (skipPeerItems && isPeerItem) {
                        skippedPeerItems++;
                        continue;
                    }
                    supportedLessons++;
                    const hasModernCompletedIcon =
                        !!lesson.querySelector(':scope > span:first-child > svg');
                    if (html.includes('>Completed<') ||
                        /\bCompleted\b/i.test(ariaLabel) ||
                        hasModernCompletedIcon) {
                        completedLessons++;
                    }
                }

                let moduleFraction = moduleCompleted(modules[currentIndex], currentIndex) ? 1 : 0;
                if (supportedLessons > 0) {
                    moduleFraction = completedLessons / supportedLessons;
                } else if (skippedAppItems > 0 ||
                    skippedPeerItems > 0 ||
                    skippedPendingResults > 0 ||
                    skippedSubmittedDiscussions > 0) {
                    moduleFraction = 1;
                }

                let progress;
                if (completedModules === modules.length) {
                    progress = 100;
                    currentIndex = modules.length - 1;
                } else {
                    // Worker xử lý module tuần tự. App Item bị bỏ qua không được tính là
                    // bài hoàn thành và cũng không nằm trong mẫu số tiến độ tự động.
                    progress = Math.round(((currentIndex + moduleFraction) / modules.length) * 100);
                }
                progress = Math.max(0, Math.min(100, progress));
                return JSON.stringify({
                    Progress: progress,
                    CurrentModule: currentIndex + 1,
                    TotalModules: modules.length
                });
            })();
            """
            .Replace("__SKIP_GRADED__", ShouldSkipGradedAppItems ? "true" : "false")
            .Replace("__SKIP_PRACTICE__", ShouldSkipPracticeAppItems ? "true" : "false")
            .Replace("__SKIP_PEER__", ShouldSkipPeerItems ? "true" : "false")
            .Replace("__PENDING_RESULT_PATHS__", pendingResultPathsJson)
            .Replace("__SUBMITTED_DISCUSSION_PATHS__", submittedDiscussionPathsJson)
            .Replace("__SKIPPED_LAUNCH_APP_PATHS__", skippedLaunchAppItemPathsJson)
            .Replace("__AUTOMATION_COMPLETED_MODULES__", automationCompletedModulesJson);

        try
        {
            string raw = await MainWebView.ExecuteScriptAsync(script);
            string decoded = DecodeWebViewString(raw);
            return string.IsNullOrWhiteSpace(decoded)
                ? null
                : JsonSerializer.Deserialize<CourseProgressSnapshot>(decoded);
        }
        catch
        {
            // DOM có thể đang chuyển trang; giữ snapshot gần nhất thay vì làm tụt tiến độ.
            return null;
        }
    }

    private async Task CompleteCourseJobAsync()
    {
        if (_courseJobCompletionReported ||
            _workerLaunchOptions.IsDirectLogin ||
            _centralWorkerClient.CurrentJob == null)
        {
            return;
        }

        _courseJobCompletionReported = true;
        _workerHeartbeatTimer?.Stop();
        await _workerHeartbeatLock.WaitAsync();
        try
        {
            CourseProgressSnapshot? snapshot =
                await ReadCourseProgressSnapshotAsync() ?? _lastCourseProgressSnapshot;
            int totalModules = snapshot?.TotalModules ??
                _centralWorkerClient.CurrentJob.TotalModules ?? 0;
            if (totalModules <= 0)
            {
                _courseJobCompletionReported = false;
                _viewModel.StatusText =
                    "⚠️ Đã xong bài hỗ trợ nhưng chưa xác minh được tổng Module; chưa đóng job.";
                _workerHeartbeatTimer?.Start();
                return;
            }

            string completionNote = GetCourseCompletionNote();
            if (_courseHasSubmittedDiscussionItems)
            {
                completionNote = completionNote.TrimEnd('.', ' ') +
                    ". Phản hồi Discussion đã gửi được giữ nguyên và không chạy lại.";
            }
            _viewModel.StatusText = completionNote;
            _lastCourseProgressSnapshot = new CourseProgressSnapshot(
                100,
                totalModules,
                totalModules);
            await _centralWorkerClient.HeartbeatAsync(
                "completed",
                completionNote,
                100,
                totalModules,
                totalModules);

            WorkerJob? nextJob = await _centralWorkerClient.ClaimNextBatchJobAsync();
            if (nextJob != null)
            {
                _courseHasSkippedLaunchAppItems = false;
                _skippedLaunchAppItemPaths.Clear();
                _automationCompletedModuleNumbers.Clear();
                _courseHasSkippedPeerItems = false;
                _courseHasPendingGradedResults = false;
                _pendingGradedResultPaths.Clear();
                _courseHasSubmittedDiscussionItems = false;
                _submittedDiscussionPaths.Clear();
                _courseJobCompletionReported = false;
                _coursePauseInProgress = false;
                _courseLandingFallbackCount = 0;
                _lastCourseProgressSnapshot = null;
                _viewModel.StatusText =
                    "✅ Khóa hiện tại đã xong. Giữ nguyên phiên khách và chuyển sang khóa tiếp theo...";
                await _centralWorkerClient.HeartbeatAsync(
                    "running",
                    "Đã giữ phiên khách; đang mở khóa tiếp theo trong batch",
                    nextJob.Progress,
                    nextJob.CurrentModule,
                    nextJob.TotalModules);
                _workerHeartbeatTimer?.Start();
                UrlTextBox.Text = string.IsNullOrWhiteSpace(nextJob.TargetUrl)
                    ? "https://www.coursera.org/"
                    : nextJob.TargetUrl;
                OnTestClick(this, new RoutedEventArgs());
                return;
            }

            // Batch đã hết job, đóng worker để host nhận batch/tài khoản kế tiếp.
            _workerClosing = true;
            await Task.Delay(350);
            Close();
        }
        catch (Exception exception)
        {
            _courseJobCompletionReported = false;
            _viewModel.StatusText = "⚠️ Chưa báo hoàn thành được: " + exception.Message;
            _workerHeartbeatTimer?.Start();
        }
        finally
        {
            _workerHeartbeatLock.Release();
        }
    }

    private string GetCourseCompletionNote()
    {
        if (_courseHasPendingGradedResults &&
            _courseHasSkippedPeerItems &&
            _courseHasSkippedLaunchAppItems)
        {
            return "✅ Đã hoàn thành hết các bài tự động. Còn bài đã nộp đang chờ chấm điểm, " +
                "Peer-graded/Peer Review và Launch App (Graded/Practice App Item) cần xử lý thủ công.";
        }

        if (_courseHasPendingGradedResults && _courseHasSkippedPeerItems)
        {
            return "✅ Đã hoàn thành hết các bài tự động. Còn bài đã nộp đang chờ chấm điểm và " +
                "các bài Peer-graded/Peer Review cần xử lý thủ công.";
        }

        if (_courseHasPendingGradedResults && _courseHasSkippedLaunchAppItems)
        {
            return "✅ Đã hoàn thành các bài tự động. Còn bài đã nộp đang chờ chấm điểm và " +
                "Launch App (Graded/Practice App Item) cần xử lý riêng.";
        }

        if (_courseHasPendingGradedResults)
        {
            return "✅ Đã hoàn thành các bài tự động. Còn bài đã nộp đang chờ Coursera chấm điểm.";
        }

        if (_courseHasSkippedPeerItems && _courseHasSkippedLaunchAppItems)
        {
            return "✅ Đã hoàn thành hết các bài tự động. Còn Peer-graded/Peer Review và " +
                "Launch App (Graded/Practice App Item) cần xử lý thủ công.";
        }

        if (_courseHasSkippedPeerItems)
        {
            return "✅ Đã hoàn thành hết các bài tự động. Chỉ còn các bài Peer-graded và " +
                "Peer Review cần xử lý thủ công.";
        }

        if (_courseHasSkippedLaunchAppItems)
        {
            return "✅ Hoàn thành các bài tự động. Còn bài Launch App " +
                "(Graded/Practice App Item) đã bỏ qua và cần xử lý riêng.";
        }

        return "✅ Đã hoàn thành toàn bộ các bài trong khóa học.";
    }

    private Task FailCourseJobForAiErrorAsync(string aiMessage) =>
        FailCourseJobAsync("❌ Lỗi AI: " + aiMessage);

    private async Task PauseCourseJobAsync(
        string activity,
        string manualActionReason,
        string errorCode)
    {
        if (_workerLaunchOptions.IsDirectLogin ||
            IsInteractiveBrowseSession ||
            _centralWorkerClient.CurrentJob is not { } jobToPause ||
            _courseJobCompletionReported ||
            _coursePauseInProgress ||
            _workerClosing)
        {
            return;
        }

        _coursePauseInProgress = true;
        _courseJobCompletionReported = true;
        _workerHeartbeatTimer?.Stop();
        _viewModel.StatusText = activity;
        bool reported = false;
        try
        {
            CourseProgressSnapshot? snapshot =
                await ReadCourseProgressSnapshotAsync() ?? _lastCourseProgressSnapshot;
            using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _workerHeartbeatLock.WaitAsync(reportTimeout.Token);
            try
            {
                if (_centralWorkerClient.CurrentJob is not { } currentJob ||
                    !string.Equals(currentJob.Id, jobToPause.Id, StringComparison.Ordinal))
                {
                    _coursePauseInProgress = false;
                    _courseJobCompletionReported = false;
                    _workerHeartbeatTimer?.Start();
                    return;
                }

                await _centralWorkerClient.PauseAsync(
                    activity,
                    manualActionReason,
                    errorCode,
                    snapshot?.Progress,
                    snapshot?.CurrentModule,
                    snapshot?.TotalModules,
                    reportTimeout.Token);
                reported = true;
            }
            finally
            {
                _workerHeartbeatLock.Release();
            }
        }
        catch (Exception exception)
        {
            _coursePauseInProgress = false;
            _courseJobCompletionReported = false;
            _workerHeartbeatTimer?.Start();
            _viewModel.StatusText =
                activity + " (chưa báo được trung tâm: " + exception.Message + ")";
        }

        if (!reported)
        {
            return;
        }

        // The central server has released this worker from the durable profile.
        // Close intentionally so MainWindow_Closed never overwrites the manual
        // pause with a failure and Worker Host can launch a visible browse job.
        _workerClosing = true;
        await Task.Delay(350);
        Close();
    }

    private async Task FailCourseJobAsync(string activity)
    {
        _viewModel.StatusText = activity;

        if (_workerLaunchOptions.IsDirectLogin ||
            _centralWorkerClient.CurrentJob is not { } jobToFail ||
            _courseJobCompletionReported ||
            _workerClosing)
        {
            return;
        }

        // A terminal workflow failure means this worker cannot make further safe
        // progress on the current course. Stop heartbeats before reporting a
        // terminal state so the server never sees the same job as running forever.
        _courseJobCompletionReported = true;
        _workerHeartbeatTimer?.Stop();
        var reported = false;
        try
        {
            using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _workerHeartbeatLock.WaitAsync(reportTimeout.Token);
            try
            {
                // A completion path can claim the next course in a batch while
                // an older AI request is still unwinding.  Never fail that next
                // course with the stale result from the previous one.
                if (_centralWorkerClient.CurrentJob is not { } currentJob ||
                    !string.Equals(currentJob.Id, jobToFail.Id, StringComparison.Ordinal))
                {
                    return;
                }

                await _centralWorkerClient.FailAsync(activity, reportTimeout.Token);
                reported = true;
            }
            finally
            {
                _workerHeartbeatLock.Release();
            }
        }
        catch
        {
            // A temporary central-server outage should not close the browser and
            // lose the recoverable course session. Resume the ordinary heartbeat.
            if (_centralWorkerClient.CurrentJob is { } currentJob &&
                string.Equals(currentJob.Id, jobToFail.Id, StringComparison.Ordinal))
            {
                _courseJobCompletionReported = false;
                _workerHeartbeatTimer?.Start();
                _viewModel.StatusText = activity + " (chưa báo được trung tâm; sẽ thử lại)";
            }
        }

        if (!reported ||
            _centralWorkerClient.CurrentJob is not { } activeJob ||
            !string.Equals(activeJob.Id, jobToFail.Id, StringComparison.Ordinal))
        {
            return;
        }

        _workerClosing = true;
        await Task.Delay(350);
        Close();
    }

    private async void MainWebView_NewWindowRequested(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (_directLoginActive)
        {
            e.Handled = true;
            Microsoft.Web.WebView2.Core.CoreWebView2Deferral oauthDeferral = e.GetDeferral();
            try
            {
                await _directLoginPopupLock.WaitAsync();
                try
                {
                    if (_directLoginOAuthWindow != null || _directLoginOAuthWebView != null)
                    {
                        _directLoginOAuthFailure = "Google đã yêu cầu nhiều cửa sổ đăng nhập cùng lúc.";
                        _directLoginOAuthPopupOpened?.TrySetResult(false);
                        return;
                    }
                    if (!IsAllowedDirectLoginPopupUri(e.Uri))
                    {
                        _directLoginOAuthFailure = BuildBlockedOAuthDestinationMessage(
                            "Google trả về",
                            e.Uri);
                        _directLoginOAuthPopupOpened?.TrySetResult(false);
                        return;
                    }
                    _directLoginOAuthExpectedRedirectUri ??=
                        TryExtractGoogleOAuthRedirectUri(e.Uri);

                    // Host OAuth in a real WPF WebView2 window. The former raw HWND
                    // controller was outside WPF layout and could be covered by the
                    // main Coursera view, making a successful click look like nothing
                    // had happened.
                    var popupBrowser = new Microsoft.Web.WebView2.Wpf.WebView2();
                    var popupWindow = new Window
                    {
                        Owner = this,
                        Title = "ACOSE · Đăng nhập Google",
                        Width = 980,
                        Height = 760,
                        MinWidth = 720,
                        MinHeight = 540,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        ShowInTaskbar = true,
                        Content = popupBrowser
                    };
                    popupWindow.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_directLoginOAuthWindow, popupWindow))
                        {
                            _directLoginOAuthWindow = null;
                            _directLoginOAuthBrowser = null;
                            _directLoginOAuthWebView = null;
                            _directLoginOAuthExpectedRedirectUri = null;
                        }
                    };

                    _directLoginOAuthWindow = popupWindow;
                    _directLoginOAuthBrowser = popupBrowser;
                    popupWindow.Show();
                    await popupBrowser.EnsureCoreWebView2Async(
                        MainWebView.CoreWebView2.Environment);
                    Microsoft.Web.WebView2.Core.CoreWebView2 oauthWebView =
                        popupBrowser.CoreWebView2
                        ?? throw new InvalidOperationException("OAuth WebView2 is not initialized.");
                    oauthWebView.Settings.IsPasswordAutosaveEnabled = false;
                    oauthWebView.Settings.IsGeneralAutofillEnabled = false;
                    oauthWebView.NavigationStarting += DirectLoginOAuth_NavigationStarting;
                    oauthWebView.NewWindowRequested += (_, childArgs) =>
                    {
                        if (!IsAllowedDirectLoginPopupUri(childArgs.Uri))
                        {
                            childArgs.Handled = true;
                            _directLoginOAuthFailure = BuildBlockedOAuthDestinationMessage(
                                "Google yêu cầu mở",
                                childArgs.Uri);
                        }
                    };
                    oauthWebView.WindowCloseRequested += (_, _) =>
                        Dispatcher.BeginInvoke(CloseDirectLoginOAuthWindow);
                    oauthWebView.ProcessFailed += (_, _) =>
                    {
                        if (_directLoginActive && !_directLoginTerminal)
                        {
                            _directLoginOAuthFailure =
                                "Cửa sổ đăng nhập Google đã dừng đột ngột. Vui lòng thử lại.";
                            _directLoginOAuthPopupOpened?.TrySetResult(false);
                        }
                    };

                    _directLoginOAuthWebView = oauthWebView;
                    e.NewWindow = oauthWebView;
                    _directLoginOAuthPopupOpened?.TrySetResult(true);
                }
                finally
                {
                    _directLoginPopupLock.Release();
                }
            }
            catch
            {
                _directLoginOAuthFailure = "Máy chủ không tạo được cửa sổ đăng nhập Google an toàn.";
                _directLoginOAuthPopupOpened?.TrySetResult(false);
                CloseDirectLoginOAuthWindow();
            }
            finally
            {
                oauthDeferral.Complete();
            }
            return;
        }

        if (IsCourseraLockUri(e.Uri))
        {
            e.Handled = true;
            _suppressedLtiNewWindow = true;
            return;
        }

        if (_suppressLtiNewWindowSourceUri == null ||
            DateTimeOffset.UtcNow > _suppressLtiNewWindowUntilUtc ||
            !IsSameCourseraDocument(_suppressLtiNewWindowSourceUri))
        {
            return;
        }

        Microsoft.Web.WebView2.Core.CoreWebView2Deferral deferral = e.GetDeferral();
        Microsoft.Web.WebView2.Core.CoreWebView2Controller? hiddenController = null;
        try
        {
            IntPtr parentWindow = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            hiddenController = await MainWebView.CoreWebView2.Environment
                .CreateCoreWebView2ControllerAsync(parentWindow);
            hiddenController.Bounds = new System.Drawing.Rectangle(0, 0, 1, 1);
            hiddenController.IsVisible = false;
            hiddenController.CoreWebView2.NavigationStarting += CancelCourseraLockNavigation;
            hiddenController.CoreWebView2.LaunchingExternalUriScheme +=
                CancelCourseraLockExternalLaunch;
            hiddenController.CoreWebView2.NewWindowRequested += (_, childWindowArgs) =>
            {
                // WebView ẩn không được phép sinh thêm tab/cửa sổ ngoài.
                childWindowArgs.Handled = true;
            };

            var navigationFinished = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            hiddenController.CoreWebView2.NavigationCompleted += (_, _) =>
                navigationFinished.TrySetResult(true);

            _hiddenLtiControllers.Add(hiddenController);
            e.NewWindow = hiddenController.CoreWebView2;
            e.Handled = true;
            _suppressedLtiNewWindow = true;
            _suppressLtiNewWindowSourceUri = null;
            _suppressLtiNewWindowUntilUtc = DateTimeOffset.MinValue;

            _ = RetireHiddenLtiControllerAsync(hiddenController, navigationFinished.Task);
            hiddenController = null;
        }
        catch
        {
            // Không để rơi ra tab mới nếu WebView ẩn không khởi tạo được.
            e.Handled = true;
            _suppressedLtiNewWindow = true;
            try { hiddenController?.Close(); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private Microsoft.Web.WebView2.Core.CoreWebView2 GetDirectLoginWebView()
    {
        return _directLoginOAuthWebView ?? MainWebView.CoreWebView2
            ?? throw new InvalidOperationException("Direct login WebView2 is not initialized.");
    }

    private static bool IsHostOrSubdomain(string? host, string expectedHost)
    {
        return string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(host) &&
                host.EndsWith("." + expectedHost, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGoogleAccountsHost(string? host)
    {
        return IsHostOrSubdomain(host, "accounts.google.com") ||
               IsHostOrSubdomain(host, "accounts.google.com.vn") ||
               IsHostOrSubdomain(host, "accounts.youtube.com");
    }

    private bool IsAllowedDirectLoginPopupUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        if (IsHostOrSubdomain(uri.Host, "google.com") ||
            IsHostOrSubdomain(uri.Host, "google.com.vn") ||
            IsHostOrSubdomain(uri.Host, "googleusercontent.com") ||
            string.Equals(uri.Host, "accounts.youtube.com", StringComparison.OrdinalIgnoreCase) ||
            IsHostOrSubdomain(uri.Host, "coursera.org"))
        {
            return true;
        }
        return _directLoginOAuthExpectedRedirectUri != null &&
               Uri.Compare(
                   uri,
                   _directLoginOAuthExpectedRedirectUri,
                   UriComponents.SchemeAndServer | UriComponents.Path,
                   UriFormat.Unescaped,
                   StringComparison.Ordinal) == 0;
    }

    private static Uri? TryExtractGoogleOAuthRedirectUri(string? value, int depth = 0)
    {
        if (depth > 3 ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }
        if (depth == 0 &&
            (uri.Scheme != Uri.UriSchemeHttps ||
             !IsGoogleAccountsHost(uri.Host)))
        {
            // Chỉ tin redirect_uri được lấy từ chính luồng HTTPS của Google.
            return null;
        }

        string query = uri.Query.TrimStart('?');
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string encodedKey = separator >= 0 ? pair[..separator] : pair;
            string encodedValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            string key = DecodeOAuthQueryComponent(encodedKey);
            string decodedValue = DecodeOAuthQueryComponent(encodedValue);

            if (key.Equals("redirect_uri", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(decodedValue, UriKind.Absolute, out Uri? redirectUri) &&
                redirectUri.Scheme == Uri.UriSchemeHttps)
            {
                return redirectUri;
            }

            if (key is "continue" or "url" or "next" or "redirect")
            {
                Uri? nested = TryExtractGoogleOAuthRedirectUri(decodedValue, depth + 1);
                if (nested != null)
                {
                    return nested;
                }
            }
        }
        return null;
    }

    private static string DecodeOAuthQueryComponent(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch
        {
            return value;
        }
    }

    private static string BuildBlockedOAuthDestinationMessage(string prefix, string? value)
    {
        string destination = Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.Host
            : "không xác định";
        return $"{prefix} một tên miền không được phép ({destination}).";
    }

    private void DirectLoginOAuth_NavigationStarting(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        _directLoginOAuthExpectedRedirectUri ??=
            TryExtractGoogleOAuthRedirectUri(e.Uri);
        if (IsAllowedDirectLoginPopupUri(e.Uri))
        {
            return;
        }
        e.Cancel = true;
        _directLoginOAuthFailure = BuildBlockedOAuthDestinationMessage(
            "Cửa sổ Google chuyển tới",
            e.Uri);
    }

    private void CloseDirectLoginOAuthWindow()
    {
        Window? popupWindow = _directLoginOAuthWindow;
        Microsoft.Web.WebView2.Core.CoreWebView2? popupWebView = _directLoginOAuthWebView;
        _directLoginOAuthWindow = null;
        _directLoginOAuthBrowser = null;
        _directLoginOAuthWebView = null;
        _directLoginOAuthExpectedRedirectUri = null;
        if (popupWindow == null)
        {
            return;
        }
        try
        {
            if (popupWebView != null)
            {
                popupWebView.NavigationStarting -= DirectLoginOAuth_NavigationStarting;
            }
        }
        catch { }
        try { popupWindow.Close(); } catch { }
    }

    private static bool IsCourseraLockUri(string? uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) &&
            string.Equals(parsed.Scheme, "coursera-lock", StringComparison.OrdinalIgnoreCase);
    }

    private static void CancelCourseraLockNavigation(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsCourseraLockUri(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static void CancelCourseraLockExternalLaunch(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        if (IsCourseraLockUri(e.Uri))
        {
            // Chặn hộp thoại “open coursera-locking-browser” của Windows/WebView2.
            e.Cancel = true;
        }
    }

    private async Task RetireHiddenLtiControllerAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2Controller controller,
        Task navigationFinished)
    {
        try
        {
            await Task.WhenAny(navigationFinished, Task.Delay(TimeSpan.FromSeconds(12)));
            await Task.Delay(2500);
        }
        finally
        {
            _hiddenLtiControllers.Remove(controller);
            try { controller.Close(); } catch { }
        }
    }

    private void StartPopupWatchdog()
    {
        if (_popupWatchdogTimer != null)
        {
            return;
        }

        _popupWatchdogTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _popupWatchdogTimer.Tick += PopupWatchdogTimer_Tick;
        _popupWatchdogTimer.Start();
    }

    private async void PopupWatchdogTimer_Tick(object? sender, EventArgs e)
    {
        if (MainWebView.CoreWebView2 == null || !IsCourseraUri(MainWebView.Source))
        {
            return;
        }

        await DismissAnyGlobalPopupsAsync(maxPasses: 1);
    }

    private bool _isHandlingDialogue = false;

    private async Task HandleDialogueAsync()
    {
        if (_isHandlingDialogue) return;
        _isHandlingDialogue = true;

        try
        {
            if (await CheckForLockedScreenAndReloadAsync()) return;

            _viewModel.StatusText = "💬 Phát hiện bài Dialogue. Đang tìm nút Start...";
            await Task.Delay(3000);
            await DismissAnyGlobalPopupsAsync();

            // Thử click Start Dialogue tối đa 10 lần (30 giây)
            bool clicked = false;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string jsClick = @"
                    (function() {
                        // Kiểm tra xem có đang ở sẵn trong chat không (nếu đã start từ trước)
                        var ta = document.querySelector('textarea');
                        var endBtn = Array.from(document.querySelectorAll('button')).find(b => (b.innerText || '').trim().toLowerCase().includes('end dialogue'));
                        if (ta || endBtn) {
                            return 'ALREADY_STARTED';
                        }

                        // Cách 1: Tìm span chứa text 'Start Dialogue' rồi click button cha
                        var spans = Array.from(document.querySelectorAll('span.cds-button-label'));
                        for (var i = 0; i < spans.length; i++) {
                            if (spans[i].textContent.trim() === 'Start Dialogue') {
                                var btn = spans[i].closest('button');
                                if (btn) { btn.click(); return 'CLICKED_VIA_SPAN'; }
                            }
                        }
                        // Cách 2: Tìm button có class cds-button-primary chứa text Start Dialogue
                        var btns = Array.from(document.querySelectorAll('button.cds-button-primary, button[class*=""cds-button-primary""]'));
                        for (var j = 0; j < btns.length; j++) {
                            if ((btns[j].textContent || '').trim().toLowerCase().includes('start dialogue')) {
                                btns[j].click();
                                return 'CLICKED_VIA_CLASS';
                            }
                        }
                        // Cách 3: Brute force - quét toàn bộ button
                        var allBtns = Array.from(document.querySelectorAll('button'));
                        for (var k = 0; k < allBtns.length; k++) {
                            if ((allBtns[k].textContent || '').trim().toLowerCase().includes('start dialogue')) {
                                allBtns[k].click();
                                return 'CLICKED_VIA_BRUTE';
                            }
                        }
                        return 'NOT_FOUND|total_buttons=' + document.querySelectorAll('button').length + '|total_spans=' + document.querySelectorAll('span').length;
                    })();
                ";

                string result = await MainWebView.ExecuteScriptAsync(jsClick);
                string cleaned = result?.Trim('"') ?? "";
                _viewModel.StatusText = $"🔍 Lần {attempt + 1}: {cleaned}";

                if (cleaned.StartsWith("CLICKED"))
                {
                    clicked = true;
                    _viewModel.StatusText = $"▶️ Đã click Start Dialogue! ({cleaned})";
                    break;
                }
                else if (cleaned == "ALREADY_STARTED")
                {
                    clicked = true;
                    _viewModel.StatusText = "▶️ Đã ở trong khung Chat từ trước! Bỏ qua bấm Start.";
                    break;
                }

                await Task.Delay(3000);
            }

            if (!clicked)
            {
                _viewModel.StatusText = "⚠️ Không tìm thấy nút Start Dialogue sau 10 lần thử.";
            }

            // Vòng lặp chat tự động & chờ hoàn thành (tối đa 5 phút)
            string lastQuestion = "";
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(5000);

                // Kiểm tra Next button & dọn dẹp các popup thừa
                string jsNext = @"
                    (function() {
                        // Nhỡ bấm nhầm End Dialogue thì Cancel nó đi
                        var cancelBtn = Array.from(document.querySelectorAll('button')).find(b => (b.innerText || '').trim().toLowerCase() === 'cancel');
                        var yesEndBtn = Array.from(document.querySelectorAll('button')).find(b => (b.innerText || '').trim().toLowerCase().includes('end the dialogue'));
                        if (cancelBtn && yesEndBtn) {
                            cancelBtn.click();
                        }

                        var nextBtn = document.querySelector('button[aria-label=""Go to next item""]');
                        if (nextBtn && nextBtn.className.includes('cds-button-primary')) {
                            nextBtn.click();
                            return 'NEXT';
                        }
                        return 'WAIT';
                    })();
                ";
                try
                {
                    string r = await MainWebView.ExecuteScriptAsync(jsNext);
                    if (r != null && r.Contains("NEXT"))
                    {
                        _viewModel.StatusText = "✅ Bài Dialogue hoàn thành! Đang chuyển bài.";
                        return;
                    }
                } catch { }

                // Nếu chưa xong, kiểm tra xem có thể dùng Trick "End Dialogue" để pass luôn không
                string jsEndTrick = @"
                    (function() {
                        // Bấm 'Yes, end the Dialogue' nếu popup đang mở
                        var yesEndBtn = Array.from(document.querySelectorAll('button')).find(b => (b.innerText || '').trim().toLowerCase().includes('end the dialogue'));
                        if (yesEndBtn) {
                            yesEndBtn.click();
                            return 'CLICKED_YES';
                        }

                        // Nếu chưa có popup, tìm nút 'End Dialogue' góc trên phải
                        var endBtn = Array.from(document.querySelectorAll('button')).find(b => (b.innerText || '').trim().toLowerCase() === 'end dialogue');
                        if (endBtn) {
                            endBtn.click();
                            return 'CLICKED_END';
                        }
                        
                        return 'WAITING';
                    })();
                ";
                
                try
                {
                    string trickResult = await MainWebView.ExecuteScriptAsync(jsEndTrick);
                    if (trickResult != null)
                    {
                        if (trickResult.Contains("CLICKED_END"))
                            _viewModel.StatusText = "⚡ Dùng trick: Đã bấm End Dialogue! Chờ xác nhận...";
                        else if (trickResult.Contains("CLICKED_YES"))
                            _viewModel.StatusText = "⚡ Dùng trick: Đã xác nhận End! Chờ nút Next sáng lên...";
                    }
                }
                catch { }

                _viewModel.StatusText = $"⏳ Dialogue đang chạy & Auto-Trick... ({(i + 1) * 5}s)";
            }

            await CheckLessonCompletedAndClickNextAsync();
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "❌ Lỗi Dialogue: " + ex.Message;
        }
        finally
        {
            _isHandlingDialogue = false;
        }
    }

    private string _lastReloadedUrl = "";

    private async Task<bool> CheckForLockedScreenAndReloadAsync()
    {
        string currentUrl = MainWebView.Source != null ? MainWebView.Source.ToString() : "";
        if (_lastReloadedUrl == currentUrl && !string.IsNullOrEmpty(currentUrl)) 
        {
            // Đã reload 1 lần ở bài này rồi. Nếu vẫn bị khoá thì có thể là khoá thật hoặc lỗi hiển thị trắng trang.
            // Bỏ qua không reload nữa để tránh lặp vô tận.
            return false; 
        }

        string jsCheckLocked = @"
            (function() {
                // CHỈ kiểm tra aria-label của sidebar item đang active
                // KHÔNG check body text vì sidebar có các bài khác bị khoá sẽ làm nhiễu!
                var activeItem = document.querySelector('a[aria-current=""page""]') || 
                                 document.querySelector('li[class*=""selected""] a');
                
                if (activeItem) {
                    var ariaLabel = (activeItem.getAttribute('aria-label') || '').toLowerCase();
                    // Fix lỗi false positive: chỉ bắt đúng chữ 'locked', không bắt 'unlocked'
                    if (/\blocked\b/i.test(ariaLabel)) {
                        return 'LOCKED';
                    }
                }
                
                return 'NOT_LOCKED';
            })();
        ";
        string result = DecodeWebViewString(
            await MainWebView.ExecuteScriptAsync(jsCheckLocked));
        if (string.Equals(result, "LOCKED", StringComparison.Ordinal))
        {
            _lastReloadedUrl = currentUrl; // Đánh dấu URL này đã được chẩn trị
            _viewModel.StatusText = "🔒 Phát hiện bài học bị khoá ảo! Đang tải lại trang để làm mới hệ thống...";
            MainWebView.Reload();
            return true; // Báo hiệu đã xử lý (đang reload)
        }
        return false;
    }

    private async Task<bool> CheckLessonCompletedAndClickNextAsync(bool isInitialCheck = false)
    {
        string jsCheck = @"
            (function() {
                var nextBtn = document.querySelector('button[aria-label=""Go to next item""]');
                
                if (nextBtn) {
                    if (nextBtn.className.includes('cds-button-primary')) {
                        nextBtn.click(); 
                        return 'CLICKED'; 
                    }
                    return 'WAITING';
                }
                
                // Cảm biến Cul-de-sac (Ngõ cụt): Không có nút Next, nhưng trang đã load xong
                var isPageLoaded = document.querySelector('div[data-testid=""page-header-wrapper""]') !== null || document.querySelector('.rc-MetatagsWrapper') !== null;
                // Các trang tương tác chưa hoàn tất (quiz, dialogue, peer review, ...)
                // thường không hiện nút Next. Chúng không phải là cuối khoá học.
                var isIncompleteActivity = window.location.href.includes('/exam/') ||
                    window.location.href.includes('/quiz/') ||
                    window.location.href.includes('/dialogue/') ||
                    window.location.href.includes('/coach/') ||
                    window.location.href.includes('/peer/');
                
                if (isPageLoaded && !nextBtn && !isIncompleteActivity) {
                    return 'END_OF_COURSE';
                }
                
                return 'LOADING';
            })();
        ";

        try
        {
            string result = await MainWebView.ExecuteScriptAsync(jsCheck);
            if (result == null) return false;
            
            result = result.Trim('"');
            
            if (result == "END_OF_COURSE" && !isInitialCheck)
            {
                // Ngõ cụt! Trở về trang chủ để cơ chế CheckModules quét lại (Sweep)
                string currentUrl = MainWebView.Source?.ToString() ?? "";
                int learnIndex = currentUrl.IndexOf("/learn/");
                if (learnIndex != -1)
                {
                    int nextSlash = currentUrl.IndexOf("/", learnIndex + 7);
                    string courseSlug = nextSlash != -1 
                        ? currentUrl.Substring(learnIndex + 7, nextSlash - (learnIndex + 7))
                        : currentUrl.Substring(learnIndex + 7);
                    
                    MainWebView.Source = new Uri($"https://www.coursera.org/learn/{courseSlug}/home/welcome");
                    return true; // Giả vờ click Next thành công để ngắt vòng lặp
                }
            }
            
            return result == "CLICKED";
        }
        catch
        {
            return false;
        }
    }

    private async Task Checkkhoahoc()
    {
        if (_isHandlingCourseLanding)
        {
            return;
        }

        _isHandlingCourseLanding = true;
        try
        {
            Uri? landingUri = MainWebView.Source;
            if (!IsCourseraUri(landingUri) ||
                !TryGetCourseHomeUri(landingUri, out Uri? courseHomeUri) ||
                courseHomeUri == null)
            {
                await PauseCourseJobAsync(
                    "⏸️ Không xác định được khu vực học; Worker đã dừng để tránh lặp vô hạn.",
                    "Hãy mở profile, kiểm tra quyền truy cập đúng khóa rồi bấm Tiếp tục.",
                    "COURSE_ENTRY_UNAVAILABLE");
                return;
            }

            _viewModel.StatusText = "🔎 Đang tìm nút vào khóa học...";
            const string probeTemplate = """
                (function() {
                    const target = new URL(__EXPECTED_URL_JSON__);
                    const current = new URL(location.href);
                    if (current.origin !== target.origin ||
                        current.pathname !== target.pathname ||
                        current.search !== target.search) {
                        return JSON.stringify({ State: 'STALE_DOCUMENT', Href: null, Label: null });
                    }

                    const normalize = value => String(value || '')
                        .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
                    const isVisibleAndEnabled = element => {
                        if (!(element instanceof HTMLElement) || !element.isConnected ||
                            element.matches('[disabled], [aria-disabled="true"]')) {
                            return false;
                        }
                        const style = window.getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return !!style && style.display !== 'none' &&
                            style.visibility !== 'hidden' &&
                            style.visibility !== 'collapse' &&
                            Number(style.opacity || '1') > 0 &&
                            rect.width > 0 && rect.height > 0;
                    };
                    const accessibleText = element => normalize(
                        element.getAttribute('aria-label') ||
                        element.getAttribute('title') ||
                        element.innerText ||
                        element.textContent || '');
                    const allowedNames = new Set([
                        'go to course',
                        'continue learning',
                        'continue course',
                        'resume course',
                        'start course'
                    ]);
                    const controls = Array.from(document.querySelectorAll('a, button, [role="button"]'))
                        .filter(isVisibleAndEnabled);
                    const exactMatches = controls.filter(control =>
                        allowedNames.has(accessibleText(control)));
                    const attributeMatches = controls.filter(control => {
                        const attributes = [
                            control.getAttribute('data-e2e'),
                            control.getAttribute('data-testid'),
                            control.getAttribute('data-track-component'),
                            control.getAttribute('data-track-action')
                        ].map(normalize).join(' ');
                        return /(?:^|[\s_-])go[\s_-]?to[\s_-]?course(?:$|[\s_-])/i.test(attributes);
                    });
                    const candidates = exactMatches.length > 0 ? exactMatches : attributeMatches;
                    if (candidates.length === 1) {
                        const candidate = candidates[0];
                        const anchor = candidate instanceof HTMLAnchorElement
                            ? candidate
                            : candidate.closest('a[href]');
                        const href = anchor ? anchor.href : null;
                        if (href) {
                            return JSON.stringify({
                                State: 'OPEN_HREF',
                                Href: href,
                                Label: accessibleText(candidate)
                            });
                        }
                        candidate.click();
                        return JSON.stringify({
                            State: 'CLICKED',
                            Href: null,
                            Label: accessibleText(candidate)
                        });
                    }
                    if (candidates.length > 1) {
                        return JSON.stringify({ State: 'AMBIGUOUS', Href: null, Label: null });
                    }

                    const paidOrTrialLabels = new Set([
                        'start free trial', 'buy now', 'purchase',
                        'subscribe', 'choose a plan', 'start trial',
                        'view plans', 'see plans', 'get coursera plus',
                        'try coursera plus', 'join coursera plus',
                        'upgrade to coursera plus', 'upgrade to plus'
                    ]);
                    const freeEnrollmentLabels = new Set([
                        'enroll', 'enroll for free', 'join for free'
                    ]);
                    const enrollmentCandidates = controls.filter(control => {
                        const label = accessibleText(control);
                        const attributes = [
                            control.getAttribute('data-e2e'),
                            control.getAttribute('data-testid'),
                            control.getAttribute('data-track-component'),
                            control.getAttribute('data-track-action')
                        ].map(normalize).join(' ');
                        const hasChargeLanguage = /trial|buy|purchase|subscribe|payment|checkout|price|coursera plus|view plans|upgrade/.test(label);
                        const isLegacyEnrollButton =
                            /enroll(?:[\s_-]?button)/i.test(attributes) &&
                            /enroll|join/.test(label);
                        return !hasChargeLanguage &&
                            (freeEnrollmentLabels.has(label) || isLegacyEnrollButton);
                    });
                    if (enrollmentCandidates.length === 1) {
                        const candidate = enrollmentCandidates[0];
                        candidate.click();
                        return JSON.stringify({
                            State: 'ENROLL_CLICKED',
                            Href: null,
                            Label: accessibleText(candidate)
                        });
                    }
                    if (enrollmentCandidates.length > 1) {
                        return JSON.stringify({ State: 'ENROLL_AMBIGUOUS', Href: null, Label: null });
                    }
                    const hasPaidControl = controls.some(control => {
                        const label = accessibleText(control);
                        return paidOrTrialLabels.has(label) ||
                            /(?:get|try|join|upgrade(?:\s+to)?|subscribe(?:\s+to)?)\s+(?:coursera\s+)?plus\b/.test(label) ||
                            /\b(?:view|see|choose)\s+(?:a\s+)?plans?\b/.test(label);
                    });
                    const pageText = normalize(document.body?.innerText || '');
                    const hasPlusGateText = /\bcoursera plus\b/.test(pageText) &&
                        /\b(?:upgrade|subscribe|subscription|trial|plan|purchase|required|access)\b/.test(pageText);
                    if (hasPaidControl || hasPlusGateText) {
                        return JSON.stringify({ State: 'PAID_OR_TRIAL', Href: null, Label: null });
                    }
                    if (controls.some(control => /\benroll\b|\bjoin\b/.test(accessibleText(control)))) {
                        return JSON.stringify({ State: 'NOT_ENROLLED', Href: null, Label: null });
                    }
                    return JSON.stringify({ State: 'NOT_FOUND', Href: null, Label: null });
                })();
                """;

            for (int attempt = 1; attempt <= 12; attempt++)
            {
                if (!IsSameCourseraDocument(landingUri))
                {
                    return;
                }

                string script = probeTemplate.Replace(
                    "__EXPECTED_URL_JSON__",
                    JsonSerializer.Serialize(landingUri!.ToString()),
                    StringComparison.Ordinal);
                string raw = await MainWebView.ExecuteScriptAsync(script);
                string decoded = DecodeWebViewString(raw);
                CourseLandingProbe? probe = null;
                try
                {
                    probe = JsonSerializer.Deserialize<CourseLandingProbe>(decoded);
                }
                catch
                {
                    // React may still be hydrating; retry below while the document stays stable.
                }

                switch (probe?.State)
                {
                    case "STALE_DOCUMENT":
                        return;

                    case "OPEN_HREF":
                        if (!TryResolveTrustedCourseEntryUri(landingUri, probe.Href, out Uri? entryUri) ||
                            entryUri == null)
                        {
                            await PauseCourseJobAsync(
                                "⏸️ Nút vào khóa học dẫn tới route không xác định; Worker đã tạm dừng.",
                                "Hãy mở profile và vào đúng khóa học trước khi bấm Tiếp tục.",
                                "COURSE_ENTRY_MISMATCH");
                            return;
                        }

                        _viewModel.StatusText = "✅ Đã tìm thấy nút vào khóa học. Đang mở nội dung học...";
                        MainWebView.Source = entryUri;
                        return;

                    case "CLICKED":
                        _viewModel.StatusText = "✅ Đã bấm nút vào khóa học. Đang chờ Coursera chuyển trang...";
                        if (await WaitForAppItemNavigationAsync(landingUri, TimeSpan.FromSeconds(5)) ||
                            !IsSameCourseraDocument(landingUri))
                        {
                            return;
                        }

                        // A recognized entry button occasionally has a React handler that
                        // renders without navigating. The canonical home route is a safe,
                        // same-course fallback and avoids leaving the worker stuck here.
                        MainWebView.Source = courseHomeUri;
                        _viewModel.StatusText = "⏭️ Nút vào khóa học không tự chuyển trang; đang mở khu vực học...";
                        return;

                    case "ENROLL_CLICKED":
                        _viewModel.StatusText = "✅ Đang đăng ký khóa học miễn phí. Đang chờ Coursera xác nhận...";
                        if (await WaitForAppItemNavigationAsync(landingUri, TimeSpan.FromSeconds(7)) ||
                            !IsSameCourseraDocument(landingUri))
                        {
                            return;
                        }

                        _viewModel.StatusText =
                            "⏸️ Coursera đang chờ xác nhận đăng ký thủ công.";
                        await PauseCourseJobAsync(
                            _viewModel.StatusText,
                            "Hãy mở profile, hoàn tất bước đăng ký khóa học và đóng profile, sau đó bấm Tiếp tục.",
                            "COURSE_ENROLLMENT_CONFIRMATION_REQUIRED");
                        return;

                    case "NOT_ENROLLED":
                        await PauseCourseJobAsync(
                            "⏸️ Khóa học yêu cầu đăng ký hoặc quyền truy cập thủ công.",
                            "Hãy mở profile và đăng ký đúng khóa. Worker sẽ không tự chọn Trial, gói trả phí hoặc thanh toán.",
                            "COURSE_ENROLLMENT_REQUIRED");
                        return;

                    case "PAID_OR_TRIAL":
                        await PauseCourseJobAsync(
                            "⏸️ Tài khoản chưa có quyền vào khóa; Coursera đang yêu cầu Plus/Trial/gói trả phí.",
                            "Tài khoản cần Coursera Plus hoặc quyền truy cập phù hợp. Hãy nâng cấp trong profile rồi bấm Tiếp tục.",
                            "COURSERA_PLUS_REQUIRED");
                        return;

                    case "ENROLL_AMBIGUOUS":
                        await PauseCourseJobAsync(
                            "⏸️ Có nhiều lựa chọn đăng ký; Worker đã tạm dừng để tránh chọn nhầm.",
                            "Hãy mở profile, chọn đúng phương án đăng ký rồi bấm Tiếp tục.",
                            "COURSE_ENROLLMENT_AMBIGUOUS");
                        return;

                    case "AMBIGUOUS":
                        await PauseCourseJobAsync(
                            "⏸️ Có nhiều nút vào khóa học; Worker đã tạm dừng để tránh bấm nhầm.",
                            "Hãy mở profile, vào đúng nội dung khóa học rồi bấm Tiếp tục.",
                            "COURSE_ENTRY_AMBIGUOUS");
                        return;
                }

                if (attempt < 12)
                {
                    await Task.Delay(500);
                }
            }

            if (!IsSameCourseraDocument(landingUri))
            {
                return;
            }

            if (_courseLandingFallbackCount >= 1)
            {
                await PauseCourseJobAsync(
                    "⏸️ Coursera không hiển thị lối vào nội dung sau các lần thử giới hạn.",
                    "Hãy mở profile, kiểm tra đăng ký/Coursera Plus và vào khóa thủ công rồi bấm Tiếp tục.",
                    "COURSE_ENTRY_NOT_FOUND");
                return;
            }

            // Try the canonical course home once. If Coursera routes the account
            // back to the same gate, the bounded counter above pauses instead of
            // creating a landing -> home -> landing loop.
            _courseLandingFallbackCount++;
            MainWebView.Source = courseHomeUri;
            _viewModel.StatusText =
                "⏭️ Không thấy nút vào khóa học sau khi chờ tải giao diện; đang mở khu vực học của khóa này...";
        }
        catch (Exception ex)
        {
            await PauseCourseJobAsync(
                "⏸️ Không thể xác minh lối vào khóa học; Worker đã tạm dừng.",
                "Hãy mở profile, kiểm tra quyền truy cập khóa học rồi bấm Tiếp tục. Chi tiết: " + ex.Message,
                "COURSE_ENTRY_CHECK_FAILED");
        }
        finally
        {
            _isHandlingCourseLanding = false;
        }
    }

    private static bool TryResolveTrustedCourseEntryUri(
        Uri landingUri,
        string? rawHref,
        out Uri? entryUri)
    {
        entryUri = null;
        if (string.IsNullOrWhiteSpace(rawHref) ||
            !Uri.TryCreate(landingUri, rawHref, out Uri? candidate) ||
            !IsCourseraUri(candidate) ||
            !TryGetCourseSlug(landingUri, out string landingCourseSlug) ||
            !TryGetCourseSlug(candidate, out string candidateCourseSlug) ||
            !string.Equals(landingCourseSlug, candidateCourseSlug, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        entryUri = candidate;
        return true;
    }

    private async Task CheckModulesAsync()
    {
        // Course Home raises NavigationCompleted again every time the scanner
        // changes Module. Keep one sweep in charge until it either opens a
        // lesson or reaches a terminal completion state.
        if (_isScanningCourseOutline)
        {
            return;
        }

        _isScanningCourseOutline = true;
        try
        {
            _viewModel.StatusText = "🔍 Đang thanh tra tiến độ các Module...";
            await Task.Delay(5000); // Chờ sidebar của React render xong

            string automationCompletedModulesJson = JsonSerializer.Serialize(
                _automationCompletedModuleNumbers.ToArray());
            string jsCode = @"
                (function() {
                    var automationCompletedModules = new Set(__AUTOMATION_COMPLETED_MODULES__);
                    var normalizePath = function(value) {
                        var path = String(value || '').replace(/\/+$/, '');
                        return path || '/';
                    };
                    // Hỗ trợ cả DOM cũ (data-testid nằm trên <a>) và DOM Coursera hiện tại
                    // (data-testid nằm ở phần tử cha, còn <a> mang aria-label/aria-current).
                    var modules = Array.from(document.querySelectorAll(
                        'a[data-testid=""rc-WeekNavigationItem""], ' +
                        '[data-testid=""rc-WeekNavigationItem""] a, ' +
                        'a[aria-label^=""Module ""]'
                    )).filter(function(module, index, allModules) {
                        return allModules.indexOf(module) === index;
                    });

                    // Không được coi danh sách rỗng là đã hoàn thành toàn bộ.
                    if (modules.length === 0) {
                        var normalize = function(value) {
                            return String(value || '').replace(/\u00a0/g, ' ')
                                .replace(/\s+/g, ' ').trim().toLowerCase();
                        };
                        var isVisible = function(element) {
                            if (!(element instanceof HTMLElement) ||
                                element.matches('[disabled], [aria-disabled=""true""]')) return false;
                            var style = window.getComputedStyle(element);
                            var rect = element.getBoundingClientRect();
                            return style.display !== 'none' && style.visibility !== 'hidden' &&
                                Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
                        };
                        var controls = Array.from(document.querySelectorAll('a, button, [role=""button""]'))
                            .filter(isVisible);
                        var labels = controls.map(function(control) {
                            return normalize(control.getAttribute('aria-label') ||
                                control.getAttribute('title') || control.innerText || control.textContent);
                        });
                        var pageText = normalize(document.body ? document.body.innerText : '');
                        var hasPaidControl = labels.some(function(label) {
                            return /coursera\s+plus|start\s+(?:a\s+)?free\s+trial|subscribe|subscription|upgrade|view\s+plans?|choose\s+(?:a\s+)?plans?|purchase|buy\s+now|nâng\s+cấp|dùng\s+thử|xem\s+gói|thanh\s+toán/.test(label);
                        });
                        var hasPlusGateText = /coursera\s+plus/.test(pageText) &&
                            /required|access|upgrade|subscribe|subscription|trial|plan|purchase|cần|yêu\s+cầu|nâng\s+cấp|gói|quyền\s+truy\s+cập/.test(pageText);
                        if (hasPaidControl || hasPlusGateText) {
                            return 'ACCESS_REQUIRES_SUBSCRIPTION';
                        }
                        if (labels.some(function(label) {
                            return /^(?:enroll|enroll\s+for\s+free|join\s+for\s+free|[đd]ăng\s+ký)(?:\b|$)/.test(label);
                        })) {
                            return 'ACCESS_REQUIRES_ENROLLMENT';
                        }
                        return 'MODULES_NOT_FOUND';
                    }

                    var currentPath = normalizePath(window.location.pathname);
                    var moduleNumber = function(module, index) {
                        var label = module.getAttribute('aria-label') || module.innerText || '';
                        var labelMatch = label.match(/^\s*Module\s+(\d+)\b/i);
                        if (labelMatch) return Number(labelMatch[1]);
                        try {
                            var pathMatch = new URL(
                                module.getAttribute('href') || '',
                                window.location.href).pathname.match(/\/home\/(?:week|module)\/(\d+)/i);
                            if (pathMatch) return Number(pathMatch[1]);
                        } catch (_) { }
                        return index + 1;
                    };
                    var isSelectedModule = function(module) {
                        var ariaLabel = module.getAttribute('aria-label') || '';
                        var selectedByPath = false;
                        try {
                            selectedByPath = normalizePath(new URL(
                                module.getAttribute('href') || '',
                                window.location.href).pathname) === currentPath;
                        } catch (_) { }
                        return module.getAttribute('aria-current') === 'page'
                            || module.getAttribute('aria-selected') === 'true'
                            || module.getAttribute('aria-expanded') === 'true'
                            || /\bselected\b/i.test(ariaLabel)
                            || selectedByPath;
                    };

                    for (var i = 0; i < modules.length; i++) {
                        var ariaLabel = modules[i].getAttribute('aria-label') || '';
                        var moduleName = (modules[i].innerText || '').trim() || ('Module ' + (i + 1));
                        var number = moduleNumber(modules[i], i);
                        var isCompleted = /\bComplete(?:d)?\b/i.test(ariaLabel)
                            || automationCompletedModules.has(number);
                        var isSelected = isSelectedModule(modules[i]);

                        if (!isCompleted) {
                            if (!isSelected) {
                                modules[i].click();
                                return 'MODULE_CLICKED|' + number + '|' + moduleName;
                            }

                            return 'MODULE_SELECTED|' + number + '|' + moduleName;
                        }
                    }
                    return '🏆 Tuyệt vời! Đã hoàn thành toàn bộ các Module!';
                })();
            ".Replace(
                "__AUTOMATION_COMPLETED_MODULES__",
                automationCompletedModulesJson);

            for (int moduleClickAttempt = 1; moduleClickAttempt <= 3; moduleClickAttempt++)
            {
                Uri? beforeModuleClickUri = MainWebView.Source;
                string result = await MainWebView.ExecuteScriptAsync(jsCode);
                if (result == null)
                {
                    continue;
                }

                string status = DecodeWebViewString(result);
                if (string.Equals(status, "ACCESS_REQUIRES_SUBSCRIPTION", StringComparison.Ordinal))
                {
                    await PauseCourseJobAsync(
                        "⏸️ Tài khoản chưa có quyền vào nội dung; Coursera đang yêu cầu Plus/Trial/gói trả phí.",
                        "Tài khoản cần Coursera Plus hoặc quyền truy cập phù hợp. Hãy nâng cấp trong profile rồi bấm Tiếp tục.",
                        "COURSERA_PLUS_REQUIRED");
                    return;
                }

                if (string.Equals(status, "ACCESS_REQUIRES_ENROLLMENT", StringComparison.Ordinal))
                {
                    await PauseCourseJobAsync(
                        "⏸️ Khóa học chưa được đăng ký hoặc cần xác nhận quyền truy cập.",
                        "Hãy mở profile, hoàn tất đăng ký đúng khóa rồi bấm Tiếp tục.",
                        "COURSE_ENROLLMENT_REQUIRED");
                    return;
                }

                if (string.Equals(status, "MODULES_NOT_FOUND", StringComparison.Ordinal))
                {
                    if (moduleClickAttempt < 3)
                    {
                        _viewModel.StatusText =
                            $"⏳ Chưa thấy danh sách Module; đang chờ Coursera render lại ({moduleClickAttempt}/3)...";
                        await Task.Delay(1500);
                        continue;
                    }

                    await PauseCourseJobAsync(
                        "⏸️ Không tìm thấy nội dung khóa học sau 3 lần kiểm tra; Worker đã tạm dừng.",
                        "Hãy mở profile, kiểm tra đăng ký/Coursera Plus và vào đúng trang nội dung rồi bấm Tiếp tục.",
                        "COURSE_CONTENT_UNAVAILABLE");
                    return;
                }

                if (status.StartsWith("MODULE_CLICKED|", StringComparison.Ordinal))
                {
                    string[] parts = status.Split('|', 3);
                    string moduleName = parts.Length == 3 ? parts[2] : "Module chưa hoàn thành";
                    _viewModel.StatusText =
                        $"➡️ Đang chuyển sang {moduleName} ({moduleClickAttempt}/3)...";
                    if (beforeModuleClickUri != null &&
                        await WaitForAppItemNavigationAsync(
                            beforeModuleClickUri,
                            TimeSpan.FromSeconds(5)))
                    {
                        await Task.Delay(2000);
                        await CheckLessonsAsync();
                        return;
                    }

                    if (moduleClickAttempt < 3)
                    {
                        _viewModel.StatusText =
                            $"⚠️ Coursera chưa chuyển sang {moduleName}; đang thử lại ({moduleClickAttempt}/3)...";
                        await Task.Delay(1000);
                        continue;
                    }

                    await FailCourseJobAsync(
                        $"❌ Không thể chuyển sang {moduleName} sau 3 lần; Worker đã dừng để tránh lặp vô hạn.");
                    return;
                }

                if (status.StartsWith("MODULE_SELECTED|", StringComparison.Ordinal))
                {
                    string[] parts = status.Split('|', 3);
                    string moduleName = parts.Length == 3 ? parts[2] : "Module hiện tại";
                    status = "👉 Đang học đúng tiến độ tại: " + moduleName;
                }
                _viewModel.StatusText = status;

                if (status.Contains("🏆", StringComparison.Ordinal))
                {
                    await CompleteCourseJobAsync();
                    return;
                }
                
                if (!status.Contains("🏆", StringComparison.Ordinal))
                {
                    await Task.Delay(3000); // Đợi React render danh sách bài học
                    await CheckLessonsAsync();
                }
                return;
            }

            await FailCourseJobAsync(
                "❌ Không đọc được trạng thái Module sau 3 lần; Worker đã dừng để tránh lặp vô hạn.");
        }
        catch (Exception ex)
        {
            await PauseCourseJobAsync(
                "⏸️ Không đọc được danh sách Module; Worker đã tạm dừng.",
                "Hãy mở profile, kiểm tra nội dung khóa học rồi bấm Tiếp tục. Chi tiết: " + ex.Message,
                "COURSE_OUTLINE_CHECK_FAILED");
        }
        finally
        {
            _isScanningCourseOutline = false;
        }
    }

    private async Task CheckLessonsAsync(
        int moduleNavigationAttempt = 0,
        int lessonHydrationAttempt = 0)
    {
        _viewModel.StatusText = "🔎 Đang dọn dẹp màn hình và quét bài chưa học...";
        await Task.Delay(2000);

        // Đóng các popup đã nhận diện trước khi quét danh sách bài.
        await DismissAnyGlobalPopupsAsync();
        await Task.Delay(1000);

        // Snapshot before embedding in the page script.  A pending assessment
        // remains visibly incomplete in Coursera's sidebar, so it must be
        // excluded explicitly rather than treated as a fresh quiz.
        string pendingResultPathsJson = JsonSerializer.Serialize(
            _pendingGradedResultPaths.ToArray());
        string submittedDiscussionPathsJson = JsonSerializer.Serialize(
            _submittedDiscussionPaths.ToArray());
        string skippedLaunchAppItemPathsJson = JsonSerializer.Serialize(
            _skippedLaunchAppItemPaths.ToArray());
        string automationCompletedModulesJson = JsonSerializer.Serialize(
            _automationCompletedModuleNumbers.ToArray());

        string jsCode = @"
            (function() {
                var skipGradedAppItems = __SKIP_GRADED__;
                var skipPracticeAppItems = __SKIP_PRACTICE__;
                var skipPeerItems = __SKIP_PEER__;
                var pendingResultPaths = new Set(__PENDING_RESULT_PATHS__);
                var submittedDiscussionPaths = new Set(__SUBMITTED_DISCUSSION_PATHS__);
                var skippedLaunchAppItemPaths = new Set(__SKIPPED_LAUNCH_APP_PATHS__);
                var automationCompletedModules = new Set(__AUTOMATION_COMPLETED_MODULES__);
                var normalizePath = function(value) {
                    var path = String(value || '').replace(/\/+$/, '');
                    return path || '/';
                };
                // Hỗ trợ cả link bài học DOM cũ và cấu trúc WeekSingleItemDisplay hiện tại.
                var lessons = Array.from(document.querySelectorAll(
                    'a[data-click-key=""open_course_home.period_page.click.item_link""], ' +
                    'li[data-testid^=""WeekSingleItemDisplay""] > a[href]'
                ));

                // Danh sách rỗng là lỗi nhận diện, không phải đã hoàn thành.
                if (lessons.length === 0) {
                    var normalize = function(value) {
                        return String(value || '').replace(/\u00a0/g, ' ')
                            .replace(/\s+/g, ' ').trim().toLowerCase();
                    };
                    var isVisible = function(element) {
                        if (!(element instanceof HTMLElement) ||
                            element.matches('[disabled], [aria-disabled=""true""]')) return false;
                        var style = window.getComputedStyle(element);
                        var rect = element.getBoundingClientRect();
                        return style.display !== 'none' && style.visibility !== 'hidden' &&
                            Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
                    };
                    var labels = Array.from(document.querySelectorAll('a, button, [role=""button""]'))
                        .filter(isVisible)
                        .map(function(control) {
                            return normalize(control.getAttribute('aria-label') ||
                                control.getAttribute('title') || control.innerText || control.textContent);
                        });
                    var pageText = normalize(document.body ? document.body.innerText : '');
                    var hasPaidControl = labels.some(function(label) {
                        return /coursera\s+plus|start\s+(?:a\s+)?free\s+trial|subscribe|subscription|upgrade|view\s+plans?|choose\s+(?:a\s+)?plans?|purchase|buy\s+now|nâng\s+cấp|dùng\s+thử|xem\s+gói|thanh\s+toán/.test(label);
                    });
                    var hasPlusGateText = /coursera\s+plus/.test(pageText) &&
                        /required|access|upgrade|subscribe|subscription|trial|plan|purchase|cần|yêu\s+cầu|nâng\s+cấp|gói|quyền\s+truy\s+cập/.test(pageText);
                    if (hasPaidControl || hasPlusGateText) {
                        return 'ACCESS_REQUIRES_SUBSCRIPTION';
                    }
                    if (labels.some(function(label) {
                        return /^(?:enroll|enroll\s+for\s+free|join\s+for\s+free|[đd]ăng\s+ký)(?:\b|$)/.test(label);
                    })) {
                        return 'ACCESS_REQUIRES_ENROLLMENT';
                    }
                    return 'LESSONS_NOT_FOUND';
                }
                
                var skippedAppItems = 0;
                var skippedPeerItems = 0;
                var skippedPendingResults = 0;
                var skippedSubmittedDiscussions = 0;
                for (var i = 0; i < lessons.length; i++) {
                    var htmlContent = lessons[i].innerHTML;
                    var ariaLabel = lessons[i].getAttribute('aria-label') || '';
                    var href = lessons[i].getAttribute('href') || '';
                    var itemText = (ariaLabel + ' ' + (lessons[i].innerText || ''))
                        .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();

                    // Coursera keeps an accepted-but-ungraded assessment marked
                    // incomplete.  Compare canonical paths so query parameters
                    // or tracking fragments cannot make the scanner reopen it.
                    var lessonPath = '';
                    try {
                        lessonPath = normalizePath(
                            new URL(href, window.location.href).pathname);
                    } catch (_) { }
                    if (lessonPath && skippedLaunchAppItemPaths.has(lessonPath)) {
                        skippedAppItems++;
                        continue;
                    }
                    if (lessonPath && pendingResultPaths.has(lessonPath)) {
                        skippedPendingResults++;
                        continue;
                    }
                    if (lessonPath && submittedDiscussionPaths.has(lessonPath)) {
                        skippedSubmittedDiscussions++;
                        continue;
                    }

                    // Graded/Practice App Item đang được cấu hình bỏ qua: không mở,
                    // không Launch App và tiếp tục tìm bài có thể tự động hóa tiếp theo.
                    var isGradedAppItem = /\bGraded\s+App\s+Item\b/i.test(itemText);
                    var isPracticeAppItem = /\b(?:Practice|Ungraded)\s+App\s+Item\b/i.test(itemText);
                    if ((skipGradedAppItems && isGradedAppItem) ||
                        (skipPracticeAppItems && isPracticeAppItem)) {
                        skippedAppItems++;
                        continue;
                    }

                    // Peer-graded assignments and peer reviews stay pending for
                    // the learner. Do not open them; keep scanning for another
                    // supported lesson in the current module.
                    var isPeerItem = /\/peer(?:\/|$)/i.test(href) ||
                        /\bpeer[-\s]?(?:graded|review(?:ed)?)\b|\bpeer\s+(?:assessment|assignment)\b/i.test(itemText);
                    if (skipPeerItems && isPeerItem) {
                        skippedPeerItems++;
                        continue;
                    }

                    // DOM mới dùng icon dấu tick trực tiếp ở vùng icon đầu tiên; icon loại bài
                    // chưa hoàn thành được bọc sâu hơn trong TooltipWrapper.
                    var hasModernCompletedIcon = !!lessons[i].querySelector(':scope > span:first-child > svg');
                    var isCompleted = htmlContent.includes('>Completed<')
                        || /\bCompleted\b/i.test(ariaLabel)
                        || hasModernCompletedIcon;
                    
                    // Nếu bài này chưa hoàn thành (kể cả nó có nút Resume hay không)
                    if (!isCompleted) {
                        var nameTag = lessons[i].querySelector('[data-test=""rc-ItemName""]');
                        var lessonName = nameTag
                            ? nameTag.innerText.trim()
                            : (ariaLabel || 'bài mới');
                        
                        lessons[i].click();
                        var skippedPrefix = skippedPendingResults > 0 && skippedSubmittedDiscussions > 0
                            ? '⏭️ Đã bỏ qua bài chờ chấm điểm và thảo luận đã gửi. Đang tiến vào học bài: '
                            : skippedPendingResults > 0
                            ? '⏭️ Đã bỏ qua bài đang chờ chấm điểm. Đang tiến vào học bài: '
                            : skippedSubmittedDiscussions > 0
                                ? '⏭️ Đã bỏ qua bài thảo luận đã gửi. Đang tiến vào học bài: '
                                : skippedPeerItems > 0 && skippedAppItems > 0
                                ? '⏭️ Đã bỏ qua Peer/Review và App Item. Đang tiến vào học bài: '
                                : skippedPeerItems > 0
                                    ? '⏭️ Đã bỏ qua Peer/Review. Đang tiến vào học bài: '
                                    : skippedAppItems > 0
                                        ? '⏭️ Đã bỏ qua App Item. Đang tiến vào học bài: '
                                        : '👉 Đang tiến vào học bài: ';
                        return skippedPrefix + lessonName;
                    }
                }
                
                // Module hiện tại đã xong: chuyển tuần tự sang Module kế tiếp thay vì dừng sớm.
                var modules = Array.from(document.querySelectorAll(
                    'a[data-testid=""rc-WeekNavigationItem""], ' +
                    '[data-testid=""rc-WeekNavigationItem""] a, ' +
                    'a[aria-label^=""Module ""], ' +
                    '[data-testid=""rc-WeekNavigationItem""] button, ' +
                    'button[aria-expanded][aria-controls]'
                )).filter(function(module, index, allModules) {
                    var moduleText = (module.getAttribute('aria-label') || module.innerText || '').trim();
                    return /^Module\s+\d+\b/i.test(moduleText)
                        && allModules.indexOf(module) === index;
                });
                var currentPath = normalizePath(window.location.pathname);
                var moduleNumber = function(module, index) {
                    var label = module.getAttribute('aria-label') || module.innerText || '';
                    var labelMatch = label.match(/^\s*Module\s+(\d+)\b/i);
                    if (labelMatch) return Number(labelMatch[1]);
                    try {
                        var pathMatch = new URL(
                            module.getAttribute('href') || '',
                            window.location.href).pathname.match(/\/home\/(?:week|module)\/(\d+)/i);
                        if (pathMatch) return Number(pathMatch[1]);
                    } catch (_) { }
                    return index + 1;
                };
                var selectedIndex = modules.findIndex(function(module) {
                    var moduleLabel = module.getAttribute('aria-label') || '';
                    var selectedByPath = false;
                    try {
                        selectedByPath = normalizePath(new URL(
                            module.getAttribute('href') || '',
                            window.location.href).pathname) === currentPath;
                    } catch (_) { }
                    return module.getAttribute('aria-current') === 'page'
                        || module.getAttribute('aria-selected') === 'true'
                        || module.getAttribute('aria-expanded') === 'true'
                        || /\bselected\b/i.test(moduleLabel)
                        || selectedByPath;
                });
                if (selectedIndex < 0) {
                    var pathMatch = currentPath.match(/\/home\/(?:week|module)\/(\d+)/i);
                    if (pathMatch) {
                        var currentNumberFromPath = Number(pathMatch[1]);
                        selectedIndex = modules.findIndex(function(module, index) {
                            return moduleNumber(module, index) === currentNumberFromPath;
                        });
                    }
                }
                if (selectedIndex < 0 || modules.length === 0) {
                    return '⚠️ Module hiện tại đã hoàn thành nhưng không xác định được Module tiếp theo.';
                }

                var currentModuleNumber = moduleNumber(modules[selectedIndex], selectedIndex);
                automationCompletedModules.add(currentModuleNumber);
                var moduleIsEffectivelyCompleted = function(module, index) {
                    var moduleLabel = (module.getAttribute('aria-label') || module.innerText || '').trim();
                    return /\bComplete(?:d)?\b/i.test(moduleLabel)
                        || automationCompletedModules.has(moduleNumber(module, index));
                };
                var nextIndex = -1;
                for (var candidateIndex = selectedIndex + 1;
                    candidateIndex < modules.length;
                    candidateIndex++) {
                    if (!moduleIsEffectivelyCompleted(modules[candidateIndex], candidateIndex)) {
                        nextIndex = candidateIndex;
                        break;
                    }
                }
                if (nextIndex < 0) {
                    for (var earlierIndex = 0; earlierIndex < selectedIndex; earlierIndex++) {
                        if (!moduleIsEffectivelyCompleted(modules[earlierIndex], earlierIndex)) {
                            nextIndex = earlierIndex;
                            break;
                        }
                    }
                }

                if (nextIndex >= 0) {
                    var nextModule = modules[nextIndex];
                    var nextName = (nextModule.innerText || '').trim() || ('Module ' + (nextIndex + 1));
                    nextModule.click();
                    var nextModulePrefix = skippedPeerItems > 0 && skippedAppItems > 0
                        ? 'SKIPPED_PEER_APP_NEXT_MODULE|'
                        : skippedPeerItems > 0
                            ? 'SKIPPED_PEER_NEXT_MODULE|'
                            : skippedAppItems > 0
                                ? 'SKIPPED_APP_NEXT_MODULE|'
                                : 'NEXT_MODULE|';
                    return 'MODULE_SCANNED|' + currentModuleNumber + '|' +
                        nextModulePrefix + nextName;
                }

                var completionStatus;
                if (skippedSubmittedDiscussions > 0) {
                    var remainingItems = ['phản hồi Discussion đã gửi'];
                    if (skippedPendingResults > 0) remainingItems.push('bài đang chờ chấm điểm');
                    if (skippedPeerItems > 0) remainingItems.push('Peer/Review đã bỏ qua');
                    if (skippedAppItems > 0) remainingItems.push('App Item đã bỏ qua');
                    completionStatus = '🏆 Đã hoàn tất các bài tự động; còn ' + remainingItems.join(', ') + '.';
                } else if (skippedPendingResults > 0 && skippedPeerItems > 0 && skippedAppItems > 0) {
                    completionStatus = '🏆 Đã hoàn tất các bài tự động; còn bài chờ chấm điểm, Peer/Review và App Item đã bỏ qua.';
                } else if (skippedPendingResults > 0 && skippedPeerItems > 0) {
                    completionStatus = '🏆 Đã hoàn tất các bài tự động; còn bài chờ chấm điểm và Peer/Review đã bỏ qua.';
                } else if (skippedPendingResults > 0 && skippedAppItems > 0) {
                    completionStatus = '🏆 Đã hoàn tất các bài tự động; còn bài chờ chấm điểm và App Item đã bỏ qua.';
                } else if (skippedPendingResults > 0) {
                    completionStatus = '🏆 Đã hoàn tất các bài tự động; còn bài đã nộp đang chờ chấm điểm.';
                } else if (skippedPeerItems > 0 && skippedAppItems > 0) {
                    completionStatus = '🏆 Đã hoàn tất các bài tự động; còn Peer/Review và App Item đã bỏ qua.';
                } else if (skippedPeerItems > 0) {
                    completionStatus = '🏆 Đã hoàn tất các bài tự động; còn Peer/Review đã bỏ qua.';
                } else {
                    completionStatus = skippedAppItems > 0
                        ? '🏆 Đã hoàn tất các bài có thể tự động và bỏ qua App Item.'
                        : '🏆 Tuyệt vời! Bạn đã hoàn thành toàn bộ các Module!';
                }
                return 'MODULE_SCANNED|' + currentModuleNumber + '|' + completionStatus;
            })();
        ";
        jsCode = jsCode
            .Replace("__SKIP_GRADED__", ShouldSkipGradedAppItems ? "true" : "false")
            .Replace("__SKIP_PRACTICE__", ShouldSkipPracticeAppItems ? "true" : "false")
            .Replace("__SKIP_PEER__", ShouldSkipPeerItems ? "true" : "false")
            .Replace("__PENDING_RESULT_PATHS__", pendingResultPathsJson)
            .Replace("__SUBMITTED_DISCUSSION_PATHS__", submittedDiscussionPathsJson)
            .Replace("__SKIPPED_LAUNCH_APP_PATHS__", skippedLaunchAppItemPathsJson)
            .Replace("__AUTOMATION_COMPLETED_MODULES__", automationCompletedModulesJson);

        try
        {
            Uri? beforeModuleNavigationUri = MainWebView.Source;
            string result = await MainWebView.ExecuteScriptAsync(jsCode);
            if (result != null)
            {
                string status = DecodeWebViewString(result);
                if (string.Equals(status, "ACCESS_REQUIRES_SUBSCRIPTION", StringComparison.Ordinal))
                {
                    await PauseCourseJobAsync(
                        "⏸️ Tài khoản chưa có quyền mở bài học; Coursera đang yêu cầu Plus/Trial/gói trả phí.",
                        "Tài khoản cần Coursera Plus hoặc quyền truy cập phù hợp. Hãy nâng cấp trong profile rồi bấm Tiếp tục.",
                        "COURSERA_PLUS_REQUIRED");
                    return;
                }

                if (string.Equals(status, "ACCESS_REQUIRES_ENROLLMENT", StringComparison.Ordinal))
                {
                    await PauseCourseJobAsync(
                        "⏸️ Khóa học chưa được đăng ký hoặc cần xác nhận quyền truy cập.",
                        "Hãy mở profile, hoàn tất đăng ký đúng khóa rồi bấm Tiếp tục.",
                        "COURSE_ENROLLMENT_REQUIRED");
                    return;
                }

                if (string.Equals(status, "LESSONS_NOT_FOUND", StringComparison.Ordinal))
                {
                    if (lessonHydrationAttempt < 2)
                    {
                        _viewModel.StatusText =
                            $"⏳ Chưa thấy danh sách bài; đang chờ Coursera render lại ({lessonHydrationAttempt + 1}/3)...";
                        await Task.Delay(1500);
                        await CheckLessonsAsync(moduleNavigationAttempt, lessonHydrationAttempt + 1);
                        return;
                    }

                    await PauseCourseJobAsync(
                        "⏸️ Không tìm thấy danh sách bài học sau 3 lần kiểm tra; Worker đã tạm dừng.",
                        "Hãy mở profile, kiểm tra quyền truy cập và trang Module hiện tại rồi bấm Tiếp tục.",
                        "COURSE_LESSONS_UNAVAILABLE");
                    return;
                }

                const string moduleScannedPrefix = "MODULE_SCANNED|";
                if (status.StartsWith(moduleScannedPrefix, StringComparison.Ordinal))
                {
                    int moduleNumberEnd = status.IndexOf(
                        '|',
                        moduleScannedPrefix.Length);
                    if (moduleNumberEnd > moduleScannedPrefix.Length &&
                        int.TryParse(
                            status[moduleScannedPrefix.Length..moduleNumberEnd],
                            out int completedModuleNumber))
                    {
                        _automationCompletedModuleNumbers.Add(completedModuleNumber);
                        status = status[(moduleNumberEnd + 1)..];
                    }
                }
                if (status.Contains("Đã bỏ qua App Item", StringComparison.Ordinal) ||
                    status.Contains("bỏ qua App Item", StringComparison.Ordinal) ||
                    status.StartsWith("SKIPPED_PEER_APP_NEXT_MODULE|", StringComparison.Ordinal))
                {
                    _courseHasSkippedLaunchAppItems = true;
                }

                bool skippedPeerItem = status.Contains("Peer/Review", StringComparison.Ordinal) ||
                    status.StartsWith("SKIPPED_PEER_NEXT_MODULE|", StringComparison.Ordinal) ||
                    status.StartsWith("SKIPPED_PEER_APP_NEXT_MODULE|", StringComparison.Ordinal);
                if (skippedPeerItem)
                {
                    _courseHasSkippedPeerItems = true;
                }

                string? prefix = status.StartsWith("SKIPPED_PEER_APP_NEXT_MODULE|", StringComparison.Ordinal)
                    ? "SKIPPED_PEER_APP_NEXT_MODULE|"
                    : status.StartsWith("SKIPPED_PEER_NEXT_MODULE|", StringComparison.Ordinal)
                        ? "SKIPPED_PEER_NEXT_MODULE|"
                        : status.StartsWith("SKIPPED_APP_NEXT_MODULE|", StringComparison.Ordinal)
                            ? "SKIPPED_APP_NEXT_MODULE|"
                            : status.StartsWith("NEXT_MODULE|", StringComparison.Ordinal)
                                ? "NEXT_MODULE|"
                                : null;
                if (prefix != null)
                {
                    string nextModule = status[prefix.Length..];
                    _viewModel.StatusText = prefix == "SKIPPED_PEER_APP_NEXT_MODULE|"
                        ? "⏭️ Đã bỏ qua Peer/Review và App Item. Đang chuyển sang: " + nextModule
                        : prefix == "SKIPPED_PEER_NEXT_MODULE|"
                            ? "⏭️ Đã bỏ qua Peer/Review. Đang chuyển sang: " + nextModule
                            : prefix == "SKIPPED_APP_NEXT_MODULE|"
                                ? "⏭️ Đã bỏ qua App Item. Đang chuyển sang: " + nextModule
                                : "➡️ Module hiện tại đã xong. Đang chuyển sang: " + nextModule;
                    if (beforeModuleNavigationUri != null &&
                        await WaitForAppItemNavigationAsync(
                            beforeModuleNavigationUri,
                            TimeSpan.FromSeconds(5)))
                    {
                        await Task.Delay(2000);
                        await CheckLessonsAsync();
                        return;
                    }

                    if (moduleNavigationAttempt < 2)
                    {
                        _viewModel.StatusText =
                            $"⚠️ Coursera chưa chuyển sang {nextModule}; đang thử lại ({moduleNavigationAttempt + 1}/3)...";
                        await Task.Delay(1000);
                        await CheckLessonsAsync(moduleNavigationAttempt + 1, 0);
                        return;
                    }

                    await FailCourseJobAsync(
                        $"❌ Không thể chuyển sang {nextModule} sau 3 lần; Worker đã dừng để tránh lặp vô hạn.");
                    return;
                }
                if (status.Contains("🏆", StringComparison.Ordinal))
                {
                    _viewModel.StatusText = status;
                    await CompleteCourseJobAsync();
                    return;
                }
                _viewModel.StatusText = status;
            }
        }
        catch (Exception ex)
        {
            await PauseCourseJobAsync(
                "⏸️ Không đọc được danh sách bài học; Worker đã tạm dừng.",
                "Hãy mở profile, kiểm tra nội dung khóa học rồi bấm Tiếp tục. Chi tiết: " + ex.Message,
                "COURSE_LESSON_CHECK_FAILED");
        }
    }

    private const string JsHandleKnownCourseraPopup = """
        (function() {
            const expectedUrl = __EXPECTED_URL_JSON__;
            const currentUrl = new URL(location.href);
            const targetUrl = new URL(expectedUrl);
            if (currentUrl.origin !== targetUrl.origin ||
                currentUrl.pathname !== targetUrl.pathname ||
                currentUrl.search !== targetUrl.search) {
                return 'STALE_DOCUMENT';
            }
            const normalize = value => String(value || '')
                .replace(/\u00a0/g, ' ')
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();
            const isVisible = element => !!element &&
                element.getClientRects().length > 0 &&
                !element.closest('[aria-hidden="true"]') &&
                !element.closest('[inert]');
            const usableButtons = container => Array.from(container.querySelectorAll('button'))
                .filter(button => isVisible(button) && !button.disabled &&
                    normalize(button.getAttribute('aria-disabled')) !== 'true');
            const findExactButton = (container, labels) => {
                const expected = labels.map(normalize);
                const matches = usableButtons(container).filter(button =>
                    expected.includes(normalize(button.innerText || button.textContent)));
                return matches.length === 1 ? matches[0] : null;
            };
            const click = (button, result) => {
                if (!button) return null;
                button.click();
                return result;
            };

            const candidates = Array.from(document.querySelectorAll(
                '[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open]'))
                .filter(isVisible);
            // Portal có thể bọc dialog nhiều lớp; chỉ xử lý lớp trong cùng đang hiển thị.
            const dialogs = candidates.filter(candidate =>
                !candidates.some(other => other !== candidate && candidate.contains(other)));

            if (dialogs.length > 1) return 'AMBIGUOUS_MULTIPLE_DIALOGS';
            if (dialogs.length === 1) {
                const dialog = dialogs[0];
                const dialogText = normalize(dialog.innerText || dialog.textContent);

                // Luồng nhạy cảm luôn thắng mọi fingerprint thông tin phía dưới.
                const protectedWorkflow = /(ready to submit|confirm submission|submit your|start (new )?attempt|end the dialogue|sign in|log in|create (an )?account|continue with (google|apple|facebook|email)|email address|username|password|verification code|one[- ]time code|two-factor|payment|checkout|purchase|subscription|delete|remove account|camera|microphone|location permission|notification permission|allow access|upload file)/i;
                const hasSensitiveControl = !!dialog.querySelector(
                    'input[type="password"],input[type="file"],input[type="email"],input[autocomplete="username" i],input[autocomplete^="cc-" i],iframe[src*="stripe" i],[data-stripe]');
                if (hasSensitiveControl || protectedWorkflow.test(dialogText)) {
                    return 'PROTECTED_WORKFLOW_DIALOG';
                }

                // Popup này là bước thông tin bắt buộc trước khi tiếp tục bài Coursera.
                if (dialogText.includes('coursera honor code')) {
                    const checkboxes = Array.from(
                        dialog.querySelectorAll('input[type="checkbox"]'))
                        .filter(box => isVisible(box) && !box.disabled &&
                            normalize(box.getAttribute('aria-disabled')) !== 'true');
                    if (checkboxes.length > 1) return 'AMBIGUOUS_HONOR_CHECKBOX';
                    if (checkboxes.length === 1 && !checkboxes[0].checked) {
                        const box = checkboxes[0];
                        const labels = Array.from(box.labels || [])
                            .map(label => label.innerText || label.textContent)
                            .join(' ');
                        const boxMetadata = normalize([
                            box.name, box.id, box.getAttribute('aria-label'), labels,
                            box.closest('label,[role="checkbox"],[class*="checkbox" i]')?.innerText
                        ].filter(Boolean).join(' '));
                        if (!/(honou?r|agree|acknowledge)/i.test(boxMetadata)) {
                            return 'AMBIGUOUS_HONOR_CHECKBOX';
                        }
                        box.click();
                        return 'DISMISSED_HONOR_CHECKBOX';
                    }

                    const continueButton = findExactButton(
                        dialog, ['continue', 'i agree', 'accept']);
                    return click(continueButton, 'DISMISSED_HONOR_CODE') ||
                        'KNOWN_HONOR_CODE_WAITING';
                }

                if (dialogText.includes("completed today's goals") ||
                    dialogText.includes('completed today’s goals')) {
                    const continueLearning = findExactButton(
                        dialog, ['continue learning', 'got it']);
                    return click(continueLearning, 'DISMISSED_DAILY_GOAL') ||
                        'KNOWN_DAILY_GOAL_WAITING';
                }

                const harmlessNudge = /(today's goals|today’s goals|quick survey|tell us what you think|personalize|recommendation|welcome to coursera|take a tour|what's new|new feature|weekly learning target)/i;
                if (harmlessNudge.test(dialogText)) {
                    const deferButton = findExactButton(dialog, [
                        'continue learning', 'got it', 'maybe later', 'not now',
                        'no thanks', 'skip'
                    ]);
                    if (deferButton) {
                        return click(deferButton, 'DISMISSED_INFORMATIONAL');
                    }

                    const closeButton = usableButtons(dialog).find(button => {
                        const aria = normalize(button.getAttribute('aria-label'));
                        const title = normalize(button.getAttribute('title'));
                        return aria === 'close' || aria === 'close modal' ||
                            aria === 'dismiss' || title === 'close';
                    });
                    return click(closeButton, 'DISMISSED_INFORMATIONAL_CLOSE') ||
                        'KNOWN_INFORMATIONAL_WAITING';
                }

                return 'UNKNOWN_BLOCKING_DIALOG';
            }

            // Banner timezone không phải dialog nhưng thường che góc trang.
            for (const button of Array.from(document.querySelectorAll('button')).filter(isVisible)) {
                if (normalize(button.innerText || button.textContent) !== 'dismiss') continue;
                let ancestor = button.parentElement;
                for (let depth = 0; ancestor && depth < 6; depth++, ancestor = ancestor.parentElement) {
                    if (normalize(ancestor.innerText).includes('timezone mismatch')) {
                        return click(button, 'DISMISSED_TIMEZONE');
                    }
                }
            }

            return 'NO_POPUP';
        })();
        """;

    private async Task DismissAnyGlobalPopupsAsync(int maxPasses = 3)
    {
        Uri? capturedUri = MainWebView.Source;
        string capturedUrl = capturedUri?.ToString() ?? string.Empty;
        if (MainWebView.CoreWebView2 == null ||
            !IsCourseraUri(capturedUri) ||
            IsCourseraLoginUri(capturedUri) ||
            _courseraProfileBootstrapState != CourseraProfileBootstrapState.Idle ||
            !await _popupWatchdogLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            for (int pass = 0; pass < Math.Max(1, maxPasses); pass++)
            {
                if (!string.Equals(
                        MainWebView.Source?.ToString(), capturedUrl,
                        StringComparison.Ordinal))
                {
                    return;
                }

                string result;
                try
                {
                    string popupScript = JsHandleKnownCourseraPopup.Replace(
                        "__EXPECTED_URL_JSON__",
                        JsonSerializer.Serialize(capturedUrl),
                        StringComparison.Ordinal);
                    result = DecodeWebViewString(
                        await MainWebView.ExecuteScriptAsync(popupScript));
                }
                catch
                {
                    return;
                }

                if (!result.StartsWith("DISMISSED_", StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(300);
            }
        }
        finally
        {
            _popupWatchdogLock.Release();
        }
    }

    private const string JsConfirmOwnedCourseraSubmission = """
        (function() {
            const expectedUrl = __EXPECTED_URL_JSON__;
            if (location.href !== expectedUrl) return 'STALE_DOCUMENT';
            const normalize = value => String(value || '')
                .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
            const visible = element => !!element &&
                element.getClientRects().length > 0 &&
                !element.closest('[aria-hidden="true"]') &&
                !element.closest('[inert]');
            const dialogCandidates = Array.from(document.querySelectorAll(
                '[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open]'))
                .filter(visible);
            const dialogs = dialogCandidates
                .filter(candidate => !dialogCandidates.some(other =>
                    other !== candidate && candidate.contains(other)))
                .filter(dialog => /(ready to submit|submit your (assignment|quiz|response)|confirm (your )?submission|are you sure.{0,120}(submit|turn in))/i.test(
                    normalize(dialog.innerText || dialog.textContent)));
            if (dialogs.length !== 1) return dialogs.length === 0
                ? 'CONFIRM_NOT_FOUND'
                : 'CONFIRM_AMBIGUOUS';

            const buttons = Array.from(dialogs[0].querySelectorAll('button'))
                .filter(visible)
                .filter(button => !button.disabled &&
                    normalize(button.getAttribute('aria-disabled')) !== 'true')
                .filter(button => ['submit', 'confirm', 'yes'].includes(
                    normalize(button.innerText || button.textContent)))
                .filter(button => button.getAttribute('data-testid') !== 'submit-button');
            if (buttons.length !== 1) return buttons.length === 0
                ? 'CONFIRM_BUTTON_NOT_FOUND'
                : 'CONFIRM_BUTTON_AMBIGUOUS';
            buttons[0].click();
            return 'CLICKED';
        })();
        """;

    private async Task<string> ConfirmOwnedCourseraSubmissionAsync()
    {
        Uri? capturedUri = MainWebView.Source;
        if (!IsCourseraUri(capturedUri) || IsCourseraLoginUri(capturedUri))
        {
            return "WRONG_PAGE";
        }

        string capturedUrl = capturedUri!.ToString();
        string script = JsConfirmOwnedCourseraSubmission.Replace(
            "__EXPECTED_URL_JSON__",
            JsonSerializer.Serialize(capturedUrl),
            StringComparison.Ordinal);
        try
        {
            return DecodeWebViewString(await MainWebView.ExecuteScriptAsync(script));
        }
        catch
        {
            return "CONFIRM_SCRIPT_FAILED";
        }
    }

    private const int CourseraSubmitPollMilliseconds = 250;
    private const int CourseraSubmitPollAttempts = 48;

    // Coursera can keep desktop/mobile/sticky copies of the Submit button in the
    // DOM.  Do not use querySelector here: it returns the first copy, which can
    // be hidden and disabled while the real quiz button is visibly ready.
    private const string JsProbeCourseraPrimarySubmit = """
        (function() {
            const expectedUrl = __EXPECTED_URL_JSON__;
            const clickWhenReady = __CLICK_WHEN_READY__;
            const currentUrl = new URL(location.href);
            const targetUrl = new URL(expectedUrl);
            if (currentUrl.origin !== targetUrl.origin ||
                currentUrl.pathname !== targetUrl.pathname ||
                currentUrl.search !== targetUrl.search) {
                return 'STALE_DOCUMENT';
            }

            const hasHiddenAncestor = element => {
                for (let current = element; current && current.nodeType === 1; current = current.parentElement) {
                    if (current.getAttribute('aria-hidden') === 'true' || current.hasAttribute('inert')) {
                        return true;
                    }
                    const style = window.getComputedStyle(current);
                    if (!style || style.display === 'none' ||
                        style.visibility === 'hidden' || style.visibility === 'collapse') {
                        return true;
                    }
                }
                return false;
            };
            const isVisibleButton = button => !!button && button.isConnected &&
                button.getClientRects().length > 0 && !hasHiddenAncestor(button);
            const isEnabled = control => !control.disabled &&
                String(control.getAttribute('aria-disabled') || '').trim().toLowerCase() !== 'true';
            const isSubmitEnabled = button => isEnabled(button) &&
                window.getComputedStyle(button).pointerEvents !== 'none';

            // First resolve Honor Code; it scopes the primary submit button to
            // the active assignment dialog when Coursera has duplicate layouts.
            const visibleHonorCodes = Array.from(
                document.querySelectorAll('#agreement-checkbox-base'))
                .filter(input => input.isConnected && !hasHiddenAncestor(input));
            if (visibleHonorCodes.length > 1) {
                return 'HONOR_CONFIRMATION_AMBIGUOUS|visible=' + visibleHonorCodes.length;
            }
            const honorCode = visibleHonorCodes.length === 1 ? visibleHonorCodes[0] : null;
            if (honorCode && !honorCode.checked) {
                if (!isEnabled(honorCode)) return 'HONOR_CONFIRMATION_BLOCKED';
                honorCode.click();
                return 'HONOR_CONFIRMATION_CLICKED';
            }

            const submitScope = honorCode?.closest(
                '[role="dialog"],[aria-modal="true"],dialog[open]');
            const allSubmitButtons = Array.from(
                document.querySelectorAll('button[data-testid="submit-button"]'))
                .filter(button => !submitScope || submitScope.contains(button));
            const visibleSubmitButtons = allSubmitButtons.filter(isVisibleButton);
            if (allSubmitButtons.length === 0) return 'SUBMIT_NOT_FOUND';
            if (visibleSubmitButtons.length === 0) {
                return 'SUBMIT_NOT_VISIBLE|total=' + allSubmitButtons.length;
            }
            const enabledSubmitButtons = visibleSubmitButtons.filter(isSubmitEnabled);
            if (enabledSubmitButtons.length > 1) {
                return 'SUBMIT_AMBIGUOUS|visible=' + visibleSubmitButtons.length +
                    '|enabled=' + enabledSubmitButtons.length + '|total=' + allSubmitButtons.length;
            }
            if (enabledSubmitButtons.length === 0) {
                const firstVisibleButton = visibleSubmitButtons[0];
                if (visibleSubmitButtons.length === 1) {
                    firstVisibleButton.scrollIntoView({ behavior: 'auto', block: 'center', inline: 'nearest' });
                }
                return 'SUBMIT_WAITING|visible=' + visibleSubmitButtons.length + '|total=' + allSubmitButtons.length +
                    '|enabled=0|disabled=' + (firstVisibleButton.disabled ? '1' : '0') +
                    '|aria-disabled=' + String(firstVisibleButton.getAttribute('aria-disabled') || 'false');
            }

            const submitButton = enabledSubmitButtons[0];
            submitButton.scrollIntoView({ behavior: 'auto', block: 'center', inline: 'nearest' });
            if (!clickWhenReady) return 'READY';
            // Re-query/validate on every polling pass, then click only the one
            // visible and enabled submit control belonging to this document.
            submitButton.click();
            return 'CLICKED';
        })();
        """;

    private async Task<string> WaitForCourseraPrimarySubmitAsync(bool clickWhenReady)
    {
        Uri? capturedUri = MainWebView.Source;
        if (!IsCourseraUri(capturedUri) || IsCourseraLoginUri(capturedUri))
        {
            return "WRONG_PAGE";
        }

        string expectedUrl = capturedUri!.ToString();
        string lastStatus = "SUBMIT_WAITING";
        for (int attempt = 1; attempt <= CourseraSubmitPollAttempts; attempt++)
        {
            if (!IsSameCourseraDocument(capturedUri))
            {
                return "STALE_DOCUMENT";
            }

            string script = JsProbeCourseraPrimarySubmit
                .Replace("__EXPECTED_URL_JSON__", JsonSerializer.Serialize(expectedUrl), StringComparison.Ordinal)
                .Replace("__CLICK_WHEN_READY__", clickWhenReady ? "true" : "false", StringComparison.Ordinal);
            string status;
            try
            {
                status = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(script));
            }
            catch
            {
                return "SUBMIT_SCRIPT_FAILED";
            }

            if ((!clickWhenReady && status == "READY") ||
                (clickWhenReady && status == "CLICKED"))
            {
                return status;
            }

            if (IsCourseraSubmitRetryableStatus(status))
            {
                lastStatus = status;
                _viewModel.StatusText = clickWhenReady
                    ? $"⏳ Coursera đang cập nhật nút Submit trước khi nộp ({attempt}/{CourseraSubmitPollAttempts})..."
                    : $"⏳ Đang chờ Coursera mở đúng nút Submit ({attempt}/{CourseraSubmitPollAttempts})...";
                if (attempt < CourseraSubmitPollAttempts)
                {
                    await Task.Delay(CourseraSubmitPollMilliseconds);
                    continue;
                }

                break;
            }

            return status;
        }

        return $"SUBMIT_TIMEOUT|{lastStatus}";
    }

    private static bool IsCourseraSubmitRetryableStatus(string status) =>
        status == "HONOR_CONFIRMATION_CLICKED" ||
        status == "HONOR_CONFIRMATION_BLOCKED" ||
        status == "SUBMIT_NOT_FOUND" ||
        status.StartsWith("SUBMIT_WAITING|", StringComparison.Ordinal) ||
        status.StartsWith("SUBMIT_NOT_VISIBLE|", StringComparison.Ordinal) ||
        status.StartsWith("SUBMIT_AMBIGUOUS|", StringComparison.Ordinal) ||
        status.StartsWith("HONOR_CONFIRMATION_AMBIGUOUS|", StringComparison.Ordinal);

    private static string DescribeCourseraSubmitStatus(string status)
    {
        if (status.StartsWith("SUBMIT_TIMEOUT|", StringComparison.Ordinal))
        {
            string finalStatus = status["SUBMIT_TIMEOUT|".Length..];
            if (finalStatus == "HONOR_CONFIRMATION_CLICKED" ||
                finalStatus == "HONOR_CONFIRMATION_BLOCKED" ||
                finalStatus.StartsWith("HONOR_CONFIRMATION_AMBIGUOUS|", StringComparison.Ordinal))
            {
                return "Coursera chưa giữ được xác nhận Honor Code sau 12 giây (" +
                    DescribeCourseraSubmitStatus(finalStatus) + ")";
            }

            return "Coursera chưa mở nút Submit sau 12 giây (" +
                DescribeCourseraSubmitStatus(finalStatus) + ")";
        }

        if (status.StartsWith("SUBMIT_WAITING|", StringComparison.Ordinal))
        {
            return "nút Submit của Coursera vẫn đang khóa (" + status + ")";
        }
        if (status.StartsWith("SUBMIT_NOT_VISIBLE|", StringComparison.Ordinal))
        {
            return "chưa thấy nút Submit đang hiển thị (" + status + ")";
        }
        if (status.StartsWith("SUBMIT_AMBIGUOUS|", StringComparison.Ordinal))
        {
            return "Coursera đang hiển thị nhiều nút Submit hợp lệ (" + status + ")";
        }
        if (status.StartsWith("HONOR_CONFIRMATION_AMBIGUOUS|", StringComparison.Ordinal))
        {
            return "Coursera đang hiển thị nhiều ô xác nhận Honor Code (" + status + ")";
        }

        return status switch
        {
            "SUBMIT_NOT_FOUND" => "không tìm thấy nút Submit của bài hiện tại",
            "SUBMIT_SCRIPT_FAILED" => "không đọc được trạng thái nút Submit",
            "HONOR_CONFIRMATION_BLOCKED" => "ô xác nhận Honor Code đang bị khóa",
            "HONOR_CONFIRMATION_CLICKED" => "Coursera chưa giữ được xác nhận Honor Code",
            "HONOR_CONFIRMATION_LOST" => "ô xác nhận Honor Code chưa được giữ",
            "STALE_DOCUMENT" => "trang Coursera đã đổi trong lúc chuẩn bị nộp",
            "WRONG_PAGE" => "không còn ở trang Coursera hợp lệ",
            _ => status
        };
    }

    private async Task HandleVideoLessonAsync()
    {
        _viewModel.StatusText = "🎥 Đang xử lý Video: Khởi tạo Enforcer...";
        await Task.Delay(2000); // Đợi khung web nạp xong
        await DismissAnyGlobalPopupsAsync(); // Phá popup ngay khi vừa load

        // KIỂM TRA BỎ QUA: Nếu bài này đã học xong từ trước (Nút Next màu xanh) -> Bấm Next luôn và thoát!
        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "⏭️ Video này đã xem rồi! Đang bỏ qua...";
            return;
        }

        bool isSkipped = false; // Biến C# để nhớ xem đã tua video lần nào chưa
        bool isCompleted = false;

        // Vòng lặp Cưỡng Chế: Chạy liên tục mỗi giây 1 lần
        while (!isCompleted)
        {
            // 1. Quét xem nút Next đã xanh chưa (Nếu xanh rồi thì vỡ vòng lặp luôn)
            isCompleted = await CheckLessonCompletedAndClickNextAsync();
            if (isCompleted) break;

            // 1.5. Dọn popup chắn màn hình bằng cùng allowlist của watchdog.
            await DismissAnyGlobalPopupsAsync(maxPasses: 2);

            // 2. Mã JS Cưỡng chế: Ép tắt tiếng, ép x2, ép Play và kiểm tra thời lượng
            string jsEnforceVideo = $@"
                (function() {{
                    var v = document.querySelector('video');
                    if (v) {{
                        // Cưỡng chế các thuộc tính
                        if (!v.muted) v.muted = true;
                        if (v.playbackRate !== 2.0) v.playbackRate = 2.0;
                        if (v.paused) v.play();
                        
                        // Lấy biến isSkipped từ C# truyền vào
                        var hasSkipped = '{isSkipped}'.toLowerCase() === 'true';
                        
                        // Nếu chưa từng tua VÀ video đã nạp xong độ dài (hết bị NaN)
                        if (!hasSkipped && !isNaN(v.duration) && v.duration > 5) {{
                            v.currentTime = v.duration - 2; // Tua!
                            return 'SKIPPED|' + v.currentTime + '|' + v.duration + '|' + v.playbackRate;
                        }}
                        
                        // Nếu đã tua rồi, hoặc đang đợi nạp độ dài
                        if (!isNaN(v.duration)) {{
                            return 'PLAYING|' + v.currentTime + '|' + v.duration + '|' + v.playbackRate;
                        }}
                    }}
                    return 'error';
                }})();
            ";

            try
            {
                string result = await MainWebView.ExecuteScriptAsync(jsEnforceVideo);
                result = result.Trim('"');

                // Kịch bản A: Lần đầu tiên tua thành công
                if (result.StartsWith("SKIPPED|"))
                {
                    isSkipped = true; // Lưu lại để các giây sau không tua nữa (kẻo nó kẹt)
                    _viewModel.StatusText = "⚡ Đã tua video thành công! Chờ Anti-Skip...";
                    await Task.Delay(1500); // Đợi 1.5s xem Coursera có giật ngược thời gian không
                    continue;
                }

                // Kịch bản B: Video đang chạy bình thường (đã tua hoặc đang đợi)
                if (result.StartsWith("PLAYING|"))
                {
                    var parts = result.Split('|');
                    double current = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    double duration = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    double rate = double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);

                    double timeRemainingSeconds = (duration - current) / rate;
                    if (timeRemainingSeconds < 0) timeRemainingSeconds = 0;

                    _viewModel.StatusText = $"⏳ Đang cưỡng chế phát x2. Đếm ngược: {Math.Round(timeRemainingSeconds)} giây...";
                }
            }
            catch { }

            await Task.Delay(1000); // Ngủ 1 giây rồi lặp lại vòng lặp
        }

        _viewModel.StatusText = "✅ Video hoàn thành! Đã chuyển bài.";
    }

    private bool _isHandlingLti;

    private async Task SkipAppItemAsync(
        string itemType,
        bool ltiGuardAlreadyHeld = false)
    {
        _courseHasSkippedLaunchAppItems = true;
        if (!ltiGuardAlreadyHeld)
        {
            if (_isHandlingLti) return;
            _isHandlingLti = true;
        }

        try
        {
            Uri? appItemUri = MainWebView.Source;
            if (IsCourseraUri(appItemUri))
            {
                string skippedPath = appItemUri!.AbsolutePath.TrimEnd('/');
                _skippedLaunchAppItemPaths.Add(
                    string.IsNullOrEmpty(skippedPath) ? "/" : skippedPath);
            }
            _viewModel.StatusText = $"⏭️ Phát hiện {itemType}. Đang bỏ qua...";
            await Task.Delay(800);
            if (appItemUri == null || !IsSameCourseraDocument(appItemUri))
            {
                return;
            }

            Uri? directNextUri = null;
            string lastSkipStatus = "NOT_FOUND";

            // Coursera đôi khi render nút trước khi React gắn xong sự kiện. Vì vậy không được
            // xem việc gọi element.click() là thành công cho tới khi URL thực sự thay đổi.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                await DismissAnyGlobalPopupsAsync(maxPasses: 2);
                if (!IsSameCourseraDocument(appItemUri))
                {
                    return;
                }

                bool useTrustedMouseClick = attempt == 3;
                (lastSkipStatus, double clickX, double clickY, Uri? candidateUri) =
                    await LocateAndClickAppItemNextAsync(clickWithDom: !useTrustedMouseClick);
                if (candidateUri != null &&
                    IsCourseraUri(candidateUri) &&
                    !AreSameCourseraDocuments(appItemUri, candidateUri))
                {
                    directNextUri = candidateUri;
                }

                if (lastSkipStatus == "READY" && useTrustedMouseClick)
                {
                    lastSkipStatus = await DispatchTrustedAppItemClickAsync(clickX, clickY)
                        ? "TRUSTED_CLICKED"
                        : "TRUSTED_CLICK_FAILED";
                }

                if (lastSkipStatus is "CLICKED" or "TRUSTED_CLICKED")
                {
                    _viewModel.StatusText =
                        $"⏭️ Đã bấm bỏ qua {itemType}; đang xác nhận bài kế tiếp ({attempt}/3)...";
                    if (await WaitForAppItemNavigationAsync(appItemUri, TimeSpan.FromSeconds(5)))
                    {
                        _viewModel.StatusText = $"✅ Đã bỏ qua {itemType} và chuyển sang bài kế tiếp.";
                        return;
                    }
                }
                else
                {
                    _viewModel.StatusText =
                        $"⏳ Chưa điều khiển được nút bài kế tiếp ({attempt}/3, {lastSkipStatus}). Đang thử lại...";
                    await Task.Delay(1000);
                }
            }

            // Nếu Coursera cung cấp href nhưng handler React không chạy, điều hướng thẳng tới href đó.
            if (directNextUri != null && IsSameCourseraDocument(appItemUri))
            {
                _viewModel.StatusText =
                    $"⏭️ Nút Next không phản hồi; đang mở trực tiếp bài kế tiếp của {itemType}...";
                MainWebView.Source = directNextUri;
                if (await WaitForAppItemNavigationAsync(appItemUri, TimeSpan.FromSeconds(8)))
                {
                    return;
                }
            }

            // Đường lui luôn khả dụng với URL /learn/{slug}/...: quay về Course Home để
            // CheckModules/CheckLessons quét lại và chọn bài tự động hóa tiếp theo.
            if (TryGetCourseHomeUri(appItemUri, out Uri? courseHomeUri))
            {
                _viewModel.StatusText =
                    $"⚠️ Nút Next không phản hồi sau 3 lần ({lastSkipStatus}). Đang quay lại danh sách bài để tiếp tục...";
                MainWebView.Source = courseHomeUri;
                if (await WaitForAppItemNavigationAsync(appItemUri, TimeSpan.FromSeconds(8)))
                {
                    return;
                }
            }

            _viewModel.StatusText =
                $"❌ Không thể rời {itemType} sau 3 lần bấm và điều hướng dự phòng ({lastSkipStatus}).";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"❌ Lỗi khi bỏ qua {itemType}: " + ex.Message;
        }
        finally
        {
            if (!ltiGuardAlreadyHeld)
            {
                _isHandlingLti = false;
            }
        }
    }

    private async Task<(string Status, double X, double Y, Uri? CandidateUri)>
        LocateAndClickAppItemNextAsync(bool clickWithDom)
    {
        const string jsLocateNext = """
            (function() {
                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                const isVisible = element => {
                    if (!element || element.closest('[aria-hidden="true"]') || element.closest('[inert]')) {
                        return false;
                    }
                    const style = window.getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' &&
                        Number(style.opacity || 1) > 0 && rect.width > 0 && rect.height > 0;
                };
                const controls = Array.from(document.querySelectorAll(
                    'button, a[href], [role="button"]'))
                    .filter(isVisible)
                    .filter(element => !element.disabled &&
                        String(element.getAttribute('aria-disabled') || '').toLowerCase() !== 'true')
                    .map(element => {
                        const ariaLabel = normalize(element.getAttribute('aria-label'));
                        const textLabel = normalize(element.innerText || element.textContent);
                        const label = ariaLabel || textLabel;
                        let score = 0;
                        if (/^Go to next item$/i.test(ariaLabel)) score += 100;
                        if (/^Go to next item$/i.test(label)) score += 80;
                        if (/^Next item$/i.test(label)) score += 40;
                        if (element.matches('button')) score += 10;
                        if (/cds-button-primary/i.test(String(element.className || ''))) score += 5;
                        return { element, score };
                    })
                    .filter(candidate => candidate.score >= 40)
                    .sort((left, right) => right.score - left.score);

                if (controls.length === 0) return 'NOT_FOUND|0|0|';
                const target = controls[0].element;
                target.scrollIntoView({ behavior: 'auto', block: 'center', inline: 'center' });
                const rect = target.getBoundingClientRect();
                const x = rect.left + rect.width / 2;
                const y = rect.top + rect.height / 2;
                const anchor = target.matches('a[href]') ? target : target.closest('a[href]');
                const href = anchor && anchor.href ? anchor.href : '';
                if (__CLICK_WITH_DOM__) {
                    try { target.focus({ preventScroll: true }); } catch (_) { target.focus(); }
                    target.click();
                    return 'CLICKED|' + x + '|' + y + '|' + encodeURIComponent(href);
                }
                return 'READY|' + x + '|' + y + '|' + encodeURIComponent(href);
            })();
            """;

        string script = jsLocateNext.Replace(
            "__CLICK_WITH_DOM__",
            clickWithDom ? "true" : "false",
            StringComparison.Ordinal);
        string rawStatus = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(script));
        string[] parts = rawStatus.Split('|');
        string status = parts.Length > 0 ? parts[0] : "INVALID_RESULT";
        _ = double.TryParse(
            parts.Length > 1 ? parts[1] : null,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double x);
        _ = double.TryParse(
            parts.Length > 2 ? parts[2] : null,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double y);

        Uri? candidateUri = null;
        if (parts.Length > 3)
        {
            string href = Uri.UnescapeDataString(parts[3]);
            _ = Uri.TryCreate(href, UriKind.Absolute, out candidateUri);
        }

        return (status, x, y, candidateUri);
    }

    private async Task<bool> DispatchTrustedAppItemClickAsync(double x, double y)
    {
        if (MainWebView.CoreWebView2 == null || x <= 0 || y <= 0)
        {
            return false;
        }

        try
        {
            string pressedPayload = JsonSerializer.Serialize(new
            {
                type = "mousePressed",
                x,
                y,
                button = "left",
                clickCount = 1
            });
            string releasedPayload = JsonSerializer.Serialize(new
            {
                type = "mouseReleased",
                x,
                y,
                button = "left",
                clickCount = 1
            });
            await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Input.dispatchMouseEvent", pressedPayload);
            await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Input.dispatchMouseEvent", releasedPayload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForAppItemNavigationAsync(Uri appItemUri, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Uri? currentUri = MainWebView.Source;
            if (IsCourseraUri(currentUri) &&
                !AreSameCourseraDocuments(appItemUri, currentUri!))
            {
                return true;
            }

            await Task.Delay(250);
        }

        Uri? finalUri = MainWebView.Source;
        return IsCourseraUri(finalUri) &&
            !AreSameCourseraDocuments(appItemUri, finalUri!);
    }

    private static bool AreSameCourseraDocuments(Uri left, Uri right)
    {
        return Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool TryGetCourseHomeUri(Uri? appItemUri, out Uri? courseHomeUri)
    {
        courseHomeUri = null;
        if (!TryGetCourseSlug(appItemUri, out string courseSlug))
        {
            return false;
        }

        courseHomeUri = new Uri(
            $"https://www.coursera.org/learn/{Uri.EscapeDataString(courseSlug)}/home/welcome");
        return true;
    }

    private static bool TryGetCourseSlug(Uri? uri, out string courseSlug)
    {
        courseSlug = string.Empty;
        if (!IsCourseraUri(uri))
        {
            return false;
        }

        string[] segments = uri!.AbsolutePath.Split(
            '/', StringSplitOptions.RemoveEmptyEntries);
        int learnIndex = Array.FindIndex(
            segments,
            segment => segment.Equals("learn", StringComparison.OrdinalIgnoreCase));
        if (learnIndex < 0 || learnIndex + 1 >= segments.Length ||
            string.IsNullOrWhiteSpace(segments[learnIndex + 1]))
        {
            return false;
        }

        courseSlug = segments[learnIndex + 1];
        return true;
    }

    private async Task<string> LaunchCourseraLtiWithoutOpeningWindowAsync(Uri expectedUri)
    {
        if (!IsSameCourseraDocument(expectedUri))
        {
            return "STALE_DOCUMENT";
        }

        _suppressedLtiNewWindow = false;
        _suppressLtiNewWindowSourceUri = expectedUri;
        _suppressLtiNewWindowUntilUtc = DateTimeOffset.UtcNow.AddSeconds(8);

        const string jsLaunch = """
            (function() {
                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
                const visible = element => element.getClientRects().length > 0 &&
                    !element.closest('[aria-hidden="true"]') && !element.closest('[inert]');
                const candidates = Array.from(document.querySelectorAll('button, a'))
                    .filter(visible)
                    .filter(element => {
                        const name = normalize([
                            element.innerText, element.textContent,
                            element.getAttribute('aria-label'), element.getAttribute('title')
                        ].filter(Boolean).join(' '));
                        return /^launch app(?:\b|\.)/.test(name);
                    });
                if (candidates.length !== 1) return candidates.length === 0
                    ? 'NOT_FOUND'
                    : 'CONTROL_AMBIGUOUS';
                const target = candidates[0];
                if (target.disabled || normalize(target.getAttribute('aria-disabled')) === 'true') {
                    return 'CONTROL_BLOCKED';
                }
                target.click();
                return 'CLICKED';
            })();
            """;

        try
        {
            string clickStatus = DecodeWebViewString(
                await MainWebView.ExecuteScriptAsync(jsLaunch));
            if (clickStatus != "CLICKED")
            {
                return clickStatus;
            }

            // NewWindowRequested được phát đồng bộ ngay sau click; chờ một nhịp để nhận event.
            await Task.Delay(700);
            return _suppressedLtiNewWindow
                ? "LAUNCHED_WINDOW_SUPPRESSED"
                : "LAUNCHED_NO_WINDOW_EVENT";
        }
        catch
        {
            return "LAUNCH_SCRIPT_FAILED";
        }
        finally
        {
            _suppressLtiNewWindowSourceUri = null;
            _suppressLtiNewWindowUntilUtc = DateTimeOffset.MinValue;
        }
    }

    private async Task HandleUngradedAppAsync()
    {
        if (_isHandlingLti) return;
        _isHandlingLti = true;

        try
        {
            Uri? handlerUri = MainWebView.Source;
            bool isGradedLti = handlerUri?.AbsolutePath.Contains(
                "/gradedLti/", StringComparison.OrdinalIgnoreCase) == true;

            if (await CheckForLockedScreenAndReloadAsync()) return;

            _viewModel.StatusText = "🛠️ Đang xử lý ứng dụng/Lab Coursera...";
            await Task.Delay(3000);
            await DismissAnyGlobalPopupsAsync();

            if (!IsSameCourseraDocument(handlerUri))
            {
                return;
            }

            const string jsDetectAppItemType = """
                (function() {
                    const normalize = value => String(value || '')
                        .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                    const leafTexts = Array.from(document.querySelectorAll('span, div, p'))
                        .filter(element => element.children.length === 0)
                        .map(element => normalize(element.textContent));
                    return leafTexts.some(text => /^(?:Practice|Ungraded)\s+App\s+Item$/i.test(text))
                        ? 'PRACTICE_APP_ITEM'
                        : 'OTHER_APP_ITEM';
                })();
                """;
            string appItemType = DecodeWebViewString(
                await MainWebView.ExecuteScriptAsync(jsDetectAppItemType));
            if (appItemType == "PRACTICE_APP_ITEM" && ShouldSkipPracticeAppItems)
            {
                await SkipAppItemAsync("Practice App Item", ltiGuardAlreadyHeld: true);
                return;
            }

            if (await CheckLessonCompletedAndClickNextAsync())
            {
                _viewModel.StatusText = "⏭️ Bài Lab này đã xong! Đang chuyển bài...";
                return;
            }

            // Graded LTI kiểu Coursera trong ảnh: chờ legal name, điền/xác minh,
            // bấm Launch App nhưng chặn riêng cửa sổ mới rồi chờ nút Next.
            if (isGradedLti)
            {
                string lastLegalNameStatus = "NOT_REQUIRED";
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    if (!IsSameCourseraDocument(handlerUri))
                    {
                        return;
                    }

                    await DismissAnyGlobalPopupsAsync(maxPasses: 2);
                    lastLegalNameStatus = await FillCourseraLegalNameIfRequiredAsync();
                    if (lastLegalNameStatus == "ALREADY_SET" ||
                        lastLegalNameStatus == "SET_AND_VERIFIED")
                    {
                        string gradedLaunchStatus = "NOT_FOUND";
                        _viewModel.StatusText = "✅ Đã điền tên. Đang chờ nút Launch App...";
                        for (int launchAttempt = 0; launchAttempt < 20; launchAttempt++)
                        {
                            await Task.Delay(500);
                            if (!IsSameCourseraDocument(handlerUri))
                            {
                                return;
                            }

                            gradedLaunchStatus = await LaunchCourseraLtiWithoutOpeningWindowAsync(
                                handlerUri!);
                            if (gradedLaunchStatus.StartsWith("LAUNCHED_", StringComparison.Ordinal))
                            {
                                break;
                            }

                            if (gradedLaunchStatus != "NOT_FOUND" &&
                                gradedLaunchStatus != "CONTROL_BLOCKED")
                            {
                                break;
                            }
                        }

                        if (!gradedLaunchStatus.StartsWith("LAUNCHED_", StringComparison.Ordinal))
                        {
                            _viewModel.StatusText = $"⚠️ Đã điền tên nhưng chưa Launch được Lab ({gradedLaunchStatus}).";
                            return;
                        }

                        _viewModel.StatusText = "✅ Đã điền tên và Launch App; tab mới đã được chặn. Đang chờ Next...";
                        for (int nextAttempt = 0; nextAttempt < 15; nextAttempt++)
                        {
                            await Task.Delay(1000);
                            if (!IsSameCourseraDocument(handlerUri))
                            {
                                return;
                            }

                            if (await CheckLessonCompletedAndClickNextAsync())
                            {
                                _viewModel.StatusText = "✅ Đã điền tên, Launch App và chuyển sang bài kế tiếp.";
                                return;
                            }
                        }

                        _viewModel.StatusText = "✅ Đã điền tên và Launch App; tab mới đã được chặn. Nút Next chưa sẵn sàng.";
                        return;
                    }

                    if (lastLegalNameStatus != "NOT_REQUIRED" &&
                        lastLegalNameStatus != "CONTROL_CHANGED" &&
                        lastLegalNameStatus != "VALUE_MISMATCH")
                    {
                        _viewModel.StatusText = $"⚠️ Chưa điền được tên cho Lab ({lastLegalNameStatus}). Đã dừng.";
                        return;
                    }

                    await Task.Delay(700);
                }

                _viewModel.StatusText = $"⚠️ Ô tên của Lab chưa sẵn sàng ({lastLegalNameStatus}). Đã dừng.";
                return;
            }

            string legalNameStatus = await FillCourseraLegalNameIfRequiredAsync();
            if (!IsLegalNameReadyStatus(legalNameStatus))
            {
                _viewModel.StatusText = $"⚠️ Chưa chuẩn bị được tên cho Lab ({legalNameStatus}). Đã dừng.";
                return;
            }

            if (!IsSameCourseraDocument(handlerUri))
            {
                return;
            }

            const string jsHandleHonorCode = """
                (function() {
                    const normalize = value => String(value || '')
                        .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                    const fieldText = field => {
                        const labels = Array.from(field.labels || [])
                            .map(label => label.innerText || label.textContent);
                        return normalize([
                            field.id, field.name, field.getAttribute('aria-label'), ...labels
                        ].filter(Boolean).join(' '));
                    };
                    let honorCode = document.getElementById('agreement-checkbox-base');
                    if (!honorCode) {
                        const candidates = Array.from(
                            document.querySelectorAll('input[type="checkbox"]'))
                            .filter(field => field.getClientRects().length > 0)
                            .filter(field => /(honou?r|agreement|responsibly)/i.test(fieldText(field)));
                        if (candidates.length > 1) return 'CONTROL_AMBIGUOUS';
                        honorCode = candidates[0] || null;
                    }
                    if (!honorCode) return 'NOT_REQUIRED';
                    if (honorCode.disabled) return 'CONTROL_BLOCKED';
                    if (!honorCode.checked) honorCode.click();
                    return honorCode.checked ? 'VERIFIED' : 'STATE_MISMATCH';
                })();
                """;
            string honorCodeStatus = DecodeWebViewString(
                await MainWebView.ExecuteScriptAsync(jsHandleHonorCode));
            if (honorCodeStatus != "VERIFIED" && honorCodeStatus != "NOT_REQUIRED")
            {
                _viewModel.StatusText = $"⚠️ Không xác nhận được Honor Code ({honorCodeStatus}). Đã dừng.";
                return;
            }

            await Task.Delay(700);
            if (!IsSameCourseraDocument(handlerUri))
            {
                return;
            }

            legalNameStatus = await FillCourseraLegalNameIfRequiredAsync();
            if (!IsLegalNameReadyStatus(legalNameStatus))
            {
                _viewModel.StatusText = $"⚠️ Tên của Lab không còn hợp lệ ({legalNameStatus}). Đã dừng.";
                return;
            }

            if (await CheckLessonCompletedAndClickNextAsync())
            {
                _viewModel.StatusText = "✅ Đã xác nhận Honor Code cho Lab! Đang chuyển bài.";
                return;
            }

            if (!IsSameCourseraDocument(handlerUri))
            {
                return;
            }

            string launchStatus = await LaunchCourseraLtiWithoutOpeningWindowAsync(
                handlerUri!);
            if (!launchStatus.StartsWith("LAUNCHED_", StringComparison.Ordinal))
            {
                _viewModel.StatusText = $"⚠️ Chưa Launch được Lab ({launchStatus}).";
                return;
            }

            await Task.Delay(2000);
            if (await CheckLessonCompletedAndClickNextAsync())
            {
                _viewModel.StatusText = "✅ Đã Launch Lab! Đang chuyển bài.";
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "❌ Lỗi khi xử lý Lab: " + ex.Message;
        }
        finally
        {
            _isHandlingLti = false;
        }
    }

    private async Task HandleUngradedWidgetAsync()
    {
        _viewModel.StatusText = "🧩 Đang xử lý bài tập tương tác (Ungraded Widget)...";
        await Task.Delay(3000); 
        await DismissAnyGlobalPopupsAsync();

        // BỎ QUA NẾU ĐÃ XONG
        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "⏭️ Bài tập này đã làm rồi! Đang chuyển bài tiếp theo...";
            return;
        }

        bool isCompleted = false;
        
        while (!isCompleted) 
        {
            isCompleted = await CheckLessonCompletedAndClickNextAsync();
            if (isCompleted) break;
            
            string jsAutoClick = @"
                (function() {
                    var container = document.getElementById('rendered-content') || document.body;
                    var elements = container.querySelectorAll('button, div[role=""button""]');
                    
                    for (var i = 0; i < elements.length; i++) {
                        var text = (elements[i].innerText || '').trim().toUpperCase();
                        if (text === 'NEXT' || text === 'NEXT >' || text === 'CONTINUE' || text === 'MARK AS COMPLETED') {
                            elements[i].click();
                            return 'Đã bấm ' + text + ' để qua bài';
                        }
                    }
                    
                    var validBtns = [];
                    var ignoreList = ['PREV', '< PREV', 'EXPAND', 'LIKE', 'DISLIKE', 'REPORT AN ISSUE', 'SAVE NOTE'];
                    
                    for (var i = 0; i < elements.length; i++) {
                        var text = (elements[i].innerText || '').trim().toUpperCase();
                        var shouldIgnore = ignoreList.indexOf(text) !== -1 || text.includes('GO TO NEXT ITEM');
                        if (!shouldIgnore && text.length > 0) {
                            if (elements[i].offsetHeight > 0 && elements[i].offsetWidth > 0) {
                                validBtns.push(elements[i]);
                            }
                        }
                    }
                    
                    if (validBtns.length > 0) {
                        var randomBtn = validBtns[Math.floor(Math.random() * validBtns.length)];
                        randomBtn.click();
                        return 'Đang thử chọn bừa: ' + randomBtn.innerText.substring(0, 15);
                    }
                    return 'Đang phân tích Widget...';
                })();
            ";
            
            try 
            {
                string result = await MainWebView.ExecuteScriptAsync(jsAutoClick);
                if (result != null) _viewModel.StatusText = $"🧩 {result.Trim('"')}";
            }
            catch {}
            
            await Task.Delay(2500); 
        }
        
        _viewModel.StatusText = "✅ Đã phá đảo bài tập tương tác! Chuyển bài.";
    }

    private bool _hasExtractedFeedbackThisSession = false;
    private bool _isHandlingQuiz = false;
    private bool _isHandlingDiscussion;
    private bool _isAdvancingPastPendingGradedResult;
    private int _quizAttemptCount = 0;
    private int _quizSpaAdvanceGeneration;
    private string _currentQuizUrl = "";

    private async Task HandleQuizAsync()
    {
        if (_isHandlingQuiz) return;
        _isHandlingQuiz = true;

        try
        {
            _viewModel.StatusText = "📝 Đang kiểm tra bài Trắc nghiệm (Quiz)...";
            await Task.Delay(3000); 
            
            if (await CheckForLockedScreenAndReloadAsync()) return;
            
            await DismissAnyGlobalPopupsAsync();

        // ========== BƯỚC 1: CHECK TRẠNG THÁI PASS/FAIL ==========
        string jsCheckPassStatus = @"
            (function() {
                var bodyText = document.body.innerText;
                var btns = Array.from(document.querySelectorAll('button, a'));

                // A submitted assessment can remain on this route while
                // Coursera grades it.  It can still expose a ""Try again""
                // button, so recognize this terminal-for-now state before
                // the generic retry/start logic below.
                var normalizedBody = String(bodyText || '')
                    .normalize('NFKC')
                    .replace(/[\u00a0]/g, ' ')
                    .replace(/[—–−]/g, '-')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                var hasViewSubmission = btns.some(b => {
                    var text = (b.innerText || b.textContent || '')
                        .replace(/\s+/g, ' ').trim().toLowerCase();
                    return text === 'view submission';
                });
                var hasPendingResults =
                    ((/\bsubmitted\b|\bsubmission\b/.test(normalizedBody) &&
                        /\b(?:results?|grades?|grading)\s+(?:are\s+)?pending\b/.test(normalizedBody)) ||
                        /\bawaiting\s+(?:results?|grades?|grading)\b/.test(normalizedBody) ||
                        /\bgrading\s+in\s+progress\b/.test(normalizedBody) ||
                        /đã\s*nộp[\s\S]{0,100}(?:đang\s*)?chờ[\s\S]{0,80}(?:kết\s*quả|chấm\s*điểm)/i.test(normalizedBody)) &&
                    (hasViewSubmission || /\bsubmitted\b/.test(normalizedBody));
                if (hasPendingResults) {
                    return 'PENDING_RESULTS';
                }
                
                // QUAN TRỌNG: Nếu có nút Start Assignment → BÀI CHƯA LÀM, không thể passed!
                var hasStartBtn = btns.some(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    return t === 'start assignment' || t === 'resume assignment';
                });
                if (hasStartBtn) {
                    return 'NEW';
                }
                
                // Tìm chữ 'You passed!' trên trang
                var hasPassed = bodyText.includes('You passed!');
                
                // Tìm chữ 'Your grade:' với điểm >= 80%
                var gradeMatch = bodyText.match(/Your grade:\s*(\d+)%/);
                if (gradeMatch && parseInt(gradeMatch[1]) >= 80) {
                    hasPassed = true;
                }
                
                // Tìm 'Your latest:' hoặc 'Your highest:' với điểm >= 80%
                var latestMatch = bodyText.match(/Your (?:latest|highest):\s*(\d+)%/);
                if (latestMatch && parseInt(latestMatch[1]) >= 80) {
                    hasPassed = true;
                }
                
                // Tìm chữ 'You didn\'t pass' hoặc tương tự
                var hasFailed = bodyText.includes('You didn') || bodyText.includes('not pass') || bodyText.includes('didn\'t pass');
                
                // Nếu vừa có pass vừa có fail text → ưu tiên pass nếu grade >= 80
                if (hasPassed && hasFailed) {
                    if (gradeMatch && parseInt(gradeMatch[1]) >= 80) {
                        hasFailed = false;
                    } else if (latestMatch && parseInt(latestMatch[1]) >= 80) {
                        hasFailed = false;
                    }
                }
                
                // Tìm nút Retake / Feedback
                var hasRetakeBtn = btns.some(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    return t === 'retake assignment' || t === 'try again' || t === 'retry';
                });
                var hasFeedbackBtn = btns.some(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    return t === 'view feedback';
                });
                
                // Tìm nút Next (navigation)
                var nextBtn = document.querySelector('button[aria-label=""Go to next item""]');
                var hasNextPrimary = nextBtn && nextBtn.className.includes('cds-button-primary');
                
                if (hasPassed) {
                    return 'PASSED|hasNext=' + !!hasNextPrimary;
                } else if (hasFailed) {
                    return 'FAILED|hasFeedback=' + hasFeedbackBtn + '|hasRetake=' + hasRetakeBtn;
                }
                return 'UNKNOWN';
            })();
        ";

        string statusResult = "";
        // Thử check pass tối đa 3 lần, mỗi lần cách nhau 3s (để trang load xong)
        for (int checkAttempt = 0; checkAttempt < 3; checkAttempt++)
        {
            try
            {
                statusResult = (await MainWebView.ExecuteScriptAsync(jsCheckPassStatus))?.Trim('"') ?? "";
                _viewModel.StatusText = $"🔍 Trạng thái Quiz (lần {checkAttempt + 1}): {statusResult}";
                
                // Nếu đã xác định được rõ ràng (không phải UNKNOWN) → dừng check
                if (statusResult.StartsWith("PASSED") ||
                    statusResult.StartsWith("FAILED") ||
                    statusResult.StartsWith("PENDING_RESULTS") ||
                    statusResult == "NEW")
                {
                    break;
                }
            }
            catch { }
            
            // Nếu UNKNOWN → chờ thêm 3s cho trang load tiếp
            await Task.Delay(3000);
        }

        // ========== BƯỚC 2: XỬ LÝ TỪNG TRẠNG THÁI ==========

        // --- TRẠNG THÁI 0: ĐÃ NỘP, ĐANG CHỜ CHẤM ---
        // Coursera may still show "Try again" here.  Do not open or submit
        // the attempt again; move on and keep this lesson out of the scanner.
        if (statusResult.StartsWith("PENDING_RESULTS", StringComparison.Ordinal))
        {
            await AdvancePastPendingGradedResultAsync();
            return;
        }
        
        // --- TRẠNG THÁI 1: ĐÃ PASS ---
        if (statusResult.StartsWith("PASSED"))
        {
            _viewModel.StatusText = "✅ Quiz này đã Pass! Đang chuyển bài tiếp theo...";
            Uri? passedQuizUri = MainWebView.Source;
            string beforeNextFingerprint = await CaptureQuizActivityFingerprintAsync();
            
            // Thử bấm Next
            if (await CheckLessonCompletedAndClickNextAsync(true))
            {
                _viewModel.StatusText = "⏭️ Đã bấm Next sau khi Pass; đang xác nhận bài kế tiếp...";
                if (IsCourseraUri(passedQuizUri))
                {
                    // Coursera sometimes swaps the next assessment in-place
                    // without raising a URL/navigation event.  Schedule a
                    // targeted re-check so the new Start assignment page does
                    // not remain idle with the old "passed" status visible.
                    int advanceGeneration = ++_quizSpaAdvanceGeneration;
                    _ = ResumeQuizAfterNextAsync(
                        passedQuizUri!,
                        beforeNextFingerprint,
                        advanceGeneration);
                }
                return;
            }
            
            // Nếu không có Next → Đây là bài cuối → Về trang chủ
            _viewModel.StatusText = "🏆 Đã Pass bài cuối cùng! Khoá học hoàn thành! Quay về Trang chủ...";
            string currentUrl = MainWebView.Source?.ToString() ?? "";
            int learnIndex = currentUrl.IndexOf("/learn/");
            if (learnIndex != -1)
            {
                int nextSlash = currentUrl.IndexOf("/", learnIndex + 7);
                string courseSlug = nextSlash != -1 
                    ? currentUrl.Substring(learnIndex + 7, nextSlash - (learnIndex + 7))
                    : currentUrl.Substring(learnIndex + 7);
                
                MainWebView.Source = new Uri($"https://www.coursera.org/learn/{courseSlug}/home/welcome");
            }
            return;
        }

        // --- TRẠNG THÁI 2: ĐÃ LÀM NHƯNG CHƯA ĐẠT (FAILED) ---
        // Logic: Đọc Feedback để học câu sai → Retake
        
        // --- TRẠNG THÁI 3: CHƯA LÀM (NEW) hoặc UNKNOWN ---
        // Logic: Bấm Start Assignment → Làm bài

        try 
        {
            // 1. JS TÌM VÀ BẤM NÚT START / RESUME / VIEW FEEDBACK
            string jsStartQuiz = $@"
                (function() {{
                    // Ưu tiên 1: Nếu thấy nút View Feedback thì bấm để học lỗi sai
                    var btns = Array.from(document.querySelectorAll('button, a'));
                    var feedbackBtn = btns.find(b => {{
                        var text = (b.innerText || '').trim().toLowerCase();
                        return text === 'view feedback';
                    }});
                    
                    var hasExtracted = '{_hasExtractedFeedbackThisSession}'.toLowerCase() === 'true';
                    
                    // Chỉ click Feedback nếu trong session này chưa từng lấy (tránh lặp vô hạn)
                    if (!hasExtracted && feedbackBtn && !feedbackBtn.disabled) {{
                        feedbackBtn.click();
                        return 'CLICKED_FEEDBACK';
                    }}

                    // Ưu tiên 2: Tìm nút Start / Retake
                    var startBtn = document.querySelector('button[data-testid=""CoverPageActionButton""]');
                    if (!startBtn || startBtn.offsetWidth === 0) {{
                        startBtn = btns.find(b => {{
                            var text = (b.innerText || b.textContent || '').trim().toLowerCase();
                            return (text === 'start assignment' || 
                                   text === 'resume assignment' || 
                                   text === 'retake assignment' ||
                                   text === 'try again' ||
                                   text === 'retry') && b.offsetWidth > 0 && b.offsetHeight > 0;
                        }});
                    }}
                    
                    if (startBtn && !startBtn.disabled) {{
                        startBtn.click();
                        return 'CLICKED_START';
                    }}
                    return 'NOT_FOUND';
                }})();
            ";

            _viewModel.StatusText = "🚪 Đang dọn dẹp Popup và mở cửa phòng thi...";
            
            // CHIẾN THUẬT BREACH & CLEAR: 
            bool isFeedback = false;
            bool hasClickedStart = false;
            for (int i = 0; i < 6; i++)
            {
                // Dọn popup (nếu có); watchdog không đụng dialog Start/Submit.
                await DismissAnyGlobalPopupsAsync(maxPasses: 2);
                
                // Bấm Start (nếu chưa bấm)
                if (!hasClickedStart)
                {
                    string startResult = await MainWebView.ExecuteScriptAsync(jsStartQuiz);
                    if (startResult != null && startResult.Contains("CLICKED_FEEDBACK"))
                    {
                        isFeedback = true;
                        break;
                    }
                    else if (startResult != null && startResult.Contains("CLICKED_START"))
                    {
                        hasClickedStart = true;
                    }
                }
                
                await Task.Delay(1000);
            }
            
            if (isFeedback)
            {
                _viewModel.StatusText = "📚 Đang đọc Feedback để học các câu trả lời sai...";
                
                // Đợi 3 giây để trang Feedback (Modal) load xong
                await Task.Delay(3000); 
                
                string jsExtractWrongAnswers = @"
                    (function() {
                        var feedbackList = [];
                        var questions = document.querySelectorAll('div[data-testid^=""part-Submission_""]');
                        questions.forEach(q => {
                            var isIncorrect = q.querySelector('svg[data-testid=""icon-incorrect""]');
                            if (isIncorrect) {
                                var promptEl = q.querySelector('div[id^=""prompt-""]');
                                var questionText = promptEl ? promptEl.innerText.trim() : """";
                                
                                var correctOpts = [];
                                var wrongOpts = [];
                                var isMissing = false;
                                
                                var textLower = q.innerText.toLowerCase();
                                if (textLower.includes(""select all the correct answers"")) {
                                    isMissing = true;
                                }

                                var optionLabels = q.querySelectorAll('label');
                                optionLabels.forEach(label => {
                                    var input = label.querySelector('input');
                                    if (input && input.checked) {
                                        var textEl = label.querySelector('.cds-checkboxAndRadio-labelText');
                                        var optText = textEl ? textEl.innerText.trim() : """";
                                        
                                        var feedbackDiv = label.nextElementSibling;
                                        var isOptCorrect = false;
                                        if (feedbackDiv) {
                                            if (feedbackDiv.querySelector('svg[data-testid=""icon-correct""]')) {
                                                isOptCorrect = true;
                                            } else if (feedbackDiv.innerText.includes('Nice work') || feedbackDiv.innerText.includes('Correct')) {
                                                isOptCorrect = true;
                                            }
                                        }
                                        
                                        if (isOptCorrect || isMissing) {
                                            correctOpts.push(optText);
                                        } else {
                                            wrongOpts.push(optText);
                                        }
                                    }
                                });
                                
                                if (questionText && (wrongOpts.length > 0 || isMissing)) {
                                    feedbackList.push({ 
                                        Question: questionText, 
                                        WrongAnswers: wrongOpts,
                                        CorrectAnswers: correctOpts,
                                        IsMissingAnswers: isMissing
                                    });
                                }
                            }
                        });
                        return JSON.stringify(feedbackList);
                    })();
                ";
                
                string wrongAnswersJson = await MainWebView.ExecuteScriptAsync(jsExtractWrongAnswers);
                if (!string.IsNullOrWhiteSpace(wrongAnswersJson) && wrongAnswersJson != "null")
                {
                    string unescaped = System.Text.RegularExpressions.Regex.Unescape(wrongAnswersJson);
                    if (unescaped.StartsWith("\"") && unescaped.EndsWith("\""))
                    {
                        unescaped = unescaped.Substring(1, unescaped.Length - 2);
                    }
                    
                    try {
                        var feedbackItems = System.Text.Json.JsonSerializer.Deserialize<List<QuizFeedbackDto>>(unescaped);
                        if (feedbackItems != null && feedbackItems.Count > 0)
                        {
                            foreach (var item in feedbackItems)
                            {
                                // Xoá feedback cũ của câu hỏi này (nếu có) để cập nhật feedback mới nhất
                                _quizFeedbackList.RemoveAll(x => x.Question == item.Question);
                                _quizFeedbackList.Add(item);
                            }
                                        _viewModel.StatusText = $"🧠 Đã rút kinh nghiệm từ {feedbackItems.Count} lỗi sai (IsMissing={feedbackItems.Any(x=>x.IsMissingAnswers)}, Correct={feedbackItems.Sum(x=>x.CorrectAnswers.Count)})! Quay lại làm bài...";
                        }
                    } catch {}
                }
                
                _hasExtractedFeedbackThisSession = true;
                
                // Bấm nút Back để thoát khỏi Modal Feedback, trở về trang bìa (Cover Page)
                string jsBackToStart = @"
                    (function() {
                        var btns = Array.from(document.querySelectorAll('button'));
                        
                        var backBtn = document.querySelector('button[aria-label=""Back""]');
                        if (backBtn) { backBtn.click(); return 'CLICKED_BACK'; }
                        
                        var tryAgainBtn = btns.find(b => {
                            var t = (b.innerText || '').trim().toLowerCase();
                            return t === 'try again' || t === 'retry' || t === 'retake assignment';
                        });
                        if (tryAgainBtn) { tryAgainBtn.click(); return 'CLICKED_TRY_AGAIN'; }
                        
                        return 'NOT_FOUND';
                    })();
                ";
                await MainWebView.ExecuteScriptAsync(jsBackToStart);
                await Task.Delay(2000); // Đợi đóng trang Feedback
                
                // Tải lại trang bìa để làm sạch trạng thái React và bắt đầu lại bài thi an toàn
                _isHandlingQuiz = false; // QUAN TRỌNG: Reset cờ trước khi reload, để lần gọi tiếp không bị chặn
                MainWebView.Reload();
                return;
            }
            
            _viewModel.StatusText = "⚙️ Đang ở trong phòng thi! Đang phân tích đề bài...";
        }
        catch { }

        _viewModel.StatusText = "⏳ Đang cuộn trang để ép tải toàn bộ danh sách câu hỏi...";
        
        // Cuộn trang để tải tất cả câu hỏi (Coursera dùng Lazy Loading)
        await MainWebView.ExecuteScriptAsync(@"
            var scrollInterval = setInterval(() => { window.scrollBy(0, 1000); }, 500);
            setTimeout(() => { clearInterval(scrollInterval); window.scrollTo(0, 0); }, 4000);
        ");
        await Task.Delay(4500); // Chờ cuộn xong

        bool questionsLoaded = false;
        for (int i = 0; i < 5; i++)
        {
            string checkJs = @"document.querySelectorAll('div[data-testid^=""part-Submission_""]').length.toString();";
            string countStr = await MainWebView.ExecuteScriptAsync(checkJs);
            if (int.TryParse(countStr?.Replace("\"", ""), out int count) && count > 0)
            {
                questionsLoaded = true;
                break;
            }
            await Task.Delay(1000);
        }

        if (!questionsLoaded)
        {
            _viewModel.StatusText = "⚠️ Lỗi: Không tìm thấy câu hỏi nào xuất hiện (Có thể React bị lỗi trắng trang).";
            return;
        }

        // Tiến hành gom đề bài, gửi qua chuỗi AI fallback và điền đáp án
        await SolveQuizQuestionsAsync();
        
        }
        finally
        {
            _isHandlingQuiz = false;
        }
    }

    private async Task<string> CaptureQuizActivityFingerprintAsync()
    {
        const string script = """
            (function() {
                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                const active = document.querySelector(
                    'a[aria-current="page"], [data-testid^="WeekSingleItemDisplay"] a[aria-current="page"]');
                const heading = document.querySelector('h1, [data-testid="page-header-wrapper"] h2');
                const hasStart = Array.from(document.querySelectorAll('button, a'))
                    .some(button => {
                        const text = normalize(button.innerText || button.textContent).toLowerCase();
                        return text === 'start assignment' || text === 'resume assignment';
                    });
                return [
                    location.pathname,
                    normalize(active?.getAttribute('href')),
                    normalize(active?.innerText),
                    normalize(heading?.innerText),
                    hasStart ? 'start' : 'no-start'
                ].join('|');
            })();
            """;

        try
        {
            return DecodeWebViewString(await MainWebView.ExecuteScriptAsync(script));
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task ResumeQuizAfterNextAsync(
        Uri previousQuizUri,
        string beforeNextFingerprint,
        int advanceGeneration)
    {
        try
        {
            // Coursera may change only DOM (SPA) or may change the URL while
            // the old quiz handler still holds its guard. In either case, wait
            // for a confirmed new Start/Resume page before re-dispatching once.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(500);
                if (advanceGeneration != _quizSpaAdvanceGeneration)
                {
                    return;
                }

                bool urlChanged = !IsSameCourseraDocument(previousQuizUri);

                string currentFingerprint = await CaptureQuizActivityFingerprintAsync();
                bool fingerprintChanged = string.IsNullOrWhiteSpace(beforeNextFingerprint) ||
                    !string.Equals(
                        beforeNextFingerprint,
                        currentFingerprint,
                        StringComparison.Ordinal);
                if (!urlChanged && !fingerprintChanged)
                {
                    continue;
                }

                const string script = """
                    (function() {
                        const visibleEnabled = element => !!element &&
                            element.getClientRects().length > 0 &&
                            !element.disabled &&
                            element.getAttribute('aria-disabled') !== 'true' &&
                            !element.closest('[aria-hidden="true"],[inert]');
                        const buttons = Array.from(document.querySelectorAll('button, a'));
                        const start = buttons.find(button => {
                            const text = String(button.innerText || button.textContent || '')
                                .replace(/\s+/g, ' ').trim().toLowerCase();
                            return (text === 'start assignment' ||
                                text === 'resume assignment') && visibleEnabled(button);
                        });
                        return start
                            ? 'START_READY'
                            : 'WAITING';
                    })();
                    """;
                string state = DecodeWebViewString(
                    await MainWebView.ExecuteScriptAsync(script));
                if (!string.Equals(state, "START_READY", StringComparison.Ordinal))
                {
                    continue;
                }

                _viewModel.StatusText =
                    "➡️ Coursera đã mở bài kiểm tra mới; đang tiếp tục ngay...";
                if (_isHandlingQuiz)
                {
                    // The earlier handler is still unwinding. Keep this
                    // one-shot probe alive rather than losing the new route.
                    continue;
                }
                await HandleQuizAsync();
                return;
            }
        }
        catch
        {
            // The normal NavigationCompleted handler remains the fallback if a
            // transient DOM read fails during an in-place transition.
        }
    }

    private async Task AdvancePastPendingGradedResultAsync()
    {
        // SourceChanged and NavigationCompleted can both arrive while Coursera
        // finishes rendering this status page.  The quiz guard already avoids
        // a second solver, and this guard keeps the navigation itself singular.
        if (_isAdvancingPastPendingGradedResult)
        {
            return;
        }

        _isAdvancingPastPendingGradedResult = true;
        try
        {
            Uri? pendingActivityUri = MainWebView.Source;
            if (!IsCourseraUri(pendingActivityUri))
            {
                _viewModel.StatusText =
                    "⏳ Bài đã nộp đang chờ chấm điểm; trang hiện tại đã thay đổi nên không chạy lại bài.";
                return;
            }

            // Use the same canonical path representation as CheckLessonsAsync.
            // Coursera may retain query/UTM data when opening an item, but the
            // sidebar link uses only its path.
            _pendingGradedResultPaths.Add(pendingActivityUri!.AbsolutePath);
            _courseHasPendingGradedResults = true;
            _viewModel.StatusText =
                "⏳ Bài đã nộp, đang chờ Coursera chấm điểm. Đang chuyển sang bài tiếp theo...";

            // Let a duplicate WebView event settle first.  Never issue a
            // fallback navigation after the learner/app has already moved on.
            await Task.Delay(250);
            if (!IsSameCourseraDocument(pendingActivityUri))
            {
                return;
            }

            string beforeNextFingerprint = await CaptureQuizActivityFingerprintAsync();

            // A submitted assessment may expose a usable Next button before
            // Coursera paints the normal green/completed style.  This is the
            // only flow allowed to use that non-primary Next control; all
            // other lesson handlers keep the normal completion check.
            if (await ClickPendingResultNextAsync(pendingActivityUri))
            {
                int advanceGeneration = ++_quizSpaAdvanceGeneration;
                _ = ResumeQuizAfterNextAsync(
                    pendingActivityUri,
                    beforeNextFingerprint,
                    advanceGeneration);

                // A click can occasionally be swallowed by a transient React
                // render.  Give it a bounded opportunity to navigate; if the
                // same pending document remains, fall through to the scanner
                // instead of clicking Try again or repeatedly clicking Next.
                if (await WaitForAppItemNavigationAsync(
                        pendingActivityUri,
                        TimeSpan.FromSeconds(2)) ||
                    !IsSameCourseraDocument(pendingActivityUri))
                {
                    _viewModel.StatusText =
                        "⏭️ Bài đang chờ chấm điểm đã được bỏ qua; đang mở bài tiếp theo...";
                    return;
                }
            }

            if (!IsSameCourseraDocument(pendingActivityUri))
            {
                return;
            }

            // Not every pending page exposes Next.  Returning to the canonical
            // course home invokes the scanner, which now ignores this exact
            // pending lesson path and selects the next eligible activity.
            if (TryGetCourseHomeUri(pendingActivityUri, out Uri? courseHomeUri) &&
                courseHomeUri != null)
            {
                MainWebView.Source = courseHomeUri;
                _viewModel.StatusText =
                    "⏭️ Bài đang chờ chấm điểm không có nút tiếp theo; đang quét bài kế tiếp...";
                return;
            }

            _viewModel.StatusText =
                "⏳ Bài đã nộp đang chờ chấm điểm. Không xác định được trang khóa học để chuyển tiếp, nên không chạy lại bài.";
        }
        catch (Exception exception)
        {
            _viewModel.StatusText =
                "⏳ Bài đã nộp đang chờ chấm điểm; Worker đã chặn làm lại bài. " + exception.Message;
        }
        finally
        {
            _isAdvancingPastPendingGradedResult = false;
        }
    }

    private async Task<bool> ClickPendingResultNextAsync(Uri pendingActivityUri)
    {
        // Deliberately separate from CheckLessonCompletedAndClickNextAsync:
        // its primary-button requirement is correct for every ordinary lesson.
        // Here we have already positively identified an accepted submission
        // that is only waiting for grading, so a visible enabled Next is safe
        // to use even before Coursera colors it as complete.
        string expectedUrlJson = JsonSerializer.Serialize(pendingActivityUri.ToString());
        string script = $$"""
            (function() {
                const expected = new URL({{expectedUrlJson}});
                const current = new URL(location.href);
                if (current.origin !== expected.origin ||
                    current.pathname !== expected.pathname ||
                    current.search !== expected.search) {
                    return 'STALE_DOCUMENT';
                }

                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                const normalizedBody = normalize(document.body?.innerText);
                const hasPendingResultState =
                    ((/\bsubmitted\b|\bsubmission\b/.test(normalizedBody) &&
                        /\b(?:results?|grades?|grading)\s+(?:are\s+)?pending\b/.test(normalizedBody)) ||
                        /\bawaiting\s+(?:results?|grades?|grading)\b/.test(normalizedBody) ||
                        /\bgrading\s+in\s+progress\b/.test(normalizedBody) ||
                        /đã\s*nộp[\s\S]{0,100}(?:đang\s*)?chờ[\s\S]{0,80}(?:kết\s*quả|chấm\s*điểm)/i.test(normalizedBody));
                if (!hasPendingResultState) {
                    return 'NOT_PENDING';
                }
                const isVisible = element => !!element &&
                    element.getClientRects().length > 0 &&
                    window.getComputedStyle(element).display !== 'none' &&
                    window.getComputedStyle(element).visibility !== 'hidden' &&
                    Number.parseFloat(window.getComputedStyle(element).opacity || '1') > 0 &&
                    !element.closest('[aria-hidden="true"]') &&
                    !element.closest('[inert]');
                const isEnabled = element => !element.matches(':disabled') &&
                    !element.closest('[aria-disabled="true"], [disabled]') &&
                    normalize(element.getAttribute('aria-disabled')) !== 'true';
                const candidates = Array.from(document.querySelectorAll(
                    'button[aria-label="Go to next item"], ' +
                    'a[aria-label="Go to next item"], ' +
                    '[role="button"][aria-label="Go to next item"]'));
                const next = candidates.find(element =>
                    isVisible(element) && isEnabled(element));
                if (!next) {
                    return 'NOT_FOUND';
                }

                next.click();
                return 'CLICKED';
            })();
            """;

        try
        {
            string result = DecodeWebViewString(
                await MainWebView.ExecuteScriptAsync(script));
            return string.Equals(result, "CLICKED", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task SolveOpenEndedQuizAsync()
    {
        _viewModel.StatusText = "✍️ Đang quét câu hỏi tự luận...";

        string jsExtractOpen = @"
            (function() {
                var parts = [];
                var qElements = document.querySelectorAll('div[data-testid^=""part-Submission_""]');
                qElements.forEach((q, index) => {
                    var promptEl = q.querySelector('div[id^=""prompt-""]');
                    var questionText = promptEl ? promptEl.innerText.trim() : '';
                    var ta = q.querySelector('textarea');
                    if (questionText && ta) {
                        parts.push({ Index: index, Question: questionText, TextareaId: ta.id || '' });
                    }
                });
                return JSON.stringify(parts);
            })();
        ";

        string raw = await MainWebView.ExecuteScriptAsync(jsExtractOpen);
        if (string.IsNullOrEmpty(raw) || raw == "null" || raw == "\"[]\"") 
        {
            _viewModel.StatusText = "⚠️ Không đọc được câu hỏi tự luận.";
            return;
        }

        string json;
        try
        {
            raw = raw.Trim('"');
            json = System.Text.RegularExpressions.Regex.Unescape(raw);
            // Nếu json vẫn bị bọc nháy thì bỏ bọc
            if (json.StartsWith("[") == false)
                json = System.Text.RegularExpressions.Regex.Unescape(
                    raw.Substring(1, raw.Length - 2));
        }
        catch { json = raw; }

        List<dynamic> parts;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var arr = doc.RootElement;
            for (int i = 0; i < arr.GetArrayLength(); i++)
            {
                var item = arr[i];
                string questionText = item.GetProperty("Question").GetString();
                string taId = item.GetProperty("TextareaId").GetString();
                int qIndex = item.GetProperty("Index").GetInt32();

                _viewModel.StatusText = $"🤖 Đang hỏi AI câu {i + 1}...";
                AiCompletionResult aiResult = await GetAnswerFromAiAsync(
                    questionText, isDiscussion: true,
                    customSystemPrompt: "You are a student completing a reflective activity. Answer the question thoughtfully in 2-3 sentences in English. Be specific and personal-sounding. Return ONLY the answer text, no preamble.");

                if (!aiResult.Success)
                {
                    await FailCourseJobForAiErrorAsync(aiResult.UserMessage);
                    return;
                }

                string aiAnswer = aiResult.Content.Trim('"').Trim();

                // Inject vào textarea dùng _valueTracker bypass
                string jsInject = $@"
                    (function() {{
                        var ta = document.querySelectorAll('div[data-testid^=""part-Submission_""]')[{qIndex}]?.querySelector('textarea');
                        if (!ta && '{taId}' !== '') ta = document.getElementById('{taId}');
                        if (ta) {{
                            var nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
                            nativeSetter.call(ta, {System.Text.Json.JsonSerializer.Serialize(aiAnswer)});
                            ta.dispatchEvent(new Event('input', {{ bubbles: true }}));
                            ta.dispatchEvent(new Event('change', {{ bubbles: true }}));
                            return 'OK';
                        }}
                        return 'NOT_FOUND';
                    }})();
                ";
                await MainWebView.ExecuteScriptAsync(jsInject);
                await Task.Delay(800);
                _viewModel.StatusText = $"✅ Đã điền câu {i + 1}/{arr.GetArrayLength()}";
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "❌ Lỗi khi điền câu tự luận: " + ex.Message;
            return;
        }

        // Cuộn xuống và Submit
        _viewModel.StatusText = "🚀 Đã điền xong! Đang cuộn xuống và nộp bài...";
        await MainWebView.ExecuteScriptAsync("window.scrollTo(0, document.body.scrollHeight);");
        await Task.Delay(1000);

        string primarySubmitStatus = await WaitForCourseraPrimarySubmitAsync(clickWhenReady: true);
        if (primarySubmitStatus != "CLICKED")
        {
            _viewModel.StatusText =
                $"⚠️ Chưa thể bấm nộp bài tự luận ({DescribeCourseraSubmitStatus(primarySubmitStatus)}). Đáp án vẫn được giữ nguyên.";
            return;
        }
        await Task.Delay(2500);

        string confirmStatus = await ConfirmOwnedCourseraSubmissionAsync();
        if (confirmStatus != "CLICKED")
        {
            _viewModel.StatusText = $"⚠️ Không xác nhận được popup nộp bài ({confirmStatus}).";
            return;
        }

        _viewModel.StatusText = "🏆 Đã nộp bài tự luận! Đang tải lại trang...";
        await Task.Delay(8000);
        _hasExtractedFeedbackThisSession = false;
        MainWebView.Reload();
    }

    private async Task SolveQuizQuestionsAsync()
    {
        _viewModel.StatusText = "🔍 Đang quét toàn bộ đề thi...";

        // Một bài Coursera có thể trộn input chữ, textarea, radio và checkbox.
        // Luôn lấy mọi khối câu hỏi theo đúng thứ tự DOM để không làm lệch số câu.
        string jsExtract = """
            (function() {
                const rootSelector = '[data-testid^="part-Submission_"]';
                const allRoots = Array.from(document.querySelectorAll(rootSelector));
                const qElements = allRoots.filter(q =>
                    !q.parentElement?.closest(rootSelector) &&
                    q.getClientRects().length > 0 &&
                    !q.closest('[aria-hidden="true"]'));
                const textSelector = [
                    'textarea',
                    'input:not([type])',
                    'input[type="text"]',
                    'input[type="search"]',
                    'input[type="email"]',
                    'input[type="url"]',
                    'input[type="tel"]',
                    'input[type="number"]'
                ].join(',');
                const protectedFieldPattern = /(agreement|honou?r|legal[-_\s]*name|full[-_\s]*name|signature)/i;
                const normalizeText = value => (value || '')
                    .replace(/\u00a0/g, ' ')
                    .replace(/\s+/g, ' ')
                    .trim();

                function isSafeTextControl(el) {
                    if (!el || el.disabled || el.readOnly || el.getClientRects().length === 0) return false;
                    if (el.id === 'agreement-checkbox-base') return false;
                    if (el.closest('.monaco-editor, .CodeMirror, [class*="codeEditor"], [class*="code-editor"]')) {
                        return false;
                    }
                    const metadata = [
                        el.id,
                        el.name,
                        el.placeholder,
                        el.getAttribute('aria-label'),
                        el.getAttribute('data-testid')
                    ].filter(Boolean).join(' ');
                    return !protectedFieldPattern.test(metadata) &&
                        !/(editor content|accessibility options|code editor|monaco)/i.test(metadata);
                }

                const questions = qElements.map((q, index) => {
                    const promptEl = q.querySelector('[id^="prompt-"]');
                    const questionText = promptEl ? normalizeText(promptEl.innerText) : '';
                    const choiceInputs = Array.from(
                        q.querySelectorAll('input[type="radio"], input[type="checkbox"]'));
                    const options = choiceInputs.map((input, controlIndex) => {
                        let label = input.labels && input.labels.length > 0 ? input.labels[0] : null;
                        if (!label && input.id) {
                            label = Array.from(q.querySelectorAll('label'))
                                .find(candidate => candidate.htmlFor === input.id) || null;
                        }
                        const textEl = label?.querySelector('.cds-checkboxAndRadio-labelText');
                        const text = normalizeText(textEl?.innerText || label?.innerText ||
                            input.getAttribute('aria-label') || '');
                        return {
                            Text: text,
                            InputId: input.id || '',
                            InputName: input.name || '',
                            InputType: (input.type || '').toLowerCase(),
                            ControlIndex: controlIndex
                        };
                    });

                    const textControls = Array.from(q.querySelectorAll(textSelector))
                        .filter(isSafeTextControl);
                    const textControl = textControls.length > 0 ? textControls[0] : null;
                    let kind = 'unsupported';
                    if (choiceInputs.length > 0) {
                        kind = choiceInputs.every(input => input.type === 'radio')
                            ? 'single_choice'
                            : (choiceInputs.every(input => input.type === 'checkbox')
                                ? 'multi_choice'
                                : 'unsupported');
                    } else if (textControl) {
                        kind = textControl.tagName === 'TEXTAREA' ? 'long_text' : 'short_text';
                    }

                    return {
                        Index: index,
                        PartTestId: q.getAttribute('data-testid') || '',
                        Question: questionText,
                        Kind: kind,
                        TextInputId: textControl?.id || '',
                        TextInputName: textControl?.name || '',
                        TextInputIndex: textControl ? textControls.indexOf(textControl) : -1,
                        Options: options
                    };
                });
                return JSON.stringify(questions);
            })();
            """;

        string rawResult = await MainWebView.ExecuteScriptAsync(jsExtract);
        if (string.IsNullOrEmpty(rawResult) || rawResult == "null" || rawResult == """[]""")
        {
            _viewModel.StatusText = "⚠️ Không tìm thấy câu hỏi nào. Đang thử lại...";
            return;
        }

        // ExecuteScriptAsync JSON-encode giá trị trả về; decode đúng một lớp thay vì Regex.Unescape.
        string json;
        try
        {
            json = System.Text.Json.JsonSerializer.Deserialize<string>(rawResult) ?? string.Empty;
        }
        catch
        {
            json = rawResult;
        }

        List<QuizQuestion>? questionList;
        try
        {
            questionList = System.Text.Json.JsonSerializer.Deserialize<List<QuizQuestion>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            _viewModel.StatusText = "⚠️ Coursera trả cấu trúc câu hỏi không hợp lệ. Đã dừng, chưa nộp bài.";
            return;
        }
        if (questionList == null || questionList.Count == 0)
        {
            _viewModel.StatusText = "⚠️ Lỗi khi đọc câu hỏi.";
            return;
        }

        var unsupportedQuestion = questionList.FirstOrDefault(q =>
            string.IsNullOrWhiteSpace(q.Question) ||
            q.Kind == "unsupported" ||
            ((q.Kind == "single_choice" || q.Kind == "multi_choice") &&
             (q.Options.Count == 0 || q.Options.Any(o => string.IsNullOrWhiteSpace(o.Text)))));
        if (unsupportedQuestion != null)
        {
            _viewModel.StatusText = $"⚠️ Câu {unsupportedQuestion.Index + 1} có kiểu ô trả lời chưa hỗ trợ. Đã dừng để tránh điền lệch.";
            return;
        }

        var ambiguousQuestion = questionList.FirstOrDefault(q =>
            (q.Kind == "single_choice" || q.Kind == "multi_choice") &&
            q.Options.GroupBy(o => NormalizeChoiceText(o.Text)).Any(group => group.Count() > 1));
        if (ambiguousQuestion != null)
        {
            _viewModel.StatusText = $"⚠️ Câu {ambiguousQuestion.Index + 1} có lựa chọn trùng nội dung. Đã dừng để tránh chọn mơ hồ.";
            return;
        }

        _viewModel.StatusText = $"🤖 Đã gom được {questionList.Count} câu hỏi! Đang gửi cho AI...";

        // Xây dựng prompt cho một bài có thể trộn nhiều kiểu ô trả lời.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Solve the following mixed-format quiz questions.");
        
        if (_quizFeedbackList.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("CRITICAL INSTRUCTION: I previously failed this quiz. Use the following feedback to correct your answers:");
            foreach (var f in _quizFeedbackList)
            {
                sb.AppendLine($"- For question '{f.Question}':");
                if (f.WrongAnswers != null && f.WrongAnswers.Count > 0)
                {
                    sb.AppendLine($"  DO NOT pick these incorrect options: {string.Join(", ", f.WrongAnswers.Select(a => $"\"{a}\""))}");
                }
                if (f.IsMissingAnswers)
                {
                    int knownCorrectCount = f.CorrectAnswers?.Count ?? 0;
                    sb.AppendLine($"  You missed some correct options! You MUST pick AT LEAST {knownCorrectCount + 1} options.");
                    if (knownCorrectCount > 0)
                    {
                        sb.AppendLine($"  These options ARE CORRECT and MUST be included in your answer: {string.Join(", ", f.CorrectAnswers!.Select(a => $"\"{a}\""))}");
                    }
                }
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("You MUST return ONLY a raw JSON array of arrays, in exactly the same question order.");
        sb.AppendLine("For SHORT_TEXT, return exactly one concise answer string in its inner array.");
        sb.AppendLine("For LONG_TEXT, return exactly one thoughtful 2-3 sentence English answer in its inner array.");
        sb.AppendLine("For SINGLE_CHOICE, return exactly one stable option token, such as Q1-O2.");
        sb.AppendLine("For MULTI_CHOICE, return every correct stable option token, such as Q4-O1 and Q4-O3.");
        sb.AppendLine("Do NOT return option prose, A/B/C letters, markdown, or explanations.");
        sb.AppendLine("Example: [[\"80\"], [\"GET\"], [\"Q3-O2\"], [\"Q4-O1\", \"Q4-O3\"]]");
        sb.AppendLine();
        foreach (var q in questionList)
        {
            string kindLabel = q.Kind switch
            {
                "short_text" => "SHORT_TEXT",
                "long_text" => "LONG_TEXT",
                "single_choice" => "SINGLE_CHOICE",
                "multi_choice" => "MULTI_CHOICE",
                _ => "UNSUPPORTED"
            };
            sb.AppendLine($"Q{q.Index + 1} [{kindLabel}]: {q.Question}");
            if (q.Kind == "single_choice" || q.Kind == "multi_choice")
            {
                for (var optionIndex = 0; optionIndex < q.Options.Count; optionIndex++)
                {
                    sb.AppendLine($"- Q{q.Index + 1}-O{optionIndex + 1}: {q.Options[optionIndex].Text}");
                }
            }
            sb.AppendLine();
        }

        const string batchQuizSystemPrompt =
            "You solve a batch of mixed short-text, long-text, single-choice, and multiple-choice questions. " +
            "Follow the exact output schema requested by the user. Return only one raw JSON array of arrays " +
            "with one inner array per question. Text questions have exactly one answer string; choice answers " +
            "must use only the stable Qn-Om option tokens provided by the user. Do not use markdown or explanations.";
        QuizAnswerResolution answerResolution = await RequestValidatedQuizAnswersAsync(
            questionList,
            sb.ToString(),
            batchQuizSystemPrompt);
        if (!answerResolution.Success)
        {
            if (answerResolution.IsAiFailure)
            {
                await FailCourseJobForAiErrorAsync(answerResolution.Message);
            }
            else
            {
                _viewModel.StatusText = "⚠️ " + answerResolution.Message + " Chưa điền hoặc nộp bài.";
            }
            return;
        }

        List<List<string>> selectedAnswers = answerResolution.Answers!;

        var finalVerificationPlan = new List<QuizVerificationQuestion>();
        _viewModel.StatusText = $"✅ {answerResolution.ProviderName} đã giải xong ({selectedAnswers.Count} câu)! Đang điền và kiểm tra lại...";

        for (int i = 0; i < selectedAnswers.Count; i++)
        {
            var ansList = selectedAnswers[i];
            var q = questionList[i];

            if (q.Kind == "short_text" || q.Kind == "long_text")
            {
                string answer = ansList[0].Trim();
                string partTestIdJson = System.Text.Json.JsonSerializer.Serialize(q.PartTestId);
                string promptJson = System.Text.Json.JsonSerializer.Serialize(q.Question);
                string inputIdJson = System.Text.Json.JsonSerializer.Serialize(q.TextInputId);
                string answerJson = System.Text.Json.JsonSerializer.Serialize(answer);

                string jsFillText = $$"""
                    (function() {
                        const rootSelector = '[data-testid^="part-Submission_"]';
                        const roots = Array.from(document.querySelectorAll(rootSelector)).filter(q =>
                            !q.parentElement?.closest(rootSelector) && q.getClientRects().length > 0 &&
                            !q.closest('[aria-hidden="true"]'));
                        const expectedPartTestId = {{partTestIdJson}};
                        const expectedPrompt = {{promptJson}};
                        const expectedId = {{inputIdJson}};
                        const expectedValue = {{answerJson}};
                        const normalizeText = value => (value || '').replace(/\u00a0/g, ' ')
                            .replace(/\s+/g, ' ').trim();
                        const promptText = root => normalizeText(
                            root?.querySelector('[id^="prompt-"]')?.innerText);
                        let root = roots[{{q.Index}}] || null;
                        if (!root || promptText(root) !== expectedPrompt) {
                            const partAndPromptMatches = roots.filter(r =>
                                (!expectedPartTestId || r.getAttribute('data-testid') === expectedPartTestId) &&
                                promptText(r) === expectedPrompt);
                            const promptMatches = roots.filter(r => promptText(r) === expectedPrompt);
                            root = partAndPromptMatches.length === 1
                                ? partAndPromptMatches[0]
                                : (promptMatches.length === 1 ? promptMatches[0] : null);
                        }
                        if (!root) return 'QUESTION_NOT_FOUND';
                        const prompt = root.querySelector('[id^="prompt-"]');
                        if (!prompt || normalizeText(prompt.innerText) !== expectedPrompt) return 'QUESTION_CHANGED';

                        const textSelector = 'textarea,input:not([type]),input[type="text"],input[type="search"],input[type="email"],input[type="url"],input[type="tel"],input[type="number"]';
                        const protectedPattern = /(agreement|honou?r|legal[-_\s]*name|full[-_\s]*name|signature)/i;
                        const isSafe = el => {
                            if (!el || el.disabled || el.readOnly || el.getClientRects().length === 0) return false;
                            if (el.id === 'agreement-checkbox-base') return false;
                            if (el.closest('.monaco-editor, .CodeMirror, [class*="codeEditor"], [class*="code-editor"]')) {
                                return false;
                            }
                            const metadata = [el.id, el.name, el.placeholder, el.getAttribute('aria-label'),
                                el.getAttribute('data-testid')].filter(Boolean).join(' ');
                            return !protectedPattern.test(metadata) &&
                                !/(editor content|accessibility options|code editor|monaco)/i.test(metadata);
                        };
                        const controls = Array.from(root.querySelectorAll(textSelector)).filter(isSafe);
                        let el = expectedId ? document.getElementById(expectedId) : null;
                        if (!el || !root.contains(el) || !isSafe(el)) el = controls[{{q.TextInputIndex}}];
                        if (!el || !root.contains(el) || !isSafe(el)) return 'CONTROL_NOT_FOUND';

                        const oldValue = el.value;
                        const proto = el instanceof HTMLTextAreaElement
                            ? HTMLTextAreaElement.prototype
                            : HTMLInputElement.prototype;
                        const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                        if (!setter) return 'UNSUPPORTED_CONTROL';
                        el.focus();
                        setter.call(el, expectedValue);
                        if (el._valueTracker) el._valueTracker.setValue(oldValue);
                        try {
                            el.dispatchEvent(new InputEvent('input', {
                                bubbles: true, composed: true, inputType: 'insertText', data: expectedValue
                            }));
                        } catch {
                            el.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
                        }
                        el.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
                        el.blur();
                        return 'SET';
                    })();
                    """;

                string fillStatus = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(jsFillText));
                if (fillStatus != "SET")
                {
                    _viewModel.StatusText = $"⚠️ Không điền được câu {q.Index + 1} ({fillStatus}). Đã dừng, chưa nộp bài.";
                    return;
                }

                await Task.Delay(300);
                string jsVerifyText = $$"""
                    (function() {
                        const rootSelector = '[data-testid^="part-Submission_"]';
                        const roots = Array.from(document.querySelectorAll(rootSelector)).filter(q =>
                            !q.parentElement?.closest(rootSelector) && q.getClientRects().length > 0 &&
                            !q.closest('[aria-hidden="true"]'));
                        const expectedPartTestId = {{partTestIdJson}};
                        const expectedPrompt = {{promptJson}};
                        const expectedId = {{inputIdJson}};
                        const expectedValue = {{answerJson}};
                        const normalizeText = value => (value || '').replace(/\u00a0/g, ' ')
                            .replace(/\s+/g, ' ').trim();
                        const promptText = root => normalizeText(
                            root?.querySelector('[id^="prompt-"]')?.innerText);
                        let root = roots[{{q.Index}}] || null;
                        if (!root || promptText(root) !== expectedPrompt) {
                            const partAndPromptMatches = roots.filter(r =>
                                (!expectedPartTestId || r.getAttribute('data-testid') === expectedPartTestId) &&
                                promptText(r) === expectedPrompt);
                            const promptMatches = roots.filter(r => promptText(r) === expectedPrompt);
                            root = partAndPromptMatches.length === 1
                                ? partAndPromptMatches[0]
                                : (promptMatches.length === 1 ? promptMatches[0] : null);
                        }
                        if (!root) return 'QUESTION_NOT_FOUND';
                        const prompt = root.querySelector('[id^="prompt-"]');
                        if (!prompt || normalizeText(prompt.innerText) !== expectedPrompt) return 'QUESTION_CHANGED';
                        const selector = 'textarea,input:not([type]),input[type="text"],input[type="search"],input[type="email"],input[type="url"],input[type="tel"],input[type="number"]';
                        const protectedPattern = /(agreement|honou?r|legal[-_\s]*name|full[-_\s]*name|signature)/i;
                        const isSafe = el => {
                            if (!el || el.disabled || el.readOnly || el.getClientRects().length === 0) return false;
                            if (el.id === 'agreement-checkbox-base') return false;
                            if (el.closest('.monaco-editor, .CodeMirror, [class*="codeEditor"], [class*="code-editor"]')) {
                                return false;
                            }
                            const metadata = [el.id, el.name, el.placeholder, el.getAttribute('aria-label'),
                                el.getAttribute('data-testid')].filter(Boolean).join(' ');
                            return !protectedPattern.test(metadata) &&
                                !/(editor content|accessibility options|code editor|monaco)/i.test(metadata);
                        };
                        const controls = Array.from(root.querySelectorAll(selector)).filter(isSafe);
                        let el = expectedId ? document.getElementById(expectedId) : null;
                        if (!el || !root.contains(el) || !isSafe(el)) el = controls[{{q.TextInputIndex}}];
                        if (!el || !root.contains(el) || !isSafe(el)) return 'CONTROL_NOT_FOUND';
                        if (el.value !== expectedValue) return 'VALUE_MISMATCH';
                        if (el.getAttribute('aria-invalid') === 'true') return 'INVALID_VALUE';
                        if (typeof el.checkValidity === 'function' && !el.checkValidity()) return 'INVALID_VALUE';
                        return 'OK';
                    })();
                    """;
                string verifyStatus = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(jsVerifyText));
                if (verifyStatus != "OK")
                {
                    _viewModel.StatusText = $"⚠️ Coursera không giữ giá trị câu {q.Index + 1} ({verifyStatus}). Đã dừng, chưa nộp bài.";
                    return;
                }

                finalVerificationPlan.Add(new QuizVerificationQuestion
                {
                    Index = q.Index,
                    PartTestId = q.PartTestId,
                    Question = q.Question,
                    Kind = q.Kind,
                    TextInputId = q.TextInputId,
                    TextInputName = q.TextInputName,
                    TextInputIndex = q.TextInputIndex,
                    ExpectedText = answer
                });
                _viewModel.StatusText = $"✍️ Q{q.Index + 1}: đã điền và xác nhận ô chữ.";
                await Task.Delay(500);
                continue;
            }

            // Chỉ áp dụng feedback cũ cho câu lựa chọn.
            var feedback = _quizFeedbackList.FirstOrDefault(f =>
            {
                string cleanQ = System.Text.RegularExpressions.Regex.Replace(q.Question, "[^a-zA-Z0-9]", "").ToLowerInvariant();
                string cleanF = System.Text.RegularExpressions.Regex.Replace(f.Question, "[^a-zA-Z0-9]", "").ToLowerInvariant();
                return cleanQ.Length > 0 && cleanF.Length > 0 &&
                       (cleanQ.Contains(cleanF) || cleanF.Contains(cleanQ));
            });

            string feedbackValidationError = string.Empty;
            if (feedback != null && feedback.IsMissingAnswers)
            {
                // Feedback may have punctuation/Unicode differences from the live
                // Coursera option. Resolve it through the same stable-token mapper
                // before it reaches the browser. If it cannot be resolved, ask the
                // AI for this one question again instead of stopping the course.
                var knownTexts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var correctAns in feedback.CorrectAnswers)
                {
                    if (!TryResolveChoiceAnswer(q, correctAns, out string? canonicalCorrect))
                    {
                        feedbackValidationError =
                            $"feedback của Coursera có lựa chọn không map được: {TrimForStatus(correctAns)}";
                        continue;
                    }

                    string normalizedCorrect = NormalizeChoiceText(canonicalCorrect!);
                    knownTexts.Add(normalizedCorrect);
                    if (!ansList.Any(a =>
                            string.Equals(
                                NormalizeChoiceText(a),
                                normalizedCorrect,
                                StringComparison.Ordinal)))
                    {
                        ansList.Add(canonicalCorrect!);
                    }
                }

                foreach (var wrongAns in feedback.WrongAnswers)
                {
                    if (TryResolveChoiceAnswer(q, wrongAns, out string? canonicalWrong))
                    {
                        string normalizedWrong = NormalizeChoiceText(canonicalWrong!);
                        knownTexts.Add(normalizedWrong);
                        // Coursera explicitly marked this option wrong on the
                        // previous attempt.  Do not let a valid-but-known-wrong
                        // answer slip through merely because its token format is
                        // structurally valid.
                        ansList.RemoveAll(a =>
                            string.Equals(
                                NormalizeChoiceText(a),
                                normalizedWrong,
                                StringComparison.Ordinal));
                    }
                }

                bool hasNewAnswer = ansList.Any(a =>
                    !knownTexts.Contains(NormalizeChoiceText(a)));
                if (!hasNewAnswer)
                {
                    var untriedOption = q.Options.FirstOrDefault(opt =>
                        !knownTexts.Contains(NormalizeChoiceText(opt.Text)));
                    if (untriedOption != null)
                    {
                        ansList.Add(untriedOption.Text);
                    }
                }
            }

            // Never pass an unmatched/duplicate choice into the DOM. A malformed
            // answer is repaired against the exact options currently on screen and
            // retried until it is locally valid.
            if (!TryCanonicalizeQuizAnswersForQuestion(
                    q,
                    ansList,
                    out List<string>? canonicalChoiceAnswers,
                    out string choiceValidationError) ||
                !string.IsNullOrEmpty(feedbackValidationError))
            {
                string repairReason = !string.IsNullOrEmpty(feedbackValidationError)
                    ? feedbackValidationError
                    : choiceValidationError;
                QuizAnswerResolution repair = await RepairQuizQuestionAnswerAsync(q, repairReason);
                if (!repair.Success)
                {
                    if (repair.IsAiFailure)
                    {
                        await FailCourseJobForAiErrorAsync(repair.Message);
                    }
                    else
                    {
                        _viewModel.StatusText = "⚠️ " + repair.Message + " Chưa điền hoặc nộp bài.";
                    }
                    return;
                }

                ansList = repair.Answers![0];
                selectedAnswers[i] = ansList;
            }
            else
            {
                ansList = canonicalChoiceAnswers!;
                selectedAnswers[i] = ansList;
            }

            var normalizedAnswers = ansList.Select(NormalizeChoiceText).ToList();

            var matchedOptions = q.Options.Where(opt =>
                normalizedAnswers.Contains(NormalizeChoiceText(opt.Text))).ToList();
            if ((q.Kind == "single_choice" && matchedOptions.Count != 1) ||
                (q.Kind == "multi_choice" && matchedOptions.Count == 0))
            {
                // Defensive fallback for a live DOM change between extraction and
                // filling. Do not choose a potentially wrong option; ask again.
                QuizAnswerResolution repair = await RepairQuizQuestionAnswerAsync(
                    q,
                    $"Coursera đang có {matchedOptions.Count} lựa chọn khớp, không đúng kiểu câu hỏi");
                if (!repair.Success)
                {
                    if (repair.IsAiFailure)
                    {
                        await FailCourseJobForAiErrorAsync(repair.Message);
                    }
                    else
                    {
                        _viewModel.StatusText = "⚠️ " + repair.Message + " Chưa điền hoặc nộp bài.";
                    }
                    return;
                }

                ansList = repair.Answers![0];
                selectedAnswers[i] = ansList;
                normalizedAnswers = ansList.Select(NormalizeChoiceText).ToList();
                matchedOptions = q.Options.Where(opt =>
                    normalizedAnswers.Contains(NormalizeChoiceText(opt.Text))).ToList();
                if ((q.Kind == "single_choice" && matchedOptions.Count != 1) ||
                    (q.Kind == "multi_choice" && matchedOptions.Count == 0))
                {
                    _viewModel.StatusText = $"⚠️ Câu {q.Index + 1} đã thay đổi lựa chọn trên Coursera. Chưa điền hoặc nộp bài.";
                    return;
                }
            }

            _viewModel.StatusText = $"📝 Q{q.Index + 1}: AI chọn {matchedOptions.Count} đáp án: " +
                                    string.Join(", ", matchedOptions.Select(o =>
                                        o.Text.Length > 30 ? o.Text.Substring(0, 30) + "..." : o.Text));

            var optionPlan = q.Options.Select(opt => new QuizVerificationOption
            {
                Text = opt.Text,
                InputId = opt.InputId,
                InputName = opt.InputName,
                InputType = opt.InputType,
                ControlIndex = opt.ControlIndex,
                ShouldBeChecked = matchedOptions.Contains(opt)
            }).ToList();
            string optionPlanJson = System.Text.Json.JsonSerializer.Serialize(optionPlan);
            string choicePartTestIdJson = System.Text.Json.JsonSerializer.Serialize(q.PartTestId);
            string choicePromptJson = System.Text.Json.JsonSerializer.Serialize(q.Question);
            string jsFillChoices = $$"""
                (function() {
                    const rootSelector = '[data-testid^="part-Submission_"]';
                    const expectedPartTestId = {{choicePartTestIdJson}};
                    const expectedPrompt = {{choicePromptJson}};
                    const plan = {{optionPlanJson}};
                    const normalizeText = value => (value || '').replace(/\u00a0/g, ' ')
                        .replace(/\s+/g, ' ').trim();
                    const promptText = root => normalizeText(
                        root?.querySelector('[id^="prompt-"]')?.innerText);
                    const getRoots = () => Array.from(document.querySelectorAll(rootSelector)).filter(q =>
                        !q.parentElement?.closest(rootSelector) && q.getClientRects().length > 0 &&
                        !q.closest('[aria-hidden="true"]'));
                    const resolveRoot = () => {
                        const currentRoots = getRoots();
                        let candidate = currentRoots[{{q.Index}}] || null;
                        if (candidate && promptText(candidate) === expectedPrompt) return candidate;
                        const partAndPromptMatches = currentRoots.filter(r =>
                            (!expectedPartTestId || r.getAttribute('data-testid') === expectedPartTestId) &&
                            promptText(r) === expectedPrompt);
                        const promptMatches = currentRoots.filter(r => promptText(r) === expectedPrompt);
                        return partAndPromptMatches.length === 1
                            ? partAndPromptMatches[0]
                            : (promptMatches.length === 1 ? promptMatches[0] : null);
                    };
                    let root = resolveRoot();
                    if (!root) return 'QUESTION_NOT_FOUND';
                    let prompt = root.querySelector('[id^="prompt-"]');
                    if (!prompt || normalizeText(prompt.innerText) !== expectedPrompt) return 'QUESTION_CHANGED';
                    for (const item of plan) {
                        // React có thể thay toàn bộ question root sau mỗi click; luôn tìm lại.
                        root = resolveRoot();
                        if (!root) return 'QUESTION_NOT_FOUND';
                        const controls = Array.from(
                            root.querySelectorAll('input[type="radio"],input[type="checkbox"]'));
                        let el = item.InputId ? document.getElementById(item.InputId) : null;
                        if (!el || !root.contains(el) || el.type !== item.InputType) {
                            el = controls[item.ControlIndex];
                        }
                        if (!el || !root.contains(el) || el.type !== item.InputType) return 'CONTROL_NOT_FOUND';
                        if (el.disabled) return 'CONTROL_DISABLED';
                        if (el.checked !== item.ShouldBeChecked) el.click();
                    }
                    return 'SET';
                })();
                """;
            string choiceFillStatus = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(jsFillChoices));
            if (choiceFillStatus != "SET")
            {
                _viewModel.StatusText = $"⚠️ Không điền được câu {q.Index + 1} ({choiceFillStatus}). Đã dừng.";
                return;
            }

            await Task.Delay(300);
            string jsVerifyChoices = $$"""
                (function() {
                    const rootSelector = '[data-testid^="part-Submission_"]';
                    const roots = Array.from(document.querySelectorAll(rootSelector)).filter(q =>
                        !q.parentElement?.closest(rootSelector) && q.getClientRects().length > 0 &&
                        !q.closest('[aria-hidden="true"]'));
                    const expectedPartTestId = {{choicePartTestIdJson}};
                    const expectedPrompt = {{choicePromptJson}};
                    const plan = {{optionPlanJson}};
                    const normalizeText = value => (value || '').replace(/\u00a0/g, ' ')
                        .replace(/\s+/g, ' ').trim();
                    const promptText = root => normalizeText(
                        root?.querySelector('[id^="prompt-"]')?.innerText);
                    let root = roots[{{q.Index}}] || null;
                    if (!root || promptText(root) !== expectedPrompt) {
                        const partAndPromptMatches = roots.filter(r =>
                            (!expectedPartTestId || r.getAttribute('data-testid') === expectedPartTestId) &&
                            promptText(r) === expectedPrompt);
                        const promptMatches = roots.filter(r => promptText(r) === expectedPrompt);
                        root = partAndPromptMatches.length === 1
                            ? partAndPromptMatches[0]
                            : (promptMatches.length === 1 ? promptMatches[0] : null);
                    }
                    if (!root) return 'QUESTION_NOT_FOUND';
                    const prompt = root.querySelector('[id^="prompt-"]');
                    if (!prompt || normalizeText(prompt.innerText) !== expectedPrompt) return 'QUESTION_CHANGED';
                    const controls = Array.from(root.querySelectorAll('input[type="radio"],input[type="checkbox"]'));
                    for (const item of plan) {
                        let el = item.InputId ? document.getElementById(item.InputId) : null;
                        if (!el || !root.contains(el) || el.type !== item.InputType) el = controls[item.ControlIndex];
                        if (!el || !root.contains(el)) return 'CONTROL_NOT_FOUND';
                        if (el.checked !== item.ShouldBeChecked) return 'STATE_MISMATCH';
                    }
                    return 'OK';
                })();
                """;
            string choiceVerifyStatus = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(jsVerifyChoices));
            if (choiceVerifyStatus != "OK")
            {
                _viewModel.StatusText = $"⚠️ Coursera không giữ lựa chọn câu {q.Index + 1} ({choiceVerifyStatus}). Đã dừng, chưa nộp bài.";
                return;
            }

            finalVerificationPlan.Add(new QuizVerificationQuestion
            {
                Index = q.Index,
                PartTestId = q.PartTestId,
                Question = q.Question,
                Kind = q.Kind,
                Options = optionPlan
            });
            await Task.Delay(500);
        }

        if (finalVerificationPlan.Count != questionList.Count)
        {
            _viewModel.StatusText = "⚠️ Kế hoạch xác minh cuối bị thiếu câu. Đã dừng, chưa nộp bài.";
            return;
        }

        string finalPlanJson = System.Text.Json.JsonSerializer.Serialize(finalVerificationPlan);
        string jsVerifyAllAnswers = $$"""
            (function() {
                const plan = {{finalPlanJson}};
                const rootSelector = '[data-testid^="part-Submission_"]';
                const roots = Array.from(document.querySelectorAll(rootSelector)).filter(q =>
                    !q.parentElement?.closest(rootSelector) && q.getClientRects().length > 0 &&
                    !q.closest('[aria-hidden="true"]'));
                if (roots.length !== plan.length) return 'ROOT_COUNT_MISMATCH';
                const normalizeText = value => (value || '').replace(/\u00a0/g, ' ')
                    .replace(/\s+/g, ' ').trim();

                const textSelector = 'textarea,input:not([type]),input[type="text"],input[type="search"],input[type="email"],input[type="url"],input[type="tel"],input[type="number"]';
                const protectedPattern = /(agreement|honou?r|legal[-_\s]*name|full[-_\s]*name|signature)/i;
                const isSafeText = el => {
                    if (!el || el.disabled || el.readOnly || el.getClientRects().length === 0) return false;
                    if (el.id === 'agreement-checkbox-base') return false;
                    if (el.closest('.monaco-editor, .CodeMirror, [class*="codeEditor"], [class*="code-editor"]')) {
                        return false;
                    }
                    const metadata = [el.id, el.name, el.placeholder, el.getAttribute('aria-label'),
                        el.getAttribute('data-testid')].filter(Boolean).join(' ');
                    return !protectedPattern.test(metadata) &&
                        !/(editor content|accessibility options|code editor|monaco)/i.test(metadata);
                };

                const promptMatches = (root, item) => {
                    const prompt = root?.querySelector('[id^="prompt-"]');
                    return !!prompt && normalizeText(prompt.innerText) === item.Question;
                };

                function resolveRoot(item) {
                    let root = roots[item.Index] || null;
                    if (root && promptMatches(root, item)) return root;
                    const partAndPromptMatches = roots.filter(r =>
                        (!item.PartTestId || r.getAttribute('data-testid') === item.PartTestId) &&
                        promptMatches(r, item));
                    const promptOnlyMatches = roots.filter(r => promptMatches(r, item));
                    root = partAndPromptMatches.length === 1
                        ? partAndPromptMatches[0]
                        : (promptOnlyMatches.length === 1 ? promptOnlyMatches[0] : null);
                    return root;
                }

                function readOptionText(root, input) {
                    let label = input.labels && input.labels.length > 0 ? input.labels[0] : null;
                    if (!label && input.id) {
                        label = Array.from(root.querySelectorAll('label'))
                            .find(candidate => candidate.htmlFor === input.id) || null;
                    }
                    const textEl = label?.querySelector('.cds-checkboxAndRadio-labelText');
                    return normalizeText(textEl?.innerText || label?.innerText ||
                        input.getAttribute('aria-label') || '');
                }

                for (const item of plan) {
                    const prefix = 'Q' + (item.Index + 1) + ':';
                    const root = resolveRoot(item);
                    if (!root) return prefix + 'QUESTION_NOT_FOUND';
                    const prompt = root.querySelector('[id^="prompt-"]');
                    if (!prompt || normalizeText(prompt.innerText) !== item.Question) {
                        return prefix + 'QUESTION_CHANGED';
                    }

                    if (item.Kind === 'short_text' || item.Kind === 'long_text') {
                        const controls = Array.from(root.querySelectorAll(textSelector)).filter(isSafeText);
                        if (controls.length !== 1) return prefix + 'TEXT_CONTROL_COUNT_MISMATCH';
                        let el = item.TextInputId ? document.getElementById(item.TextInputId) : null;
                        if (!el || !root.contains(el) || !isSafeText(el)) {
                            el = controls[item.TextInputIndex];
                        }
                        if (!el || !root.contains(el) || !isSafeText(el)) {
                            return prefix + 'CONTROL_NOT_FOUND';
                        }
                        if (item.Kind === 'long_text' && el.tagName !== 'TEXTAREA') return prefix + 'CONTROL_CHANGED';
                        if (item.Kind === 'short_text' && el.tagName !== 'INPUT') return prefix + 'CONTROL_CHANGED';
                        if (el.value !== item.ExpectedText) return prefix + 'VALUE_MISMATCH';
                        if (el.getAttribute('aria-invalid') === 'true') return prefix + 'INVALID_VALUE';
                        if (typeof el.checkValidity === 'function' && !el.checkValidity()) {
                            return prefix + 'INVALID_VALUE';
                        }
                        continue;
                    }

                    const controls = Array.from(
                        root.querySelectorAll('input[type="radio"],input[type="checkbox"]'));
                    if (controls.length !== item.Options.length) return prefix + 'CONTROL_COUNT_MISMATCH';
                    let expectedCheckedCount = 0;
                    let actualCheckedCount = 0;
                    for (const state of item.Options) {
                        let el = state.InputId ? document.getElementById(state.InputId) : null;
                        if (!el || !root.contains(el) || el.type !== state.InputType) {
                            el = controls[state.ControlIndex];
                        }
                        if (!el || !root.contains(el) || el.type !== state.InputType) {
                            return prefix + 'CONTROL_NOT_FOUND';
                        }
                        if (el.disabled) return prefix + 'CONTROL_DISABLED';
                        if (readOptionText(root, el) !== normalizeText(state.Text)) {
                            return prefix + 'OPTION_TEXT_CHANGED';
                        }
                        if (state.ShouldBeChecked) expectedCheckedCount++;
                        if (el.checked) actualCheckedCount++;
                        if (el.checked !== state.ShouldBeChecked) return prefix + 'STATE_MISMATCH';
                    }
                    if (item.Kind === 'single_choice' &&
                        (expectedCheckedCount !== 1 || actualCheckedCount !== 1)) {
                        return prefix + 'RADIO_COUNT_MISMATCH';
                    }
                    if (item.Kind === 'multi_choice' &&
                        (expectedCheckedCount < 1 || actualCheckedCount !== expectedCheckedCount)) {
                        return prefix + 'CHECKBOX_COUNT_MISMATCH';
                    }
                }
                return 'OK';
            })();
            """;

        await Task.Delay(500);
        string finalVerifyStatus = DecodeWebViewString(
            await MainWebView.ExecuteScriptAsync(jsVerifyAllAnswers));
        if (finalVerifyStatus != "OK")
        {
            _viewModel.StatusText = $"⚠️ Xác minh cuối thất bại ({finalVerifyStatus}). Đã dừng, chưa nộp bài.";
            return;
        }

        string legalNameStatus = await FillCourseraLegalNameIfRequiredAsync();
        if (legalNameStatus == "PROFILE_NAME_UNAVAILABLE")
        {
            _viewModel.StatusText = "⚠️ Bài yêu cầu tên pháp lý nhưng chưa đọc được tên hồ sơ Coursera. Đã dừng, chưa nộp bài.";
            return;
        }
        if (legalNameStatus == "PROFILE_ACCOUNT_CHANGED")
        {
            _viewModel.StatusText = "⚠️ Tài khoản Coursera hiện tại khác tài khoản đã đọc Profile. Đã dừng, chưa nộp bài.";
            return;
        }
        if (legalNameStatus == "NAME_CONFLICT")
        {
            _viewModel.StatusText = "⚠️ Ô tên pháp lý đang chứa giá trị khác tên Profile. Đã dừng, chưa ghi đè và chưa nộp bài.";
            return;
        }
        if (!IsLegalNameReadyStatus(legalNameStatus))
        {
            _viewModel.StatusText = $"⚠️ Không điền được tên pháp lý ({legalNameStatus}). Đã dừng, chưa nộp bài.";
            return;
        }

        // Điền tên có thể khiến React render lại; xác minh toàn bộ đáp án thêm một lần.
        string postLegalNameVerifyStatus = DecodeWebViewString(
            await MainWebView.ExecuteScriptAsync(jsVerifyAllAnswers));
        if (postLegalNameVerifyStatus != "OK")
        {
            _viewModel.StatusText = $"⚠️ Đáp án thay đổi sau khi điền tên ({postLegalNameVerifyStatus}). Đã dừng, chưa nộp bài.";
            return;
        }

        string? dryRunValue = Environment.GetEnvironmentVariable("AUTOMATION_DRY_RUN");
        bool isDryRun = dryRunValue == "1" ||
                        string.Equals(dryRunValue, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dryRunValue, "yes", StringComparison.OrdinalIgnoreCase);
        if (isDryRun)
        {
            _viewModel.StatusText = $"🧪 DRY RUN: Đã điền tên và xác minh đủ {questionList.Count} câu; không nộp bài.";
            return;
        }

        _viewModel.StatusText = "🚀 Đang cuộn xuống cuối trang để nộp bài...";
        
        // Cuộn xuống cuối trang để nút Submit và Honor Code hiện ra
        await MainWebView.ExecuteScriptAsync("window.scrollTo(0, document.body.scrollHeight);");
        await Task.Delay(1000);

        string scrolledLegalNameStatus = await FillCourseraLegalNameIfRequiredAsync();
        if (!IsLegalNameReadyStatus(scrolledLegalNameStatus))
        {
            _viewModel.StatusText = $"⚠️ Tên pháp lý không còn hợp lệ ({scrolledLegalNameStatus}). Đã dừng, chưa nộp bài.";
            return;
        }
        
        // Coursera can momentarily render duplicate/disabled Submit controls.
        // Poll the one visible control until React has applied the Honor Code
        // state; do not confuse a hidden copy with the live quiz button.
        string prepareSubmitStatus = await WaitForCourseraPrimarySubmitAsync(clickWhenReady: false);
        if (prepareSubmitStatus != "READY")
        {
            _viewModel.StatusText =
                $"⚠️ Không chuẩn bị được nút nộp ({DescribeCourseraSubmitStatus(prepareSubmitStatus)}). Chưa nộp bài.";
            return;
        }

        await Task.Delay(250);
        string secondVerifyStatus = DecodeWebViewString(
            await MainWebView.ExecuteScriptAsync(jsVerifyAllAnswers));
        if (secondVerifyStatus != "OK")
        {
            _viewModel.StatusText = $"⚠️ Đáp án thay đổi trước khi nộp ({secondVerifyStatus}). Đã dừng, chưa nộp bài.";
            return;
        }

        // Kiểm tra đúng ID tài khoản + đúng tên ngay sát thời điểm bấm Submit.
        string immediateLegalNameStatus = await FillCourseraLegalNameIfRequiredAsync();
        if (!IsLegalNameReadyStatus(immediateLegalNameStatus))
        {
            _viewModel.StatusText = $"⚠️ Xác minh tên ngay trước khi nộp thất bại ({immediateLegalNameStatus}). Đã dừng, chưa nộp bài.";
            return;
        }
        if (immediateLegalNameStatus == "SET_AND_VERIFIED")
        {
            string afterLateNameFillVerifyStatus = DecodeWebViewString(
                await MainWebView.ExecuteScriptAsync(jsVerifyAllAnswers));
            if (afterLateNameFillVerifyStatus != "OK")
            {
                _viewModel.StatusText = $"⚠️ Đáp án đổi sau khi khôi phục tên ({afterLateNameFillVerifyStatus}). Đã dừng, chưa nộp bài.";
                return;
            }

            immediateLegalNameStatus = await FillCourseraLegalNameIfRequiredAsync();
            if (immediateLegalNameStatus != "NOT_REQUIRED" &&
                immediateLegalNameStatus != "ALREADY_SET")
            {
                _viewModel.StatusText = $"⚠️ Ô tên không ổn định ({immediateLegalNameStatus}). Đã dừng, chưa nộp bài.";
                return;
            }
        }

        // Re-query immediately before the click because the verification/name
        // checks above can cause Coursera to re-render its submit region.
        string primarySubmitStatus = await WaitForCourseraPrimarySubmitAsync(clickWhenReady: true);
        if (primarySubmitStatus != "CLICKED")
        {
            _viewModel.StatusText =
                $"⚠️ Chưa thể bấm nộp ({DescribeCourseraSubmitStatus(primarySubmitStatus)}). Đáp án vẫn được giữ nguyên.";
            return;
        }

        // Đợi Popup ""Ready to submit?"" hiện lên rõ ràng.
        await Task.Delay(2000);

        // Submit lần 2: chỉ nhận đúng dialog xác nhận nộp do luồng này sở hữu.
        string confirmSubmitStatus = await ConfirmOwnedCourseraSubmissionAsync();
        if (confirmSubmitStatus != "CLICKED")
        {
            _viewModel.StatusText = $"⚠️ Không tìm thấy hộp xác nhận nộp ({confirmSubmitStatus}).";
            return;
        }
        
        _viewModel.StatusText = "🏆 Đã nộp bài Quiz! Đang chờ Coursera chấm điểm...";
        
        // Đợi 6 giây để hệ thống chấm điểm và lưu kết quả vào server
        await Task.Delay(8000);
        
        // Reset lại biến cờ để nếu rớt lần này, hệ thống sẽ được phép click View Feedback đọc Sổ Đen lại
        _hasExtractedFeedbackThisSession = false;
        
        _viewModel.StatusText = "🔄 Đã nộp bài! Đang tải lại trang để kiểm tra kết quả (Pass/Fail)...";
        MainWebView.Reload();
    }

    private static bool IsCourseraUri(Uri? uri)
    {
        if (uri == null)
        {
            return false;
        }

        return uri.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".coursera.org", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSameCourseraDocument(Uri? expectedUri)
    {
        Uri? currentUri = MainWebView.Source;
        return IsCourseraUri(expectedUri) &&
               IsCourseraUri(currentUri) &&
               Uri.Compare(
                   expectedUri!,
                   currentUri!,
                   UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                   UriFormat.SafeUnescaped,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool IsCourseraLoginUri(Uri? uri)
    {
        if (!IsCourseraUri(uri))
        {
            return false;
        }

        string value = uri!.ToString();
        return value.Contains("authMode=login", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase);
    }

    private void ResetCourseraProfileBootstrap()
    {
        _courseraProfileBootstrapState = CourseraProfileBootstrapState.Idle;
        _courseraProfileReturnUri = null;
        _courseraProfileExpectedNavigationId = null;
        _courseraProfilePendingNavigationUri = null;
        _courseraProfileAcceptLoginContinuation = false;
    }

    private void NavigateCourseraBootstrap(Uri uri)
    {
        _courseraProfileExpectedNavigationId = null;
        _courseraProfilePendingNavigationUri = uri;
        _courseraProfileAcceptLoginContinuation = false;

        if (MainWebView.Source != null &&
            Uri.Compare(
                MainWebView.Source,
                uri,
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            MainWebView.CoreWebView2.Reload();
        }
        else
        {
            MainWebView.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
    }

    private static bool IsCourseraProfileUri(Uri? uri)
    {
        return IsCourseraUri(uri) &&
               (uri!.AbsolutePath.Equals("/account-settings", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Equals("/account-settings/", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> EnsureCourseraIdentityAsync(
        int maxAttempts = 8,
        bool forceRefresh = false,
        int? expectedGeneration = null)
    {
        if (!forceRefresh && _courseraIdentity != null)
        {
            return true;
        }

        if (!IsCourseraUri(MainWebView.Source))
        {
            return false;
        }

        await _courseraProfileNameLock.WaitAsync();
        try
        {
            if (expectedGeneration.HasValue &&
                expectedGeneration.Value != _courseraProfileBootstrapGeneration)
            {
                return false;
            }

            if (!forceRefresh && _courseraIdentity != null)
            {
                return true;
            }

            const string jsReadProfileIdentity = """
                (function() {
                    const normalize = value => String(value || '')
                        .normalize('NFC')
                        .replace(/\u00a0/g, ' ')
                        .replace(/\s+/g, ' ')
                        .trim();
                    const user = window.coursera && window.coursera.user
                        ? window.coursera.user
                        : {};
                    const userId = normalize(user.id);
                    let fullName = normalize(user.full_name || user.fullName);

                    if (!fullName) {
                        const headerProfile = document.querySelector(
                            'button[data-e2e="header-profile"]');
                        const ariaLabel = normalize(headerProfile?.getAttribute('aria-label'));
                        const prefix = 'User dropdown menu for ';
                        if (ariaLabel.toLocaleLowerCase().startsWith(prefix.toLocaleLowerCase())) {
                            fullName = normalize(ariaLabel.slice(prefix.length));
                        }
                    }

                    if (!fullName && /\/account-settings\/?$/i.test(location.pathname)) {
                        const fieldText = el => {
                            const labels = Array.from(el.labels || []).map(label => label.innerText);
                            const described = (el.getAttribute('aria-describedby') || '')
                                .split(/\s+/)
                                .filter(Boolean)
                                .map(id => document.getElementById(id)?.innerText || '');
                            return normalize([
                                el.name, el.id, el.placeholder, el.getAttribute('aria-label'),
                                ...labels, ...described
                            ].filter(Boolean).join(' '));
                        };
                        const fullNameFields = Array.from(document.querySelectorAll(
                            'input:not([type]),input[type="text"]'))
                            .filter(el => el.getClientRects().length > 0)
                            .filter(el => /(^|\s)full\s*name(\s|$)/i.test(fieldText(el)));
                        if (fullNameFields.length === 1) {
                            fullName = normalize(fullNameFields[0].value);
                        }
                    }

                    return JSON.stringify({ userId, fullName });
                })();
                """;

            for (int attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
            {
                try
                {
                    string rawIdentity = await MainWebView.ExecuteScriptAsync(jsReadProfileIdentity);
                    if (expectedGeneration.HasValue &&
                        expectedGeneration.Value != _courseraProfileBootstrapGeneration)
                    {
                        return false;
                    }

                    using JsonDocument identityDocument = JsonDocument.Parse(
                        DecodeWebViewString(rawIdentity));
                    JsonElement root = identityDocument.RootElement;
                    string userId = root.TryGetProperty("userId", out JsonElement userIdElement)
                        ? userIdElement.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    string? fullName = root.TryGetProperty("fullName", out JsonElement nameElement)
                        ? TryNormalizeCourseraProfileName(nameElement.GetString())
                        : null;

                    bool validUserId = userId.Length is >= 1 and <= 200 &&
                                       !userId.Any(char.IsControl);
                    if (validUserId && !string.IsNullOrWhiteSpace(fullName))
                    {
                        _courseraIdentity = new CourseraIdentity(userId, fullName);
                        if (_workerLaunchOptions.Enabled && _centralWorkerClient.CurrentJob != null)
                        {
                            try
                            {
                                await _centralWorkerClient.ReportIdentityAsync(userId, fullName);
                            }
                            catch
                            {
                                // Nhận diện tại chỗ vẫn hợp lệ; lần heartbeat sau sẽ giữ worker online.
                            }
                        }
                        return true;
                    }
                }
                catch
                {
                    // React có thể đang thay document; thử lại sau khi profile render xong.
                }

                if (attempt + 1 < maxAttempts)
                {
                    await Task.Delay(500);
                    if (expectedGeneration.HasValue &&
                        expectedGeneration.Value != _courseraProfileBootstrapGeneration)
                    {
                        return false;
                    }
                }
            }

            return false;
        }
        finally
        {
            _courseraProfileNameLock.Release();
        }
    }

    private async Task<bool> HandleCourseraProfileBootstrapCompletedAsync(
        bool navigationSucceeded)
    {
        CourseraProfileBootstrapState state = _courseraProfileBootstrapState;
        if (state == CourseraProfileBootstrapState.Idle)
        {
            return false;
        }

        int generation = _courseraProfileBootstrapGeneration;
        Uri? currentUri = MainWebView.Source;

        if (!navigationSucceeded)
        {
            if (state == CourseraProfileBootstrapState.LoadingProfile &&
                _courseraProfileReturnUri != null)
            {
                Uri returnUri = _courseraProfileReturnUri;
                _courseraProfileBootstrapState = CourseraProfileBootstrapState.ReturningTarget;
                _viewModel.StatusText = "⚠️ Không mở được Profile Coursera; đang quay lại bài.";
                NavigateCourseraBootstrap(returnUri);
                return true;
            }

            ResetCourseraProfileBootstrap();
            return false;
        }

        if (!IsCourseraUri(currentUri))
        {
            ResetCourseraProfileBootstrap();
            return false;
        }

        if (IsCourseraLoginUri(currentUri))
        {
            _courseraProfileExpectedNavigationId = null;
            _courseraProfilePendingNavigationUri = null;
            _courseraProfileAcceptLoginContinuation = true;
            _viewModel.StatusText = "🔐 Hãy đăng nhập Coursera; tool sẽ tự tiếp tục sau khi đăng nhập xong.";
            return true;
        }

        if (state == CourseraProfileBootstrapState.AwaitingTarget)
        {
            if (IsCourseraProfileUri(currentUri))
            {
                await EnsureCourseraIdentityAsync(
                    forceRefresh: true,
                    expectedGeneration: generation);
                if (generation != _courseraProfileBootstrapGeneration)
                {
                    return true;
                }

                ResetCourseraProfileBootstrap();
                return false;
            }

            _courseraProfileReturnUri = currentUri;
            _courseraProfileBootstrapState = CourseraProfileBootstrapState.LoadingProfile;
            _viewModel.StatusText = "👤 Đã vào link thành công; đang đọc tên từ Profile Coursera...";
            NavigateCourseraBootstrap(new Uri("https://www.coursera.org/account-settings"));
            return true;
        }

        if (state == CourseraProfileBootstrapState.LoadingProfile)
        {
            bool isProfilePage = IsCourseraProfileUri(currentUri);
            bool identityLoaded = isProfilePage && await EnsureCourseraIdentityAsync(
                forceRefresh: true,
                expectedGeneration: generation);
            if (generation != _courseraProfileBootstrapGeneration ||
                _courseraProfileBootstrapState != CourseraProfileBootstrapState.LoadingProfile)
            {
                return true;
            }

            Uri? returnUri = _courseraProfileReturnUri;
            if (returnUri == null)
            {
                ResetCourseraProfileBootstrap();
                return false;
            }

            _courseraProfileBootstrapState = CourseraProfileBootstrapState.ReturningTarget;
            _viewModel.StatusText = identityLoaded
                ? "✅ Đã đọc Profile Coursera; đang quay lại bài..."
                : "⚠️ Chưa đọc được tên từ Profile Coursera; đang quay lại bài.";
            NavigateCourseraBootstrap(returnUri);
            return true;
        }

        // Trang đích đã tải lại sau vòng target -> profile -> target.
        ResetCourseraProfileBootstrap();
        return false;
    }

    private static string? TryNormalizeCourseraProfileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = System.Text.RegularExpressions.Regex.Replace(
            value.Replace('\u00a0', ' '), @"\s+", " ")
            .Trim()
            .Normalize(NormalizationForm.FormC);
        if (normalized.Length < 2 || normalized.Length > 120 || normalized.Contains('@') ||
            normalized.Any(char.IsControl) || !normalized.Any(char.IsLetter))
        {
            return null;
        }

        string[] forbiddenNames =
        {
            "profile", "account", "user", "settings", "my profile", "view profile",
            "sign out", "log out"
        };
        return forbiddenNames.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private async Task<string> FillCourseraLegalNameIfRequiredAsync()
    {
        CourseraIdentity? identity = _courseraIdentity;
        string fullNameJson = JsonSerializer.Serialize(identity?.FullName ?? string.Empty);
        string userIdJson = JsonSerializer.Serialize(identity?.UserId ?? string.Empty);

        string jsFillLegalName = $$"""
            (function() {
                const expectedName = {{fullNameJson}};
                const expectedUserId = {{userIdJson}};
                const canonical = value => String(value || '')
                    .normalize('NFC')
                    .replace(/\u00a0/g, ' ')
                    .replace(/\s+/g, ' ')
                    .trim();
                const protectedPattern = /(legal[-_\s]*name|government\s+issued|signature)/i;
                const fieldText = el => {
                    let label = el.labels && el.labels.length > 0 ? el.labels[0] : null;
                    if (!label && el.id) {
                        label = Array.from(document.querySelectorAll('label'))
                            .find(candidate => candidate.htmlFor === el.id) || null;
                    }
                    const describedText = (el.getAttribute('aria-describedby') || '')
                        .split(/\s+/)
                        .filter(Boolean)
                        .map(id => document.getElementById(id)?.innerText || '')
                        .join(' ');
                    return [
                        el.id, el.name, el.placeholder,
                        el.getAttribute('aria-label'), el.getAttribute('data-testid'),
                        label?.innerText, describedText
                    ].filter(Boolean).join(' ');
                };
                const fields = Array.from(document.querySelectorAll(
                    'input:not([type]),input[type="text"],input[type="search"],textarea'))
                    .filter(el => el.getClientRects().length > 0 && protectedPattern.test(fieldText(el)));
                if (fields.length === 0) return 'NOT_REQUIRED';
                if (fields.length !== 1) return 'CONTROL_AMBIGUOUS';
                if (!expectedName || !expectedUserId) return 'PROFILE_NAME_UNAVAILABLE';

                const currentUserId = canonical(
                    window.coursera && window.coursera.user
                        ? window.coursera.user.id
                        : '');
                if (!currentUserId || currentUserId !== canonical(expectedUserId)) {
                    return 'PROFILE_ACCOUNT_CHANGED';
                }

                const el = fields[0];
                if (el.disabled || el.readOnly) return 'CONTROL_BLOCKED';
                if (canonical(el.value)) {
                    if (canonical(el.value) !== canonical(expectedName)) return 'NAME_CONFLICT';
                    if (el.getAttribute('aria-invalid') === 'true') return 'INVALID_VALUE';
                    if (typeof el.checkValidity === 'function' && !el.checkValidity()) return 'INVALID_VALUE';
                    return 'ALREADY_SET';
                }

                const oldValue = el.value;
                const proto = el instanceof HTMLTextAreaElement
                    ? HTMLTextAreaElement.prototype
                    : HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                if (!setter) return 'UNSUPPORTED_CONTROL';
                el.focus();
                setter.call(el, expectedName);
                if (el._valueTracker) el._valueTracker.setValue(oldValue);
                try {
                    el.dispatchEvent(new InputEvent('input', {
                        bubbles: true, composed: true, inputType: 'insertText', data: expectedName
                    }));
                } catch {
                    el.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
                }
                el.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
                el.blur();
                return 'SET';
            })();
            """;

        string fillStatus = DecodeWebViewString(
            await MainWebView.ExecuteScriptAsync(jsFillLegalName));
        if (fillStatus != "SET")
        {
            return fillStatus;
        }

        await Task.Delay(400);
        string jsVerifyLegalName = $$"""
            (function() {
                const expectedName = {{fullNameJson}};
                const expectedUserId = {{userIdJson}};
                const canonical = value => String(value || '')
                    .normalize('NFC')
                    .replace(/\u00a0/g, ' ')
                    .replace(/\s+/g, ' ')
                    .trim();
                const protectedPattern = /(legal[-_\s]*name|government\s+issued|signature)/i;
                const fieldText = el => {
                    let label = el.labels && el.labels.length > 0 ? el.labels[0] : null;
                    if (!label && el.id) {
                        label = Array.from(document.querySelectorAll('label'))
                            .find(candidate => candidate.htmlFor === el.id) || null;
                    }
                    const describedText = (el.getAttribute('aria-describedby') || '')
                        .split(/\s+/)
                        .filter(Boolean)
                        .map(id => document.getElementById(id)?.innerText || '')
                        .join(' ');
                    return [
                        el.id, el.name, el.placeholder,
                        el.getAttribute('aria-label'), el.getAttribute('data-testid'),
                        label?.innerText, describedText
                    ].filter(Boolean).join(' ');
                };
                const fields = Array.from(document.querySelectorAll(
                    'input:not([type]),input[type="text"],input[type="search"],textarea'))
                    .filter(el => el.getClientRects().length > 0 && protectedPattern.test(fieldText(el)));
                if (fields.length !== 1) return 'CONTROL_CHANGED';
                const currentUserId = canonical(
                    window.coursera && window.coursera.user
                        ? window.coursera.user.id
                        : '');
                if (!currentUserId || currentUserId !== canonical(expectedUserId)) {
                    return 'PROFILE_ACCOUNT_CHANGED';
                }
                const el = fields[0];
                if (canonical(el.value) !== canonical(expectedName)) return 'VALUE_MISMATCH';
                if (el.getAttribute('aria-invalid') === 'true') return 'INVALID_VALUE';
                if (typeof el.checkValidity === 'function' && !el.checkValidity()) return 'INVALID_VALUE';
                return 'OK';
            })();
            """;
        string verifyStatus = DecodeWebViewString(
            await MainWebView.ExecuteScriptAsync(jsVerifyLegalName));
        return verifyStatus == "OK" ? "SET_AND_VERIFIED" : verifyStatus;
    }

    private static bool IsLegalNameReadyStatus(string status)
    {
        return status == "NOT_REQUIRED" ||
               status == "ALREADY_SET" ||
               status == "SET_AND_VERIFIED";
    }

    private async Task<QuizAnswerResolution> RequestValidatedQuizAnswersAsync(
        IReadOnlyList<QuizQuestion> questions,
        string batchPrompt,
        string batchSystemPrompt)
    {
        List<List<string>>? rawAnswers = null;
        string providerName = "AgentRouter";
        string validationError = string.Empty;

        // A malformed batch has no safe question-to-answer alignment, so ask for
        // the complete matrix again before touching any Coursera input.
        for (var batchAttempt = 1; ; batchAttempt++)
        {
            if (batchAttempt > 1)
            {
                _viewModel.StatusText =
                    $"🤖 AI trả sai cấu trúc; đang yêu cầu lại toàn bộ đáp án (lần {batchAttempt})...";
            }

            AiCompletionResult aiResult = await GetAnswerFromAiAsync(
                batchAttempt == 1
                    ? batchPrompt
                    : BuildQuizBatchRepairPrompt(batchPrompt, validationError),
                isDiscussion: false,
                customSystemPrompt: batchSystemPrompt);
            if (!aiResult.Success)
            {
                return QuizAnswerResolution.AiFailed(aiResult.UserMessage);
            }

            providerName = aiResult.ProviderName;
            if (TryDeserializeQuizAnswerMatrix(
                    aiResult.Content,
                    questions.Count,
                    out rawAnswers,
                    out validationError))
            {
                break;
            }

            await Task.Delay(300);
        }

        // The loop only exits after a successful deserialize; retain a defensive
        // guard so a future control-flow change cannot dereference a null matrix.
        if (rawAnswers == null)
        {
            return QuizAnswerResolution.ValidationFailed(
                "Không nhận được ma trận đáp án từ AI");
        }

        var canonicalAnswers = new List<List<string>>(questions.Count);
        for (var questionPosition = 0; questionPosition < questions.Count; questionPosition++)
        {
            QuizQuestion question = questions[questionPosition];
            if (TryCanonicalizeQuizAnswersForQuestion(
                    question,
                    rawAnswers[questionPosition],
                    out List<string>? canonical,
                    out validationError))
            {
                canonicalAnswers.Add(canonical!);
                continue;
            }

            // The surrounding matrix is aligned, so only repair the bad question.
            // This preserves every already valid answer and retries until a token
            // can be mapped unambiguously to one visible Coursera option.
            QuizAnswerResolution repair = await RepairQuizQuestionAnswerAsync(
                question,
                validationError);
            if (!repair.Success)
            {
                return repair;
            }

            canonicalAnswers.Add(repair.Answers![0]);
            providerName = repair.ProviderName;
        }

        return QuizAnswerResolution.Completed(canonicalAnswers, providerName);
    }

    private async Task<QuizAnswerResolution> RepairQuizQuestionAnswerAsync(
        QuizQuestion question,
        string initialValidationError)
    {
        string validationError = initialValidationError;
        string providerName = "AgentRouter";
        const string repairSystemPrompt =
            "You repair one quiz answer format. Return only one raw JSON array of strings. " +
            "For a choice question, use only the exact Qn-Om option token supplied by the user. " +
            "Never return explanations, markdown, or option prose.";

        for (var repairAttempt = 1; ; repairAttempt++)
        {
            _viewModel.StatusText =
                $"🤖 AI trả lời chưa khớp ở câu {question.Index + 1}; đang hỏi lại (lần {repairAttempt})...";

            AiCompletionResult aiResult = await GetAnswerFromAiAsync(
                BuildQuizQuestionRepairPrompt(question, validationError),
                isDiscussion: false,
                customSystemPrompt: repairSystemPrompt);
            if (!aiResult.Success)
            {
                return QuizAnswerResolution.AiFailed(aiResult.UserMessage);
            }

            providerName = aiResult.ProviderName;
            if (TryDeserializeSingleQuizAnswer(aiResult.Content, out List<string>? repairedAnswers) &&
                TryCanonicalizeQuizAnswersForQuestion(
                    question,
                    repairedAnswers!,
                    out List<string>? canonical,
                    out validationError))
            {
                return QuizAnswerResolution.Completed(
                    new List<List<string>> { canonical! },
                    providerName);
            }

            await Task.Delay(300);
        }
    }

    private static string BuildQuizBatchRepairPrompt(string batchPrompt, string validationError) =>
        $"""
        {batchPrompt}

        FORMAT REPAIR REQUIRED: The previous answer was rejected locally because: {validationError}
        Return the complete raw JSON array again, with exactly one inner array for every question.
        For every choice question, return only the supplied Qn-Om tokens. Do not return option text or A/B/C letters.
        """;

    private static string BuildQuizQuestionRepairPrompt(
        QuizQuestion question,
        string validationError)
    {
        var prompt = new StringBuilder();
        string kindLabel = question.Kind switch
        {
            "short_text" => "SHORT_TEXT",
            "long_text" => "LONG_TEXT",
            "single_choice" => "SINGLE_CHOICE",
            "multi_choice" => "MULTI_CHOICE",
            _ => "UNSUPPORTED"
        };

        prompt.AppendLine("Repair the answer for exactly one quiz question.");
        prompt.AppendLine($"Q{question.Index + 1} [{kindLabel}]: {question.Question}");
        if (question.Kind is "single_choice" or "multi_choice")
        {
            for (var optionIndex = 0; optionIndex < question.Options.Count; optionIndex++)
            {
                prompt.AppendLine(
                    $"- Q{question.Index + 1}-O{optionIndex + 1}: {question.Options[optionIndex].Text}");
            }
        }

        prompt.AppendLine($"The previous answer was invalid: {validationError}");
        prompt.AppendLine("Return ONLY one raw JSON array of strings.");
        prompt.AppendLine(question.Kind == "single_choice"
            ? $"Example: [\"Q{question.Index + 1}-O2\"]"
            : question.Kind == "multi_choice"
                ? $"Example: [\"Q{question.Index + 1}-O1\", \"Q{question.Index + 1}-O3\"]"
                : "Example: [\"one concise answer\"]");
        return prompt.ToString();
    }

    private static bool TryDeserializeQuizAnswerMatrix(
        string response,
        int expectedQuestionCount,
        out List<List<string>>? answers,
        out string validationError)
    {
        answers = null;
        validationError = string.Empty;
        string cleanedResponse = CleanAiJsonResponse(response);
        try
        {
            List<List<string>>? parsed = JsonSerializer.Deserialize<List<List<string>>>(cleanedResponse);
            if (parsed == null)
            {
                validationError = "AI trả JSON null";
                return false;
            }

            if (parsed.Count != expectedQuestionCount)
            {
                validationError = $"AI trả {parsed.Count}/{expectedQuestionCount} nhóm đáp án";
                return false;
            }

            answers = parsed.Select(answerList =>
                    answerList?.Select(answer => answer?.Trim() ?? string.Empty).ToList()
                    ?? new List<string>())
                .ToList();
            return true;
        }
        catch (JsonException)
        {
            validationError = "AI không trả về raw JSON array hợp lệ";
            return false;
        }
    }

    private static bool TryDeserializeSingleQuizAnswer(
        string response,
        out List<string>? answers)
    {
        answers = null;
        try
        {
            List<string>? parsed = JsonSerializer.Deserialize<List<string>>(
                CleanAiJsonResponse(response));
            if (parsed == null)
            {
                return false;
            }

            answers = parsed.Select(answer => answer?.Trim() ?? string.Empty).ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CleanAiJsonResponse(string response) =>
        (response ?? string.Empty)
            .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static bool TryCanonicalizeQuizAnswersForQuestion(
        QuizQuestion question,
        IReadOnlyList<string> answers,
        out List<string>? canonicalAnswers,
        out string validationError)
    {
        canonicalAnswers = null;
        validationError = string.Empty;
        if (answers.Count == 0 || answers.Any(string.IsNullOrWhiteSpace))
        {
            validationError = $"câu {question.Index + 1} có đáp án trống";
            return false;
        }

        if (question.Kind is "short_text" or "long_text")
        {
            if (answers.Count != 1)
            {
                validationError = $"câu điền chữ {question.Index + 1} cần đúng 1 đáp án";
                return false;
            }

            canonicalAnswers = new List<string> { answers[0].Trim() };
            return true;
        }

        if (question.Kind == "single_choice" && answers.Count != 1)
        {
            validationError = $"câu radio {question.Index + 1} cần đúng 1 mã lựa chọn";
            return false;
        }

        if (question.Kind is not ("single_choice" or "multi_choice"))
        {
            validationError = $"câu {question.Index + 1} có kiểu trả lời chưa hỗ trợ";
            return false;
        }

        var canonical = new List<string>(answers.Count);
        foreach (string answer in answers)
        {
            if (!TryResolveChoiceAnswer(question, answer, out string? canonicalOption))
            {
                validationError =
                    $"mã/lựa chọn \"{TrimForStatus(answer)}\" không thuộc câu {question.Index + 1}";
                return false;
            }

            if (canonical.Any(existing =>
                    string.Equals(
                        NormalizeChoiceText(existing),
                        NormalizeChoiceText(canonicalOption!),
                        StringComparison.Ordinal)))
            {
                validationError = $"câu {question.Index + 1} có mã lựa chọn trùng";
                return false;
            }

            canonical.Add(canonicalOption!);
        }

        canonicalAnswers = canonical;
        return true;
    }

    private static bool TryResolveChoiceAnswer(
        QuizQuestion question,
        string answer,
        out string? canonicalOption)
    {
        canonicalOption = null;
        string normalizedAnswer = NormalizeChoiceText(answer);
        QuizOption? exactOption = question.Options.FirstOrDefault(option =>
            string.Equals(
                NormalizeChoiceText(option.Text),
                normalizedAnswer,
                StringComparison.Ordinal));
        if (exactOption != null)
        {
            canonicalOption = exactOption.Text;
            return true;
        }

        string token = (answer ?? string.Empty).Trim().Trim('"', '\'', '`');
        var taggedToken = System.Text.RegularExpressions.Regex.Match(
            token,
            @"^q\s*(?<question>\d+)\s*[-_:.]\s*(?:option|opt|o)\s*(?<option>\d+)\s*[.)]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (taggedToken.Success)
        {
            if (!int.TryParse(taggedToken.Groups["question"].Value, out int taggedQuestion) ||
                taggedQuestion != question.Index + 1 ||
                !int.TryParse(taggedToken.Groups["option"].Value, out int taggedOption))
            {
                return false;
            }

            return TryResolveOptionNumber(question, taggedOption, out canonicalOption);
        }

        var numberedToken = System.Text.RegularExpressions.Regex.Match(
            token,
            @"^(?:option|opt|o)?\s*[_#:\-]?\s*(?<option>\d+)\s*[.)]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (numberedToken.Success &&
            int.TryParse(numberedToken.Groups["option"].Value, out int numberedOption))
        {
            return TryResolveOptionNumber(question, numberedOption, out canonicalOption);
        }

        var letterToken = System.Text.RegularExpressions.Regex.Match(
            token,
            @"^(?:option\s*)?(?<letter>[A-Za-z])\s*[.)]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!letterToken.Success)
        {
            return false;
        }

        int letterOption = char.ToUpperInvariant(letterToken.Groups["letter"].Value[0]) - 'A' + 1;
        return TryResolveOptionNumber(question, letterOption, out canonicalOption);
    }

    private static bool TryResolveOptionNumber(
        QuizQuestion question,
        int optionNumber,
        out string? canonicalOption)
    {
        canonicalOption = null;
        if (optionNumber < 1 || optionNumber > question.Options.Count)
        {
            return false;
        }

        canonicalOption = question.Options[optionNumber - 1].Text;
        return true;
    }

    private static string TrimForStatus(string value)
    {
        string normalized = string.Join(
            " ",
            (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 80 ? normalized : normalized[..80] + "…";
    }

    private static string NormalizeChoiceText(string value)
    {
        string unicodeNormalized = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Replace('\u00A0', ' ')
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-');
        return System.Text.RegularExpressions.Regex.Replace(unicodeNormalized, @"\s+", " ")
            .Trim()
            .ToLowerInvariant();
    }

    private static string DecodeWebViewString(string rawResult)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(rawResult) ?? string.Empty;
        }
        catch
        {
            return rawResult?.Trim('"') ?? string.Empty;
        }
    }

    private Task<AiCompletionResult> GetAnswerFromAiAsync(
        string questionText,
        bool isDiscussion = false,
        string? customSystemPrompt = null)
    {
        string systemPrompt = customSystemPrompt
            ?? (isDiscussion
                ? "Bạn là một học viên đang tham gia khóa học Coursera. Hãy đọc nội dung trang web và viết MỘT BÀI LUẬN/THẢO LUẬN NGẮN (khoảng 3-4 câu) bằng tiếng Anh để đăng lên diễn đàn. Chỉ trả về nội dung bài viết, không thêm lời chào hay giải thích."
                : "Bạn là một AI chuyên giải bài tập trắc nghiệm. Người dùng sẽ đưa câu hỏi và các đáp án [A, B, C, D...]. Bạn PHẢI CHỌN 1 ĐÁP ÁN ĐÚNG NHẤT và CHỈ TRẢ VỀ ĐÚNG NỘI DUNG CỦA ĐÁP ÁN ĐÓ. Tuyệt đối không giải thích, không thêm chữ 'Đáp án là', không dùng dấu ngoặc kép.");

        var progress = new Progress<string>(message => _viewModel.StatusText = message);
        return _aiCompletionService.CompleteAsync(
            systemPrompt,
            questionText,
            isDiscussion ? 0.7 : 0.1,
            progress);
    }
    private const string DefaultDiscussionReply =
        "Thanks for sharing. This was a useful learning activity.";

    private async Task HandleDiscussionAsync()
    {
        // Both SourceChanged and NavigationCompleted can be raised for the same
        // Coursera document.  A discussion must never be posted (or inspected)
        // twice because of that duplicate notification.
        if (_isHandlingDiscussion)
        {
            return;
        }

        _isHandlingDiscussion = true;
        try
        {
            _viewModel.StatusText = "🗣️ Đang xử lý bài Thảo luận (Discussion Prompt)...";
            await Task.Delay(3000);

            // A submitted discussion is not a fresh prompt.  Coursera can keep
            // it visually incomplete for a short time, which previously caused
            // the whole submitted page to be sent to AgentRouter unnecessarily.
            if (await TryAdvanceSubmittedDiscussionAsync())
            {
                return;
            }

            await DismissAnyGlobalPopupsAsync();
            if (await TryAdvanceSubmittedDiscussionAsync())
            {
                return;
            }

            if (await CheckLessonCompletedAndClickNextAsync())
            {
                _viewModel.StatusText = "⏭️ Bài Thảo luận này đã xong! Đang chuyển bài...";
                return;
            }

            // Check once more after the generic Next probe, because React can
            // render the submission confirmation and Next control separately.
            if (await TryAdvanceSubmittedDiscussionAsync())
            {
                return;
            }

            // Discussion prompts use a short local reply.  They never need to
            // send the entire page to AgentRouter, so a provider-side block
            // cannot stop a course that only needs this acknowledgement.
            bool isOptional = await IsOptionalDiscussionAsync();
            _viewModel.StatusText = isOptional
                ? "📝 Bài thảo luận tùy chọn; đang đăng phản hồi ngắn..."
                : "📝 Đang đăng phản hồi ngắn cho bài thảo luận...";
            if (!await PostDiscussionReplyAsync(DefaultDiscussionReply))
            {
                await FailCourseJobAsync(
                    "❌ Không thể đăng phản hồi ngắn cho bài thảo luận.");
            }
        }
        finally
        {
            _isHandlingDiscussion = false;
        }
    }

    private async Task<bool> TryAdvanceSubmittedDiscussionAsync()
    {
        Uri? discussionUri = MainWebView.Source;
        if (!IsCourseraUri(discussionUri))
        {
            return false;
        }

        string expectedUrlJson = JsonSerializer.Serialize(discussionUri!.ToString());
        string script = $$"""
            (function() {
                const expected = new URL({{expectedUrlJson}});
                const current = new URL(location.href);
                if (current.origin !== expected.origin ||
                    current.pathname !== expected.pathname ||
                    current.search !== expected.search) {
                    return 'STALE_DOCUMENT';
                }

                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                const isVisible = element => !!element &&
                    element.getClientRects().length > 0 &&
                    window.getComputedStyle(element).display !== 'none' &&
                    window.getComputedStyle(element).visibility !== 'hidden' &&
                    Number.parseFloat(window.getComputedStyle(element).opacity || '1') > 0 &&
                    !element.closest('[aria-hidden="true"]') &&
                    !element.closest('[inert]');
                const isEnabled = element => !element.matches(':disabled') &&
                    !element.closest('[aria-disabled="true"], [disabled]') &&
                    normalize(element.getAttribute('aria-disabled')) !== 'true';
                const controls = Array.from(document.querySelectorAll(
                    'button, a, [role="button"]'));
                const textOf = element => normalize(
                    element.getAttribute('aria-label') ||
                    element.getAttribute('title') ||
                    element.innerText ||
                    element.textContent);
                const pageText = normalize(document.body?.innerText);
                const hasSubmissionConfirmation =
                    /your\s+(?:response|reply)\s+has\s+been\s+submitted|response\s+has\s+been\s+submitted|đã\s+gửi\s+(?:phản\s+hồi|câu\s+trả\s+lời)/i.test(pageText);
                const hasViewResponse = controls.some(element => {
                    const text = textOf(element);
                    return text === 'view my response' || text === 'view your response' ||
                        text === 'view response';
                });
                const hasEditableReply = Array.from(document.querySelectorAll(
                    '[role="textbox"], textarea, [contenteditable="true"]'))
                    .some(element => isVisible(element) &&
                        /your\s+reply|reply|response/i.test(
                            element.getAttribute('aria-label') || ''));
                if (!hasSubmissionConfirmation &&
                    !(hasViewResponse && !hasEditableReply)) {
                    return 'NOT_SUBMITTED';
                }

                const next = controls.find(element =>
                    textOf(element) === 'go to next item' &&
                    isVisible(element) && isEnabled(element));
                if (!next) {
                    return 'SUBMITTED_NO_NEXT';
                }

                next.click();
                return 'CLICKED';
            })();
            """;

        string result;
        try
        {
            result = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(script));
        }
        catch
        {
            return false;
        }

        if (string.Equals(result, "NOT_SUBMITTED", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(result, "STALE_DOCUMENT", StringComparison.Ordinal))
        {
            // Do not let a stale event use AI on a newer document.
            return true;
        }

        _courseHasSubmittedDiscussionItems = true;
        _submittedDiscussionPaths.Add(discussionUri.AbsolutePath);

        if (string.Equals(result, "CLICKED", StringComparison.Ordinal))
        {
            _viewModel.StatusText =
                "⏭️ Phản hồi Discussion đã gửi; đang chuyển ngay sang bài tiếp theo...";
            if (await WaitForAppItemNavigationAsync(
                    discussionUri,
                    TimeSpan.FromSeconds(2)) ||
                !IsSameCourseraDocument(discussionUri))
            {
                return true;
            }
        }

        // Some submitted discussions do not expose a Next button.  Return to
        // the canonical course page; its scanner excludes this exact path so
        // it will continue with the next eligible lesson instead of looping.
        if (IsSameCourseraDocument(discussionUri) &&
            TryGetCourseHomeUri(discussionUri, out Uri? courseHomeUri) &&
            courseHomeUri != null)
        {
            MainWebView.Source = courseHomeUri;
            _viewModel.StatusText =
                "⏭️ Phản hồi Discussion đã gửi; đang quét ngay bài tiếp theo...";
        }
        else if (IsSameCourseraDocument(discussionUri))
        {
            _viewModel.StatusText =
                "✅ Phản hồi Discussion đã gửi; Worker sẽ không gọi AI hoặc đăng lại bài này.";
        }

        return true;
    }

    private async Task<bool> PostDiscussionReplyAsync(string reply)
    {
        // Dùng CDP để React nhận đúng sự kiện nhập liệu của editor.
        _viewModel.StatusText = "⌨️ Đang nhập phản hồi thảo luận...";
        string jsFocus = @"
            (function() {
                var editor = document.querySelector('div[role=""textbox""][aria-label=""Your Reply""]');
                if (editor) {
                    editor.focus();
                    document.execCommand('selectAll', false, null);
                    document.execCommand('delete', false, null);
                    return 'OK';
                }
                return 'NOT_FOUND';
            })();
        ";
        string focusResult = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(jsFocus));
        if (!string.Equals(focusResult, "OK", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = new { text = reply.Trim() + " " };
        string jsonPayload = JsonSerializer.Serialize(payload);
        await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", jsonPayload);
        await Task.Delay(1000);

        string jsClickReply = @"
            (function() {
                var btns = Array.from(document.querySelectorAll('button'));
                var replyBtn = btns.find(b => (b.innerText || '').trim() === 'Reply' || (b.innerText || '').trim() === 'Post');
                if (replyBtn && !replyBtn.disabled) {
                    replyBtn.click();
                    return 'SUCCESS';
                }
                return 'FAILED';
            })();
        ";
        string replyResult = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(jsClickReply));
        if (!string.Equals(replyResult, "SUCCESS", StringComparison.Ordinal))
        {
            return false;
        }

        _viewModel.StatusText = "✅ Đã đăng bài thảo luận! Đang tải lại trang để xác nhận...";
        await Task.Delay(3000);
        MainWebView.CoreWebView2.Reload();
        return true;
    }

    private async Task<bool> IsOptionalDiscussionAsync()
    {
        const string script = """
            (function() {
                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                const pageText = normalize(document.body?.innerText || '');
                return /participation\s+is\s+optional|optional\s+participation/i.test(pageText);
            })();
            """;

        try
        {
            string result = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(script));
            return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // A Reading can raise both SourceChanged and NavigationCompleted. A pending
    // flag (in addition to the guard) preserves a second Reading reached while
    // the first handler is still unwinding.
    private bool _isHandlingReading;
    private bool _readingHandlerRequested;

    private async Task HandleReadingLessonAsync()
    {
        _readingHandlerRequested = true;
        if (_isHandlingReading)
        {
            return;
        }

        _isHandlingReading = true;
        try
        {
            while (_readingHandlerRequested)
            {
                _readingHandlerRequested = false;
                Uri? readingUri = MainWebView.Source;
                if (!IsCourseraUri(readingUri) ||
                    !readingUri!.AbsolutePath.Contains(
                        "/supplement/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await HandleSingleReadingLessonAsync(readingUri);
            }
        }
        finally
        {
            _isHandlingReading = false;
        }
    }

    private async Task HandleSingleReadingLessonAsync(Uri readingUri)
    {
        if (await CheckForLockedScreenAndReloadAsync())
        {
            return;
        }

        _viewModel.StatusText = "📖 Đang xử lý bài Đọc (Reading)...";
        await Task.Delay(2000);
        if (!IsSameCourseraDocument(readingUri))
        {
            return;
        }

        bool completionReloadAttempted = false;
        bool completionReloadVerified = false;
        string lastProbeState = "NOT_STARTED";

        // A transient React render must not become an endless course loop. Eight
        // bounded probes give Coursera time to persist the completion state.
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            await DismissAnyGlobalPopupsAsync(maxPasses: 2);
            if (!IsSameCourseraDocument(readingUri))
            {
                return;
            }

            string state = await ProbeAndAdvanceReadingAsync(
                readingUri,
                completionReloadVerified);
            lastProbeState = state;

            if (state == "STALE_DOCUMENT")
            {
                return;
            }

            if (state == "COMPLETED_NEXT_CLICKED")
            {
                _viewModel.StatusText =
                    "✅ Đã xác nhận bài Reading hoàn thành; đang mở bài kế tiếp...";
                if (await WaitForAppItemNavigationAsync(
                        readingUri,
                        TimeSpan.FromSeconds(3)))
                {
                    return;
                }

                // The exact Next click was swallowed by React. Completion is
                // already proven, so Course Home is now a safe fallback.
                if (TryReturnToCourseHomeAfterReading(
                        readingUri,
                        "✅ Reading đã hoàn thành nhưng nút Next không chuyển trang; đang quét phần còn lại..."))
                {
                    return;
                }

                await FailCourseJobAsync(
                    "❌ Reading đã hoàn thành nhưng không xác định được trang khóa học để tiếp tục.");
                return;
            }

            if (state == "COMPLETED_NO_NEXT")
            {
                // In the final Reading Coursera shows "Go to My Learning". That
                // link is deliberately not treated as Next.
                if (TryReturnToCourseHomeAfterReading(
                        readingUri,
                        "✅ Đã đánh dấu Reading cuối là hoàn thành; đang kiểm tra kết thúc khóa học..."))
                {
                    return;
                }

                await FailCourseJobAsync(
                    "❌ Reading đã hoàn thành nhưng không xác định được trang khóa học để kết thúc.");
                return;
            }

            if (state == "MARK_CLICKED")
            {
                completionReloadAttempted = false;
                completionReloadVerified = false;
                _viewModel.StatusText =
                    $"📖 Đã bấm Mark as completed; đang xác nhận với Coursera ({attempt}/8)...";
            }
            else if (state == "NO_MARK" && !completionReloadAttempted)
            {
                completionReloadAttempted = true;
                _viewModel.StatusText =
                    "📖 Nút Mark đã biến mất; đang tải lại chính bài Reading để xác nhận đã lưu...";
                completionReloadVerified =
                    await ReloadReadingForCompletionVerificationAsync(readingUri);
                if (!IsSameCourseraDocument(readingUri))
                {
                    return;
                }
            }
            else
            {
                _viewModel.StatusText =
                    $"📖 Đang chờ nút Mark as completed sẵn sàng ({attempt}/8)...";
            }

            await Task.Delay(1250);
        }

        await FailCourseJobAsync(
            "❌ Không xác nhận được bài Reading đã hoàn thành; Worker đã dừng để tránh lặp vô hạn. " +
            $"Trạng thái cuối: {lastProbeState}.");
    }

    private async Task<string> ProbeAndAdvanceReadingAsync(
        Uri readingUri,
        bool completionReloadVerified)
    {
        string expectedUrlJson = JsonSerializer.Serialize(readingUri.ToString());
        string script = """
            (function() {
                const expected = new URL(__EXPECTED_URL__);
                const current = new URL(window.location.href);
                if (current.origin !== expected.origin ||
                    current.pathname !== expected.pathname ||
                    current.search !== expected.search) {
                    return 'STALE_DOCUMENT';
                }

                window.scrollTo(0, document.body.scrollHeight);
                const normalize = value => String(value || '')
                    .replace(/\u00a0/g, ' ')
                    .replace(/\s+/g, ' ')
                    .trim()
                    .toLowerCase();
                const isVisible = element => !!element &&
                    element.getClientRects().length > 0 &&
                    window.getComputedStyle(element).display !== 'none' &&
                    window.getComputedStyle(element).visibility !== 'hidden' &&
                    Number.parseFloat(window.getComputedStyle(element).opacity || '1') > 0 &&
                    !element.closest('[aria-hidden="true"], [inert]');
                const isEnabled = element => !element.matches(':disabled') &&
                    !element.closest('[disabled], [aria-disabled="true"]') &&
                    normalize(element.getAttribute('aria-disabled')) !== 'true';
                const accessibleName = element => normalize(
                    element.getAttribute('aria-label') ||
                    element.getAttribute('title') ||
                    element.innerText ||
                    element.textContent);
                const controls = Array.from(document.querySelectorAll(
                    'button, a[href], [role="button"]'));
                const visibleControls = controls.filter(isVisible);
                const enabledControls = visibleControls.filter(isEnabled);
                const markCompletedNames = new Set([
                    'mark as completed',
                    'mark as complete',
                    'đánh dấu là đã hoàn thành',
                    'đánh dấu hoàn thành'
                ]);
                const markIncompleteNames = new Set([
                    'mark as incomplete',
                    'đánh dấu là chưa hoàn thành'
                ]);
                const visibleText = element => normalize(
                    element.innerText || element.textContent);
                const markButton = visibleControls.find(element =>
                    (element.matches('button') || element.getAttribute('role') === 'button') &&
                    (markCompletedNames.has(accessibleName(element)) ||
                        markCompletedNames.has(visibleText(element))));
                const markIncomplete = visibleControls.find(element =>
                    markIncompleteNames.has(accessibleName(element)) ||
                    markIncompleteNames.has(visibleText(element)));

                // Coursera has used several wrappers for the outline item. The
                // canonical href is stable, so select from all anchors but only
                // accept the one whose origin + pathname exactly matches this
                // Reading document. This cannot confuse a global current link.
                const lessonLinks = Array.from(document.querySelectorAll('a[href]'));
                const activeLesson = lessonLinks.find(link => {
                    try {
                        const linkUrl = new URL(
                            link.getAttribute('href') || '',
                            window.location.href);
                        return linkUrl.origin === current.origin &&
                            linkUrl.pathname === current.pathname;
                    } catch (_) {
                        return false;
                    }
                });
                const activeContainer = activeLesson?.closest(
                    'li[data-testid^="WeekSingleItemDisplay"]') || activeLesson;
                const activeLabel = normalize(
                    activeLesson?.getAttribute('aria-label') ||
                    activeLesson?.innerText);
                const activeCompletionMarker = !!activeContainer && (
                    /\bcompleted\b/.test(activeLabel) ||
                    !!activeContainer.querySelector(
                        '[aria-label*="Completed" i], [data-testid*="completed" i]') ||
                    Array.from(activeContainer.querySelectorAll('span')).some(span =>
                        normalize(span.textContent) === 'completed') ||
                    !!activeLesson?.querySelector(':scope > span:first-child > svg'));
                const explicitCompletedControl = visibleControls.some(element => {
                    const name = accessibleName(element);
                    return name === 'completed' && (
                        element.matches(':disabled') ||
                        normalize(element.getAttribute('aria-disabled')) === 'true' ||
                        normalize(element.getAttribute('aria-pressed')) === 'true');
                });
                const currentCourseMatch = current.pathname.match(
                    /^\/learn\/([^/]+)(?:\/|$)/i);
                const myLearningControl = enabledControls.find(element => {
                    const text = visibleText(element);
                    const name = accessibleName(element);
                    const href = element.getAttribute('href') || '';
                    return text === 'go to my learning' ||
                        name === 'go to my learning' ||
                        /(?:^|\/)my-learning(?:\/|$)/i.test(href);
                });
                const isTrustedNext = element => {
                    const visibleText = normalize(
                        element.innerText || element.textContent);
                    const rawHref = element.getAttribute('href') || '';
                    if (visibleText === 'go to my learning' ||
                        accessibleName(element) === 'go to my learning' ||
                        /(?:^|\/)my-learning(?:\/|$)/i.test(rawHref)) {
                        return false;
                    }
                    if (accessibleName(element) !== 'go to next item') {
                        return false;
                    }
                    if (!element.matches('a[href]')) {
                        return true;
                    }
                    if (!currentCourseMatch) {
                        return false;
                    }
                    try {
                        const target = new URL(rawHref, window.location.href);
                        return target.origin === current.origin &&
                            target.pathname.startsWith(
                                '/learn/' + currentCourseMatch[1] + '/');
                    } catch (_) {
                        return false;
                    }
                };
                const next = enabledControls.find(isTrustedNext);
                const primaryNext = !!next &&
                    /(?:^|\s)cds-button-primary(?:\s|$)/i.test(next.className || '');
                const completed = activeCompletionMarker ||
                    !!markIncomplete ||
                    explicitCompletedControl ||
                    primaryNext ||
                    (__COMPLETION_RELOAD_VERIFIED__ &&
                        !markButton &&
                        !!myLearningControl);

                if (completed) {
                    if (next) {
                        next.click();
                        return 'COMPLETED_NEXT_CLICKED';
                    }
                    return 'COMPLETED_NO_NEXT';
                }

                if (markButton && isEnabled(markButton)) {
                    markButton.click();
                    return 'MARK_CLICKED';
                }
                return markButton ? 'MARK_WAITING' : 'NO_MARK';
            })();
            """
            .Replace("__EXPECTED_URL__", expectedUrlJson)
            .Replace(
                "__COMPLETION_RELOAD_VERIFIED__",
                completionReloadVerified ? "true" : "false");

        try
        {
            return DecodeWebViewString(
                await MainWebView.ExecuteScriptAsync(script));
        }
        catch
        {
            return "DOM_NOT_READY";
        }
    }

    private async Task<bool> ReloadReadingForCompletionVerificationAsync(
        Uri readingUri)
    {
        if (!IsSameCourseraDocument(readingUri))
        {
            return false;
        }

        MainWebView.Reload();
        await Task.Delay(500);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (!IsSameCourseraDocument(readingUri))
            {
                return false;
            }

            try
            {
                string readyState = DecodeWebViewString(
                    await MainWebView.ExecuteScriptAsync("document.readyState"));
                if (string.Equals(
                        readyState,
                        "complete",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Give the Reading CTA one bounded React render after the
                    // persisted document has loaded. If Mark returns, the next
                    // probe clicks it again instead of claiming completion.
                    await Task.Delay(2500);
                    return IsSameCourseraDocument(readingUri);
                }
            }
            catch
            {
                // The WebView document is between reload phases.
            }

            await Task.Delay(250);
        }

        return false;
    }

    private bool TryReturnToCourseHomeAfterReading(
        Uri readingUri,
        string activity)
    {
        if (!IsSameCourseraDocument(readingUri) ||
            !TryGetCourseHomeUri(readingUri, out Uri? courseHomeUri) ||
            courseHomeUri == null)
        {
            return false;
        }

        MainWebView.Source = courseHomeUri;
        _viewModel.StatusText = activity;
        return true;
    }


    // SourceChanged and NavigationCompleted can both fire for the same Peer page.
    // Keep this guard around the entire skip so duplicate events cannot start
    // competing navigation requests back to Course Home.
    private bool _isHandlingPeerAssignment = false;

    private async Task HandlePeerAssignmentAsync()
    {
        if (_isHandlingPeerAssignment)
        {
            return;
        }

        _isHandlingPeerAssignment = true;
        try
        {
            Uri? peerActivityUri = MainWebView.Source;
            if (!IsCourseraUri(peerActivityUri))
            {
                return;
            }

            // Coursera uses several peer routes.  A review URL can contain
            // "give-feedback" or "review"; both must be treated as one
            // intentionally skipped category and never receive clicks, text,
            // files, Honor Code confirmation, or a submission.
            bool isPeerReview = peerActivityUri.AbsolutePath.Contains(
                    "review", StringComparison.OrdinalIgnoreCase) ||
                peerActivityUri.AbsolutePath.Contains(
                    "feedback", StringComparison.OrdinalIgnoreCase);
            string peerActivityName = isPeerReview
                ? "Peer Review"
                : "Peer-graded Assignment";

            // Completion reporting uses this course-scoped marker to explain
            // that Peer-graded/Review items remain intentionally skipped.
            _courseHasSkippedPeerItems = true;

            if (!TryGetCourseHomeUri(peerActivityUri, out Uri? courseHomeUri) ||
                courseHomeUri == null)
            {
                _viewModel.StatusText =
                    $"⚠️ Đã phát hiện {peerActivityName} nhưng không xác định được khóa học để bỏ qua an toàn.";
                return;
            }

            _viewModel.StatusText =
                $"⏭️ Đã bỏ qua {peerActivityName}. Đang quay lại danh sách bài để tiếp tục...";

            // Yield once so SourceChanged and NavigationCompleted can settle,
            // then make sure this is still the very same peer document.  This
            // prevents a late handler from replacing a newer lesson selected by
            // the course scanner.
            await Task.Delay(250);
            if (!IsSameCourseraDocument(peerActivityUri))
            {
                return;
            }

            // Do not interact with the peer page itself.  Course Home is the
            // normal scanner entry point, where it can choose the next eligible
            // lesson.  Navigating there also avoids peer pages that bounce
            // between the assignment and review tabs.
            MainWebView.Source = courseHomeUri;
            _viewModel.StatusText =
                $"⏭️ Đã bỏ qua {peerActivityName}; đang quét bài tiếp theo trong khóa học...";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText =
                "⚠️ Không thể bỏ qua bài Peer-graded an toàn: " + ex.Message;
        }
        finally
        {
            _isHandlingPeerAssignment = false;
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        string linkCanMo = UrlTextBox.Text;
        if (string.IsNullOrWhiteSpace(linkCanMo))
        {
            _viewModel.StatusText = "Vui lòng nhập link vào ô trống!";
            return;
        }

        if (!Uri.TryCreate(linkCanMo, UriKind.Absolute, out Uri? targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttps && targetUri.Scheme != Uri.UriSchemeHttp))
        {
            _viewModel.StatusText = "Lỗi đường dẫn không hợp lệ.";
            return;
        }

        _viewModel.StatusText = $"Đang tải trang: {targetUri}";

        int requestGeneration;
        unchecked
        {
            requestGeneration = ++_courseraProfileBootstrapGeneration;
        }

        if (IsCourseraUri(targetUri))
        {
            // Mỗi lần Start đều làm mới danh tính để không dùng tên của tài khoản cũ.
            _courseraIdentity = null;
            _courseraProfileReturnUri = targetUri;
            _courseraProfileExpectedNavigationId = null;
            _courseraProfilePendingNavigationUri = null;
            _courseraProfileAcceptLoginContinuation = false;
            _courseraProfileBootstrapState = CourseraProfileBootstrapState.AwaitingTarget;
        }
        else
        {
            ResetCourseraProfileBootstrap();
        }

        try
        {
            await MainWebView.EnsureCoreWebView2Async(null);
            if (requestGeneration != _courseraProfileBootstrapGeneration)
            {
                return;
            }

            MainWebView.CoreWebView2.Stop();
            if (IsCourseraUri(targetUri))
            {
                NavigateCourseraBootstrap(targetUri);
            }
            else
            {
                MainWebView.Source = targetUri;
            }
        }
        catch (Exception ex)
        {
            if (requestGeneration == _courseraProfileBootstrapGeneration)
            {
                ResetCourseraProfileBootstrap();
                _viewModel.StatusText = "Lỗi đường dẫn không hợp lệ: " + ex.Message;
            }
        }
    }

    private void MainWebView_NavigationStarting(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (_courseraProfileBootstrapState != CourseraProfileBootstrapState.Idle)
        {
            if (_courseraProfileExpectedNavigationId == e.NavigationId)
            {
                return;
            }

            if (_courseraProfilePendingNavigationUri != null &&
                Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? startingUri) &&
                Uri.Compare(
                    _courseraProfilePendingNavigationUri,
                    startingUri,
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                _courseraProfileExpectedNavigationId = e.NavigationId;
                _courseraProfilePendingNavigationUri = null;
                return;
            }

            if (_courseraProfileAcceptLoginContinuation &&
                Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? loginContinuationUri) &&
                IsCourseraUri(loginContinuationUri))
            {
                _courseraProfileExpectedNavigationId = e.NavigationId;
                _courseraProfileAcceptLoginContinuation = false;
            }
        }
    }

    private async void MainWebView_SourceChanged(object sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e)
    {
        if (_directLoginActive || IsInteractiveBrowseSession)
        {
            return;
        }

        if (_courseraProfileBootstrapState != CourseraProfileBootstrapState.Idle)
        {
            return;
        }

        string currenUrl = MainWebView.Source?.ToString()?.ToLower() ?? "";
        
        if (currenUrl.Contains("/lecture/"))
        {
            await HandleVideoLessonAsync();
        }
        else if (currenUrl.Contains("/ungradedwidget/"))
        {
            await HandleUngradedWidgetAsync();
        }
        else if (currenUrl.Contains("/gradedlti/"))
        {
            if (ShouldSkipGradedAppItems)
            {
                await SkipAppItemAsync("Graded App Item");
            }
            else
            {
                await HandleUngradedAppAsync();
            }
        }
        else if (currenUrl.Contains("/ungradedlti/") ||
                 currenUrl.Contains("/lti/"))
        {
            await HandleUngradedAppAsync();
        }
        else if (!currenUrl.Contains("/peer/") &&
                 (currenUrl.Contains("/assignment-submission/") || currenUrl.Contains("/exam/") || currenUrl.Contains("/quiz/")))
        {
            await HandleQuizAsync();
        }
        else if (currenUrl.Contains("/discussionprompt/"))
        {
            await HandleDiscussionAsync();
        }
        else if (currenUrl.Contains("/supplement/"))
        {
            await HandleReadingLessonAsync();
        }
        else if (currenUrl.Contains("/peer/"))
        {
            await HandlePeerAssignmentAsync();
        }
        else if (currenUrl.Contains("/coach/") || currenUrl.Contains("/dialogue/"))
        {
            await HandleDialogueAsync();
        }
    }

    private async void MainWebView_NavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_directLoginActive)
        {
            return;
        }

        if (IsInteractiveBrowseSession)
        {
            _viewModel.StatusText = e.IsSuccess
                ? "👤 Profile riêng đang mở để thao tác thủ công. Đóng cửa sổ khi hoàn tất."
                : $"⚠️ Profile không tải được trang: {e.WebErrorStatus}";
            return;
        }

        if (_courseraProfileBootstrapState != CourseraProfileBootstrapState.Idle &&
            (!_courseraProfileExpectedNavigationId.HasValue ||
             _courseraProfileExpectedNavigationId.Value != e.NavigationId))
        {
            // Completion của một lần Start cũ; không được tác động state hiện tại.
            return;
        }

        if (await HandleCourseraProfileBootstrapCompletedAsync(e.IsSuccess))
        {
            return;
        }

        if (e.IsSuccess)
        {
            _viewModel.StatusText = "Tải trang thành công!";
            string currenUrl = MainWebView.Source.ToString();
            if (Uri.TryCreate(currenUrl, UriKind.Absolute, out Uri? currentNavigationUri) &&
                IsHostOrSubdomain(currentNavigationUri.Host, "coursera.org") &&
                (currentNavigationUri.AbsolutePath.Contains("/payments", StringComparison.OrdinalIgnoreCase) ||
                 currentNavigationUri.AbsolutePath.Contains("/checkout", StringComparison.OrdinalIgnoreCase) ||
                 currentNavigationUri.AbsolutePath.Contains("/subscriptions", StringComparison.OrdinalIgnoreCase) ||
                 currentNavigationUri.AbsolutePath.Contains("/coursera-plus", StringComparison.OrdinalIgnoreCase) ||
                 currentNavigationUri.AbsolutePath.Contains("/courseraplus", StringComparison.OrdinalIgnoreCase)))
            {
                await PauseCourseJobAsync(
                    "⏸️ Coursera đã chuyển sang trang Plus/Trial/thanh toán; Worker đã tạm dừng.",
                    "Tài khoản cần Coursera Plus hoặc quyền truy cập phù hợp. Hãy hoàn tất trong profile rồi bấm Tiếp tục.",
                    "COURSERA_PLUS_REQUIRED");
                return;
            }
            
            // Ưu tiên các trang bài học cụ thể trước
            if (currenUrl.Contains("/home/"))
            {
                _viewModel.StatusText = $"Đã tải trang: {currenUrl}";
                await CheckModulesAsync();
            }
            else if (currenUrl.Contains("/lecture/"))
            {
                await HandleVideoLessonAsync();
            }
            else if (currenUrl.Contains("/ungradedWidget/"))
            {
                await HandleUngradedWidgetAsync();
            }
            else if (currenUrl.Contains("/gradedLti/", StringComparison.OrdinalIgnoreCase))
            {
                if (ShouldSkipGradedAppItems)
                {
                    await SkipAppItemAsync("Graded App Item");
                }
                else
                {
                    await HandleUngradedAppAsync();
                }
            }
            else if (currenUrl.Contains("/ungradedLti/", StringComparison.OrdinalIgnoreCase) ||
                     currenUrl.Contains("/lti/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUngradedAppAsync();
            }
            else if (!currenUrl.Contains("/peer/", StringComparison.OrdinalIgnoreCase) &&
                     (currenUrl.Contains("/assignment-submission/") || currenUrl.Contains("/exam/") || currenUrl.Contains("/quiz/")))
            {
                await HandleQuizAsync();
            }
            else if (currenUrl.Contains("/discussionPrompt/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscussionAsync();
            }
            else if (currenUrl.Contains("/supplement/"))
            {
                await HandleReadingLessonAsync();
            }

            else if (currenUrl.Contains("/peer/", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePeerAssignmentAsync();
            }
            else if (currenUrl.Contains("/coach/") || currenUrl.Contains("/dialogue/"))
            {
                await HandleDialogueAsync();
            }
            // Trang giới thiệu khoá học (bên ngoài)
            else if (currenUrl.Contains("/learn/"))
            {
                await Checkkhoahoc();
            }
            else
            {
                _viewModel.StatusText = $"Tải trang: {currenUrl}";
            }
        }
        else
        {
            await PauseCourseJobAsync(
                $"⏸️ Không tải được trang khóa học ({e.WebErrorStatus}); Worker đã tạm dừng.",
                "Hãy mở profile, kiểm tra kết nối/quyền truy cập rồi bấm Tiếp tục.",
                "COURSE_NAVIGATION_FAILED");
        }
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        unchecked
        {
            _courseraProfileBootstrapGeneration++;
        }
        ResetCourseraProfileBootstrap();
        _courseraIdentity = null;
        _viewModel.StatusText = "Đang kiểm tra trạng thái đăng nhập...";

        try
        {
            await MainWebView.EnsureCoreWebView2Async(null);
            var cookies = await MainWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.coursera.org");

            bool isLogged = false;
            foreach (var cookie in cookies)
            {
                if (cookie.Name == "CAUTH")
                {
                    isLogged = true;
                    break;
                }
            }

            if (isLogged)
            {
                _viewModel.StatusText = "Đã đăng nhập thành công";
                MainWebView.Source = new Uri("https://www.coursera.org/");
            }
            else
            {
                _viewModel.StatusText = "Chưa đăng nhập. Trình duyệt đang mở form Login...";
                MainWebView.Source = new Uri("https://www.coursera.org/?authMode=login");
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Lỗi WebView2: " + ex.Message;
        }
    }
}

public class QuizQuestion
{
    public int Index { get; set; }
    public string PartTestId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string TextInputId { get; set; } = string.Empty;
    public string TextInputName { get; set; } = string.Empty;
    public int TextInputIndex { get; set; } = -1;
    public List<QuizOption> Options { get; set; } = new();
}

public class QuizOption
{
    public string Text { get; set; } = string.Empty;
    public string InputId { get; set; } = string.Empty;
    public string InputName { get; set; } = string.Empty;
    public string InputType { get; set; } = string.Empty;
    public int ControlIndex { get; set; }
}

public class QuizVerificationQuestion
{
    public int Index { get; set; }
    public string PartTestId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string TextInputId { get; set; } = string.Empty;
    public string TextInputName { get; set; } = string.Empty;
    public int TextInputIndex { get; set; } = -1;
    public string ExpectedText { get; set; } = string.Empty;
    public List<QuizVerificationOption> Options { get; set; } = new();
}

public class QuizVerificationOption
{
    public string Text { get; set; } = string.Empty;
    public string InputId { get; set; } = string.Empty;
    public string InputName { get; set; } = string.Empty;
    public string InputType { get; set; } = string.Empty;
    public int ControlIndex { get; set; }
    public bool ShouldBeChecked { get; set; }
}

public class QuizFeedbackDto
{
    public string Question { get; set; }
    public List<string> WrongAnswers { get; set; } = new List<string>();
    public List<string> CorrectAnswers { get; set; } = new List<string>();
    public bool IsMissingAnswers { get; set; }
}
