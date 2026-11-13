using System.IO;
using System.Text.Json;

namespace Acose.CustomerManager;

public sealed record ActivitySkipSettings(bool SkipPdf = false, bool SkipReview = false)
{
    public static ActivitySkipSettings Load(string directory)
    {
        string file = Path.Combine(directory, "activity-skips.json");
        return File.Exists(file)
            ? JsonSerializer.Deserialize<ActivitySkipSettings>(File.ReadAllText(file))
                ?? throw new InvalidDataException("Không đọc được tùy chọn bỏ qua dạng bài.")
            : new();
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "activity-skips.json");
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
