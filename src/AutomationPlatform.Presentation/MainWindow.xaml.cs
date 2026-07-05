using AutomationPlatform.Presentation.Services;
using AutomationPlatform.Presentation.ViewModels;
using System.Windows;
using System.Threading.Tasks;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AutomationPlatform.Presentation;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private static readonly HttpClient _httpClient = new HttpClient();
    private List<QuizFeedbackDto> _quizFeedbackList = new List<QuizFeedbackDto>();

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        MainWebView.NavigationCompleted += MainWebView_NavigationCompleted;
        MainWebView.SourceChanged += MainWebView_SourceChanged;

        this.Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Cài đặt User-Agent chuyên dụng đã vượt qua bài test Lockdown Browser
            MainWebView.CoreWebView2InitializationCompleted += (s, args) => {
                if (args.IsSuccess) {
                    MainWebView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 coursera-locking-browser/0.6.5";
                    
                    // Chặn tuyệt đối MỌI popup văng ra (bao gồm cả coursera-lock)
                    MainWebView.CoreWebView2.NewWindowRequested += (senderCore, eventArgs) => {
                        eventArgs.Handled = true; // Chặn mở tab/cửa sổ mới hoàn toàn
                    };
                    
                    MainWebView.CoreWebView2.NavigationStarting += (senderCore, eventArgs) => {
                        if (eventArgs.Uri.StartsWith("coursera-lock:")) {
                            eventArgs.Cancel = true; // Chặn chuyển trang
                        }
                    };
                }
            };
            var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--remote-debugging-port=9222");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, null, options);
            await MainWebView.EnsureCoreWebView2Async(env);
            
            // Tiêm cờ giả lập Lockdown Browser vào React Window object trước khi trang load
            await MainWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                Object.defineProperty(window, 'isLockdownBrowser', { get: () => true, set: () => {} });
                Object.defineProperty(window, 'CourseraLockdownBrowser', { get: () => true, set: () => {} });
                window.localStorage.setItem('isLockdownBrowser', 'true');
            ");
        }
        catch { }

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
                // CHỈ kiểm tra 2 điều kiện chắc chắn:
                
                // Cách 1: Sidebar item đang active có aria-label chứa 'Locked'
                var activeItem = document.querySelector('a[aria-current=""page""]') || 
                                 document.querySelector('li[class*=""selected""] a');
                
                if (activeItem) {
                    var ariaLabel = (activeItem.getAttribute('aria-label') || '').toLowerCase();
                    if (ariaLabel.includes('locked')) {
                        return 'LOCKED';
                    }
                }
                
                // Cách 2: Body text có chữ rõ ràng 'this item is locked'
                // VÀ trang trắng trơn (không có nội dung quiz, video, bài đọc nào)
                var bodyText = document.body.innerText.toLowerCase();
                var hasContent = document.querySelector('.rc-LessonCollectionBody, .rc-SubmissionBody, .rc-PeerReviewBody, video, .rc-CML, textarea, div[role=""radiogroup""], .rc-FormPartsQuestion, div[data-testid*=""part-""]');
                if (!hasContent && bodyText.includes('this item is locked')) {
                    return 'LOCKED';
                }
                
                return 'NOT_LOCKED';
            })();
        ";
        string result = await MainWebView.ExecuteScriptAsync(jsCheckLocked);
        if (result != null && result.Contains("LOCKED"))
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
                // Nếu đang ở bài thi (exam/quiz) hoặc dialogue mà không thấy nút Next thì ĐÓ LÀ BÌNH THƯỜNG (chưa làm xong), không phải ngõ cụt!
                var isExam = window.location.href.includes('/exam/') || window.location.href.includes('/quiz/') || window.location.href.includes('/dialogue/') || window.location.href.includes('/coach/');
                
                if (isPageLoaded && !nextBtn && !isExam) {
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
        _viewModel.StatusText = "Đang check khoá học...";

        string jsCode = @"
            (function() {
                var goToCourseBtn = document.querySelector('button[data-e2e=""enroll-button""]');
                if (goToCourseBtn) {
                    goToCourseBtn.click();
                    return '✅ Khoá học đã đăng kí. Đang vào khoá học...';
                }
        
                var enrollBtn = document.querySelector('button[data-e2e=""EnrollButton""]');
                if (enrollBtn) {
                    enrollBtn.click();
                    return '⚠️ Khoá học chưa đăng kí. Đang tiến hành đăng ký và vào lớp...';
                }
        
                return '❌ Không tìm thấy nút nào có điểm neo data-e2e hợp lệ!';
            })();
        ";

        try
        {
            string result = await MainWebView.ExecuteScriptAsync(jsCode);
            if (result != null)
            {
                result = result.Trim('"');
                _viewModel.StatusText = result;
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Lỗi xử lý tự động: " + ex.Message;
        }
    }

    private async Task CheckModulesAsync()
    {
        _viewModel.StatusText = "🔍 Đang thanh tra tiến độ các Module...";
        await Task.Delay(5000); // Chờ sidebar của React render xong
        string jsCode = @"
            (function() {
                // Lấy tất cả các thẻ <a> đại diện cho Module ở Sidebar bên trái
                var modules = document.querySelectorAll('a[data-testid=""rc-WeekNavigationItem""]');
                
                for (var i = 0; i < modules.length; i++) {
                    var ariaLabel = modules[i].getAttribute('aria-label') || '';
                    var moduleName = modules[i].innerText.trim();
                    
                    // Nếu Module này CHƯA hoàn thành
                    if (!ariaLabel.includes('Completed')) {
                        
                        // Nếu Module này chưa được click (chưa được chọn) -> Ép click chuyển về nó!
                        if (!ariaLabel.includes('selected')) {
                            modules[i].click();
                            return '⚠️ Phát hiện ' + moduleName + ' chưa học! Đã chuyển hướng về đó...';
                        }
                        
                        // Nếu đang ở đúng Module chưa học này rồi thì thôi
                        return '👉 Đang học đúng tiến độ tại: ' + moduleName;
                    }
                }
                return '🏆 Tuyệt vời! Đã hoàn thành toàn bộ các Module!';
            })();
        ";
        try
        {
            string result = await MainWebView.ExecuteScriptAsync(jsCode);
            if (result != null)
            {
                _viewModel.StatusText = result.Trim('"');
                
                if (!result.Contains("🏆")) 
                {
                    await Task.Delay(3000); // Đợi React render danh sách bài học
                    await CheckLessonsAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Lỗi thanh tra Module: " + ex.Message;
        }
    }

    private async Task CheckLessonsAsync()
    {
        _viewModel.StatusText = "🔎 Đang dọn dẹp màn hình và quét bài chưa học...";
        await Task.Delay(2000);

        // Đóng các popup có thể xuất hiện khi vừa vào Module
        string jsDismissEarly = @"
            (function() {
                var btns = Array.from(document.querySelectorAll('button'));
                var closeBtns = btns.filter(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    var aria = (b.getAttribute('aria-label') || '').toLowerCase();
                    return t === 'continue learning' || t === 'got it' || t === 'maybe later' || t === 'start attempt' || t === 'start new attempt' || aria === 'close' || aria.includes('close modal');
                });
                if (closeBtns.length > 0) {
                    closeBtns[0].click();
                }
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsDismissEarly);
        await Task.Delay(1000);

        string jsCode = @"
            (function() {
                // Quét từ trên xuống dưới tìm bài đầu tiên chưa có chữ 'Completed'
                var lessons = document.querySelectorAll('a[data-click-key=""open_course_home.period_page.click.item_link""]');
                
                for (var i = 0; i < lessons.length; i++) {
                    var htmlContent = lessons[i].innerHTML;
                    
                    // Nếu bài này chưa hoàn thành (kể cả nó có nút Resume hay không)
                    if (!htmlContent.includes('>Completed<')) {
                        var nameTag = lessons[i].querySelector('p[data-test=""rc-ItemName""]');
                        var lessonName = nameTag ? nameTag.innerText.trim() : 'bài mới';
                        
                        lessons[i].click();
                        return '👉 Đang tiến vào học bài: ' + lessonName;
                    }
                }
                
                return '🏆 Tuyệt vời! Bạn đã hoàn thành toàn bộ bài học trong Module này!';
            })();
        ";

        try
        {
            string result = await MainWebView.ExecuteScriptAsync(jsCode);
            if (result != null)
            {
                _viewModel.StatusText = result.Trim('"');
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Lỗi thanh tra bài học: " + ex.Message;
        }
    }

    private async Task DismissAnyGlobalPopupsAsync()
    {
        string jsDismiss = @"
            (function() {
                var btns = Array.from(document.querySelectorAll('button'));
                var closeBtns = btns.filter(b => {
                    var t = (b.innerText || b.textContent || '').trim().toLowerCase();
                    var aria = (b.getAttribute('aria-label') || '').toLowerCase();
                    return (t === 'continue learning' || t === 'got it' || t === 'maybe later' || t === 'continue' || t === 'start attempt' || t === 'start new attempt' || aria === 'close' || aria.includes('close modal')) && b.offsetWidth > 0 && b.offsetHeight > 0;
                });
                if (closeBtns.length > 0) {
                    closeBtns[0].click();
                }
            })();
        ";
        try { await MainWebView.ExecuteScriptAsync(jsDismiss); await Task.Delay(500); } catch { }
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

            // 1.5. ĐÓNG MỌI POPUP CHẮN MÀN HÌNH (Ví dụ: "You've completed today's goals!")
            string jsDismissPopup = @"
                (function() {
                    var btns = Array.from(document.querySelectorAll('button'));
                    var continueBtn = btns.find(b => {
                        var t = (b.innerText || '').trim().toLowerCase();
                        var aria = (b.getAttribute('aria-label') || '').toLowerCase();
                        return t === 'continue learning' || t === 'got it' || t === 'maybe later' || aria === 'close' || aria.includes('close modal');
                    });
                    
                    if (continueBtn && !continueBtn.disabled) {
                        continueBtn.click();
                        return 'DISMISSED';
                    }
                    return 'NO_POPUP';
                })();
            ";
            await MainWebView.ExecuteScriptAsync(jsDismissPopup);

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

    private async Task HandleUngradedAppAsync()
    {
        if (await CheckForLockedScreenAndReloadAsync()) return;

        _viewModel.StatusText = "🛠️ Đang xử lý Thực hành/Lab (Ungraded App Item)...";
        await Task.Delay(3000);
        await DismissAnyGlobalPopupsAsync();

        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "⏭️ Bài Lab này đã xong! Đang chuyển bài...";
            return;
        }

        // Tick Honor Code
        string jsHandleApp = @"
            (function() {
                var honorCode = document.querySelector('input[type=""checkbox""]');
                if (honorCode && !honorCode.checked) {
                    honorCode.click();
                }
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsHandleApp);
        await Task.Delay(1500);

        // Click Next
        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "✅ Đã xác nhận Honor Code cho Lab! Đang chuyển bài.";
            return;
        }

        // Bấm Launch App nếu Next chưa được 
        string jsLaunch = @"
            (function() {
                var btns = Array.from(document.querySelectorAll('button, a'));
                var launchBtn = btns.find(b => (b.innerText || '').trim().toLowerCase().includes('launch app'));
                if (launchBtn && !launchBtn.disabled) {
                    launchBtn.click();
                    return 'LAUNCHED';
                }
                return 'NOT_FOUND';
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsLaunch);
        await Task.Delay(2000);
        
        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "✅ Đã Launch Lab! Đang chuyển bài.";
            return;
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
    private int _quizAttemptCount = 0;
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
                
                // Tìm chữ 'You passed!' trên trang
                var hasPassed = bodyText.includes('You passed!');
                
                // Tìm chữ 'Your grade:' với điểm >= 80%
                var gradeMatch = bodyText.match(/Your grade:\s*(\d+)%/);
                if (gradeMatch && parseInt(gradeMatch[1]) >= 80) {
                    hasPassed = true;
                }
                
                // Tìm nút 'Next item' (dạng button xanh có chữ Next item)
                var btns = Array.from(document.querySelectorAll('button, a'));
                var hasNextItemBtn = btns.some(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    return t.includes('next item');
                });
                if (hasNextItemBtn && !bodyText.includes('didn')) {
                    hasPassed = true;
                }
                
                // Tìm chữ 'You didn\'t pass' hoặc tương tự
                var hasFailed = bodyText.includes('You didn') || bodyText.includes('not pass') || bodyText.includes('didn\'t pass');
                
                // Nếu vừa có pass vừa có fail text → ưu tiên pass (có thể đang xem feedback của bài đã pass)
                if (hasPassed && hasFailed) {
                    // Nếu có grade >= 80 thì chắc chắn pass
                    if (gradeMatch && parseInt(gradeMatch[1]) >= 80) {
                        hasFailed = false;
                    }
                }
                
                // Tìm nút Start/Resume/Retake (nghĩa là chưa vào làm bài)
                var hasStartBtn = btns.some(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    return t === 'start assignment' || t === 'resume assignment';
                });
                var hasRetakeBtn = btns.some(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    return t === 'retake assignment' || t === 'try again' || t === 'retry';
                });
                var hasFeedbackBtn = btns.some(b => {
                    var t = (b.innerText || '').trim().toLowerCase();
                    return t === 'view feedback';
                });
                
                // Tìm nút Next
                var nextBtn = document.querySelector('button[aria-label=""Go to next item""]');
                var hasNextPrimary = nextBtn && nextBtn.className.includes('cds-button-primary');
                
                if (hasPassed) {
                    return 'PASSED|hasNext=' + !!hasNextPrimary;
                } else if (hasFailed) {
                    return 'FAILED|hasFeedback=' + hasFeedbackBtn + '|hasRetake=' + hasRetakeBtn;
                } else if (hasStartBtn) {
                    return 'NEW';
                }
                return 'UNKNOWN';
            })();
        ";

        string statusResult = "";
        try
        {
            statusResult = (await MainWebView.ExecuteScriptAsync(jsCheckPassStatus))?.Trim('"') ?? "";
            _viewModel.StatusText = $"🔍 Trạng thái Quiz: {statusResult}";
        }
        catch { }

        // ========== BƯỚC 2: XỬ LÝ TỪNG TRẠNG THÁI ==========
        
        // --- TRẠNG THÁI 1: ĐÃ PASS ---
        if (statusResult.StartsWith("PASSED"))
        {
            _viewModel.StatusText = "✅ Quiz này đã Pass! Đang chuyển bài tiếp theo...";
            
            // Thử bấm Next
            if (await CheckLessonCompletedAndClickNextAsync(true))
            {
                _viewModel.StatusText = "⏭️ Đã Pass và chuyển bài thành công!";
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

            // 2. JS XỬ LÝ POPUP (HONOR CODE) 
            string jsDismissPopup = @"
                (function() {
                    var btns = Array.from(document.querySelectorAll('button'));
                    var continueBtn = btns.find(b => {
                        var t = (b.innerText || b.textContent || '').trim().toLowerCase();
                        var aria = (b.getAttribute('aria-label') || '').toLowerCase();
                        return (t === 'continue learning' || t === 'got it' || t === 'maybe later' || t === 'continue' || t === 'i agree' || t === 'start attempt' || t === 'start new attempt' || aria === 'close' || aria.includes('close modal')) && b.offsetWidth > 0 && b.offsetHeight > 0;
                    });
                    
                    if (continueBtn && !continueBtn.disabled) {
                        var container = continueBtn.closest('div[role=""dialog""]') || document;
                        var checkboxes = container.querySelectorAll('input[type=""checkbox""]');
                        checkboxes.forEach(cb => { if (!cb.checked && cb.offsetWidth > 0) cb.click(); });
                        
                        continueBtn.click();
                        return 'DISMISSED';
                    }
                    return 'NO_POPUP';
                })();
            ";
            
            _viewModel.StatusText = "🚪 Đang dọn dẹp Popup và mở cửa phòng thi...";
            
            // CHIẾN THUẬT BREACH & CLEAR: 
            bool isFeedback = false;
            bool hasClickedStart = false;
            for (int i = 0; i < 6; i++)
            {
                // Diệt popup (nếu có)
                await MainWebView.ExecuteScriptAsync(jsDismissPopup);
                
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

        // Tiến hành gom đề bài, ném cho DeepSeek và điền đáp án
        await SolveQuizQuestionsAsync();
        
        }
        finally
        {
            _isHandlingQuiz = false;
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
                string aiAnswer = await GetAnswerFromDeepSeekAsync(
                    questionText, isDiscussion: true,
                    customSystemPrompt: "You are a student completing a reflective activity. Answer the question thoughtfully in 2-3 sentences in English. Be specific and personal-sounding. Return ONLY the answer text, no preamble.");

                if (aiAnswer.StartsWith("ERROR")) continue;
                aiAnswer = aiAnswer.Trim('"').Trim();

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

        string jsSubmit = @"
            (function() {
                var honorCode = document.getElementById('agreement-checkbox-base');
                if (honorCode && !honorCode.checked) honorCode.click();
                
                var submitBtn = document.querySelector('button[data-testid=""submit-button""]');
                if (submitBtn) submitBtn.scrollIntoView({ block: 'center' });
                
                setTimeout(function() {
                    var btn = document.querySelector('button[data-testid=""submit-button""]');
                    if (btn && !btn.disabled) btn.click();
                }, 500);
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsSubmit);
        await Task.Delay(2500);

        // Confirm popup nếu có
        string jsConfirm = @"
            (function() {
                var dialogs = document.querySelectorAll('div[role=""dialog""]');
                var clicked = false;
                
                // Cách 1: Tìm trong dialog
                for (var d of dialogs) {
                    var btns = Array.from(d.querySelectorAll('button'));
                    var ok = btns.find(b => {
                        var t = (b.innerText || '').trim().toLowerCase();
                        return (t === 'submit' || t === 'yes' || t === 'confirm') && 
                               b.getAttribute('data-testid') !== 'submit-button';
                    });
                    if (ok) { ok.click(); clicked = true; break; }
                }
                
                // Cách 2: Quét toàn bộ trang tìm nút Submit thứ 2 (popup)
                if (!clicked) {
                    var allBtns = Array.from(document.querySelectorAll('button'));
                    var secondarySubmit = allBtns.find(b => 
                        (b.innerText || '').trim().toLowerCase() === 'submit' && 
                        b.getAttribute('data-testid') !== 'submit-button' &&
                        b.offsetWidth > 0
                    );
                    if (secondarySubmit) {
                        secondarySubmit.click();
                    }
                }
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsConfirm);

        _viewModel.StatusText = "🏆 Đã nộp bài tự luận! Đang tải lại trang...";
        await Task.Delay(8000);
        _hasExtractedFeedbackThisSession = false;
        MainWebView.Reload();
    }

    private async Task SolveQuizQuestionsAsync()
    {
        _viewModel.StatusText = "🔍 Đang quét toàn bộ đề thi...";

        // Ưu tiên: Nếu là quiz tự luận (textarea) thì dùng handler riêng
        var taCountRaw = await MainWebView.ExecuteScriptAsync(
            "(function(){var c=0;document.querySelectorAll('div[data-testid^=\"part-Submission_\"]').forEach(q=>{if(q.querySelector('textarea'))c++;});return c;})()");
        if (int.TryParse(taCountRaw, out int taNum) && taNum > 0)
        {
            _viewModel.StatusText = $"✍️ Phát hiện {taNum} câu tự luận! Chuyển sang chế độ Essay...";
            await SolveOpenEndedQuizAsync();
            return;
        }
        
        string jsExtract = @"
            (function() {
                var questions = [];
                // Tìm tất cả các khối câu hỏi
                var qElements = document.querySelectorAll('div[data-testid^=""part-Submission_""]');
                qElements.forEach((q, index) => {
                    var promptEl = q.querySelector('div[id^=""prompt-""]');
                    var questionText = promptEl ? promptEl.innerText.trim() : """";
                    
                    var options = [];
                    // Quét các nút Radio hoặc Checkbox
                    var optionLabels = q.querySelectorAll('label');
                    optionLabels.forEach((lbl) => {
                        var input = lbl.querySelector('input[type=""radio""], input[type=""checkbox""]');
                        var textEl = lbl.querySelector('.cds-checkboxAndRadio-labelText');
                        var text = textEl ? textEl.innerText.trim() : """";
                        if (input && text) {
                            options.push({ Text: text, InputId: input.id });
                        }
                    });
                    
                    if (questionText && options.length > 0) {
                        questions.push({ Index: index, Question: questionText, Options: options });
                    }
                });
                return JSON.stringify(questions);
            })();
        ";

        string rawResult = await MainWebView.ExecuteScriptAsync(jsExtract);
        if (string.IsNullOrEmpty(rawResult) || rawResult == "null" || rawResult == """[]""")
        {
            _viewModel.StatusText = "⚠️ Không tìm thấy câu hỏi nào. Đang thử lại...";
            return;
        }

        // WebView2 trả về chuỗi JSON bị bọc bởi dấu nháy "", cần gỡ ra
        string json = System.Text.RegularExpressions.Regex.Unescape(rawResult.Substring(1, rawResult.Length - 2));
        
        var questionList = System.Text.Json.JsonSerializer.Deserialize<List<QuizQuestion>>(json);
        if (questionList == null || questionList.Count == 0)
        {
            _viewModel.StatusText = "⚠️ Lỗi khi đọc câu hỏi.";
            return;
        }

        _viewModel.StatusText = $"🤖 Đã gom được {questionList.Count} câu hỏi! Đang gửi cho DeepSeek...";

        // Xây dựng Prompt ném cho DeepSeek
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Solve the following multiple-choice questions.");
        
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
                    sb.AppendLine($"  You missed some correct options! You MUST pick AT LEAST {f.CorrectAnswers.Count + 1} options.");
                    if (f.CorrectAnswers != null && f.CorrectAnswers.Count > 0)
                    {
                        sb.AppendLine($"  These options ARE CORRECT and MUST be included in your answer: {string.Join(", ", f.CorrectAnswers.Select(a => $"\"{a}\""))}");
                    }
                }
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("You MUST return ONLY a raw JSON array of arrays. Each inner array corresponds to a question and contains the EXACT text of the correct options.");
        sb.AppendLine("For questions with multiple correct answers (checkboxes), include all correct options in the inner array.");
        sb.AppendLine("Do NOT include any markdown formatting like ```json. ONLY return the raw array like: [[\"Answer 1A\", \"Answer 1B\"], [\"Answer 2 text\"], [\"Answer 3 text\"]]");
        sb.AppendLine();
        foreach (var q in questionList)
        {
            sb.AppendLine($"Q{q.Index + 1}: {q.Question}");
            foreach (var opt in q.Options)
            {
                sb.AppendLine($"- {opt.Text}");
            }
            sb.AppendLine();
        }

        string aiResponse = await GetAnswerFromDeepSeekAsync(sb.ToString(), false);
        
        // Làm sạch response (Đề phòng AI vẫn bọc markdown)
        aiResponse = aiResponse.Replace("```json", "").Replace("```", "").Trim();
        
        try
        {
            var selectedAnswers = System.Text.Json.JsonSerializer.Deserialize<List<List<string>>>(aiResponse);
            if (selectedAnswers != null)
            {
                _viewModel.StatusText = $"✅ AI đã giải xong ({selectedAnswers.Count} câu)! FeedbackCount={_quizFeedbackList.Count}. Đang điền đáp án...";
                
                for (int i = 0; i < selectedAnswers.Count; i++)
                {
                    if (i >= questionList.Count) break;
                    
                    var ansList = selectedAnswers[i];
                    var q = questionList[i];

                    // === BỘ NÃO SẮT: Ép cứng đáp án từ Feedback, không phụ thuộc AI ===
                    var feedback = _quizFeedbackList.FirstOrDefault(f =>
                    {
                        string cleanQ = System.Text.RegularExpressions.Regex.Replace(q.Question, "[^a-zA-Z0-9]", "").ToLower();
                        string cleanF = System.Text.RegularExpressions.Regex.Replace(f.Question, "[^a-zA-Z0-9]", "").ToLower();
                        return cleanQ.Contains(cleanF) || cleanF.Contains(cleanQ);
                    });

                    if (feedback != null && feedback.IsMissingAnswers)
                    {
                        // Bước 1: Ép cứng tất cả đáp án đã biết là ĐÚNG vào danh sách
                        foreach (var correctAns in feedback.CorrectAnswers)
                        {
                            bool alreadyInList = ansList.Any(a =>
                            {
                                string c1 = System.Text.RegularExpressions.Regex.Replace(a, "[^a-zA-Z0-9]", "").ToLower();
                                string c2 = System.Text.RegularExpressions.Regex.Replace(correctAns, "[^a-zA-Z0-9]", "").ToLower();
                                return c1.Contains(c2) || c2.Contains(c1);
                            });
                            if (!alreadyInList)
                            {
                                ansList.Add(correctAns);
                            }
                        }

                        // Bước 2: Nếu AI vẫn chưa thêm đáp án MỚI nào ngoài những cái đã biết,
                        // thì ta tự ý thử thêm 1 đáp án chưa từng thử (brute force)
                        var knownTexts = feedback.CorrectAnswers.Concat(feedback.WrongAnswers).ToList();
                        bool hasNewAnswer = ansList.Any(a =>
                        {
                            string ca = System.Text.RegularExpressions.Regex.Replace(a, "[^a-zA-Z0-9]", "").ToLower();
                            return !knownTexts.Any(k =>
                            {
                                string ck = System.Text.RegularExpressions.Regex.Replace(k, "[^a-zA-Z0-9]", "").ToLower();
                                return ca.Contains(ck) || ck.Contains(ca);
                            });
                        });

                        if (!hasNewAnswer)
                        {
                            // Tìm option chưa từng thử (không nằm trong CorrectAnswers và WrongAnswers)
                            foreach (var opt in q.Options)
                            {
                                string co = System.Text.RegularExpressions.Regex.Replace(opt.Text, "[^a-zA-Z0-9]", "").ToLower();
                                bool isKnown = knownTexts.Any(k =>
                                {
                                    string ck = System.Text.RegularExpressions.Regex.Replace(k, "[^a-zA-Z0-9]", "").ToLower();
                                    return co.Contains(ck) || ck.Contains(co);
                                });
                                if (!isKnown)
                                {
                                    ansList.Add(opt.Text);
                                    _viewModel.StatusText = $"🧪 Thử thêm đáp án mới: {opt.Text.Substring(0, Math.Min(opt.Text.Length, 40))}...";
                                    break; // Chỉ thêm 1 đáp án mới mỗi lần thử
                                }
                            }
                        }
                    }
                    
                    _viewModel.StatusText = $"📝 Q{i+1}: AI chọn {ansList.Count} đáp án: {string.Join(", ", ansList.Select(a => a.Length > 30 ? a.Substring(0,30)+"..." : a))}";
                    await Task.Delay(1000); // Cho user thấy debug info

                    foreach (var opt in q.Options)
                    {
                        bool shouldBeChecked = ansList.Any(ans =>
                        {
                            string cleanOpt = System.Text.RegularExpressions.Regex.Replace(opt.Text, "[^a-zA-Z0-9]", "").ToLower();
                            string cleanAns = System.Text.RegularExpressions.Regex.Replace(ans, "[^a-zA-Z0-9]", "").ToLower();
                            return cleanOpt.Contains(cleanAns) || cleanAns.Contains(cleanOpt);
                        });
                        
                        string jsClick = $@"
                            var el = document.getElementById('{opt.InputId}');
                            if (el) {{
                                var wantChecked = {(shouldBeChecked ? "true" : "false")};
                                if (el.checked !== wantChecked) {{
                                    el.click();
                                }}
                            }}
                        ";
                        await MainWebView.ExecuteScriptAsync(jsClick);
                        await Task.Delay(200); // Rút ngắn thời gian vì phải check tất cả các option
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "❌ AI trả về sai định dạng! Đang thử lại...";
            return;
        }

        _viewModel.StatusText = "🚀 Đang cuộn xuống cuối trang để nộp bài...";
        
        // Cuộn xuống cuối trang để nút Submit và Honor Code hiện ra
        await MainWebView.ExecuteScriptAsync("window.scrollTo(0, document.body.scrollHeight);");
        await Task.Delay(1000);
        
        // Tick Honor Code và Submit lần 1
        string jsSubmit1 = @"
            (function() {
                // Cuộn tới nút Submit trước
                var submitBtn = document.querySelector('button[data-testid=""submit-button""]');
                if (submitBtn) {
                    submitBtn.scrollIntoView({ behavior: 'smooth', block: 'center' });
                }
                
                var honorCode = document.getElementById('agreement-checkbox-base');
                if (honorCode && !honorCode.checked) {
                    honorCode.click();
                }
                
                // Đợi 1 tick cho Honor Code kích hoạt Submit
                setTimeout(function() {
                    var btn = document.querySelector('button[data-testid=""submit-button""]');
                    if (btn && !btn.disabled) {
                        btn.click();
                    }
                }, 500);
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsSubmit1);
        
        // Đợi 2 giây để Popup ""Ready to submit?"" hiện lên rõ ràng
        await Task.Delay(2000);
        
        // Submit lần 2 (Bấm Confirm trong Popup)
        string jsSubmit2 = @"
            (function() {
                var dialogs = document.querySelectorAll('div[role=""dialog""]');
                for(var i=0; i<dialogs.length; i++) {
                    var btns = Array.from(dialogs[i].querySelectorAll('button'));
                    // Tìm nút Submit nhưng không phải là nút Submit chính (data-testid=submit-button)
                    var confirmBtn = btns.find(b => 
                        (b.innerText || '').trim().toLowerCase() === 'submit' && 
                        b.getAttribute('data-testid') !== 'submit-button'
                    );
                    if (confirmBtn) {
                        confirmBtn.click();
                        break;
                    }
                }
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsSubmit2);
        
        _viewModel.StatusText = "🏆 Đã nộp bài Quiz! Đang chờ Coursera chấm điểm...";
        
        // Đợi 6 giây để hệ thống chấm điểm và lưu kết quả vào server
        await Task.Delay(8000);
        
        // Reset lại biến cờ để nếu rớt lần này, hệ thống sẽ được phép click View Feedback đọc Sổ Đen lại
        _hasExtractedFeedbackThisSession = false;
        
        _viewModel.StatusText = "🔄 Đã nộp bài! Đang tải lại trang để kiểm tra kết quả (Pass/Fail)...";
        MainWebView.Reload();
    }

    private async Task<string> GetAnswerFromDeepSeekAsync(string questionText, bool isDiscussion = false, string customSystemPrompt = null)
    {
        string apiKey = "sk-3adcdb7dc707481ca21fa63471d25b46";
        string apiUrl = "https://api.deepseek.com/chat/completions";

        string sysPrompt = customSystemPrompt;
        if (string.IsNullOrEmpty(sysPrompt))
        {
            sysPrompt = isDiscussion 
                ? "Bạn là một học viên đang tham gia khóa học Coursera. Hãy đọc nội dung trang web và viết MỘT BÀI LUẬN/THẢO LUẬN NGẮN (khoảng 3-4 câu) bằng tiếng Anh để đăng lên diễn đàn. Chỉ trả về nội dung bài viết, không thêm lời chào hay giải thích."
                : "Bạn là một AI chuyên giải bài tập trắc nghiệm. Người dùng sẽ đưa câu hỏi và các đáp án [A, B, C, D...]. Bạn PHẢI CHỌN 1 ĐÁP ÁN ĐÚNG NHẤT và CHỈ TRẢ VỀ ĐÚNG NỘI DUNG CỦA ĐÁP ÁN ĐÓ. Tuyệt đối không giải thích, không thêm chữ 'Đáp án là', không dùng dấu ngoặc kép.";
        }

        // Cấu trúc JSON gửi lên DeepSeek (chuẩn OpenAI)
        var requestBody = new
        {
            model = "deepseek-chat", // Dùng V3 chat cho nhanh và chuẩn
            messages = new[]
            {
                new { 
                    role = "system", 
                    content = sysPrompt
                },
                new { 
                    role = "user", 
                    content = questionText 
                }
            },
            temperature = isDiscussion ? 0.7 : 0.1 // Thảo luận cần sáng tạo một chút, trắc nghiệm thì cần chính xác
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                return "ERROR_API: " + err;
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(responseString);
            
            string answer = jsonDoc.RootElement
                                   .GetProperty("choices")[0]
                                   .GetProperty("message")
                                   .GetProperty("content")
                                   .GetString();
                                   
            return answer?.Trim();
        }
        catch (Exception ex)
        {
            return "ERROR_EXCEPTION: " + ex.Message;
        }
    }

    private async Task HandleDiscussionAsync()
    {
        _viewModel.StatusText = "🗣️ Đang xử lý bài Thảo luận (Discussion Prompt)...";
        await Task.Delay(3000);
        await DismissAnyGlobalPopupsAsync();

        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "⏭️ Bài Thảo luận này đã xong! Đang chuyển bài...";
            return;
        }

        // 1. Quét nội dung trang web để lấy câu hỏi
        string jsGetContent = @"
            (function() {
                var container = document.querySelector('.rc-LessonCollectionBody') || document.body;
                return container.innerText;
            })();
        ";
        
        string pageContent = await MainWebView.ExecuteScriptAsync(jsGetContent);
        if (string.IsNullOrEmpty(pageContent) || pageContent == "null") return;

        // 2. Nhờ AI viết bài
        _viewModel.StatusText = "🧠 Đang nhờ DeepSeek AI viết bài thảo luận (English)...";
        string aiResponse = await GetAnswerFromDeepSeekAsync(pageContent, isDiscussion: true);
        
        if (aiResponse.StartsWith("ERROR"))
        {
            _viewModel.StatusText = "❌ Lỗi AI: " + aiResponse;
            return;
        }

        // 3. Dùng CDP (Chrome DevTools Protocol) để giả lập gõ phím ở cấp độ Trình duyệt
        _viewModel.StatusText = "⌨️ Đang giả lập gõ phím để vượt qua React...";

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
        await MainWebView.ExecuteScriptAsync(jsFocus);

        // Gõ phím thực sự qua CDP (Tuyệt chiêu phá vỡ mọi lớp phòng ngự của React)
        var payload = new { text = aiResponse + " " }; 
        string jsonPayload = JsonSerializer.Serialize(payload);
        await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", jsonPayload);
        
        // Nghỉ 1 giây để React (Slate JS) kịp cập nhật state và kích hoạt nút Reply
        await Task.Delay(1000);

        // 4. Tìm và bấm nút Reply
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
        await MainWebView.ExecuteScriptAsync(jsClickReply);

        _viewModel.StatusText = "✅ Đã đăng bài thảo luận! Đang tải lại trang để xác nhận...";
        
        // Đợi 3 giây cho Server của Coursera kịp lưu bài
        await Task.Delay(3000);
        
        // F5 Tải lại trang (Reload)
        MainWebView.CoreWebView2.Reload();
        
        // KẾT THÚC HÀM Ở ĐÂY. 
        // Khi trang tải lại xong, sự kiện NavigationCompleted sẽ tự động mồi lại hàm này.
        // Nhưng ở lần chạy thứ 2, nút Next đã màu xanh -> Nó sẽ chui vào lệnh if đầu tiên và bấm Next chuyển bài!
    }

    private async Task HandleReadingLessonAsync()
    {
        if (await CheckForLockedScreenAndReloadAsync()) return;

        _viewModel.StatusText = "📖 Đang xử lý bài Đọc (Reading)...";
        await Task.Delay(2000); 
        await DismissAnyGlobalPopupsAsync();

        // 1. Kiểm tra nếu đã xong từ trước
        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "⏭️ Tài liệu này đã đọc xong! Đang chuyển bài...";
            return;
        }

        bool isCompleted = false;

        // Vòng lặp chờ hoặc tương tác để hoàn thành bài đọc
        while (!isCompleted)
        {
            // Đóng các popup chắn màn hình (nếu có)
            string jsDismissPopup = @"
                (function() {
                    var btns = Array.from(document.querySelectorAll('button'));
                    var continueBtn = btns.find(b => {
                        var t = (b.innerText || '').trim().toLowerCase();
                        var aria = (b.getAttribute('aria-label') || '').toLowerCase();
                        return t === 'continue learning' || t === 'got it' || t === 'maybe later' || aria === 'close' || aria.includes('close modal');
                    });
                    if (continueBtn) { continueBtn.click(); return 'DISMISSED'; }
                })();
            ";
            await MainWebView.ExecuteScriptAsync(jsDismissPopup);

            // Cuộn trang xuống cuối cùng (Giả lập hành vi đọc kéo chuột)
            await MainWebView.ExecuteScriptAsync("window.scrollTo(0, document.body.scrollHeight);");
            
            // Một số bài đọc trên Coursera có nút "Mark as completed" ở cuối trang
            string clickMarkCompletedJs = @"
                (function() {
                    var btns = Array.from(document.querySelectorAll('button'));
                    var markBtn = btns.find(b => (b.innerText || '').trim() === 'Mark as completed');
                    if (markBtn && !markBtn.disabled) {
                        markBtn.click();
                    }
                })();
            ";
            await MainWebView.ExecuteScriptAsync(clickMarkCompletedJs);

            // Đợi 2 giây
            await Task.Delay(2000);

            // Kiểm tra xem nút Next Coursera đã xanh chưa
            isCompleted = await CheckLessonCompletedAndClickNextAsync();
            if (isCompleted) break;
        }

        _viewModel.StatusText = "✅ Đã đọc xong tài liệu! Chuyển bài.";
    }


    private async Task HandlePeerAssignmentAsync()
    {
        _viewModel.StatusText = "✍️ Đang xử lý bài tập tự luận chấm chéo (Peer-graded)...";
        await Task.Delay(3000);
        
        if (await CheckForLockedScreenAndReloadAsync()) return;
        
        await DismissAnyGlobalPopupsAsync();

        if (await CheckLessonCompletedAndClickNextAsync())
        {
            _viewModel.StatusText = "⏭️ Bài tập này đã làm xong! Đang chuyển bài...";
            return;
        }

        // Tạm thời tự động chuyển sang tab "My submission" nếu có
        string jsSwitchTab = @"
            (function() {
                var tabs = Array.from(document.querySelectorAll('[role=""tab""], button, a, div, span, li'));
                var subTab = tabs.find(b => (b.innerText || b.textContent || '').trim().toLowerCase() === 'my submission');
                if (subTab) {
                    subTab.click();
                    return 'CLICKED';
                }
                return 'NOT_FOUND';
            })();
        ";
        
        bool tabClicked = false;
        for (int i = 0; i < 5; i++)
        {
            string res = await MainWebView.ExecuteScriptAsync(jsSwitchTab);
            if (res != null && res.Contains("CLICKED"))
            {
                tabClicked = true;
                break;
            }
            await Task.Delay(1000);
        }
        
        await Task.Delay(2000); // Chờ giao diện tab render
        await SolvePeerAssignmentAsync();
    }

    public class PeerAssignmentPartDto
    {
        public string Id { get; set; }
        public string Prompt { get; set; }
        public bool IsTitle { get; set; }
        public int Index { get; set; }
    }

    private async Task SolvePeerAssignmentAsync()
    {
        _viewModel.StatusText = "🔍 Đang quét đề bài Tự luận...";

        string jsExtract = @"
            (function() {
                var parts = [];
                
                var titleInput = document.getElementById('title');
                if (titleInput) {
                    parts.push({ Id: 'title', Prompt: 'Write ONE short creative phrase (max 5 words) as a title for a project about the future.', IsTitle: true, Index: -1 });
                }

                var qElements = document.querySelectorAll('.rc-SubmissionPartEditView');
                qElements.forEach((q, index) => {
                    var promptEl = q.querySelector('div[data-testid=""cml-viewer""]');
                    var questionText = promptEl ? promptEl.innerText.trim() : '';
                    
                    var editor = q.querySelector('div[role=""textbox""]');
                    if (editor && questionText) {
                        parts.push({ Id: 'editor_' + index, Prompt: questionText, IsTitle: false, Index: index });
                    }
                });

                return JSON.stringify(parts);
            })();
        ";

        string rawResult = await MainWebView.ExecuteScriptAsync(jsExtract);
        if (string.IsNullOrEmpty(rawResult) || rawResult == "null" || rawResult.Contains("[]"))
        {
            _viewModel.StatusText = "⚠️ Không tìm thấy khung điền bài nào. Có thể đang ở tab khác hoặc khóa học cấm nộp.";
            return;
        }

        string json = System.Text.RegularExpressions.Regex.Unescape(rawResult.Substring(1, rawResult.Length - 2));
        var partList = System.Text.Json.JsonSerializer.Deserialize<List<PeerAssignmentPartDto>>(json);

        if (partList == null || partList.Count == 0)
        {
            _viewModel.StatusText = "⚠️ Không tìm thấy khung điền bài nào. Chuyển JSON thất bại.";
            return;
        }

        _viewModel.StatusText = $"🤖 Đã gom được {partList.Count} câu hỏi! Đang gửi cho DeepSeek...";

        foreach (var part in partList)
        {
            string aiResponse;
            if (part.IsTitle)
            {
                aiResponse = await GetAnswerFromDeepSeekAsync(part.Prompt, false, "You are a creative writer. Reply with ONLY the title. No quotes, no markdown, no explanation.");
            }
            else
            {
                aiResponse = await GetAnswerFromDeepSeekAsync(part.Prompt, true);
            }

            if (aiResponse.StartsWith("ERROR")) continue;

            aiResponse = aiResponse.Replace("```", "").Replace("\"", "").Trim();

            string jsFocus = $@"
                (function() {{
                    var el = null;
                    if ('{part.IsTitle}' === 'True') {{
                        el = document.getElementById('title');
                    }} else {{
                        var parts = document.querySelectorAll('.rc-SubmissionPartEditView');
                        if ({part.Index} < parts.length) {{
                            el = parts[{part.Index}].querySelector('div[role=""textbox""]');
                        }}
                    }}

                    if (el) {{
                        el.focus();
                        if (el.tagName === 'DIV') {{
                            var s = window.getSelection();
                            var r = document.createRange();
                            r.selectNodeContents(el);
                            s.removeAllRanges();
                            s.addRange(r);
                            document.execCommand('delete', false, null);
                        }} else {{
                            var proto = el.tagName === 'INPUT' ? window.HTMLInputElement.prototype : window.HTMLTextAreaElement.prototype;
                            var nativeSetter = Object.getOwnPropertyDescriptor(proto, 'value').set;
                            if (nativeSetter) {{
                                nativeSetter.call(el, '');
                            }} else {{
                                el.value = '';
                            }}
                            el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                            
                            // Dự phòng select All
                            el.select();
                            document.execCommand('delete', false, null);
                        }}
                        
                        return 'OK';
                    }}
                    return 'NOT_FOUND';
                }})();
            ";
            await MainWebView.ExecuteScriptAsync(jsFocus);

            var payload = new { text = aiResponse + " " };
            string jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
            await MainWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", jsonPayload);
            
            await Task.Delay(1500); 
        }

        _viewModel.StatusText = "🚀 Đang ký tên Honor Code và Submit...";

        string jsSubmit = @"
            (function() {
                var honorCode = document.getElementById('agreement-checkbox-base');
                if (honorCode && !honorCode.checked) {
                    honorCode.click();
                }
                
                var btns = Array.from(document.querySelectorAll('button'));
                var submitBtn = document.querySelector('button[data-testid=""preview""]') || btns.find(b => (b.innerText || '').trim().toLowerCase() === 'submit');
                if (submitBtn && !submitBtn.disabled) {
                    submitBtn.click();
                }
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsSubmit);
        
        await Task.Delay(2000);
        
        string jsConfirm = @"
            (function() {
                var dialogs = document.querySelectorAll('div[role=""dialog""]');
                for(var i=0; i<dialogs.length; i++) {
                    var btns = Array.from(dialogs[i].querySelectorAll('button'));
                    var confirmBtn = btns.find(b => (b.innerText || '').trim().toLowerCase() === 'submit' || (b.innerText || '').trim().toLowerCase() === 'yes' || (b.innerText || '').trim().toLowerCase() === 'confirm');
                    if (confirmBtn) {
                        confirmBtn.click();
                        break;
                    }
                }
            })();
        ";
        await MainWebView.ExecuteScriptAsync(jsConfirm);

        _viewModel.StatusText = "🏆 Đã nộp bài Tự luận! Đang chờ Coursera xử lý...";
        
        // Chờ Coursera xử lý submit (KHÔNG reload trang)
        // Chỉ đợi DOM cập nhật và check sidebar có tick xanh chưa
        for (int waitLoop = 0; waitLoop < 10; waitLoop++)
        {
            await Task.Delay(3000);
            
            // Check sidebar xem bài đã có tick xanh chưa
            string jsCheckSidebar = @"
                (function() {
                    // Tìm item đang active trong sidebar
                    var items = document.querySelectorAll('a[data-click-key*=""item_link""]');
                    for (var i = 0; i < items.length; i++) {
                        var html = items[i].innerHTML;
                        if (items[i].getAttribute('aria-current') === 'page' || items[i].classList.contains('rc-ItemLink--active')) {
                            if (html.includes('>Completed<')) {
                                return 'COMPLETED';
                            }
                        }
                    }
                    
                    // Cách 2: Check text trên body
                    var bodyText = document.body.innerText;
                    if (bodyText.includes('Grade: 100%') || bodyText.includes('Your grade') || bodyText.includes('Submission confirmed')) {
                        return 'COMPLETED';
                    }
                    
                    return 'WAITING';
                })();
            ";
            
            try
            {
                string sidebarResult = await MainWebView.ExecuteScriptAsync(jsCheckSidebar);
                if (sidebarResult != null && sidebarResult.Contains("COMPLETED"))
                {
                    _viewModel.StatusText = "✅ Bài tự luận đã được chấp nhận! Tick xanh rồi!";
                    await Task.Delay(1000);
                    await CheckLessonCompletedAndClickNextAsync();
                    return;
                }
            }
            catch { }
            
            _viewModel.StatusText = $"⏳ Đang chờ Coursera xác nhận bài nộp... ({(waitLoop + 1) * 3}s)";
        }
        
        // Nếu chờ 30s mà chưa thấy tick xanh → thử bấm Next
        _viewModel.StatusText = "⏭️ Đã chờ đủ lâu. Thử chuyển bài...";
        await CheckLessonCompletedAndClickNextAsync();
    }

    private bool _isHandlingPeerReview = false;

    private async Task HandlePeerReviewAsync()
    {
        if (_isHandlingPeerReview) return;
        _isHandlingPeerReview = true;

        try
        {
            _viewModel.StatusText = "👀 Đang tự động chấm điểm cho bạn cùng lớp (Peer Review)...";
            await Task.Delay(3000);
            
            int reviewCount = 0;
            int maxReviews = 5; // Giới hạn tối đa, thường chỉ cần 3
            bool isAllDone = false;
            
            while (!isAllDone && reviewCount < maxReviews)
            {
                await DismissAnyGlobalPopupsAsync();

                // CHECK SIDEBAR CÓ TICK XANH CHƯA (Hoàn thành = Dừng ngay)
                string jsCheckCompleted = @"
                    (function() {
                        var bodyText = document.body.innerText.toLowerCase();
                        
                        // Cách 1: Check sidebar item đang active
                        var items = document.querySelectorAll('a[data-click-key*=""item_link""]');
                        for (var i = 0; i < items.length; i++) {
                            if (items[i].getAttribute('aria-current') === 'page' || items[i].classList.contains('rc-ItemLink--active')) {
                                if (items[i].innerHTML.includes('>Completed<')) {
                                    return 'COMPLETED';
                                }
                            }
                        }
                        
                        // Cách 2: Check active item trong sidebar bằng aria-label
                        var activeItem = document.querySelector('a[data-testid=""rc-WeekNavigationItem""][aria-current=""page""]');
                        var isSidebarCompleted = activeItem && activeItem.getAttribute('aria-label') && activeItem.getAttribute('aria-label').includes('Completed');
                        
                        if (isSidebarCompleted || 
                            bodyText.includes('0 left to complete') || 
                            bodyText.includes('you have completed all required reviews') || 
                            bodyText.includes(""you've finished your peer reviews"") ||
                            bodyText.includes(""you have finished your peer reviews"")) 
                        {
                            return 'COMPLETED';
                        }
                        return 'NOT_YET';
                    })();
                ";
                string checkResult = await MainWebView.ExecuteScriptAsync(jsCheckCompleted);
                if (checkResult != null && checkResult.Contains("COMPLETED"))
                {
                    _viewModel.StatusText = "✅ Sidebar đã tick xanh! Đã chấm đủ số lượng bài yêu cầu!";
                    isAllDone = true;
                    // Bấm Next để qua bài
                    await CheckLessonCompletedAndClickNextAsync();
                    break;
                }

                // Bước 1: Bấm Start Reviewing
                string jsStart = @"
                    (function() {
                        var btns = Array.from(document.querySelectorAll('button, a'));
                        var startBtn = btns.find(b => {
                            var text = (b.innerText || b.textContent || '').trim().toLowerCase();
                            return text === 'start reviewing' || text === 'review fellow learners';
                        });
                        if (startBtn && !startBtn.disabled) {
                            startBtn.click();
                            return 'CLICKED_START';
                        }
                        return 'NO_START';
                    })();
                ";
                string resultStart = await MainWebView.ExecuteScriptAsync(jsStart);
                
                if (resultStart != null && resultStart.Contains("CLICKED_START"))
                {
                    _viewModel.StatusText = $"👀 Đang mở bài #{reviewCount + 1} của bạn cùng lớp...";
                    await Task.Delay(4000); 
                }

                // Bước 2: Chấm điểm (Chọn max điểm) và điền lời khen
                string jsGrade = @"
                    (function() {
                        var actionTaken = false;

                        // Chọn điểm cao nhất cho mỗi Rubric
                        var groups = document.querySelectorAll('div[role=""radiogroup""]');
                        groups.forEach(g => {
                            var radios = g.querySelectorAll('input[type=""radio""]');
                            if (radios.length > 0) {
                                var targetRadio = radios[radios.length - 1];
                                if (!targetRadio.checked) {
                                    targetRadio.click();
                                    actionTaken = true;
                                }
                            }
                        });

                        // Tìm và điền Textarea bằng tuyệt chiêu lừa React 16+
                        var textboxes = document.querySelectorAll('textarea');
                        for (var i = 0; i < textboxes.length; i++) {
                            var tb = textboxes[i];
                            if (!tb.value || tb.value.trim() === '') {
                                var msg = 'Great job on this assignment! Your responses were well-thought-out and covered all the necessary points. Keep up the good work!';
                                
                                let lastValue = tb.value;
                                tb.value = msg;
                                let event = new Event('input', { bubbles: true });
                                event.simulated = true;
                                let tracker = tb._valueTracker;
                                if (tracker) {
                                    tracker.setValue(lastValue);
                                }
                                tb.dispatchEvent(event);
                                tb.dispatchEvent(new Event('change', { bubbles: true }));
                                tb.dispatchEvent(new Event('blur', { bubbles: true }));
                                
                                actionTaken = true;
                            }
                        }
                        
                        var btns = Array.from(document.querySelectorAll('button'));
                        var submitBtn = btns.find(b => {
                            var text = (b.innerText || b.textContent || '').trim().toLowerCase();
                            return text === 'submit review' || text === 'submit';
                        });

                        if (submitBtn && !submitBtn.disabled) {
                            submitBtn.click();
                            return 'SUBMITTED';
                        }
                        
                        return actionTaken ? 'GRADED' : 'NO_ACTION';
                    })();
                ";
                string gradeResult = await MainWebView.ExecuteScriptAsync(jsGrade);
                
                if (gradeResult != null && gradeResult.Contains("SUBMITTED"))
                {
                    reviewCount++;
                    _viewModel.StatusText = $"✅ Đã nộp phiếu #{reviewCount}! Chờ Coursera xử lý...";
                    await Task.Delay(5000); // Chờ React render
                    
                    // SAU KHI SUBMIT: Check sidebar ngay lập tức
                    string recheck = await MainWebView.ExecuteScriptAsync(jsCheckCompleted);
                    if (recheck != null && recheck.Contains("COMPLETED"))
                    {
                        _viewModel.StatusText = $"✅ Đã chấm {reviewCount} bài, sidebar tick xanh rồi! Qua bài tiếp!";
                        isAllDone = true;
                        await CheckLessonCompletedAndClickNextAsync();
                        break;
                    }
                }
                else
                {
                    _viewModel.StatusText = "⏳ Đã điền xong nhưng nút Submit chưa bấm được. Chờ thêm...";
                    await Task.Delay(2000);
                }
            } // Kết thúc vòng lặp while
            
            // Nếu đã chấm max bài mà vẫn chưa tick xanh → dừng lại, thử bấm Next
            if (!isAllDone)
            {
                _viewModel.StatusText = $"⚠️ Đã chấm {reviewCount}/{maxReviews} bài. Thử chuyển bài...";
                await CheckLessonCompletedAndClickNextAsync();
            }

        }
        finally
        {
            _isHandlingPeerReview = false;
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

        _viewModel.StatusText = $"Đang tải trang: {linkCanMo}";

        try
        {
            await MainWebView.EnsureCoreWebView2Async(null);
            MainWebView.Source = new Uri(linkCanMo);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Lỗi đường dẫn không hợp lệ: " + ex.Message;
        }
    }

    private async void MainWebView_SourceChanged(object sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e)
    {
        string currenUrl = MainWebView.Source?.ToString()?.ToLower() ?? "";
        
        if (currenUrl.Contains("/lecture/"))
        {
            await HandleVideoLessonAsync();
        }
        else if (currenUrl.Contains("/ungradedwidget/"))
        {
            await HandleUngradedWidgetAsync();
        }
        else if (currenUrl.Contains("/ungradedlti/") || currenUrl.Contains("/lti/"))
        {
            await HandleUngradedAppAsync();
        }
        else if (currenUrl.Contains("/assignment-submission/") || currenUrl.Contains("/exam/") || currenUrl.Contains("/quiz/"))
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
            if (currenUrl.Contains("give-feedback") || currenUrl.Contains("review"))
            {
                await HandlePeerReviewAsync();
            }
            else
            {
                await HandlePeerAssignmentAsync();
            }
        }
        else if (currenUrl.Contains("/coach/") || currenUrl.Contains("/dialogue/"))
        {
            await HandleDialogueAsync();
        }
    }

    private async void MainWebView_NavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _viewModel.StatusText = "Tải trang thành công!";
            string currenUrl = MainWebView.Source.ToString();
            
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
            else if (currenUrl.Contains("/ungradedLti/") || currenUrl.Contains("/lti/"))
            {
                await HandleUngradedAppAsync();
            }
            else if (currenUrl.Contains("/assignment-submission/") || currenUrl.Contains("/exam/") || currenUrl.Contains("/quiz/"))
            {
                await HandleQuizAsync();
            }
            else if (currenUrl.Contains("/discussionPrompt/"))
            {
                await HandleDiscussionAsync();
            }
            else if (currenUrl.Contains("/supplement/"))
            {
                await HandleReadingLessonAsync();
            }

            else if (currenUrl.Contains("/peer/"))
            {
                if (currenUrl.Contains("give-feedback") || currenUrl.Contains("review"))
                {
                    await HandlePeerReviewAsync();
                }
                else
                {
                    await HandlePeerAssignmentAsync();
                }
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
            _viewModel.StatusText = $"❌ Lỗi tải trang: {e.WebErrorStatus}";
        }
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
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
    public string Question { get; set; }
    public List<QuizOption> Options { get; set; }
}

public class QuizOption
{
    public string Text { get; set; }
    public string InputId { get; set; }
}

public class QuizFeedbackDto
{
    public string Question { get; set; }
    public List<string> WrongAnswers { get; set; } = new List<string>();
    public List<string> CorrectAnswers { get; set; } = new List<string>();
    public bool IsMissingAnswers { get; set; }
}
