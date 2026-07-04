using Xunit;
using AutomationPlatform.Domain.Entities;

namespace AutomationPlatform.Tests;

public class CourseEntityTests
{
    [Fact]
    public void CourseEntity_DefaultStatus_IsNotStarted()
    {
        var course = new CourseEntity();
        Assert.Equal(EnrollmentStatus.NotStarted, course.Status);
    }
}
