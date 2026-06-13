using Detective.Core.Git;
using Detective.Core.Infrastructure;
using Detective.Core.Model;

namespace Detective.Core.Analysis;

/// <summary>
/// Temporal coupling: how often pairs of modules change together in the same
/// commit. Port of <c>change-coupling.ts</c>.
/// </summary>
public static class ChangeCouplingAnalyzer
{
    public static ChangeCouplingResult Calc(string log, Config config, Limits limits)
    {
        var displayModules = config.Scopes.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var modules = displayModules.Select(PathUtils.NormalizeFolder).ToList();

        var matrix = Matrix.Empty(modules.Count);
        var commitsPerModule = new int[modules.Count];
        var sumOfCoupling = new int[modules.Count];

        var parseOptions = new ParseOptions { Limits = limits, Filter = config.Filter };

        GitLogParser.Parse(log, entry =>
        {
            var touched = new SortedSet<int>();
            foreach (var change in entry.Body)
            {
                for (var i = 0; i < modules.Count; i++)
                {
                    if (change.Path.StartsWith(modules[i], StringComparison.Ordinal) && !touched.Contains(i))
                    {
                        commitsPerModule[i]++;
                        touched.Add(i);
                    }
                }
            }

            UpdateSumOfCoupling(touched, sumOfCoupling);
            AddToMatrix(touched, matrix);
        }, parseOptions);

        return new ChangeCouplingResult
        {
            Matrix = matrix,
            Dimensions = displayModules,
            Groups = config.Groups,
            SumOfCoupling = sumOfCoupling.ToList(),
            FileCount = commitsPerModule.ToList(),
            Cohesion = Enumerable.Repeat(-1, matrix.Length).ToList(),
        };
    }

    private static void UpdateSumOfCoupling(SortedSet<int> touched, int[] sumOfCoupling)
    {
        if (touched.Count <= 1) return;
        var others = touched.Count - 1;
        foreach (var module in touched) sumOfCoupling[module] += others;
    }

    private static void AddToMatrix(SortedSet<int> touched, int[][] matrix)
    {
        var arr = touched.ToArray();
        foreach (var a in arr)
            foreach (var b in arr)
                if (a < b) matrix[a][b]++;
    }
}
