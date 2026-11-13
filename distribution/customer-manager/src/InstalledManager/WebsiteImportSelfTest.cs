using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace Acose.CustomerManager;

internal static class WebsiteImportSelfTest
{
    // Pure fixtures: no live profiles, backend, cookies, login or worker process is accessed.
    public static void Run(string root, Action<bool, string> check)
    {
        const string a = "https://www.coursera.org/learn/fixture-one";
        const string b = "https://www.coursera.org/learn/fixture-two";
        const string c = "https://www.coursera.org/learn/fixture-three";
        string profiles = Path.Combine(root, "WorkerProfiles");
        string managed = Path.Combine(profiles, "_ManagedAccounts");
        Directory.CreateDirectory(managed);
        var store = new ManagerStore(Path.Combine(root, "Data"));
        var data = new ManagerData { ImportRoots = [profiles] };
        string WriteMarker(string group, string folder, string device, int day, params string[] urls)
        {
            string profile = Path.Combine(profiles, group, folder);
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".acose-website-profile.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "ready", attemptId = folder, deviceId = device,
                fullName = "Fixture Customer", courseraUserId = "fixture-user",
                completedAtUtc = new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero), courseUrls = urls
            }));
            return profile;
        }
        string first = WriteMarker("_ManagedAccounts", "attempt-one", "device-one", 1, a, a, "https://invalid.example/learn/test");
        WriteMarker("_DirectLogin", "attempt-pending", "device-pending", 1, b);
        WriteMarker("_CourseJobs", "attempt-job", "device-job", 1, b);
        Directory.CreateDirectory(Path.Combine(managed, "empty-profile"));
        string malformed = Path.Combine(managed, "bad-marker");
        Directory.CreateDirectory(malformed);
        File.WriteAllText(Path.Combine(malformed, ".acose-website-profile.json"), "{bad-json");
        File.WriteAllText(Path.Combine(malformed, ".acose-profile-identity.json"), "{\"version\":1,\"fullName\":\"Fixture Identity\"}");
        check(store.ImportExisting(data) == 1, "Only ready managed website profile imports; pending/job/empty/malformed skipped");
        CustomerOrder order = data.Orders.Single();
        check(order.ProfilePath == first && order.Source == "Website" && order.WebsiteDeviceId == "device-one", "Website profile path and stable ID retained");
        check(order.Courses.Count == 1 && order.Courses[0].Url == a, "Duplicate and non-Coursera website links rejected");
        check(order.Name == "FIXTURE CUSTOMER", "Website customer name imported");
        check(order.Courses.All(x => x.State == CourseState.Ready) && data.Runs.Count == 0, "Import never queues courses or starts workers");
        check(store.LastWarning is not null, "Invalid source produces warning without losing valid profile");
        check(store.ImportExisting(data) == 0 && data.Orders.Count == 1, "Repeated refresh does not duplicate customer");
        order.Name = "LOCAL NAME";
        order.Courses[0].Url = c;
        store.Save(data);
        data = store.Load(); order = data.Orders.Single();
        check(store.ImportExisting(data) == 0 && order.Name == "LOCAL NAME" && order.Courses.Single().Url == c, "Local name/link edits survive restart and sync");
        order.Courses.Clear();
        store.ImportExisting(data);
        check(order.Courses.Count == 0, "Removed website link is not resurrected on refresh");
        string second = WriteMarker("_ManagedAccounts", "attempt-two", "device-one", 2, a, b);
        order.SessionBusy = true;
        store.ImportExisting(data);
        check(order.ProfilePath == first && data.Orders.Count == 1, "Busy customer keeps current browser profile");
        check(order.Courses.Count == 1 && order.Courses.Single().Url == b, "New website selection added without restoring removed links");
        order.SessionBusy = false;
        order.Courses[0].State = CourseState.Queued;
        store.ImportExisting(data);
        check(order.ProfilePath == first, "Queued worker prevents profile replacement");
        order.Courses[0].State = CourseState.Completed;
        data.Runs.Add(new SavedRun { OrderId = order.Id, JobId = "fixture-running-process" });
        store.ImportExisting(data);
        check(order.ProfilePath == first, "Terminal report does not replace profile while worker process is still registered");
        data.Runs.Clear();
        order.Courses[0].State = CourseState.Ready;
        store.ImportExisting(data);
        check(order.ProfilePath == second && order.WebsiteAttemptId == "attempt-two" && order.Name == "LOCAL NAME", "Idle customer adopts newest login without resetting local name");
        store.Save(data); data = store.Load(); order = data.Orders.Single();
        store.ImportExisting(data);
        check(order.ProfilePath == second && data.Orders.Count == 1, "Persisted timestamp prevents old profile rollback");
        WriteMarker("_ManagedAccounts", "attempt-other", "device-other", 3, a);
        check(store.ImportExisting(data) == 1 && data.Orders.Count == 2, "Same customer name with different stable ID stays separate");
        string legacy = Path.Combine(managed, "legacy-profile");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, ".acose-account-manager.json"), JsonSerializer.Serialize(new { displayName = "Legacy Fixture", launchUrl = a }));
        check(store.ImportExisting(data) == 1 && data.Orders.Any(x => x.ProfilePath == legacy), "Legacy local profiles remain importable");
        check(WebsiteProfileImportSettings.NormalizeRoot(managed) == profiles, "ManagedAccounts picker normalizes to WorkerProfiles root");
        check(WebsiteProfileImportSettings.NormalizeRoot("") == "", "Blank path disables website sync");
        bool rejected = false;
        try { WebsiteProfileImportSettings.NormalizeRoot("https://example.invalid/"); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Website URL rejected as filesystem import root");
        rejected = false;
        try { WebsiteProfileImportSettings.NormalizeRoot("//fixture-server.invalid/share"); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Forward-slash UNC path rejected before filesystem access");
        // Locks a sentinel where Chromium cookies would be; importing must never open it.
        string cookieDir = Path.Combine(first, "Default", "Network"); Directory.CreateDirectory(cookieDir);
        using (var cookie = new FileStream(Path.Combine(cookieDir, "Cookies"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            check(store.ImportExisting(data) == 0, "Import never opens cookie stores");
        string junction = Path.Combine(managed, "linked-profile");
        // Junctions work for a standard Windows user; symlinks require extra privileges.
        var linkInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + first.Replace("'", "''") + "' | Out-Null" })
            linkInfo.ArgumentList.Add(arg);
        using (Process linkProcess = Process.Start(linkInfo)!)
        {
            if (!linkProcess.WaitForExit(15000) || linkProcess.ExitCode != 0)
                throw new InvalidOperationException("Could not create isolated junction fixture.");
        }
        try { check(store.ImportExisting(data) == 0 && store.LastWarning is not null, "Linked profiles rejected"); }
        finally { Directory.Delete(junction); }
    }
}
