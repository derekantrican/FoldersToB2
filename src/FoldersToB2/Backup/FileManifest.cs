using Microsoft.Data.Sqlite;

namespace FoldersToB2.Backup;

public class FileManifest : IDisposable
{
    private readonly SqliteConnection _db;

    public FileManifest(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS files (
                local_path TEXT PRIMARY KEY,
                b2_file_name TEXT NOT NULL,
                b2_file_id TEXT NOT NULL,
                sha256_hash TEXT NOT NULL,
                last_modified_utc TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                last_backed_up_utc TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public FileRecord? GetRecord(string localPath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT local_path, b2_file_name, b2_file_id, sha256_hash, last_modified_utc, file_size, last_backed_up_utc FROM files WHERE local_path = @path";
        cmd.Parameters.AddWithValue("@path", localPath);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return ReadRecord(reader);
    }

    public void UpsertRecord(FileRecord record)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (local_path, b2_file_name, b2_file_id, sha256_hash, last_modified_utc, file_size, last_backed_up_utc)
            VALUES (@path, @b2name, @b2id, @hash, @modified, @size, @backed_up)
            ON CONFLICT(local_path) DO UPDATE SET
                b2_file_name = @b2name,
                b2_file_id = @b2id,
                sha256_hash = @hash,
                last_modified_utc = @modified,
                file_size = @size,
                last_backed_up_utc = @backed_up
            """;
        cmd.Parameters.AddWithValue("@path", record.LocalPath);
        cmd.Parameters.AddWithValue("@b2name", record.B2FileName);
        cmd.Parameters.AddWithValue("@b2id", record.B2FileId);
        cmd.Parameters.AddWithValue("@hash", record.Sha256Hash);
        cmd.Parameters.AddWithValue("@modified", record.LastModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@size", record.FileSize);
        cmd.Parameters.AddWithValue("@backed_up", record.LastBackedUpUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RemoveRecord(string localPath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE local_path = @path";
        cmd.Parameters.AddWithValue("@path", localPath);
        cmd.ExecuteNonQuery();
    }

    public List<FileRecord> GetAllRecords()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT local_path, b2_file_name, b2_file_id, sha256_hash, last_modified_utc, file_size, last_backed_up_utc FROM files";

        var records = new List<FileRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }
        return records;
    }

    private static FileRecord ReadRecord(SqliteDataReader reader)
    {
        return new FileRecord
        {
            LocalPath = reader.GetString(0),
            B2FileName = reader.GetString(1),
            B2FileId = reader.GetString(2),
            Sha256Hash = reader.GetString(3),
            LastModifiedUtc = DateTime.Parse(reader.GetString(4)),
            FileSize = reader.GetInt64(5),
            LastBackedUpUtc = DateTime.Parse(reader.GetString(6))
        };
    }

    public void Dispose() => _db.Dispose();
}

public class FileRecord
{
    public string LocalPath { get; set; } = "";
    public string B2FileName { get; set; } = "";
    public string B2FileId { get; set; } = "";
    public string Sha256Hash { get; set; } = "";
    public DateTime LastModifiedUtc { get; set; }
    public long FileSize { get; set; }
    public DateTime LastBackedUpUtc { get; set; }
}
