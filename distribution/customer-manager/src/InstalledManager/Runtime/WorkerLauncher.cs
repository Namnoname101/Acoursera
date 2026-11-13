using System.Diagnostics;
using System.IO;

namespace Acose.CustomerManager.Runtime;

public static class WorkerLauncher
{
    /// <summary>Starts the unchanged worker executable using its supported central-worker protocol.</summary>
    public static Process Launch(string engineExecutable, WorkerBridge bridge, WorkerJobRequest request)
        => Process.Start(CreateStartInfo(engineExecutable, bridge, request))
            ?? throw new InvalidOperationException("Không khởi động được khóa học. Hãy thử lại.");

    public static ProcessStartInfo CreateStartInfo(string engineExecutable, WorkerBridge bridge, WorkerJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        WorkerBridge.ValidateRequest(request);
        string executable = Path.GetFullPath(engineExecutable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Không tìm thấy bộ tự động hóa.", executable);
        string profilePath = Path.GetFullPath(request.ProfilePath);
        if (!Directory.Exists(profilePath)) throw new DirectoryNotFoundException("Không tìm thấy thư mục phiên đăng nhập của khách.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = request.Mode == "browse" ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--worker-mode");
        AddArgument(startInfo, "--server-url", bridge.BaseUri.AbsoluteUri);
        AddArgument(startInfo, "--worker-id", WorkerBridge.WorkerIdFor(request.JobId));
        AddArgument(startInfo, "--job-id", request.JobId);
        AddArgument(startInfo, "--device-id", request.CustomerId);
        AddArgument(startInfo, "--job-mode", request.Mode);
        AddArgument(startInfo, "--profile-path", profilePath);
        startInfo.Environment["ACOSE_WORKER_KEY"] = bridge.Secret;
        startInfo.Environment["ACOSE_SKIP_PEER_PDF"] = request.SkipPdf ? "1" : "0";
        startInfo.Environment["ACOSE_SKIP_PEER_REVIEW"] = request.SkipReview ? "1" : "0";
        // Match the selected profile even if the operator's environment contains an old override.
        // Identical worker-mode options allow multiple course windows to share this profile.
        startInfo.Environment["WEBVIEW2_USER_DATA_FOLDER"] = profilePath;

        return startInfo;
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }
}
