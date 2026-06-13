using System.Text.Json;
using Detective.Core;
using Detective.Core.Model;

var parsed = CliArgs.Parse(args);
if (parsed.ShowHelp)
{
    CliArgs.PrintUsage();
    return 0;
}

var options = new AnalysisOptions
{
    Path = parsed.Path,
    DemoMode = parsed.Demo,
};

var engine = new DetectiveEngine(options);

if (!engine.IsRepo())
{
    Console.Error.WriteLine($"error: '{engine.RepoPath}' is not a git repository.");
    return 1;
}

Console.Error.WriteLine($"Detective .NET — analyzing {engine.RepoPath}");
Console.Error.WriteLine($"Modules (scopes): {engine.Config.Scopes.Count}");

if (parsed.FillCache)
{
    Console.Error.WriteLine("Filling git log cache ...");
    engine.FillCache();
}

var limits = new Limits { LimitCommits = parsed.LimitCommits, LimitMonths = parsed.LimitMonths };
var writer = new ResultWriter(parsed.Json);

switch (parsed.Analysis)
{
    case "coupling":
        writer.Coupling(engine.Coupling());
        break;
    case "change-coupling":
        writer.ChangeCoupling(engine.ChangeCoupling(limits));
        break;
    case "team-alignment":
        writer.TeamAlignment(engine.TeamAlignment(parsed.ByUser, limits));
        break;
    case "hotspots":
        var criteria = new HotspotCriteria
        {
            Module = parsed.Module,
            MinScore = parsed.MinScore,
            Metric = parsed.Metric,
        };
        writer.Hotspots(engine.Hotspots(criteria, limits));
        break;
    case "code":
        writer.Code(engine.CodeMetrics());
        break;
    case "all":
        writer.Coupling(engine.Coupling());
        writer.ChangeCoupling(engine.ChangeCoupling(limits));
        writer.TeamAlignment(engine.TeamAlignment(parsed.ByUser, limits));
        writer.Hotspots(engine.Hotspots(new HotspotCriteria { MinScore = parsed.MinScore, Metric = parsed.Metric }, limits));
        writer.Code(engine.CodeMetrics());
        break;
    default:
        Console.Error.WriteLine($"error: unknown analysis '{parsed.Analysis}'.");
        CliArgs.PrintUsage();
        return 1;
}

return 0;

/// <summary>Parsed command-line options.</summary>
internal sealed class CliOptions
{
    public string Path = string.Empty;
    public string Analysis = "all";
    public int? LimitCommits;
    public int? LimitMonths;
    public int MinScore;
    public ComplexityMetric Metric = ComplexityMetric.McCabe;
    public string Module = string.Empty;
    public bool ByUser;
    public bool Json;
    public bool Demo;
    public bool FillCache;
    public bool ShowHelp;
}

