using Detective.Core.Analysis;
using Detective.Core.Infrastructure;
using Detective.Core.Model;
using Detective.Core.Roslyn;
using Xunit;

namespace Detective.Tests;

public class AnalyzerTests
{
    [Fact]
    public void Cyclomatic_complexity_counts_decision_points()
    {
        const string code = """
            class C
            {
                int M(int x)
                {
                    if (x > 0 && x < 10)
                    {
                        for (int i = 0; i < x; i++) { }
                    }
                    return x;
                }
            }
            """;
        // base 1 + if + && + for = 4
        Assert.Equal(4, CSharpComplexityAnalyzer.CyclomaticComplexity(code));
    }

    [Fact]
    public void File_metrics_report_methods_and_types()
    {
        const string code = """
            namespace N;
            class A { void M1() { } void M2() { } }
            class B { }
            """;
        var m = CSharpComplexityAnalyzer.FileMetrics(code, "N/A.cs");
        Assert.Equal(2, m.TypeCount);
        Assert.Equal(2, m.MethodCount);
    }

    [Fact]
    public void Glob_matcher_includes_positive_and_excludes_negative()
    {
        var g = new GlobMatcher(new[] { "**/*.cs", "!**/*.g.cs" });
        Assert.True(g.IsMatch("a/b/C.cs"));
        Assert.False(g.IsMatch("a/b/C.g.cs"));
        Assert.False(g.IsMatch("a/b/C.ts"));
    }

    [Fact]
    public void Coupling_builds_module_matrix_from_deps()
    {
        var deps = new Dictionary<string, List<string>>
        {
            ["src/a/F1.cs"] = new() { "src/b/G.cs" },
            ["src/a/F2.cs"] = new(),
            ["src/b/G.cs"] = new(),
        };
        var config = new Config { Scopes = new() { "src/a", "src/b" } };

        var r = CouplingAnalyzer.Calc(deps, config);

        Assert.Equal(2, r.FileCount[0]); // two files under src/a
        Assert.Equal(1, r.FileCount[1]); // one under src/b
        Assert.Equal(1, r.Matrix[0][1]); // a -> b: one import
        Assert.Equal(0, r.Matrix[1][0]); // b -> a: none
    }

    [Fact]
    public void Roslyn_dependency_analyzer_resolves_usings_to_files()
    {
        using var repo = new TempGitRepo();
        repo.Write("A/A.csproj", "<Project/>");
        repo.Write("A/Service.cs", "namespace A; using B; public class Service { }");
        repo.Write("B/B.csproj", "<Project/>");
        repo.Write("B/Helper.cs", "namespace B; public class Helper { }");

        var deps = RoslynDependencyAnalyzer.BuildDeps(repo.Path, new[] { "**/*.cs" });

        Assert.Contains("B/Helper.cs", deps["A/Service.cs"]);
        Assert.Empty(deps["B/Helper.cs"]);
    }

    [Fact]
    public void Change_coupling_links_modules_changed_together()
    {
        const string log =
            "\"Dev <d@x.io>,2024-01-01T00:00:00+00:00\tA1,init\"\n" +
            "1\t0\tsrc/a/File.cs\n" +
            "1\t0\tsrc/b/File.cs\n" +
            "\n";
        var config = new Config { Scopes = new() { "src/a", "src/b" } };

        var r = ChangeCouplingAnalyzer.Calc(log, config, Limits.None);

        Assert.Equal(1, r.Matrix[0][1]);
        Assert.Equal(1, r.SumOfCoupling[0]);
        Assert.Equal(1, r.SumOfCoupling[1]);
    }
}
