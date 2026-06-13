namespace Detective.Core.Model;

/// <summary>
/// Top level options for a run. <see cref="Path"/> is the repository to analyze
/// (defaults to the current working directory when empty).
/// </summary>
public sealed class AnalysisOptions
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Config file location, relative to <see cref="Path"/>.</summary>
    public string ConfigPath { get; set; } = ".detective/config.json";

    /// <summary>When set, author names are replaced by a rotating set of demo users.</summary>
    public bool DemoMode { get; set; }

    public string ResolvedPath => string.IsNullOrWhiteSpace(Path)
        ? Directory.GetCurrentDirectory()
        : System.IO.Path.GetFullPath(Path);
}
