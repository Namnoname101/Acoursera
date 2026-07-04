namespace AutomationPlatform.Domain.Entities;

/// <summary>
/// Đại diện cho một khóa học trên Coursera (hoặc nền tảng khác sau này)
/// </summary>
public sealed class CourseEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Platform { get; init; } = "Coursera"; // Mở rộng: edX, Udemy, ...
    public string CourseUrl { get; init; } = string.Empty;
    public string CourseName { get; init; } = string.Empty;
    public string? Instructor { get; init; }
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.NotStarted;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastAccessedAt { get; set; }
}

public enum EnrollmentStatus
{
    NotStarted,
    InProgress,
    Completed,
    Paused,
    Failed
}
