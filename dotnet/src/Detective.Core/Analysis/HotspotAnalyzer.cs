using Detective.Core.Git;
using Detective.Core.Infrastructure;
using Detective.Core.Model;
using Detective.Core.Roslyn;

namespace Detective.Core.Analysis;

/// <summary>
/// Hotspots = change frequency × complexity. A complex file changed often is a
/// higher risk for bugs. Port of <c>hotspot.ts</c>; complexity for <c>.cs</c>
/// files is computed with Roslyn.
/// </summary>
public sealed class HotspotAnalyzer
{
    private readonly string _repoPath;

    public HotspotAnalyzer(string repoPath) => _repoPath = repoPath;

    private sealed class Hotspot
    {
        public int Commits;
        public int ChangedLines;
        public int Complexity;
        public int Score;
    }

    public HotspotResult FindHotspotFiles(string log, Config config, HotspotCriteria criteria, Limits limits)
    {
        var hotspots = AnalyzeLogs(log, config, criteria, limits);

        var filtered = new List<FlatHotspot>();
        foreach (var (fileName, h) in hotspots)
        {
            h.Score = h.Complexity == -1 ? -1 : h.Complexity * h.Commits;
            if (h.Score >= criteria.MinScore)
                filtered.Add(new FlatHotspot
                {
                    FileName = fileName,
                    Commits = h.Commits,
                    ChangedLines = h.ChangedLines,
                    Complexity = h.Complexity,
                    Score = h.Score,
                });
        }

        filtered.Sort((a, b) => b.Score.CompareTo(a.Score));
        return new HotspotResult { Hotspots = filtered };
    }

    public AggregatedHotspotsResult AggregateHotspots(string log, Config config, HotspotCriteria criteria, Limits limits)
    {
        var phase1 = new HotspotCriteria { Module = criteria.Module, Metric = criteria.Metric, MinScore = 0 };
        var hotspots = FindHotspotFiles(log, config, phase1, limits).Hotspots;

        var modules = config.Scopes.Select(PathUtils.NormalizeFolder).ToList();
        var (maxScore, minScore, scores) = CollectStats(modules, hotspots);

        var warningBoundary = maxScore * (criteria.MinScore / 100.0);
        var hotspotBoundary = warningBoundary + (maxScore - warningBoundary) / 2;

        var aggregated = new List<AggregatedHotspot>();
        foreach (var module in modules)
        {
            int countWarning = 0, countHotspot = 0, countOk = 0;
            foreach (var s in scores[module])
            {
                if (s >= hotspotBoundary) countHotspot++;
                else if (s >= warningBoundary) countWarning++;
                else countOk++;
            }

            var display = PathUtils.ToDisplayFolder(module);
            aggregated.Add(new AggregatedHotspot
            {
                Parent = PathUtils.Dirname(display),
                Module = display,
                Count = countOk,
                CountOk = countOk,
                CountWarning = countWarning,
                CountHotspot = countHotspot,
            });
        }

        aggregated.Sort((a, b) => b.Count.CompareTo(a.Count));

        return new AggregatedHotspotsResult
        {
            Aggregated = aggregated,
            MaxScore = maxScore,
            MinScore = minScore,
            HotspotBoundary = hotspotBoundary,
            WarningBoundary = warningBoundary,
        };
    }

    private static (double maxScore, double minScore, Dictionary<string, List<int>> scores) CollectStats(
        List<string> modules, List<FlatHotspot> hotspots)
    {
        var minScore = double.MaxValue;
        double maxScore = 0;
        var scores = new Dictionary<string, List<int>>();
        foreach (var module in modules)
        {
            var moduleScores = new List<int>();
            foreach (var h in hotspots)
            {
                if (h.FileName.StartsWith(module, StringComparison.Ordinal))
                {
                    minScore = Math.Min(minScore, h.Score);
                    maxScore = Math.Max(maxScore, h.Score);
                    moduleScores.Add(h.Score);
                }
            }
            scores[module] = moduleScores;
        }
        return (maxScore, minScore, scores);
    }

    private Dictionary<string, Hotspot> AnalyzeLogs(string log, Config config, HotspotCriteria criteria, Limits limits)
    {
        var hotspots = new Dictionary<string, Hotspot>();
        var module = string.IsNullOrEmpty(criteria.Module) ? "" : PathUtils.NormalizeFolder(criteria.Module);
        var parseOptions = new ParseOptions { Limits = limits, Filter = config.Filter };

        GitLogParser.Parse(log, entry =>
        {
            foreach (var change in entry.Body)
            {
                if (!change.Path.StartsWith(module, StringComparison.Ordinal)) continue;

                if (!hotspots.TryGetValue(change.Path, out var hotspot))
                {
                    hotspot = new Hotspot { Complexity = CalcComplexity(change, criteria) };
                    hotspots[change.Path] = hotspot;
                }

                hotspot.Commits++;
                hotspot.ChangedLines += change.LinesAdded + change.LinesRemoved;
            }
        }, parseOptions);

        return hotspots;
    }

    private int CalcComplexity(LogBodyEntry change, HotspotCriteria criteria)
    {
        var filePath = Path.Combine(_repoPath, change.Path.Replace('/', Path.DirectorySeparatorChar));
        // Not in the working tree (e.g. since-deleted file) -> complexity unknown.
        if (!File.Exists(filePath)) return -1;
        try
        {
            if (criteria.Metric == ComplexityMetric.Length)
                return CSharpComplexityAnalyzer.CountLines(File.ReadAllText(filePath));
            if (filePath.EndsWith(".cs"))
                return CSharpComplexityAnalyzer.CyclomaticComplexity(File.ReadAllText(filePath));
            return 1; // McCabe is only meaningful for C#; treat others as neutral.
        }
        catch
        {
            // Unreadable/locked file or parse failure must not abort the whole analysis.
            // The -1 sentinel yields score -1 in FindHotspotFiles, so it is excluded
            // from any positive-threshold hotspot list rather than scored misleadingly.
            return -1;
        }
    }
}
