using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomationPlatform.Presentation.Services;

/// <summary>
/// Stores the identity discovered for one durable course profile. The cache is
/// deliberately small and contains no login secret or cookie.
/// </summary>
public static class CourseraProfileIdentityCache
{
    public const string FileName = ".acose-profile-identity.json";
    private const int CurrentVersion = 1;
    private const long MaximumCacheBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
    };

    public static CachedCourseraProfileIdentity? TryLoad(string profilePath)
    {
        try
        {
            string cachePath = GetCachePath(profilePath);
            var cacheInfo = new FileInfo(cachePath);
            if (!cacheInfo.Exists || cacheInfo.Length is <= 0 or > MaximumCacheBytes)
            {
                return null;
            }

            using FileStream stream = new(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(
                stream,
                JsonOptions);
            if (document?.Version != CurrentVersion)
            {
                return null;
            }

            string? fullName = TryNormalizeFullName(document.FullName);
            if (fullName == null)
            {
                return null;
            }

            return new CachedCourseraProfileIdentity(
                TryNormalizeUserId(document.UserId),
                fullName);
        }
        catch
        {
            // A damaged or partially copied cache is never trusted. The worker
            // falls back to the existing live Coursera profile lookup instead.
            return null;
        }
    }

    public static bool TrySave(
        string profilePath,
        string? userId,
        string? fullName)
    {
        string? normalizedFullName = TryNormalizeFullName(fullName);
        if (normalizedFullName == null)
        {
            return false;
        }

        string? temporaryPath = null;
        try
        {
            string normalizedProfilePath = Path.GetFullPath(profilePath);
            Directory.CreateDirectory(normalizedProfilePath);
            string cachePath = GetCachePath(normalizedProfilePath);
            temporaryPath = Path.Combine(
                normalizedProfilePath,
                $".{FileName}.{Guid.NewGuid():N}.tmp");
            var document = new CacheDocument
            {
                Version = CurrentVersion,
                UserId = TryNormalizeUserId(userId),
                FullName = normalizedFullName,
            };
            string json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, cachePath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public static CachedCourseraProfileIdentity? ResolveForCourseJob(
        string profilePath,
        string? suppliedFullName,
        string? suppliedUserId)
    {
        CachedCourseraProfileIdentity? cachedIdentity = TryLoad(profilePath);
        string? normalizedFullName = TryNormalizeFullName(suppliedFullName);
        if (normalizedFullName == null)
        {
            return cachedIdentity;
        }

        string? normalizedUserId = TryNormalizeUserId(suppliedUserId);
        if (normalizedUserId == null &&
            cachedIdentity != null &&
            string.Equals(
                cachedIdentity.FullName,
                normalizedFullName,
                StringComparison.Ordinal))
        {
            normalizedUserId = cachedIdentity.UserId;
        }

        var resolvedIdentity = new CachedCourseraProfileIdentity(
            normalizedUserId,
            normalizedFullName);
        TrySave(
            profilePath,
            resolvedIdentity.UserId,
            resolvedIdentity.FullName);
        return resolvedIdentity;
    }

    public static string? TryNormalizeFullName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = Regex.Replace(
                value.Replace('\u00a0', ' '),
                @"\s+",
                " ")
            .Trim()
            .Normalize(NormalizationForm.FormC);
        if (normalized.Length < 2 || normalized.Length > 120 ||
            normalized.Contains('@') || normalized.Any(char.IsControl) ||
            !normalized.Any(char.IsLetter))
        {
            return null;
        }

        string[] forbiddenNames =
        {
            "profile", "account", "user", "settings", "my profile", "view profile",
            "sign out", "log out",
        };
        return forbiddenNames.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    public static string? TryNormalizeUserId(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        return normalized.Length is >= 1 and <= 200 &&
               !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private static string GetCachePath(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            throw new ArgumentException("A durable profile path is required.", nameof(profilePath));
        }

        return Path.Combine(Path.GetFullPath(profilePath), FileName);
    }

    private sealed class CacheDocument
    {
        public int Version { get; set; }
        public string? UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
    }
}

public sealed record CachedCourseraProfileIdentity(
    string? UserId,
    string FullName);
