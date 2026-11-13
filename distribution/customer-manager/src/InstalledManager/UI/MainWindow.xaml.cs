using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Acose.CustomerManager.UI;

public partial class MainWindow : Window
{
    private readonly ManagerController _controller;
    private bool _initialized;
    private bool _closing;
    private bool _canClose;

    public MainWindow(ManagerController controller)
    {
        InitializeComponent();
        _controller = controller;
        DataContext = controller;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await RunAsync(null, _controller.InitializeAsync);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(sender, _controller.RefreshAsync);

    private async void AddOrder_Click(object sender, RoutedEventArgs e)
    {
        var name = new DialogField("Họ và tên đầy đủ của khách", "", uppercase: true);
        var links = new DialogField("Link khóa học — mỗi dòng một link", "", multiline: true);
        var dialog = new EditDialog(this, "Thêm khách / đơn thủ công",
            "Tạo một đơn mới và mở cửa sổ đăng nhập tài khoản Coursera của khách. Bạn có thể thêm link sau.",
            [name, links], "Tạo đơn và đăng nhập", () =>
            {
                InputRules.FullName(name.Value);
                InputRules.CourseUrls([links.Value]);
            });
        if (dialog.ShowDialog() != true) return;
        await RunAsync(sender, async () =>
        {
            var order = await _controller.AddOrderAsync(name.Value, [links.Value]);
            _controller.SelectedOrder = order;
        });
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.SelectedOrder is not { } order) return;
        var name = new DialogField("Họ và tên đầy đủ của khách", order.Name, uppercase: true);
        var dialog = new EditDialog(this, "Sửa họ tên khách", "Tên khách được lưu bằng chữ IN HOA trên máy này.",
            [name], "Lưu họ tên", () => InputRules.FullName(name.Value));
        if (dialog.ShowDialog() == true) Run(() => _controller.RenameOrder(order, name.Value));
    }