internal static class CliArgs
{
    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path" or "-p": o.Path = Next(args, ref i); break;
                case "--analysis" or "-a": o.Analysis = Next(args, ref i); break;
                case "--limit-commits": o.LimitCommits = int.Parse(Next(args, ref i)); break;
                case "--limit-months": o.LimitMonths = int.Parse(Next(args, ref i)); break;
                case "--min-score": o.MinScore = int.Parse(Next(args, ref i)); break;
                case "--metric": o.Metric = Enum.Parse<ComplexityMetric>(Next(args, ref i), ignoreCase: true); break;
                case "--module" or "-m": o.Module = Next(args, ref i); break;
                case "--by-user": o.ByUser = true; break;
                case "--json": o.Json = true; break;
                case "--demo": o.Demo = true; break;
                case "--fill-cache": o.FillCache = true; break;
                case "--help" or "-h": o.ShowHelp = true; break;
                default:
                    if (string.IsNullOrEmpty(o.Path) && !args[i].StartsWith('-')) o.Path = args[i];
                    break;
            }
        }
        return o;
    }

    private static string Next(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for {args[i - 1]}");

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            Detective .NET — forensic code analysis for .NET repositories.

            Usage: detective [--path <dir>] [--analysis <name>] [options]

              -p, --path <dir>        Repository to analyze (default: current dir)
              -a, --analysis <name>   coupling | change-coupling | hotspots |
                                      team-alignment | code | all  (default: all)
                  --limit-commits <n> Only consider the most recent <n> commits
                  --limit-months <n>  Only consider commits from the last <n> months
                  --min-score <n>     Hotspot score threshold (default: 0)
                  --metric <m>        McCabe | Length  (hotspot complexity)
              -m, --module <scope>    Restrict hotspots to a module/scope
                  --by-user           Team alignment broken down by user
                  --demo              Anonymize authors with demo users
                  --fill-cache        Force-refresh the git log cache
                  --json              Emit JSON instead of tables
              -h, --help              Show this help
            """);
    }
}

/// <summary>Renders analysis results as either JSON or compact tables.</summary>
internal sealed class ResultWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly bool _json;

    public ResultWriter(bool json) => _json = json;

    private void Dump(string title, object value)
    {
        if (_json) { Console.WriteLine(JsonSerializer.Serialize(value, Json)); return; }
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    public void Coupling(CouplingResult r)
    {
        Dump("Coupling (static dependencies)", r);
        if (_json) return;
        for (var i = 0; i < r.Dimensions.Count; i++)
            Console.WriteLine($"  {r.Dimensions[i],-40} files={r.FileCount[i],-4} cohesion={r.Cohesion[i]}%");
    }

    public void ChangeCoupling(ChangeCouplingResult r)
    {
        Dump("Change coupling (temporal)", r);
        if (_json) return;
        var pairs = new List<(string a, string b, int n)>();
        for (var i = 0; i < r.Dimensions.Count; i++)
            for (var j = i + 1; j < r.Dimensions.Count; j++)
                if (r.Matrix[i][j] > 0) pairs.Add((r.Dimensions[i], r.Dimensions[j], r.Matrix[i][j]));
        foreach (var (a, b, n) in pairs.OrderByDescending(p => p.n).Take(20))
            Console.WriteLine($"  {n,4}×  {a}  <->  {b}");
        if (pairs.Count == 0) Console.WriteLine("  (no modules changed together)");
    }

    public void TeamAlignment(TeamAlignmentResult r)
    {
        Dump("Team alignment", r);
        if (_json) return;
        Console.WriteLine($"  teams: {string.Join(", ", r.Teams)}");
        foreach (var (module, details) in r.Modules)
        {
            if (details.Changes.Count == 0) continue;
            var top = details.Changes.OrderByDescending(c => c.Value).Take(3)
                .Select(c => $"{c.Key}={c.Value}");
            Console.WriteLine($"  {module,-40} {string.Join("  ", top)}");
        }
    }

    public void Hotspots(HotspotResult r)
    {
        Dump("Hotspots (churn × complexity)", r);
        if (_json) return;
        Console.WriteLine($"  {"score",6}  {"commits",7}  {"cmplx",5}  file");
        foreach (var h in r.Hotspots.Take(25))
            Console.WriteLine($"  {h.Score,6}  {h.Commits,7}  {h.Complexity,5}  {h.FileName}");
        if (r.Hotspots.Count == 0) Console.WriteLine("  (none)");
    }

    public void Code(List<CodeMetrics> metrics)
    {
        Dump("Code metrics (Roslyn x-ray)", metrics);
        if (_json) return;
        Console.WriteLine($"  {"cmplx",5}  {"lines",5}  {"methods",7}  {"nest",4}  file");
        foreach (var m in metrics.Take(25))
            Console.WriteLine($"  {m.CyclomaticComplexity,5}  {m.Lines,5}  {m.MethodCount,7}  {m.MaxNestingDepth,4}  {m.FilePath}");
        if (metrics.Count == 0) Console.WriteLine("  (no C# files)");
    }
}
