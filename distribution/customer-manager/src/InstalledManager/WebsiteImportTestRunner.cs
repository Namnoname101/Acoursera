using System.IO;
using System.Text.Json;

namespace Acose.CustomerManager;

internal static class WebsiteImportTestRunner
{
    // Runs before vault, data store, bridge, or any real customer process is opened.
    public static int Run(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "AcoseWebsiteImportTest-" + Guid.NewGuid().ToString("N"));
        var passed = new List<string>();
        string? failure = null;
        try
        {
            Directory.CreateDirectory(root);
            WebsiteImportSelfTest.Run(root, (value, name) =>
            {
                if (!value) throw new InvalidOperationException(name);
                passed.Add(name);
            });
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            string full = Path.GetFullPath(root);
            if (Directory.Exists(full) && Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(full).StartsWith("AcoseWebsiteImportTest-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
        string json = JsonSerializer.Serialize(new { result = failure is null ? "passed" : "failed", count = passed.Count, tests = passed, error = failure }, new JsonSerializerOptions { WriteIndented = true });
        int index = Array.IndexOf(args, "--website-import-self-test");
        if (index + 1 < args.Length)
        {
            using var writer = new StreamWriter(new FileStream(Path.GetFullPath(args[index + 1]), FileMode.CreateNew, FileAccess.Write, FileShare.None));
            writer.Write(json);
        }
        else Console.WriteLine(json);
        return failure is null ? 0 : 1;
    }
}
