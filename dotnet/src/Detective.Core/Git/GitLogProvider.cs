namespace Detective.Core.Git;

/// <summary>
/// Produces and caches the raw <c>git log --numstat</c> output for a repository.
/// The full (unlimited) log is cached under <c>.detective/log</c> and invalidated
/// by the current tree hash, exactly like the original tool's log cache; commit
/// and date limits are applied later by <see cref="GitLogParser"/>.
/// </summary>
public sealed class GitLogProvider
{
    public const string DetectiveDir = ".detective";
    private const string LogFile = "log";
    private const string HashFile = "hash";

    // ISO-8601 author date (%aI) keeps date parsing culture-invariant on .NET.
    private static readonly string[] LogArgs =
    {
        "log", "--numstat", "--pretty=format:\"%an <%ae>,%aI%x09%H,%s\""
    };

    private readonly string _repoPath;

    public GitLogProvider(string repoPath) => _repoPath = repoPath;

    public bool IsRepo()
    {
        var (code, _, _) = ProcessRunner.Run("git", new[] { "rev-parse", "--is-inside-work-tree" }, _repoPath);
        return code == 0;
    }

    public string TreeHash()
    {
        var (code, outp, err) = ProcessRunner.Run("git", new[] { "rev-parse", "HEAD^{tree}" }, _repoPath);
        if (code != 0) throw new InvalidOperationException($"git tree hash failed: {err}");
        return outp.Trim();
    }

    public string CommitCount()
    {
        var (code, outp, err) = ProcessRunner.Run("git", new[] { "rev-list", "--count", "HEAD" }, _repoPath);
        if (code != 0) throw new InvalidOperationException($"git rev-list failed: {err}");
        return outp.Trim();
    }

    /// <summary>True when the cached log is missing or the tree changed since it was written.</summary>
    public bool IsStale() =>
        !File.Exists(HashPath) || File.ReadAllText(HashPath).Trim() != TreeHash();

    private string DetectivePath => Path.Combine(_repoPath, DetectiveDir);
    private string LogPath => Path.Combine(DetectivePath, LogFile);
    private string HashPath => Path.Combine(DetectivePath, HashFile);

    /// <summary>Returns the cached log, refreshing it when the tree hash changed.</summary>
    public string GetLog(bool forceRefresh = false)
    {
        Directory.CreateDirectory(DetectivePath);
        var current = TreeHash();
        var cachedHash = File.Exists(HashPath) ? File.ReadAllText(HashPath).Trim() : null;

        if (!forceRefresh && cachedHash == current && File.Exists(LogPath))
            return File.ReadAllText(LogPath);

        var log = RunFullLog();
        File.WriteAllText(LogPath, log);
        File.WriteAllText(HashPath, current);
        return log;
    }

    /// <summary>Force a cache fill (used by the CLI <c>--fill-cache</c> switch).</summary>
    public void FillCache() => GetLog(forceRefresh: true);

    private string RunFullLog()
    {
        var (code, outp, err) = ProcessRunner.Run("git", LogArgs, _repoPath);
        if (code != 0) throw new InvalidOperationException($"git log failed: {err}");
        return outp;
    }
}
