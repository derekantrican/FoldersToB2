using FoldersToB2.B2;
using Serilog;

namespace FoldersToB2.Backup;

public class VersionCleaner
{
    private readonly B2Client _b2;
    private readonly int _retentionDays;

    public VersionCleaner(B2Client b2, int retentionDays)
    {
        _b2 = b2;
        _retentionDays = retentionDays;
    }

    public async Task CleanAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays).ToUnixTimeMilliseconds();
        int deleted = 0;
        string? nextFileName = null;
        string? nextFileId = null;

        Log.Information("Cleaning file versions older than {Days} days", _retentionDays);

        do
        {
            ct.ThrowIfCancellationRequested();

            var listing = await _b2.ListFileVersionsAsync(nextFileName, nextFileId, 1000, ct);

            // Group by file name to identify old versions
            var groups = listing.Files.GroupBy(f => f.FileName);

            foreach (var group in groups)
            {
                // Keep the latest version, consider deleting older ones past retention
                var versions = group.OrderByDescending(f => f.UploadTimestamp).ToList();

                foreach (var old in versions.Skip(1))
                {
                    if (old.UploadTimestamp < cutoff)
                    {
                        try
                        {
                            await _b2.DeleteFileVersionAsync(old.FileName, old.FileId, ct);
                            deleted++;
                            Log.Debug("Deleted old version: {FileName} (uploaded {Time})",
                                old.FileName,
                                DateTimeOffset.FromUnixTimeMilliseconds(old.UploadTimestamp));
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("Failed to delete old version {FileName}/{FileId}: {Error}",
                                old.FileName, old.FileId, ex.Message);
                        }
                    }
                }
            }

            nextFileName = listing.NextFileName;
            nextFileId = listing.NextFileId;
        } while (nextFileName is not null);

        if (deleted > 0)
            Log.Information("Cleaned up {Count} old file versions", deleted);
        else
            Log.Information("No old file versions to clean up");
    }
}
