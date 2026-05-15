using System.Security.Cryptography;
using FoldersToB2.B2;
using FoldersToB2.Config;
using FoldersToB2.Notifications;
using Serilog;

namespace FoldersToB2.Backup;

public class BackupService
{
    private readonly BackupConfig _config;
    private readonly FileManifest _manifest;
    private readonly WebhookNotifier? _webhook;

    public event Action<string>? StatusChanged;
    public event Action<int, int>? ProgressChanged;

    public BackupService(BackupConfig config, FileManifest manifest, WebhookNotifier? webhook)
    {
        _config = config;
        _manifest = manifest;
        _webhook = webhook;
    }

    public async Task<bool> RunBackupAsync(CancellationToken ct = default)
    {
        StatusChanged?.Invoke("Authorizing...");

        using var b2 = new B2Client(
            _config.Backblaze.ApplicationKeyId,
            _config.Backblaze.ApplicationKey,
            _config.Backblaze.BucketId);

        try
        {
            await b2.AuthorizeAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to authorize with Backblaze B2");
            StatusChanged?.Invoke("Authorization failed");
            await NotifyErrorAsync("Authorization failed", ex.Message);
            return false;
        }

        // Scan and detect changes
        StatusChanged?.Invoke("Scanning files...");
        var filesToProcess = ScanFiles();
        Log.Information("Scan complete: {Count} files to evaluate", filesToProcess.Count);

        StatusChanged?.Invoke("Checking for changes...");
        var changedFiles = DetectChanges(filesToProcess);
        Log.Information("Change detection complete: {Changed} changed/new of {Total} files",
            changedFiles.Count, filesToProcess.Count);

        bool hadFailures = false;

        if (changedFiles.Count == 0)
        {
            StatusChanged?.Invoke("No changes detected");
        }
        else
        {
            int uploaded = 0;
            int failed = 0;
            int skipped = 0;

            for (int i = 0; i < changedFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (localPath, b2FileName) = changedFiles[i];

                ProgressChanged?.Invoke(i + 1, changedFiles.Count);
                StatusChanged?.Invoke($"Uploading {i + 1}/{changedFiles.Count}: {Path.GetFileName(localPath)}");

                var fileProgress = new Progress<(long BytesUploaded, long TotalBytes)>(p =>
                {
                    var pct = p.TotalBytes > 0 ? (double)p.BytesUploaded / p.TotalBytes * 100 : 0;
                    var uploaded = FormatBytes(p.BytesUploaded);
                    var total = FormatBytes(p.TotalBytes);
                    StatusChanged?.Invoke($"Uploading {i + 1}/{changedFiles.Count}: {Path.GetFileName(localPath)} — {uploaded} / {total} ({pct:F1}%)");
                });

                try
                {
                    var result = await UploadWithRetryAsync(b2, b2FileName, localPath, ct, progress: fileProgress);

                    var fileInfo = new FileInfo(localPath);
                    _manifest.UpsertRecord(new FileRecord
                    {
                        LocalPath = localPath,
                        B2FileName = b2FileName,
                        B2FileId = result.FileId,
                        Sha256Hash = ComputeSha256(localPath),
                        LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                        FileSize = fileInfo.Length,
                        LastBackedUpUtc = DateTime.UtcNow
                    });

                    uploaded++;
                    Log.Information("Uploaded: {Path} -> {B2Name}", localPath, b2FileName);
                }
                catch (IOException ex) when (IsFileLocked(ex))
                {
                    Log.Warning("Skipping locked file: {Path} - {Error}", localPath, ex.Message);
                    skipped++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to upload: {Path}", localPath);
                    failed++;
                }
            }

            var summary = skipped > 0
                ? $"Uploaded {uploaded}, failed {failed}, skipped {skipped} locked of {changedFiles.Count} files"
                : $"Uploaded {uploaded}, failed {failed} of {changedFiles.Count} files";
            StatusChanged?.Invoke(summary);
            Log.Information(summary);

            if (failed > 0)
                await NotifyErrorAsync("Backup completed with errors", summary);

            hadFailures = failed > 0;
        }

        // Clean up old versions
        StatusChanged?.Invoke("Cleaning old versions...");
        try
        {
            var cleaner = new VersionCleaner(b2, _config.OldVersionRetentionDays);
            await cleaner.CleanAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clean old versions");
        }

        StatusChanged?.Invoke("Idle - last backup: " + DateTime.Now.ToString("g"));
        return !hadFailures;
    }