    private void AddLinks_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.SelectedOrder is not { } order) return;
        var links = new DialogField("Link khóa học — mỗi dòng một link", "", multiline: true);
        var dialog = new EditDialog(this, "Thêm link khóa học", $"Thêm vào đơn của {order.Name}. Các link chỉ được lưu trên máy này.",
            [links], "Thêm vào đơn", () =>
            {
                if (InputRules.CourseUrls([links.Value]).Count == 0)
                    throw new InvalidOperationException("Hãy dán ít nhất một link khóa học.");
            });
        if (dialog.ShowDialog() == true) Run(() => _controller.AddLinks(order, [links.Value]));
    }

    private void EditLink_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCourse(sender, out var order, out var course)) return;
        var link = new DialogField("Link khóa học", course.Url);
        var dialog = new EditDialog(this, "Sửa link khóa học", $"Đơn của {order.Name}. Thay đổi được lưu trên máy này.",
            [link], "Lưu link", () => InputRules.CourseUrl(link.Value));
        if (dialog.ShowDialog() == true) Run(() => _controller.EditLink(order, course, link.Value));
    }

    private void RemoveLink_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCourse(sender, out var order, out var course)) return;
        if (!ConfirmationDialog.Ask(this, "Xóa link khỏi đơn?",
                $"{order.Name}\n\n{course.Url}\n\nLink này sẽ được xóa khỏi đơn trên máy. Tài khoản Coursera của khách vẫn được giữ.",
                "Xóa link", "Giữ lại")) return;
        Run(() => _controller.RemoveLink(order, course));
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.SelectedOrder is { } order)
            await RunAsync(sender, () => _controller.OpenLoginAsync(order));
    }

    private async void OpenCourse_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetCourse(sender, out var order, out var course))
            await RunAsync(sender, () => _controller.OpenCourseAsync(order, course));
    }

    private async void StartCourse_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetCourse(sender, out var order, out var course))
            await RunAsync(sender, () => _controller.StartCourseAsync(order, course));
    }

    private async void ToggleWorkerWindow_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetCourse(sender, out var order, out var course))
            await RunAsync(sender, () => _controller.ToggleWorkerWindowAsync(order, course));
    }

    private async void StopCourse_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetCourse(sender, out var order, out var course))
            await RunAsync(sender, () => _controller.StopCourseAsync(order, course));
    }

    private async void StartOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.SelectedOrder is { } order)
            await RunAsync(sender, () => _controller.StartOrderAsync(order));
    }

    private async void StopOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.SelectedOrder is { } order)
            await RunAsync(sender, () => _controller.StopOrderAsync(order));
    }

    private void ActivitySkips_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ActivitySkipsDialog(this, _controller.ActivitySkips);
        if (dialog.ShowDialog() == true)
            Run(() => _controller.SetActivitySkips(dialog.SkipPdf, dialog.SkipReview));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var parallel = new DialogField("Số khóa được chạy đồng thời tối đa", _controller.MaxParallel.ToString(CultureInfo.InvariantCulture));
        var engine = new DialogField("Bản tự động hóa đang sử dụng", _controller.EnginePath, readOnly: true);
        var website = new DialogField("Nhận khách website: thư mục WorkerProfiles (để trống để tắt)", _controller.WebsiteProfileRoot);
        var dialog = new EditDialog(this, "Thiết lập nâng cao",
            "Giới hạn áp dụng cho tất cả khách hàng. Để nhận khách từ website, Worker Host phải chạy trên cùng PC. Ví dụ thư mục D:\\Acose\\WorkerProfiles; chỉ nhập profile hoàn tất trong _ManagedAccounts, không tự chạy khóa học.",
            [parallel, website, engine], "Lưu thiết lập", () =>
            {
                if (!int.TryParse(parallel.Value, out int value) || value is < 1 or > 64)
                    throw new InvalidOperationException("Số khóa đồng thời phải là số nguyên từ 1 đến 64.");
                WebsiteProfileImportSettings.NormalizeRoot(website.Value);
            });
        if (dialog.ShowDialog() == true) Run(() =>
        {
            _controller.SetWebsiteProfileRoot(website.Value);
            _controller.MaxParallel = int.Parse(parallel.Value, CultureInfo.InvariantCulture);
        });
    }

    private bool TryGetCourse(object sender, out CustomerOrder order, out CourseItem course)
    {
        order = _controller.SelectedOrder!;
        course = (sender as FrameworkElement)?.DataContext as CourseItem ?? null!;
        return order is not null && course is not null && order.Courses.Contains(course);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { ShowError(error); }
    }

    private async Task RunAsync(object? sender, Func<Task> action)
    {
        var button = sender as Button;
        var binding = button is null ? null : BindingOperations.GetBindingBase(button, IsEnabledProperty);
        object localValue = button?.ReadLocalValue(IsEnabledProperty) ?? DependencyProperty.UnsetValue;
        bool wasEnabled = button?.IsEnabled ?? false;
        if (button is not null) button.SetCurrentValue(IsEnabledProperty, false);
        try { await action(); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            if (button is not null)
            {
                if (binding is not null) BindingOperations.SetBinding(button, IsEnabledProperty, binding);
                else if (localValue == DependencyProperty.UnsetValue) button.ClearValue(IsEnabledProperty);
                else button.SetCurrentValue(IsEnabledProperty, wasEnabled);
            }
        }
    }

    private void ShowError(Exception error) =>
        MessageBox.Show(this, error.Message, "Chưa thể thực hiện thao tác", MessageBoxButton.OK, MessageBoxImage.Information);

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_closing) return;
        if (_controller.HasActiveWork && !ConfirmationDialog.Ask(this, "Đóng trình quản lý?",
                "Các khóa đang chạy sẽ dừng; cửa sổ đăng nhập và kiểm tra do trình quản lý mở sẽ đóng. Tiến độ và thông tin khách đã lưu vẫn được giữ trên máy.",
                "Dừng và đóng", "Tiếp tục sử dụng")) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            await _controller.ShutdownAsync();
            _canClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception error)
        {
            _closing = false;
            IsEnabled = true;
            ShowError(error);
        }
    }
}

