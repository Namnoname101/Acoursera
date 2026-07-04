using System.ComponentModel;
using System.Runtime.CompilerServices;
using AutomationPlatform.Domain.Interfaces;
using AutomationPlatform.Application.Interfaces;
using AutomationPlatform.Domain.Entities;

namespace AutomationPlatform.Presentation.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IRepository<CourseEntity, Guid> _repository;
    private readonly IBrowserService _browser;

    private string _statusText = "Sẵn sàng";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public MainViewModel(IRepository<CourseEntity, Guid> repository, IBrowserService browser)
    {
        _repository = repository;
        _browser = browser;
    }

    // Ví dụ Use Case: Thêm khóa học và điều hướng đến URL
    public async Task AddCourseAndNavigateAsync(string url, string courseName, CancellationToken ct = default)
    {
        StatusText = "Đang khởi tạo trình duyệt...";
        await _browser.InitializeAsync(new BrowserConfig { Headless = false }, ct);

        var course = new CourseEntity
        {
            CourseUrl = url,
            CourseName = courseName,
            Platform = "Coursera",
            Status = EnrollmentStatus.NotStarted
        };
        await _repository.AddAsync(course, ct);
        StatusText = $"Đã lưu khóa học '{courseName}' (ID: {course.Id})";

        StatusText = "Đang điều hướng đến khóa học...";
        await using var page = await _browser.NavigateToAsync(url, ct);
        StatusText = $"Đã tải trang: {await page.TitleAsync()}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