    private List<(string LocalPath, string B2FileName)> ScanFiles()
    {
        var files = new List<(string, string)>();

        // Add individual files
        foreach (var fileEntry in _config.Files)
        {
            var (filePath, b2Override) = Config.PathEntry.Parse(fileEntry);

            if (!File.Exists(filePath))
            {
                Log.Warning("Configured file does not exist: {File}", filePath);
                continue;
            }

            string b2FileName;
            if (b2Override is not null)
            {
                b2FileName = b2Override;
            }
            else
            {
                var rootDrive = Path.GetPathRoot(filePath) ?? "";
                var relativePath = filePath;
                if (relativePath.StartsWith(rootDrive, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath[rootDrive.Length..];
                b2FileName = relativePath.Replace('\\', '/');
            }

            files.Add((filePath, b2FileName));
        }

        var excludeExtensions = _config.ExcludeFileTypes
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excludeFolders = _config.ExcludeFolders
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var folderEntry in _config.Folders)
        {
            var (folder, b2Override) = Config.PathEntry.Parse(folderEntry);

            if (!Directory.Exists(folder))
            {
                Log.Warning("Configured folder does not exist: {Folder}", folder);
                continue;
            }

            var rootDrive = Path.GetPathRoot(folder) ?? "";
            var folderFullPath = Path.GetFullPath(folder);

            foreach (var filePath in EnumerateFilesSafe(folder, excludeFolders))
            {
                if (excludeExtensions.Contains(Path.GetExtension(filePath)))
                    continue;

                string b2FileName;
                if (b2Override is not null)
                {
                    // Custom B2 root: replace local folder prefix with the override
                    var relativeToFolder = Path.GetFullPath(filePath)[folderFullPath.Length..]
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    b2FileName = b2Override.TrimEnd('/') + "/" + relativeToFolder.Replace('\\', '/');
                }
                else
                {
                    // Mirror local structure: C:\Users\user\Doc\file.txt -> Users/user/Doc/file.txt
                    var relativePath = filePath;
                    if (relativePath.StartsWith(rootDrive, StringComparison.OrdinalIgnoreCase))
                        relativePath = relativePath[rootDrive.Length..];
                    b2FileName = relativePath.Replace('\\', '/');
                }

                files.Add((filePath, b2FileName));
            }
        }

        // Apply .gitignore filtering if enabled
        if (_config.RespectGitIgnore && GitIgnoreChecker.IsGitAvailable())
        {
            files = ApplyGitIgnoreFilter(files);
        }

        return files;
    }

    private List<(string LocalPath, string B2FileName)> ApplyGitIgnoreFilter(
        List<(string LocalPath, string B2FileName)> files)
    {
        // 1. Discover repos by scanning for .git folders (no git processes)
        var repos = GitIgnoreChecker.DiscoverRepos(_config.Folders);
        if (repos.Count == 0)
        {
            Log.Information("No git repos found - skipping .gitignore filtering");
            return files;
        }

        // 2. Get the allowed file set for each repo (ONE git ls-files per repo)
        var allowedByRepo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
        {
            var allowed = GitIgnoreChecker.GetNonIgnoredFiles(repo);
            allowedByRepo[repo] = allowed;
            Log.Debug("Repo {Repo}: {Count} non-ignored files", repo, allowed.Count);
        }

        // 3. Filter: keep files that are in a repo's allowed set, or not in any repo
        var result = new List<(string LocalPath, string B2FileName)>();
        int totalFiltered = 0;

        foreach (var file in files)
        {
            var normalizedPath = Path.GetFullPath(file.LocalPath);
            string? matchedRepo = null;

            foreach (var repo in repos)
            {
                if (normalizedPath.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    matchedRepo = repo;
                    break;
                }
            }

            if (matchedRepo is null)
            {
                // Not in any repo — keep it
                result.Add(file);
            }
            else if (allowedByRepo[matchedRepo].Contains(normalizedPath))
            {
                // In a repo and not gitignored — keep it
                result.Add(file);
            }
            else
            {
                totalFiltered++;
            }
        }

        if (totalFiltered > 0)
            Log.Information("Filtered {Count} gitignored files across {RepoCount} repos", totalFiltered, repos.Count);

        return result;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory, HashSet<string> excludeFolders)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warning("Access denied: {Directory}", directory);
            yield break;
        }
        catch (Exception ex)
        {
            Log.Warning("Error scanning {Directory}: {Error}", directory, ex.Message);
            yield break;
        }

