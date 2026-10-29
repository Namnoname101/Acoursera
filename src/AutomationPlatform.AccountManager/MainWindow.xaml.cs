using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AutomationPlatform.AccountManager;

public partial class MainWindow : Window
{
    private const string IdentityFileName = ".acose-profile-identity.json";
    private const string ManagerMetadataFileName = ".acose-account-manager.json";
    private const string WorkerProcessName = "AutomationPlatform.Presentation.exe";
    private const string ProfileLoginExecutableEnvironment = "ACOSE_PROFILE_LOGIN_EXECUTABLE";
    private const string DefaultServerUrl = "https://coursera-cookie-srv.fly.dev";
    private static readonly Uri CourseraHome = new("https://www.coursera.org/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ObservableCollection<ProfileEntry> _visibleProfiles = [];
    private readonly List<ProfileEntry> _allProfiles = [];
    
    private sealed record RunningWorker(int ProcessId, string ProfilePath, string Url);
    private sealed record QueuedJob(string ProfilePath, string Url);
    
    private readonly List<RunningWorker> _runningWorkers = [];
    private readonly List<QueuedJob> _pendingJobs = [];
    
    private readonly DispatcherTimer _refreshTimer;
    private readonly string _settingsPath;
    private ManagerSettings _settings;
    private string _profileRoot = string.Empty;
    private string _courseProfileRoot = string.Empty;
    private string _managedProfileRoot = string.Empty;
    private string? _lastSelectedProfilePath;

    public MainWindow()
    {
        InitializeComponent();

        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Acose",
            "AccountManager",
            "settings.json");
        _settings = LoadSettings();
        ApplySettingsToControls();
        SetProfileRoots(_settings.ProfileRoot);
        ProfilesGrid.ItemsSource = _visibleProfiles;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => LoadProfiles(preserveSelection: true);
        _refreshTimer.Start();

        Loaded += (_, _) => LoadProfiles(preserveSelection: false);
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private ManagerSettings LoadSettings()
    {
        ManagerSettings defaults = CreateDefaultSettings();
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return defaults;
            }

            ManagerSettings? saved = JsonSerializer.Deserialize<ManagerSettings>(
                File.ReadAllText(_settingsPath),
                JsonOptions);
            if (saved == null)
            {
                return defaults;
            }

            return new ManagerSettings
            {
                ServerUrl = FirstValue(saved.ServerUrl, defaults.ServerUrl),
                WorkerExecutable = FirstValue(saved.WorkerExecutable, defaults.WorkerExecutable),
                ProfileRoot = FirstValue(saved.ProfileRoot, defaults.ProfileRoot),
                MaxCourseWorkers = saved.MaxCourseWorkers is >= 1 and <= 64
                    ? saved.MaxCourseWorkers
                    : defaults.MaxCourseWorkers,
            };
        }
        catch
        {
            return defaults;
        }
    }

    private static ManagerSettings CreateDefaultSettings()
    {
        string machineProfileRoot = @"D:\Acose\WorkerProfiles";
        string configuredRoot = Environment.GetEnvironmentVariable("ACOSE_WORKER_PROFILE_ROOT") ?? string.Empty;
        string profileRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? configuredRoot
            : Directory.Exists(machineProfileRoot)
                ? machineProfileRoot
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Acose",
                    "WorkerProfiles");

        string configuredExecutable =
            Environment.GetEnvironmentVariable("ACOSE_WORKER_EXECUTABLE") ?? string.Empty;
        string configuredServer =
            Environment.GetEnvironmentVariable("ACOSE_WORKER_SERVER_URL") ?? DefaultServerUrl;
        int maxWorkers = int.TryParse(
            Environment.GetEnvironmentVariable("ACOSE_WORKER_MAX_COURSE_WORKERS"),
            out int parsedMax) && parsedMax is >= 1 and <= 64
                ? parsedMax
                : 20;

        return new ManagerSettings
        {
            ServerUrl = configuredServer,
            WorkerExecutable = configuredExecutable,
            ProfileRoot = profileRoot,
            MaxCourseWorkers = maxWorkers,
        };
    }

    private static string FirstValue(string? candidate, string fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();

    private void ApplySettingsToControls()
    {
        ServerUrlTextBox.Text = _settings.ServerUrl;
        WorkerExecutableTextBox.Text = _settings.WorkerExecutable;
        ProfileRootTextBox.Text = _settings.ProfileRoot;
        MaxWorkersTextBox.Text = _settings.MaxCourseWorkers.ToString(CultureInfo.InvariantCulture);
    }

    private void SetProfileRoots(string profileRoot)
    {
        _profileRoot = Path.GetFullPath(profileRoot);
        _courseProfileRoot = Path.Combine(_profileRoot, "_CourseJobs");
        _managedProfileRoot = Path.Combine(_profileRoot, "_ManagedAccounts");
        Directory.CreateDirectory(_courseProfileRoot);
        Directory.CreateDirectory(_managedProfileRoot);
        ProfileRootSummaryText.Text = _profileRoot;
    }

    private void LoadProfiles(bool preserveSelection)
    {
        string? selectedPath = preserveSelection
            ? (ProfilesGrid.SelectedItem as ProfileEntry)?.ProfilePath
            : null;
        
        SyncRunningWorkers();

        _allProfiles.Clear();
        AddProfilesFromRoot(_managedProfileRoot, isManaged: true);
        AddProfilesFromRoot(_courseProfileRoot, isManaged: false);
        _allProfiles.Sort((left, right) => right.LastUsed.CompareTo(left.LastUsed));
        ApplyFilter(selectedPath);
        UpdateRuntimeStatus();
    }

    private void SyncRunningWorkers()
    {
        _runningWorkers.Clear();
        foreach (ProcessSnapshot process in QueryProcesses(WorkerProcessName))
        {
            string? profilePath =
                TryReadArgument(process.CommandLine, "--automation-profile-path") ??
                TryReadArgument(process.CommandLine, "--interactive-profile-path");
            string? url =
                TryReadArgument(process.CommandLine, "--automation-url") ??
                TryReadArgument(process.CommandLine, "--interactive-url");
            
            if (!string.IsNullOrWhiteSpace(profilePath) && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    _runningWorkers.Add(new RunningWorker(process.ProcessId, Path.GetFullPath(profilePath), url));
                }
                catch
                {
                }
            }
        }
        
        // Remove pending jobs that are now running
        _pendingJobs.RemoveAll(job => _runningWorkers.Any(w => 
            string.Equals(w.ProfilePath, job.ProfilePath, StringComparison.OrdinalIgnoreCase) && 
            string.Equals(w.Url, job.Url, StringComparison.OrdinalIgnoreCase)));

        ProcessQueue();
    }
    
    private void ProcessQueue()
    {
        int availableSlots = _settings.MaxCourseWorkers - _runningWorkers.Count;
        if (availableSlots <= 0) return;

        var jobsToStart = _pendingJobs.Take(availableSlots).ToList();
        foreach (var job in jobsToStart)
        {
            if (TryStartProcess(job.ProfilePath, job.Url))
            {
                _pendingJobs.Remove(job);
            }
        }
    }

    private bool TryStartProcess(string profilePath, string url)
    {
        try
        {
            EnsureInsideProfileRoot(profilePath);
            string executable = ResolveWorkerExecutable();

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--automation-profile-path");
            startInfo.ArgumentList.Add(profilePath);
            startInfo.ArgumentList.Add("--automation-url");
            startInfo.ArgumentList.Add(url);
            startInfo.Environment["ACOSE_WORKER_PROFILE_ROOT"] = _profileRoot;
            // Standalone workers in the legacy course build do not pass the
            // selected folder to CoreWebView2Environment. WebView2 supports a
            // per-process override, so force this launch to reuse the exact
            // authenticated profile selected in Account Manager.
            startInfo.Environment["WEBVIEW2_USER_DATA_FOLDER"] = profilePath;
            
            Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Không thể khởi chạy tiến trình.");
            _runningWorkers.Add(new RunningWorker(process.Id, profilePath, url));
            return true;
        }
        catch (Exception exception)
        {
            ShowOperationError("Không thể Start worker", exception);
            return false;
        }
    }

    private void AddProfilesFromRoot(
        string root,
        bool isManaged)
    {
        DirectoryInfo[] directories;
        try
        {
            directories = new DirectoryInfo(root).EnumerateDirectories().ToArray();
        }
        catch
        {
            return;
        }

        foreach (DirectoryInfo directory in directories)
        {
            try
            {
                string profilePath = Path.GetFullPath(directory.FullName);
                ProfileIdentity identity = ReadIdentity(profilePath);
                ProfileMetadata metadata = ReadProfileMetadata(profilePath);
                string displayName = metadata.DisplayName ?? identity.FullName ??
                    (isManaged ? "Profile chưa đặt tên" : $"Đơn {ShortKey(directory.Name)}");
                
                int runningCount = _runningWorkers.Count(w => string.Equals(w.ProfilePath, profilePath, StringComparison.OrdinalIgnoreCase));
                int pendingCount = _pendingJobs.Count(j => string.Equals(j.ProfilePath, profilePath, StringComparison.OrdinalIgnoreCase));
                
                string sessionStatus = "Đã dừng";
                if (runningCount > 0)
                {
                    sessionStatus = $"Đang chạy {runningCount} khoá";
                }
                else if (pendingCount > 0)
                {
                    sessionStatus = $"Đang chờ {pendingCount} khoá";
                }

                _allProfiles.Add(new ProfileEntry(
                    directory.Name,
                    displayName,
                    profilePath,
                    isManaged ? "Tạo trên máy" : "Profile đơn",
                    sessionStatus,
                    metadata.LaunchUrl ?? CourseraHome.ToString(),
                    directory.LastWriteTime,
                    identity.FullName != null,
                    runningCount > 0 || pendingCount > 0,
                    true));
            }
            catch
            {
            }
        }
    }

    private static ProfileIdentity ReadIdentity(string profilePath)
    {
        string path = Path.Combine(profilePath, IdentityFileName);
        if (!File.Exists(path))
        {
            return new ProfileIdentity(null);
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            string? fullName = document.RootElement.TryGetProperty("fullName", out JsonElement value)
                ? NormalizeName(value.GetString())
                : null;
            return new ProfileIdentity(fullName);
        }
        catch
        {
            return new ProfileIdentity(null);
        }
    }

    private static ProfileMetadata ReadProfileMetadata(string profilePath)
    {
        string path = Path.Combine(profilePath, ManagerMetadataFileName);
        if (!File.Exists(path))
        {
            return new ProfileMetadata(null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            string? displayName = root.TryGetProperty("displayName", out JsonElement nameValue)
                ? NormalizeName(nameValue.GetString())
                : null;
            string? launchUrl = root.TryGetProperty("launchUrl", out JsonElement urlValue)
                ? urlValue.GetString()?.Trim()
                : null;
            return new ProfileMetadata(displayName, launchUrl);
        }
        catch
        {
            return new ProfileMetadata(null, null);
        }
    }

    private static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = Regex.Replace(value.Replace('\u00a0', ' '), @"\s+", " ").Trim();
        return normalized.Length is >= 2 and <= 120 && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private bool _isApplyingFilter;

    private void ApplyFilter(string? selectedPath = null)
    {
        selectedPath ??= (ProfilesGrid.SelectedItem as ProfileEntry)?.ProfilePath;
        string query = SearchTextBox.Text.Trim();
        ProfileEntry[] matches = string.IsNullOrWhiteSpace(query)
            ? _allProfiles.ToArray()
            : _allProfiles.Where(profile =>
                profile.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                profile.ProfileKey.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                profile.KindLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase))
              .ToArray();

        _isApplyingFilter = true;
        try
        {
            _visibleProfiles.Clear();
            foreach (ProfileEntry profile in matches)
            {
                _visibleProfiles.Add(profile);
            }

            if (selectedPath != null)
            {
                ProfilesGrid.SelectedItem = _visibleProfiles.FirstOrDefault(profile =>
                    string.Equals(profile.ProfilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _isApplyingFilter = false;
        }
    }

    private void UpdateRuntimeStatus()
    {
        int identified = _allProfiles.Count(profile => profile.HasIdentity);

        ProfileCountText.Text = _allProfiles.Count.ToString(CultureInfo.InvariantCulture);
        RunningWorkerCountText.Text = _runningWorkers.Count.ToString(CultureInfo.InvariantCulture);
        MaxWorkerLimitText.Text = _settings.MaxCourseWorkers.ToString(CultureInfo.InvariantCulture);

        OverallStatusText.Text =
            $"{_allProfiles.Count} profile · {identified} có danh tính · {_runningWorkers.Count} worker đang chạy";

        UpdateSelectedProfileControls();
    }

    private void UpdateSelectedProfileControls()
    {
        ProfileEntry? selected = ProfilesGrid.SelectedItem as ProfileEntry;
        bool hasSelection = selected != null;
        SaveProfileButton.IsEnabled = hasSelection;
        StartProfileButton.IsEnabled = hasSelection && selected!.CanStartWorker;
        StopProfileButton.IsEnabled = hasSelection && selected!.IsRunning;
        OpenFolderButton.IsEnabled = hasSelection;
        SelectedNameTextBox.IsEnabled = hasSelection;
        SelectedLinkTextBox.IsEnabled = hasSelection;

        if (selected == null)
        {
            SelectedProfileHintText.Text = "Chọn một profile ở danh sách bên trái";
            if (_lastSelectedProfilePath != null)
            {
                SelectedNameTextBox.Clear();
                SelectedLinkTextBox.Clear();
                ProfileLinksListView.ItemsSource = null;
                _lastSelectedProfilePath = null;
            }
            return;
        }

        SelectedProfileHintText.Text = $"{selected.KindLabel} · {selected.ProfileKey}";
        if (_lastSelectedProfilePath != selected.ProfilePath)
        {
            SelectedNameTextBox.Text = selected.DisplayName;
            SelectedLinkTextBox.Text = selected.LaunchUrl;
            _lastSelectedProfilePath = selected.ProfilePath;
        }
        
        var lines = selected.LaunchUrl.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(l => l.Trim())
                                      .Where(l => !string.IsNullOrWhiteSpace(l))
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .ToList();

        var linkItems = new List<LinkItemViewModel>();
        foreach (var url in lines)
        {
            bool isRunning = _runningWorkers.Any(w => string.Equals(w.ProfilePath, selected.ProfilePath, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Url, url, StringComparison.OrdinalIgnoreCase));
            bool isPending = _pendingJobs.Any(j => string.Equals(j.ProfilePath, selected.ProfilePath, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Url, url, StringComparison.OrdinalIgnoreCase));

            string status = isRunning ? "Đang chạy" : (isPending ? "Đang chờ queue" : "Chưa chạy");
            var color = isRunning ? new SolidColorBrush(Color.FromRgb(20, 122, 55)) : (isPending ? new SolidColorBrush(Color.FromRgb(217, 119, 6)) : new SolidColorBrush(Color.FromRgb(107, 114, 128)));

            linkItems.Add(new LinkItemViewModel
            {
                Url = url,
                StatusText = status,
                StatusColor = color,
                CanStart = !isRunning && !isPending,
                CanStop = isRunning || isPending,
                CanModify = !isRunning && !isPending
            });
        }
        
        ProfileLinksListView.ItemsSource = linkItems;
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProfiles(preserveSelection: true);
        StatusText.Text = "Danh sách profile đã được làm mới.";
    }

    private void ProfilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingFilter)
        {
            UpdateSelectedProfileControls();
        }
    }

    private void ProfilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is ProfileEntry selected && selected.CanStartWorker)
        {
            StartWorkerForProfile(selected, null);
        }
    }

    private void CreateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        string? displayName = NormalizeName(NewProfileNameTextBox.Text);
        if (displayName == null)
        {
            MessageBox.Show(
                "Hãy nhập tên profile từ 2 đến 120 ký tự.",
                "ACOSE Account Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            string slug = Regex.Replace(
                    RemoveDiacritics(displayName).ToLowerInvariant(),
                    "[^a-z0-9]+",
                    "-")
                .Trim('-');
            if (string.IsNullOrWhiteSpace(slug))
            {
                slug = "account";
            }
            slug = slug[..Math.Min(slug.Length, 40)];
            string profileKey = $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 49)];
            string profilePath = Path.GetFullPath(Path.Combine(_managedProfileRoot, profileKey));
            EnsureInsideProfileRoot(profilePath);
            Directory.CreateDirectory(profilePath);
            WriteProfileMetadata(profilePath, displayName, CourseraHome.ToString());

            NewProfileNameTextBox.Clear();
            LoadProfiles(preserveSelection: false);
            ProfilesGrid.SelectedItem = _visibleProfiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfilePath, profilePath, StringComparison.OrdinalIgnoreCase));
            if (ProfilesGrid.SelectedItem != null)
            {
                ProfilesGrid.ScrollIntoView(ProfilesGrid.SelectedItem);
            }
            StatusText.Text = TryOpenFirstLogin(profilePath, displayName)
                ? $"Đã tạo {displayName}. Hãy đăng nhập Coursera trong cửa sổ vừa mở, rồi đóng cửa sổ đó khi hoàn tất."
                : $"Đã tạo {displayName}, nhưng chưa mở được cửa sổ đăng nhập.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Không thể tạo profile", exception);
        }
    }

    private bool TryOpenFirstLogin(string profilePath, string displayName)
    {
        try
        {
            EnsureInsideProfileRoot(profilePath);
            string executable = ResolveProfileLoginExecutable();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--interactive-profile-path");
            startInfo.ArgumentList.Add(profilePath);
            startInfo.ArgumentList.Add("--interactive-url");
            startInfo.ArgumentList.Add("https://www.coursera.org/?authMode=login");
            startInfo.Environment["ACOSE_WORKER_PROFILE_ROOT"] = _profileRoot;
            startInfo.Environment["WEBVIEW2_USER_DATA_FOLDER"] = profilePath;

            _ = Process.Start(startInfo) ?? throw new InvalidOperationException(
                "Không thể khởi chạy cửa sổ đăng nhập.");
            return true;
        }
        catch (Exception exception)
        {
            ShowOperationError($"Đã tạo {displayName} nhưng không thể mở đăng nhập", exception);
            return false;
        }
    }

    private string ResolveProfileLoginExecutable()
    {
        string configured =
            Environment.GetEnvironmentVariable(ProfileLoginExecutableEnvironment) ??
            Environment.GetEnvironmentVariable(
                ProfileLoginExecutableEnvironment,
                EnvironmentVariableTarget.User) ??
            string.Empty;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return ResolveWorkerExecutable();
        }

        string executable = Path.GetFullPath(configured.Trim());
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"Không tìm thấy trình đăng nhập profile: {executable}",
                executable);
        }

        return executable;
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveSelectedProfile(out ProfileEntry? selected, out _))
        {
            StatusText.Text = $"Đã lưu tên và link cho {selected!.DisplayName}.";
            LoadProfiles(preserveSelection: true);
        }
    }

    private bool TrySaveSelectedProfile(out ProfileEntry? selected, out string? launchUrlText)
    {
        selected = ProfilesGrid.SelectedItem as ProfileEntry;
        launchUrlText = null;
        if (selected == null)
        {
            return false;
        }

        string? displayName = NormalizeName(SelectedNameTextBox.Text);
        if (displayName == null)
        {
            MessageBox.Show(
                "Tên hiển thị phải có từ 2 đến 120 ký tự.",
                "Tên profile chưa hợp lệ",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }
        
        var lines = SelectedLinkTextBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(l => l.Trim())
                                            .Where(l => !string.IsNullOrWhiteSpace(l))
                                            .ToList();
                                            
        foreach (var line in lines)
        {
            if (!TryGetCourseraUri(line, out _))
            {
                MessageBox.Show(
                    $"Link không hợp lệ: {line}\nChỉ chấp nhận link HTTPS thuộc coursera.org.",
                    "Link chưa hợp lệ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }
        }
        
        launchUrlText = string.Join("\n", lines);

        try
        {
            EnsureInsideProfileRoot(selected.ProfilePath);
            WriteProfileMetadata(selected.ProfilePath, displayName, launchUrlText);
            _lastSelectedProfilePath = selected.ProfilePath;
            return true;
        }
        catch (Exception exception)
        {
            ShowOperationError("Không thể lưu profile", exception);
            return false;
        }
    }

    private static void WriteProfileMetadata(string profilePath, string displayName, string launchUrlText)
    {
        string metadata = JsonSerializer.Serialize(new
        {
            version = 2,
            displayName,
            launchUrl = launchUrlText,
            updatedAt = DateTimeOffset.UtcNow,
        }, JsonOptions);
        File.WriteAllText(
            Path.Combine(profilePath, ManagerMetadataFileName),
            metadata,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void StartWorkerInGrid_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } &&
            _allProfiles.FirstOrDefault(p => string.Equals(p.ProfilePath, path, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            StartWorkerForProfile(profile, null);
        }
    }

    private void StopWorkerInGrid_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } &&
            _allProfiles.FirstOrDefault(p => string.Equals(p.ProfilePath, path, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            StopWorkerForProfile(profile, null);
        }
    }

    private void StartProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is ProfileEntry selected)
        {
            StartWorkerForProfile(selected, null);
        }
    }
    
    private void StartSingleLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && ProfilesGrid.SelectedItem is ProfileEntry selected)
        {
            StartWorkerForProfile(selected, url);
        }
    }

    private void EditSingleLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string originalUrl } ||
            ProfilesGrid.SelectedItem is not ProfileEntry selected)
        {
            return;
        }

        if (IsLinkRunningOrPending(selected.ProfilePath, originalUrl))
        {
            MessageBox.Show(
                "Hãy dừng khóa học này trước khi sửa link.",
                "Không thể sửa link",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new CourseLinkDialog(originalUrl) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!TryGetCourseraUri(dialog.CourseUrl, out Uri? updatedUri))
        {
            MessageBox.Show(
                "Link khóa học phải là địa chỉ HTTPS thuộc coursera.org.",
                "Link chưa hợp lệ",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string updatedUrl = updatedUri!.ToString();
        List<string> links = GetCourseLinksFromEditor();
        int linkIndex = links.FindIndex(link =>
            string.Equals(link, originalUrl, StringComparison.OrdinalIgnoreCase));
        if (linkIndex < 0)
        {
            MessageBox.Show(
                "Không còn tìm thấy link này trong profile. Hãy làm mới rồi thử lại.",
                "Link đã thay đổi",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        bool duplicate = links.Where((_, index) => index != linkIndex).Any(link =>
            string.Equals(link, updatedUrl, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            MessageBox.Show(
                "Link khóa học này đã có trong profile.",
                "Link bị trùng",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        links[linkIndex] = updatedUrl;
        SelectedLinkTextBox.Text = string.Join(Environment.NewLine, links);
        if (TrySaveSelectedProfile(out ProfileEntry? savedProfile, out _))
        {
            StatusText.Text = $"Đã sửa link khóa học cho {savedProfile!.DisplayName}.";
            LoadProfiles(preserveSelection: true);
        }
    }

    private void DeleteSingleLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } ||
            ProfilesGrid.SelectedItem is not ProfileEntry selected)
        {
            return;
        }

        if (IsLinkRunningOrPending(selected.ProfilePath, url))
        {
            MessageBox.Show(
                "Hãy dừng khóa học này trước khi xóa link.",
                "Không thể xóa link",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            $"Xóa link khóa học này khỏi profile?\n\n{url}",
            "Xác nhận xóa link",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        List<string> links = GetCourseLinksFromEditor();
        int removed = links.RemoveAll(link =>
            string.Equals(link, url, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return;
        }

        SelectedLinkTextBox.Text = string.Join(Environment.NewLine, links);
        if (TrySaveSelectedProfile(out ProfileEntry? savedProfile, out _))
        {
            StatusText.Text = $"Đã xóa link khóa học khỏi {savedProfile!.DisplayName}.";
            LoadProfiles(preserveSelection: true);
        }
    }

    private List<string> GetCourseLinksFromEditor() =>
        SelectedLinkTextBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(link => link.Trim())
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .ToList();

    private bool IsLinkRunningOrPending(string profilePath, string url) =>
        _runningWorkers.Any(worker =>
            string.Equals(worker.ProfilePath, profilePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(worker.Url, url, StringComparison.OrdinalIgnoreCase)) ||
        _pendingJobs.Any(job =>
            string.Equals(job.ProfilePath, profilePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(job.Url, url, StringComparison.OrdinalIgnoreCase));

    private void StartWorkerForProfile(ProfileEntry profile, string? specificUrl)
    {
        string launchUrlText = profile.LaunchUrl;
        
        if (ProfilesGrid.SelectedItem is ProfileEntry selected &&
            string.Equals(profile.ProfilePath, selected.ProfilePath, StringComparison.OrdinalIgnoreCase))
        {
            if (!TrySaveSelectedProfile(out _, out string? updatedText))
            {
                return;
            }
            launchUrlText = updatedText ?? "";
        }
        
        var urls = new List<string>();
        if (specificUrl != null)
        {
            urls.Add(specificUrl);
        }
        else
        {
            var lines = launchUrlText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(l => l.Trim())
                                     .Where(l => !string.IsNullOrWhiteSpace(l));
            urls.AddRange(lines);
        }

        int added = 0;
        foreach (var urlText in urls)
        {
            if (TryGetCourseraUri(urlText, out Uri? uri))
            {
                string url = uri!.ToString();
                bool isRunning = _runningWorkers.Any(w => string.Equals(w.ProfilePath, profile.ProfilePath, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Url, url, StringComparison.OrdinalIgnoreCase));
                bool isPending = _pendingJobs.Any(j => string.Equals(j.ProfilePath, profile.ProfilePath, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Url, url, StringComparison.OrdinalIgnoreCase));
                
                if (!isRunning && !isPending)
                {
                    _pendingJobs.Add(new QueuedJob(profile.ProfilePath, url));
                    added++;
                }
            }
        }

        if (added > 0)
        {
            StatusText.Text = $"Đã thêm {added} khoá vào hàng đợi cho {profile.DisplayName}.";
            ProcessQueue();
            LoadProfiles(preserveSelection: true);
        }
    }

    private string ResolveWorkerExecutable()
    {
        string configured = WorkerExecutableTextBox.Text.Trim();
        if (File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        const string artifactRoot = @"D:\Acose\WorkerArtifacts";
        string? latest = Directory.Exists(artifactRoot)
            ? new DirectoryInfo(artifactRoot).EnumerateDirectories("worker-*")
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Select(directory => Path.Combine(
                    directory.FullName,
                    WorkerProcessName))
                .FirstOrDefault(File.Exists)
            : null;
        return latest ?? throw new FileNotFoundException(
            "Không tìm thấy Worker. Hãy chọn AutomationPlatform.Presentation.exe trong cấu hình worker.");
    }

    private void StopProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is ProfileEntry selected)
        {
            StopWorkerForProfile(selected, null);
        }
    }
    
    private void StopSingleLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && ProfilesGrid.SelectedItem is ProfileEntry selected)
        {
            StopWorkerForProfile(selected, url);
        }
    }

    private void StopWorkerForProfile(ProfileEntry profile, string? specificUrl)
    {
        try
        {
            bool stopped = false;
            
            var pendingToRemove = _pendingJobs.Where(j => 
                string.Equals(j.ProfilePath, profile.ProfilePath, StringComparison.OrdinalIgnoreCase) &&
                (specificUrl == null || string.Equals(j.Url, specificUrl, StringComparison.OrdinalIgnoreCase))).ToList();
                
            foreach (var p in pendingToRemove)
            {
                _pendingJobs.Remove(p);
                stopped = true;
            }

            var runningToKill = _runningWorkers.Where(w => 
                string.Equals(w.ProfilePath, profile.ProfilePath, StringComparison.OrdinalIgnoreCase) &&
                (specificUrl == null || string.Equals(w.Url, specificUrl, StringComparison.OrdinalIgnoreCase))).ToList();
                
            foreach (var w in runningToKill)
            {
                StopProcess(w.ProcessId);
                _runningWorkers.Remove(w);
                stopped = true;
            }
            
            foreach (ProcessSnapshot session in FindInteractiveProfileProcesses(profile.ProfilePath))
            {
                string? url =
                    TryReadArgument(session.CommandLine, "--automation-url") ??
                    TryReadArgument(session.CommandLine, "--interactive-url");
                if (specificUrl == null || string.Equals(url, specificUrl, StringComparison.OrdinalIgnoreCase))
                {
                    StopProcess(session.ProcessId);
                    stopped = true;
                }
            }

            StatusText.Text = stopped
                ? $"Đã Stop khoá học cho {profile.DisplayName}."
                : $"{profile.DisplayName} không có khoá đang chạy.";
            
            ProcessQueue();
            LoadProfiles(preserveSelection: true);
        }
        catch (Exception exception)
        {
            ShowOperationError("Không thể Stop worker", exception);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not ProfileEntry selected)
        {
            return;
        }

        try
        {
            string profilePath = Path.GetFullPath(selected.ProfilePath);
            EnsureInsideProfileRoot(profilePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                ArgumentList = { profilePath },
            });
        }
        catch (Exception exception)
        {
            ShowOperationError("Không thể mở thư mục profile", exception);
        }
    }

    private void BrowseWorkerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn AutomationPlatform.Presentation.exe",
            Filter = "ACOSE Worker|AutomationPlatform.Presentation.exe|Ứng dụng Windows|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            WorkerExecutableTextBox.Text = dialog.FileName;
        }
    }

    private void SaveHostSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveHostSettings())
        {
            StatusText.Text = "Đã lưu cấu hình worker.";
            LoadProfiles(preserveSelection: true);
        }
    }

    private bool TrySaveHostSettings()
    {
        try
        {
            if (!Uri.TryCreate(ServerUrlTextBox.Text.Trim(), UriKind.Absolute, out Uri? serverUri) ||
                (serverUri.Scheme != Uri.UriSchemeHttps && !serverUri.IsLoopback))
            {
                throw new InvalidOperationException("Server URL phải dùng HTTPS, trừ địa chỉ localhost.");
            }

            string workerExecutable = Path.GetFullPath(WorkerExecutableTextBox.Text.Trim());
            if (!File.Exists(workerExecutable) ||
                !string.Equals(
                    Path.GetFileName(workerExecutable),
                    WorkerProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Worker executable phải trỏ đến AutomationPlatform.Presentation.exe đang tồn tại.");
            }

            string profileRoot = Path.GetFullPath(ProfileRootTextBox.Text.Trim());
            if (!int.TryParse(MaxWorkersTextBox.Text.Trim(), out int maxWorkers) ||
                maxWorkers is < 1 or > 64)
            {
                throw new InvalidOperationException("Số khóa chạy đồng thời phải từ 1 đến 64.");
            }

            _settings = new ManagerSettings
            {
                ServerUrl = serverUri.ToString().TrimEnd('/'),
                WorkerExecutable = workerExecutable,
                ProfileRoot = profileRoot,
                MaxCourseWorkers = maxWorkers,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(_settings, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            SetEnvironment("ACOSE_WORKER_SERVER_URL", _settings.ServerUrl);
            SetEnvironment("ACOSE_WORKER_EXECUTABLE", _settings.WorkerExecutable);
            SetEnvironment("ACOSE_WORKER_PROFILE_ROOT", _settings.ProfileRoot);
            SetEnvironment(
                "ACOSE_WORKER_MAX_COURSE_WORKERS",
                _settings.MaxCourseWorkers.ToString(CultureInfo.InvariantCulture));
            SetProfileRoots(_settings.ProfileRoot);
            ApplySettingsToControls();
            return true;
        }
        catch (Exception exception)
        {
            ShowOperationError("Cấu hình worker chưa hợp lệ", exception);
            return false;
        }
    }

    private static void SetEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    }

    private ProcessSnapshot[] FindInteractiveProfileProcesses(string profilePath)
    {
        string expectedPath = Path.GetFullPath(profilePath);
        return QueryProcesses(WorkerProcessName)
            .Where(process =>
            {
                string? candidate =
                    TryReadArgument(process.CommandLine, "--automation-profile-path") ??
                    TryReadArgument(process.CommandLine, "--interactive-profile-path");
                try
                {
                    return candidate != null && string.Equals(
                        Path.GetFullPath(candidate),
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            })
            .ToArray();
    }

    private static ProcessSnapshot[] QueryProcesses(params string[] processNames)
    {
        try
        {
            var allowedNames = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process");
            using ManagementObjectCollection results = searcher.Get();
            return results.Cast<ManagementObject>()
                .Select(item => new ProcessSnapshot(
                    Convert.ToInt32(item["ProcessId"], CultureInfo.InvariantCulture),
                    Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(item["CommandLine"], CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(item["ExecutablePath"], CultureInfo.InvariantCulture) ?? string.Empty))
                .Where(process => allowedNames.Contains(process.Name))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? TryReadArgument(string commandLine, string argumentName)
    {
        Match match = Regex.Match(
            commandLine,
            $$"""(?:^|\s){{Regex.Escape(argumentName)}}\s+(?:"(?<quoted>[^"]+)"|(?<plain>\S+))""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? (match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value)
            : null;
    }

    private static void StopProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void EnsureInsideProfileRoot(string candidate)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string allowedPrefix = _profileRoot.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Profile nằm ngoài thư mục quản lý được cho phép.");
        }
    }

    private static bool TryGetCourseraUri(string? value, out Uri? uri)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               (uri.Host.Equals("coursera.org", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".coursera.org", StringComparison.OrdinalIgnoreCase));
    }

    private static string RemoveDiacritics(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormD);
        return string.Concat(normalized.Where(character =>
            CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
            .Normalize(NormalizationForm.FormC);
    }

    private static string ShortKey(string value) => value.Length <= 8 ? value : value[..8];

    private void ShowOperationError(string title, Exception exception)
    {
        StatusText.Text = title + ".";
        MessageBox.Show(
            exception.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private sealed record ProfileIdentity(string? FullName);
    private sealed record ProfileMetadata(string? DisplayName, string? LaunchUrl);
    private sealed record ProcessSnapshot(
        int ProcessId,
        string Name,
        string CommandLine,
        string ExecutablePath);

    public sealed record ProfileEntry(
        string ProfileKey,
        string DisplayName,
        string ProfilePath,
        string KindLabel,
        string SessionStatus,
        string LaunchUrl,
        DateTime LastUsed,
        bool HasIdentity,
        bool IsRunning,
        bool CanStartWorker)
    {
        public string LastUsedText => LastUsed.ToString("dd/MM/yyyy HH:mm");
    }

    private sealed class ManagerSettings
    {
        public string ServerUrl { get; set; } = DefaultServerUrl;
        public string WorkerExecutable { get; set; } = string.Empty;
        public string ProfileRoot { get; set; } = string.Empty;
        public int MaxCourseWorkers { get; set; } = 20;
    }
    
    public sealed class LinkItemViewModel
    {
        public string Url { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public SolidColorBrush StatusColor { get; set; } = Brushes.Gray;
        public bool CanStart { get; set; }
        public bool CanStop { get; set; }
        public bool CanModify { get; set; }
    }
}
