using System.IO;

namespace Acose.CustomerManager;

public static class WebsiteProfileImportSettings
{
    public static string NormalizeRoot(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "";
        if (value.Length > 2000 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value) ||
            value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chọn thư mục WorkerProfiles trên chính PC này, không nhập link website hoặc đường dẫn mạng.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Chỉ nhận profile từ ổ đĩa trên PC này, không dùng đường dẫn mạng.");
        if (Path.GetFileName(root).Equals("_ManagedAccounts", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Thư mục profile không hợp lệ.");
        if (root.Length < 4 || !Directory.Exists(root))
            throw new InvalidOperationException("Không tìm thấy thư mục WorkerProfiles đã chọn.");
        string? current = Path.Combine(root, "_ManagedAccounts");
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Không nhận profile qua thư mục liên kết hoặc junction.");
            current = Path.GetDirectoryName(current);
        }
        return root;
    }
}
