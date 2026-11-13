using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Acose.CustomerManager;

// Keep the existing engine configuration usable when launched from Explorer.
// Windows DPAPI binds the encrypted settings to this Windows user; no plaintext keys are written.
public static class EnvironmentVault
{
    private static readonly string[] Names = ["AGENTROUTER_API_KEY", "AGENTROUTER_MODEL", "AGENTROUTER_CODEX_PATH"];
    public static void Capture(string dataDirectory)
    {
        var values = new Dictionary<string, string>();
        foreach (string name in Names)
        {
            string? value = Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(value)) values[name] = value;
        }
        if (!values.ContainsKey("AGENTROUTER_API_KEY"))
            throw new InvalidOperationException("Không tìm thấy cấu hình AI của bộ auto đang dùng.");
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            byte[] protectedBytes = Transform(plaintext, true);
            Directory.CreateDirectory(dataDirectory);
            string path = Path.Combine(dataDirectory, "engine-settings.protected");
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public static void Apply(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "engine-settings.protected");
        if (!File.Exists(path)) return;
        byte[] plaintext;
        try { plaintext = Transform(File.ReadAllBytes(path), false); }
        catch { throw new InvalidOperationException("Không mở được cấu hình auto đã mã hóa. Hãy dùng đúng tài khoản Windows đã cài công cụ."); }
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) ?? [];
            foreach (string name in Names)
                if (values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) &&
                    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                    Environment.SetEnvironmentVariable(name, value);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            DataBlob output;
            bool success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, output.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data);
        }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
