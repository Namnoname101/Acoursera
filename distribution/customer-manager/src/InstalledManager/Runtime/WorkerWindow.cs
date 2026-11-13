using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Acose.CustomerManager.Runtime;

public sealed record WorkerWindowState(nint Handle, bool IsVisible, bool IsMinimized)
{
    public bool IsShown => IsVisible && !IsMinimized;
}

/// <summary>Controls only the main WPF window of an already tracked course worker.</summary>
public static class WorkerWindow
{
    public static WorkerWindowState? Read(Process process, SavedRun saved)
    {
        if (!MatchesProcess(process, saved)) return null;
        nint mainWindow = 0;
        // MainWindowHandle excludes hidden windows. Enumerate by the tracked PID instead.
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != saved.ProcessId || GetWindow(window, 4) != 0) return true; // GW_OWNER
            var className = new StringBuilder(256);
            var title = new StringBuilder(512);
            GetClassName(window, className, className.Capacity);
            GetWindowText(window, title, title.Capacity);
            if (!className.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal) ||
                !string.Equals(title.ToString(), $"ACOSE Worker · {saved.OrderId}", StringComparison.Ordinal)) return true;
            mainWindow = window;
            return false;
        }, 0);
        return mainWindow == 0 ? null : new(mainWindow, IsWindowVisible(mainWindow), IsIconic(mainWindow));
    }

    public static bool RequestVisibility(Process process, SavedRun saved, bool show)
    {
        // Resolve again at click time: never act on a stale HWND or a recycled PID.
        var window = Read(process, saved);
        if (window is null) return false;
        return ShowWindowAsync(window.Handle, show ? (window.IsMinimized ? 9 : 5) : 0);
    }

    private static bool MatchesProcess(Process process, SavedRun saved)
    {
        try
        {
            return saved.Mode == "course" && process.Id == saved.ProcessId && !process.HasExited &&
                process.StartTime.ToUniversalTime() == saved.StartTimeUtc.ToUniversalTime() &&
                string.Equals(process.MainModule?.FileName, saved.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
}
