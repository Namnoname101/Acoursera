using Microsoft.Data.Sqlite;
using AutomationPlatform.Domain.Entities;
using AutomationPlatform.Domain.Interfaces;
using System.IO;
using System.Text.Json;

namespace AutomationPlatform.Infrastructure.Data;

public sealed class SqliteCourseRepository : IRepository<CourseEntity, Guid>
{
    private readonly string _connectionString;

    public SqliteCourseRepository(string? dbPath = null)
    {
        string resolvedPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Acose",
                "Data",
                "automation.db")
            : Path.GetFullPath(dbPath);
        string? dataDirectory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = resolvedPath
        }.ToString();
        EnsureDatabaseCreated();
    }

    private void EnsureDatabaseCreated()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Courses (
                Id TEXT PRIMARY KEY,
                Platform TEXT NOT NULL,
                CourseUrl TEXT NOT NULL,
                CourseName TEXT NOT NULL,
                Instructor TEXT,
                Status INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastAccessedAt TEXT,
                Metadata TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_courses_status ON Courses(Status);
            CREATE INDEX IF NOT EXISTS idx_courses_platform ON Courses(Platform);
        ";
        cmd.ExecuteNonQuery();
    }

    public async Task<CourseEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Platform, CourseUrl, CourseName, Instructor, Status, CreatedAt, LastAccessedAt FROM Courses WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapFromReader(reader);
        }
        return null;
    }

    public async Task<IReadOnlyList<CourseEntity>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<CourseEntity>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Platform, CourseUrl, CourseName, Instructor, Status, CreatedAt, LastAccessedAt FROM Courses ORDER BY CreatedAt DESC";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapFromReader(reader));
        }
        return list.AsReadOnly();
    }

    public async Task AddAsync(CourseEntity entity, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Courses (Id, Platform, CourseUrl, CourseName, Instructor, Status, CreatedAt, LastAccessedAt, Metadata)
            VALUES (@id, @platform, @url, @name, @instructor, @status, @created, @lastAccessed, @metadata)";
        cmd.Parameters.AddWithValue("@id", entity.Id.ToString());
        cmd.Parameters.AddWithValue("@platform", entity.Platform);
        cmd.Parameters.AddWithValue("@url", entity.CourseUrl);
        cmd.Parameters.AddWithValue("@name", entity.CourseName);
        cmd.Parameters.AddWithValue("@instructor", entity.Instructor ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (int)entity.Status);
        cmd.Parameters.AddWithValue("@created", entity.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@lastAccessed", entity.LastAccessedAt?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", "{}"); // Dự phòng mở rộng
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(CourseEntity entity, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Courses SET
                Platform = @platform,
                CourseUrl = @url,
                CourseName = @name,
                Instructor = @instructor,
                Status = @status,
                LastAccessedAt = @lastAccessed
            WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", entity.Id.ToString());
        cmd.Parameters.AddWithValue("@platform", entity.Platform);
        cmd.Parameters.AddWithValue("@url", entity.CourseUrl);
        cmd.Parameters.AddWithValue("@name", entity.CourseName);
        cmd.Parameters.AddWithValue("@instructor", entity.Instructor ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (int)entity.Status);
        cmd.Parameters.AddWithValue("@lastAccessed", entity.LastAccessedAt?.ToString("o") ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Courses WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CourseEntity MapFromReader(SqliteDataReader reader)
    {
        return new CourseEntity
        {
            Id = Guid.Parse(reader.GetString(0)),
            Platform = reader.GetString(1),
            CourseUrl = reader.GetString(2),
            CourseName = reader.GetString(3),
            Instructor = reader.IsDBNull(4) ? null : reader.GetString(4),
            Status = (EnrollmentStatus)reader.GetInt32(5),
            CreatedAt = DateTime.Parse(reader.GetString(6)),
            LastAccessedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7))
        };
    }
}
