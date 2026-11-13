using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Acose.CustomerManager.UI;

namespace Acose.CustomerManager;

public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int skipsTest = Array.IndexOf(e.Args, "--activity-skips-self-test");
        if (skipsTest >= 0 && skipsTest + 1 < e.Args.Length)
        {
            Shutdown(ActivitySkipsSelfTest.Run(Path.GetFullPath(e.Args[skipsTest + 1]))); return;
        }
        if (e.Args.Contains("--website-import-self-test"))
        {
            Shutdown(WebsiteImportTestRunner.Run(e.Args)); return;
        }
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            string dataDirectory = Path.Combine(baseDirectory, "Data");
            if (e.Args.Contains("--configure-from-environment"))
            {
                EnvironmentVault.Capture(dataDirectory);
                Shutdown(0); return;
            }
            bool preview = e.Args.Contains("--preview");
            string mutexName = "Local\\AcoseCustomerManager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDirectory.ToUpperInvariant())))[..24];
            _mutex = new Mutex(true, mutexName + (preview ? "-preview" : ""), out bool acquired);
            if (!acquired)
            {
                MessageBox.Show("Trình quản lý đang mở. Hãy chọn cửa sổ ACOSE trên thanh tác vụ.", "ACOSE", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(); return;
            }
            if (!preview) EnvironmentVault.Apply(dataDirectory);
            var controller = new ManagerController(dataDirectory,
                Path.Combine(baseDirectory, "Engine", "AutomationPlatform.Presentation.exe"), preview);
            var window = new MainWindow(controller);
            if (preview) window.Title = "ACOSE · Bản xem trước — dữ liệu minh họa";
            MainWindow = window;
            int screenshotIndex = Array.IndexOf(e.Args, "--screenshot");
            if (preview && screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
            {
                string output = Path.GetFullPath(e.Args[screenshotIndex + 1]);
                window.ContentRendered += async (_, _) =>
                {
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Window captureWindow = window;
                    if (e.Args.Contains("--preview-activity-skips"))
                    {
                        captureWindow = new ActivitySkipsDialog(window, new());
                        captureWindow.Show();
                        await captureWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    captureWindow.UpdateLayout();
                    var content = (FrameworkElement)captureWindow.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen())
                        drawing.DrawRectangle(captureWindow.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                    bitmap.Render(background);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using (var stream = File.Create(output)) encoder.Save(stream);
                    if (captureWindow != window) captureWindow.Close();
                    if (e.Args.Contains("--exit-after-screenshot")) window.Close();
                };
            }
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không mở được ACOSE", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
