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
    public string? WebhookUrl { get; set; }
    public int OldVersionRetentionDays { get; set; } = 7;
}

public class BackblazeSettings
{
    public string ApplicationKeyId { get; set; } = "";
    public string ApplicationKey { get; set; } = "";
    public string BucketId { get; set; } = "";
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
    }
}