public sealed class ProgressValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int progress ? (double)progress : 0d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed class DialogField
{
    public string Label { get; }
    public TextBox Input { get; }
    public string Value => Input.Text.Trim();

    public DialogField(string label, string value, bool multiline = false, bool uppercase = false, bool readOnly = false)
    {
        Label = label;
        Input = new TextBox
        {
            Text = value,
            Padding = new Thickness(11, 10, 11, 10),
            Background = readOnly ? new SolidColorBrush(Color.FromRgb(244, 246, 250)) : Brushes.White,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 36, 60)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(211, 221, 235)),
            BorderThickness = new Thickness(1),
            FontSize = 14,
            MinHeight = multiline ? 145 : 42,
            MaxHeight = multiline ? 250 : 100,
            AcceptsReturn = multiline,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            CharacterCasing = uppercase ? CharacterCasing.Upper : CharacterCasing.Normal,
            IsReadOnly = readOnly,
            MaxLength = uppercase ? 120 : 24000,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(Input, label);
    }
}

internal sealed class EditDialog : Window
{
    public EditDialog(MainWindow owner, string title, string introduction, IReadOnlyList<DialogField> fields,
        string acceptText, Action validate)
    {
        Owner = owner;
        Title = title;
        Width = 650;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(600, SystemParameters.WorkArea.Height - 70);
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        Foreground = new SolidColorBrush(Color.FromRgb(23, 36, 60));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = introduction, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(104, 117, 139)),
            Margin = new Thickness(0, 10, 0, 23), LineHeight = 21
        });
        foreach (var field in fields)
        {
            var label = new Label { Content = field.Label, Target = field.Input, FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 7) };
            stack.Children.Add(label);
            field.Input.Margin = new Thickness(0, 0, 0, 17);
            stack.Children.Add(field.Input);
        }
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(170, 53, 43)),
            Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Assertive);
        stack.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0) };
        var cancel = new Button { Content = "Hủy", IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 9, 0), Style = (Style)owner.FindResource(typeof(Button)) };
        var accept = new Button { Content = acceptText, MinWidth = 125, Style = (Style)owner.FindResource("PrimaryButton") };
        // Multiline link fields keep Enter for a new line; Ctrl+Enter submits the form.
        accept.IsDefault = !fields.Any(field => field.Input.AcceptsReturn);
        void Submit()
        {
            try { validate(); DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
        }
        accept.Click += (_, _) => Submit();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Submit(); e.Handled = true; }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        stack.Children.Add(buttons);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Loaded += (_, _) =>
        {
            var first = fields.FirstOrDefault(field => !field.Input.IsReadOnly)?.Input;
            first?.Focus();
            first?.SelectAll();
        };
    }
}

internal sealed class ConfirmationDialog : Window
{
    private ConfirmationDialog(MainWindow owner, string title, string message, string acceptText, string cancelText)
    {
        Owner = owner;
        Title = title;
        Width = 570;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        Foreground = new SolidColorBrush(Color.FromRgb(23, 36, 60));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 24), LineHeight = 22 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = cancelText, IsCancel = true, IsDefault = true, Style = (Style)owner.FindResource(typeof(Button)), Margin = new Thickness(0, 0, 9, 0) };
        var accept = new Button { Content = acceptText, Style = (Style)owner.FindResource("StopButton") };
        accept.Click += (_, _) => DialogResult = true;
        row.Children.Add(cancel);
        row.Children.Add(accept);
        stack.Children.Add(row);
        Content = stack;
    }

    public static bool Ask(MainWindow owner, string title, string message, string acceptText, string cancelText) =>
        new ConfirmationDialog(owner, title, message, acceptText, cancelText).ShowDialog() == true;
}
