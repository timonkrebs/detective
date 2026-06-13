using System.Diagnostics;
using Detective.Core;
using Detective.Core.Model;
using Detective.ViewModels;
using Xunit;

namespace Detective.Tests;

public class EngineViewModelTests
{
    /// <summary>A two-module C# repo where Service.cs changes twice (a hotspot).</summary>
    private static TempGitRepo BuildSampleRepo()
    {
        var repo = new TempGitRepo();
        repo.Write("ProjA/ProjA.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("ProjB/ProjB.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("ProjB/Helper.cs", "namespace ProjB; public class Helper { public int Bump(int i) => i + 1; }");
        repo.Write("ProjA/Service.cs", "namespace ProjA; public class Service { public int Calc(int x) => x; }");
        repo.Commit("init ProjA");

        // Second commit touches BOTH modules -> temporal coupling, and grows Service.cs complexity.
        repo.Write("ProjA/Service.cs", """
            namespace ProjA;
            using ProjB;
            public class Service
            {
                public int Calc(int x)
                {
                    var h = new Helper();
                    if (x > 0 && x < 100) { for (int i = 0; i < x; i++) { if (i % 2 == 0) x += h.Bump(i); } }
                    else if (x < 0) { x = -x; }
                    return x > 50 ? x : 0;
                }
            }
            """);
        repo.Commit("feature touching ProjA and ProjB");
        return repo;
    }

    [Fact]
    public void Engine_analyzes_dotnet_repository()
    {
        using var repo = BuildSampleRepo();
        var engine = new DetectiveEngine(new AnalysisOptions { Path = repo.Path });

        Assert.True(engine.IsRepo());
        Assert.Equal(new[] { "ProjA", "ProjB" }, engine.Config.Scopes.ToArray());

        // Static coupling: ProjA depends on ProjB (using ProjB;).
        var coupling = engine.Coupling();
        Assert.Equal(1, coupling.Matrix[0][1]);

        // Temporal coupling: the two modules changed together at least once.
        var change = engine.ChangeCoupling();
        Assert.True(change.Matrix[0][1] >= 1);

        // Hotspots: Service.cs changed twice and has real complexity.
        var hotspots = engine.Hotspots(new HotspotCriteria { Metric = ComplexityMetric.McCabe });
        var service = hotspots.Hotspots.Single(h => h.FileName == "ProjA/Service.cs");
        Assert.Equal(2, service.Commits);
        Assert.True(service.Complexity > 1);

        Assert.NotEmpty(engine.CodeMetrics());
    }

    [Fact]
    public void Aggregated_hotspot_count_is_total_scored_files()
    {
        using var repo = BuildSampleRepo();
        var engine = new DetectiveEngine(new AnalysisOptions { Path = repo.Path });

        var result = engine.AggregatedHotspots(new HotspotCriteria { Metric = ComplexityMetric.McCabe });

        Assert.NotEmpty(result.Aggregated);
        foreach (var row in result.Aggregated)
            Assert.Equal(row.CountOk + row.CountWarning + row.CountHotspot, row.Count);
    }

    [Fact]
    public async Task ViewModel_reactively_populates_and_recomputes()
    {
        using var repo = BuildSampleRepo();
        var ctx = new ImmediateSynchronizationContext();
        var vm = new DetectiveViewModel(ctx) { RepoPathInput = repo.Path };

        await vm.AnalyzeAsync();

        Assert.Contains("Done", vm.Status);
        Assert.Equal(2, vm.Modules.Count);

        // MemoizR reactions push results into the bindable properties.
        Assert.True(await WaitUntil(() => vm.Coupling.Dimensions.Count == 2));
        Assert.True(await WaitUntil(() => vm.Hotspots.Hotspots.Count > 0));
        var withAll = vm.Hotspots.Hotspots.Count;

        // Live input: raising the min-score recomputes ONLY hotspots, reactively.
        vm.MinScoreInput = 100_000;
        Assert.True(await WaitUntil(() => vm.Hotspots.Hotspots.Count < withAll));
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}

/// <summary>Runs posted callbacks inline so MemoizR reactions are deterministic in tests.</summary>
internal sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);
    public override void Send(SendOrPostCallback d, object? state) => d(state);
}
