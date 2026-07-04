using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AutomationPlatform.Application.Interfaces;
using AutomationPlatform.Infrastructure.Browser;
using AutomationPlatform.Infrastructure.Data;
using AutomationPlatform.Domain.Interfaces;
using AutomationPlatform.Presentation.ViewModels;

namespace AutomationPlatform.Presentation;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((context, services) =>
            {
                // Đăng ký Repository (SQLite)
                services.AddSingleton<IRepository<Domain.Entities.CourseEntity, Guid>, SqliteCourseRepository>();

                // Đăng ký Browser Service (Playwright)
                services.AddScoped<IBrowserService, PlaywrightService>();

                // Đăng ký ViewModels
                services.AddTransient<MainViewModel>();

                // Đăng ký MainWindow (khởi tạo sau khi DI sẵn sàng)
                services.AddSingleton<MainWindow>();
            })
            .Build();

        // Khởi động Host và lấy MainWindow từ DI
        _host.Start();
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
