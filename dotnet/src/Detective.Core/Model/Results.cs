namespace Detective.Core.Model;

/// <summary>Complexity metric used when scoring hotspots.</summary>
public enum ComplexityMetric
{
    /// <summary>McCabe cyclomatic complexity (computed with Roslyn for C#).</summary>
    McCabe,
    /// <summary>Raw line count.</summary>
    Length
}

// ---- Coupling (static, dependency based) -----------------------------------

public sealed class CouplingResult
{
    public List<string> Groups { get; set; } = new();
    public List<string> Dimensions { get; set; } = new();
    public List<int> FileCount { get; set; } = new();
    public List<int> Cohesion { get; set; } = new();
    public int[][] Matrix { get; set; } = Array.Empty<int[]>();
}

// ---- Change coupling (temporal, git based) ---------------------------------

public sealed class ChangeCouplingResult
{
    public int[][] Matrix { get; set; } = Array.Empty<int[]>();
    public List<string> Dimensions { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<int> SumOfCoupling { get; set; } = new();
    public List<int> FileCount { get; set; } = new();
    public List<int> Cohesion { get; set; } = new();
}

// ---- Hotspots --------------------------------------------------------------

public sealed class HotspotCriteria
{
    public string Module { get; set; } = string.Empty;
    public int MinScore { get; set; }
    public ComplexityMetric Metric { get; set; } = ComplexityMetric.McCabe;
}

public sealed class FlatHotspot
{
    public string FileName { get; set; } = string.Empty;
    public int Commits { get; set; }
    public int ChangedLines { get; set; }
    public int Complexity { get; set; }
    public int Score { get; set; }
}

public sealed class HotspotResult
{
    public List<FlatHotspot> Hotspots { get; set; } = new();
}

public sealed class AggregatedHotspot
{
    public string Parent { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int Count { get; set; }
    public int CountWarning { get; set; }
    public int CountHotspot { get; set; }
    public int CountOk { get; set; }
}

public sealed class AggregatedHotspotsResult
{
    public List<AggregatedHotspot> Aggregated { get; set; } = new();
    public double MinScore { get; set; }
    public double MaxScore { get; set; }
    public double WarningBoundary { get; set; }
    public double HotspotBoundary { get; set; }
}

// ---- Team alignment --------------------------------------------------------

public sealed class ModuleDetails
{
    public Dictionary<string, int> Changes { get; set; } = new();
}

public sealed class TeamAlignmentResult
{
    public Dictionary<string, ModuleDetails> Modules { get; set; } = new();
    public List<string> Teams { get; set; } = new();
}

// ---- Code metrics (Roslyn x-ray) -------------------------------------------

public sealed class CodeMetrics
{
    public string FilePath { get; set; } = string.Empty;
    public int Lines { get; set; }
    public int CyclomaticComplexity { get; set; }
    public int MethodCount { get; set; }
    public int TypeCount { get; set; }
    public int MaxNestingDepth { get; set; }
    public int MaxMethodLength { get; set; }
}
