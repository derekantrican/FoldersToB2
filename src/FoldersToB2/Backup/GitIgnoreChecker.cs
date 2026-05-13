using System.Diagnostics;
using Serilog;

namespace FoldersToB2.Backup;

/// <summary>
/// Uses git ls-files to get the list of non-ignored files in a repo.
/// Discovers repos by scanning for .git directories (no git processes needed for discovery).
/// </summary>
public static class GitIgnoreChecker
{
    private static bool? _gitAvailable;

    public static bool IsGitAvailable()
    {
        if (_gitAvailable.HasValue) return _gitAvailable.Value;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
            _gitAvailable = process?.ExitCode == 0;
        }
        catch
        {
            _gitAvailable = false;
        }

        if (!_gitAvailable.Value)
            Log.Warning("git not found on PATH - .gitignore rules will not be applied");

        return _gitAvailable.Value;
    }

    /// <summary>
    /// Discovers git repo roots under the given directories by scanning for .git folders.
    /// </summary>
    public static List<string> DiscoverRepos(IEnumerable<string> searchDirectories)
    {
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in searchDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            if (Directory.Exists(Path.Combine(dir, ".git")))
                repos.Add(NormalizePath(dir));

            try
            {
                foreach (var subdir in Directory.EnumerateDirectories(dir))
                {
                    if (Directory.Exists(Path.Combine(subdir, ".git")))
                        repos.Add(NormalizePath(subdir));
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Error scanning for repos in {Dir}: {Error}", dir, ex.Message);
            }
        }

        Log.Information("Discovered {Count} git repos", repos.Count);
        return repos.ToList();
    }

    /// <summary>
    /// Runs "git ls-files" to get all non-ignored files in a repo (tracked + untracked but not ignored).
    /// Returns absolute paths.
    /// </summary>
    public static HashSet<string> GetNonIgnoredFiles(string repoRoot)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // --cached: tracked files
            // --others: untracked files
            // --exclude-standard: apply .gitignore rules to --others
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "ls-files --cached --others --exclude-standard",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    // git ls-files returns repo-relative paths; convert to absolute
                    var absolutePath = Path.GetFullPath(Path.Combine(repoRoot, line.Trim()));
                    files.Add(absolutePath);
                }
            }

            process.WaitForExit(60000);

            if (process.ExitCode != 0)
                Log.Warning("git ls-files exited with code {Code} for {Repo}", process.ExitCode, repoRoot);
        }
        catch (Exception ex)
        {
            Log.Warning("git ls-files failed for {Repo}: {Error}", repoRoot, ex.Message);
        }

        return files;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
