namespace Detective.Core.Infrastructure;

public static class PathUtils
{
    /// <summary>Ensures a trailing slash so prefix matches do not bleed across folders.</summary>
    public static string NormalizeFolder(string folder) =>
        folder.EndsWith('/') ? folder : folder + "/";

    /// <summary>Strips a single trailing slash for display.</summary>
    public static string ToDisplayFolder(string folder) =>
        !string.IsNullOrEmpty(folder) && folder.EndsWith('/')
            ? folder[..^1]
            : folder;

    public static int ToPercent(double factor) => (int)Math.Round(factor * 100);

    /// <summary>Always-forward-slash path normalization, matching git log output.</summary>
    public static string ToPosix(string path) => path.Replace('\\', '/');

    /// <summary>Parent folder of a posix path; "." when there is none (matches Node path.dirname).</summary>
    public static string Dirname(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? (idx == 0 ? "/" : ".") : path[..idx];
    }
}
