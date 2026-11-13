using System.IO;
using System.Text.Json;
using Acose.CustomerManager.Runtime;

namespace Acose.CustomerManager;

internal static class ActivitySkipsSelfTest
{
    public static int Run(string report)
    {
        string root = Path.Combine(Path.GetTempPath(), "AcoseActivitySkipsTest-" + Guid.NewGuid().ToString("N"));
        var passed = new List<string>();
        string? error = null;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            passed.Add(name);
        }
        try
        {
            Directory.CreateDirectory(root);
            var defaults = ActivitySkipSettings.Load(root);
            Check(!defaults.SkipPdf && !defaults.SkipReview, "Missing settings enable both activity types");
            string sentinel = Path.Combine(root, "customer-data.sentinel");
            File.WriteAllText(sentinel, "preserve");
            string executable = Path.Combine(root, "fixture.exe");
            File.WriteAllText(executable, "fixture only; never executed");
            var bridge = new WorkerBridge();
            try
            {
                Task.Run(() => bridge.StartAsync()).GetAwaiter().GetResult();
                foreach (bool pdf in new[] { false, true })
                foreach (bool review in new[] { false, true })
                {
                    var settings = new ActivitySkipSettings(pdf, review);
                    settings.Save(root);
                    Check(ActivitySkipSettings.Load(root) == settings, $"Settings round trip PDF={pdf}, review={review}");
                    var request = new WorkerJobRequest("fixture-job", "fixture-customer", "Fixture", root,
                        "https://www.coursera.org/learn/fixture", "course", SkipPdf: pdf, SkipReview: review);
                    var start = WorkerLauncher.CreateStartInfo(executable, bridge, request);
                    Check(start.Environment["ACOSE_SKIP_PEER_PDF"] == (pdf ? "1" : "0"), $"Worker PDF option {pdf}/{review}");
                    Check(start.Environment["ACOSE_SKIP_PEER_REVIEW"] == (review ? "1" : "0"), $"Worker review option {pdf}/{review}");
                    Check(start.Environment["WEBVIEW2_USER_DATA_FOLDER"] == root, $"Profile unchanged {pdf}/{review}");
                }
            }
            finally { Task.Run(async () => await bridge.DisposeAsync()).GetAwaiter().GetResult(); }
            Check(File.ReadAllText(sentinel) == "preserve", "Saving settings preserves customer data");
            Check(!Directory.GetFiles(root, "*.tmp").Any(), "No temporary settings remain");
            File.WriteAllText(Path.Combine(root, "activity-skips.json"), "invalid json");
            bool rejected = false;
            try { ActivitySkipSettings.Load(root); }
            catch (JsonException) { rejected = true; }
            Check(rejected, "Malformed settings never silently reset selected options");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(root).StartsWith("AcoseActivitySkipsTest-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
        using var output = new StreamWriter(new FileStream(report, FileMode.CreateNew));
        output.Write(JsonSerializer.Serialize(new { result = error == null ? "passed" : "failed", count = passed.Count, tests = passed, error }));
        return error == null ? 0 : 1;
    }
}
