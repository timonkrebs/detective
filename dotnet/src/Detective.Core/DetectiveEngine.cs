using Detective.Core.Analysis;
using Detective.Core.Git;
using Detective.Core.Infrastructure;
using Detective.Core.Model;
using Detective.Core.Roslyn;

namespace Detective.Core;

/// <summary>
/// High-level facade over the analysis engine. Owns config loading, the git log
/// cache and the (lazily built) Roslyn dependency map, and exposes one method
/// per analysis. This is what both the CLI and the WPF view models call.
/// </summary>
public sealed class DetectiveEngine
{
    private readonly AnalysisOptions _options;
    private readonly GitLogProvider _git;
    private readonly Lazy<Config> _config;
    private Dictionary<string, List<string>>? _deps;

    public DetectiveEngine(AnalysisOptions options)
    {
        _options = options;
        _git = new GitLogProvider(options.ResolvedPath);
        _config = new Lazy<Config>(() => ConfigService.LoadOrCreate(_options));
    }

    public string RepoPath => _options.ResolvedPath;
    public Config Config => _config.Value;
    public bool IsRepo() => _git.IsRepo();

    public string GetLog(bool forceRefresh = false) => _git.GetLog(forceRefresh);
    public void FillCache() => _git.FillCache();

    public CouplingResult Coupling() =>
        CouplingAnalyzer.Calc(Deps(), Config);

    public ChangeCouplingResult ChangeCoupling(Limits? limits = null) =>
        ChangeCouplingAnalyzer.Calc(GetLog(), Config, limits ?? Limits.None);

    public TeamAlignmentResult TeamAlignment(bool byUser = false, Limits? limits = null) =>
        TeamAlignmentAnalyzer.Calc(GetLog(), Config, limits ?? Limits.None, byUser, _options.DemoMode);

    public HotspotResult Hotspots(HotspotCriteria criteria, Limits? limits = null) =>
        new HotspotAnalyzer(RepoPath).FindHotspotFiles(GetLog(), Config, criteria, limits ?? Limits.None);

    public AggregatedHotspotsResult AggregatedHotspots(HotspotCriteria criteria, Limits? limits = null) =>
        new HotspotAnalyzer(RepoPath).AggregateHotspots(GetLog(), Config, criteria, limits ?? Limits.None);

    /// <summary>Roslyn x-ray over every analyzed C# file in the working tree.</summary>
    public List<CodeMetrics> CodeMetrics()
    {
        var filter = new GlobMatcher(
            Config.Filter.Files is { Count: > 0 } f ? f : new List<string> { "**/*.cs" });
        var metrics = new List<CodeMetrics>();
        foreach (var full in Directory.EnumerateFiles(RepoPath, "*.cs", SearchOption.AllDirectories))
        {
            var rel = PathUtils.ToPosix(Path.GetRelativePath(RepoPath, full));
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("bin/") || rel.StartsWith("obj/"))
                continue;
            if (!filter.IsMatch(rel)) continue;
            try { metrics.Add(CSharpComplexityAnalyzer.FileMetrics(File.ReadAllText(full), rel)); }
            catch { /* skip unreadable/unparsable files */ }
        }
        metrics.Sort((a, b) => b.CyclomaticComplexity.CompareTo(a.CyclomaticComplexity));
        return metrics;
    }

    private Dictionary<string, List<string>> Deps() =>
        _deps ??= RoslynDependencyAnalyzer.BuildDeps(
            RepoPath,
            Config.Filter.Files is { Count: > 0 } f ? f : new List<string> { "**/*.cs" });
}
