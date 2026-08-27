using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AutomationPlatform.Application.Interfaces;
using AutomationPlatform.Infrastructure.Browser;
using AutomationPlatform.Infrastructure.Data;
using AutomationPlatform.Domain.Interfaces;
using AutomationPlatform.Presentation.Services;
using AutomationPlatform.Presentation.ViewModels;

namespace AutomationPlatform.Presentation;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WorkerLaunchOptions workerLaunchOptions;
        try
        {
            workerLaunchOptions = WorkerLaunchOptions.FromArgs(e.Args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ACOSE Worker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((context, services) =>
            {
                // Đăng ký Repository (SQLite)
                services.AddSingleton<IRepository<Domain.Entities.CourseEntity, Guid>, SqliteCourseRepository>();

                // Đăng ký Browser Service (Playwright)
                services.AddScoped<IBrowserService, PlaywrightService>();

                // Đăng ký ViewModels
                services.AddTransient<MainViewModel>();

                // AgentRouter chạy qua Codex harness; lỗi/quota sẽ chuyển sang các HTTP provider.
                services.AddSingleton<AiCompletionService>();

                // Worker mode chỉ thêm lớp điều phối; logic automation trong MainWindow được giữ nguyên.
                services.AddSingleton(workerLaunchOptions);
                services.AddSingleton<CentralWorkerClient>();

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
