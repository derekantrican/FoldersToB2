using System.Text.Json;

namespace FoldersToB2.Config;

public class BackupConfig
{
    public BackblazeSettings Backblaze { get; set; } = new();
    public int BackupFrequencyMinutes { get; set; } = 15;
    public List<string> Folders { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public List<string> ExcludeFolders { get; set; } = new();
    public List<string> ExcludeFileTypes { get; set; } = new();
    public bool RespectGitIgnore { get; set; } = true;
    public bool CopyToTempOnLock { get; set; } = true;
    public string? WebhookUrl { get; set; }
    public int OldVersionRetentionDays { get; set; } = 7;
}

public class BackblazeSettings
{
    public string ApplicationKeyId { get; set; } = "";
    public string ApplicationKey { get; set; } = "";
    public string BucketId { get; set; } = "";
}

public static class PathEntry
{
    /// <summary>
    /// Parses a config entry that may contain a pipe separator for custom B2 destinations.
    /// Format: "localPath" or "localPath|b2Path"
    /// </summary>
    public static (string LocalPath, string? B2Override) Parse(string entry)
    {
        var pipeIndex = entry.IndexOf('|');
        if (pipeIndex < 0)
            return (entry, null);

        // Normalize local path so forward slashes work on Windows
        var localPath = entry[..pipeIndex].Replace('/', Path.DirectorySeparatorChar);
        // Normalize B2 path to always use forward slashes
        var b2Path = entry[(pipeIndex + 1)..].Replace('\\', '/').TrimStart('/');
        return (localPath, b2Path);
    }
}

public static class ConfigLoader
{
    public static BackupConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}");

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<BackupConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("Failed to deserialize configuration");

        Validate(config);
        return config;
    }

    private static void Validate(BackupConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Backblaze.ApplicationKeyId))
            throw new InvalidOperationException("Backblaze ApplicationKeyId is required");
        if (string.IsNullOrWhiteSpace(config.Backblaze.ApplicationKey))
            throw new InvalidOperationException("Backblaze ApplicationKey is required");
        if (string.IsNullOrWhiteSpace(config.Backblaze.BucketId))
            throw new InvalidOperationException("Backblaze BucketId is required");
        if (config.Folders.Count == 0)
            throw new InvalidOperationException("At least one folder must be configured");
        if (config.BackupFrequencyMinutes < 1)
            throw new InvalidOperationException("BackupFrequencyMinutes must be at least 1");

        ValidatePathEntries(config.Folders, "folders");
        ValidatePathEntries(config.Files, "files");
    }

    private static void ValidatePathEntries(List<string> entries, string sectionName)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var pipeIndex = entry.IndexOf('|');
            if (pipeIndex < 0)
                continue;

            var localPath = entry[..pipeIndex];
            var b2Path = entry[(pipeIndex + 1)..];

            if (string.IsNullOrWhiteSpace(localPath))
                throw new InvalidOperationException(
                    $"Invalid entry in {sectionName}[{i}]: local path (left of '|') is empty: \"{entry}\"");
            if (string.IsNullOrWhiteSpace(b2Path))
                throw new InvalidOperationException(
                    $"Invalid entry in {sectionName}[{i}]: B2 destination (right of '|') is empty: \"{entry}\"");

            if (b2Path.Contains('|'))
                throw new InvalidOperationException(
                    $"Invalid entry in {sectionName}[{i}]: multiple '|' characters found: \"{entry}\"");
        }
    }
}
