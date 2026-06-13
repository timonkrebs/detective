using Detective.Core.Infrastructure;
using Detective.Core.Model;

namespace Detective.Core.Analysis;

/// <summary>
/// Static (dependency based) coupling between modules, plus per-module cohesion.
/// Port of <c>coupling.ts</c> + <c>module-info.ts</c>, fed by the Roslyn
/// dependency map instead of Sheriff.
/// </summary>
public static class CouplingAnalyzer
{
    public static CouplingResult Calc(Dictionary<string, List<string>> deps, Config config)
    {
        var files = deps.Keys.ToList();
        var scopes = config.Scopes;
        var modules = scopes.Select(PathUtils.NormalizeFolder).ToList();

        var size = modules.Count;
        var matrix = Matrix.Empty(size);

        for (var i = 0; i < size; i++)
            for (var j = 0; j < size; j++)
                matrix[i][j] = CalcCell(files, deps, modules[i], modules[j]);

        var fileCount = CalcFileCount(deps, scopes);
        var cohesion = CalcCohesion(fileCount, matrix);

        return new CouplingResult
        {
            Groups = config.Groups,
            Dimensions = scopes,
            FileCount = fileCount,
            Cohesion = cohesion,
            Matrix = matrix,
        };
    }

    private static int CalcCell(List<string> files, Dictionary<string, List<string>> deps, string row, string col)
    {
        var count = 0;
        foreach (var file in files)
            if (file.StartsWith(row, StringComparison.Ordinal))
                count += deps[file].Count(imp => imp.StartsWith(col, StringComparison.Ordinal));
        return count;
    }

    private static List<int> CalcFileCount(Dictionary<string, List<string>> deps, List<string> scopes)
    {
        var fileCount = new int[scopes.Count];
        foreach (var dep in deps.Keys)
            for (var i = 0; i < scopes.Count; i++)
                if (dep.StartsWith(scopes[i], StringComparison.Ordinal))
                    fileCount[i]++;
        return fileCount.ToList();
    }

    private static List<int> CalcCohesion(List<int> fileCount, int[][] matrix)
    {
        var cohesion = new List<int>(fileCount.Count);
        for (var index = 0; index < fileCount.Count; index++)
        {
            var count = fileCount[index];
            var edges = matrix[index][index];
            var maxEdges = count * (count - 1) / 2.0;
            var factor = maxEdges > 0 ? edges / maxEdges : 1;
            cohesion.Add(PathUtils.ToPercent(factor));
        }
        return cohesion;
    }
}
