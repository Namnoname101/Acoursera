using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string engineDirectory = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            string file = Path.Combine(engineDirectory, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        var assembly = Assembly.LoadFrom(Path.Combine(engineDirectory, "AutomationPlatform.Presentation.dll"));
        string? oldPdf = Environment.GetEnvironmentVariable("ACOSE_SKIP_PEER_PDF");
        string? oldReview = Environment.GetEnvironmentVariable("ACOSE_SKIP_PEER_REVIEW");
        int checks = 0;
        try
        {
            foreach (bool pdf in new[] { false, true })
            foreach (bool review in new[] { false, true })
            {
                Environment.SetEnvironmentVariable("ACOSE_SKIP_PEER_PDF", pdf ? "1" : "0");
                Environment.SetEnvironmentVariable("ACOSE_SKIP_PEER_REVIEW", review ? "1" : "0");
                object options = assembly.GetType("AutomationPlatform.Presentation.Services.WorkerLaunchOptions", true)!
                    .GetMethod("FromArgs")!.Invoke(null, [Array.Empty<string>()])!;
                object viewModel = Activator.CreateInstance(assembly.GetType("AutomationPlatform.Presentation.ViewModels.MainViewModel", true)!, [null, null])!;
                object ai = Activator.CreateInstance(assembly.GetType("AutomationPlatform.Presentation.Services.AiCompletionService", true)!)!;
                using var client = (IDisposable)Activator.CreateInstance(assembly.GetType("AutomationPlatform.Presentation.Services.CentralWorkerClient", true)!, [options])!;
                var window = (Window)Activator.CreateInstance(assembly.GetType("AutomationPlatform.Presentation.MainWindow", true)!, [viewModel, ai, options, client])!;
                // Never show the Window: Loaded/browser automation cannot run in this check.
                void Check(bool value, string detail) { if (!value) throw new Exception(detail); checks++; }
                Check(((CheckBox)window.FindName("SkipPeerPdfCheckBox")).IsChecked == pdf, "PDF environment option reaches WPF control");
                Check(((CheckBox)window.FindName("SkipPeerReviewCheckBox")).IsChecked == review, "Review environment option reaches WPF control");
                Check((bool)window.GetType().GetField("ShouldSkipPeerPdfItems", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! == pdf,
                    "PDF option reaches automation field");
                Check((bool)window.GetType().GetField("ShouldSkipPeerReviewItems", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! == review,
                    "Review option reaches automation field");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACOSE_SKIP_PEER_PDF", oldPdf);
            Environment.SetEnvironmentVariable("ACOSE_SKIP_PEER_REVIEW", oldReview);
        }
        Console.WriteLine($"Passed {checks} installed Engine option/WPF checks; no browser initialized.");
        return 0;
    }
}