        foreach (var file in files)
            yield return file;

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var subdir in subdirs)
        {
            var dirName = Path.GetFileName(subdir);
            if (excludeFolders.Contains(dirName))
                continue;

            foreach (var file in EnumerateFilesSafe(subdir, excludeFolders))
                yield return file;
        }
    }

    private static bool IsInExcludedFolder(string filePath, HashSet<string> excludedFolders)
    {
        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => excludedFolders.Contains(p));
    }

    private List<(string LocalPath, string B2FileName)> DetectChanges(
        List<(string LocalPath, string B2FileName)> files)
    {
        var changed = new List<(string, string)>();

        foreach (var (localPath, b2FileName) in files)
        {
            try
            {
                var record = _manifest.GetRecord(localPath);
                var fileInfo = new FileInfo(localPath);

                if (record is null)
                {
                    // New file - not yet in manifest
                    changed.Add((localPath, b2FileName));
                    continue;
                }

                // Quick check: if size and last-modified are identical, skip
                if (record.FileSize == fileInfo.Length &&
                    record.LastModifiedUtc == fileInfo.LastWriteTimeUtc)
                    continue;

                // Size or timestamp changed - verify with SHA256
                var currentHash = ComputeSha256(localPath);
                if (currentHash != record.Sha256Hash)
                    changed.Add((localPath, b2FileName));
            }
            catch (IOException ex) when (IsFileLocked(ex))
            {
                Log.Debug("Skipping locked file during scan: {Path}", localPath);
            }
            catch (Exception ex)
            {
                Log.Warning("Error checking file {Path}: {Error}", localPath, ex.Message);
            }
        }

        return changed;
    }

    private static async Task<B2FileResponse> UploadWithRetryAsync(
        B2Client b2, string b2FileName, string localPath, CancellationToken ct, int maxRetries = 3, IProgress<(long BytesUploaded, long TotalBytes)>? progress = null)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await b2.UploadFileAsync(b2FileName, localPath, ct, progress);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Log.Warning("Upload attempt {Attempt} failed for {File}. Retrying in {Delay}s: {Error}",
                    attempt, b2FileName, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct);

                // Re-authorize on second retry in case token expired
                if (attempt == 2)
                {
                    try { await b2.AuthorizeAsync(); }
                    catch { /* will fail on next attempt */ }
                }
            }
        }

        throw new InvalidOperationException($"Upload failed after {maxRetries} attempts: {b2FileName}");
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsFileLocked(IOException ex)
    {
        var errorCode = ex.HResult & 0xFFFF;
        return errorCode is 32 or 33; // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B"
    };

    private async Task NotifyErrorAsync(string message, string description)
    {
        if (_webhook is null) return;

        try
        {
            await _webhook.SendAsync(message, description);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send webhook notification");
        }
    }
}
