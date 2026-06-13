using Detective.Core.Infrastructure;
using Detective.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Detective.Core.Roslyn;

/// <summary>
/// Builds a file-level dependency map for a C# repository by resolving
/// <c>using</c> directives against the namespaces declared in the repo. This is
/// the .NET equivalent of the Sheriff-produced <c>deps.json</c> the original
/// (TypeScript) tool consumed: <c>file -&gt; list of imported repo files</c>.
/// </summary>
public static class RoslynDependencyAnalyzer
{
    public static Dictionary<string, List<string>> BuildDeps(string repoPath, IEnumerable<string> fileGlobs)
    {
        var filter = new GlobMatcher(fileGlobs);
        var files = EnumerateCsFiles(repoPath, filter);

        // Parse once; remember declared namespaces and usings per file.
        var declaredNs = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // file -> namespaces
        var usings = new Dictionary<string, List<string>>(StringComparer.Ordinal);       // file -> using names
        var nsToFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); // namespace -> files

        foreach (var (rel, full) in files)
        {
            string text;
            try { text = File.ReadAllText(full); }
            catch { continue; }

            var root = CSharpSyntaxTree.ParseText(text).GetRoot();

            var nss = root.DescendantNodes()
                .Where(n => n is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax)
                .Select(n => ((BaseNamespaceDeclarationSyntax)n).Name.ToString())
                .Distinct().ToList();
            declaredNs[rel] = nss;
            foreach (var ns in nss)
            {
                if (!nsToFiles.TryGetValue(ns, out var set)) nsToFiles[ns] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(rel);
            }

            usings[rel] = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Name != null)
                .Select(u => u.Name!.ToString())
                .Distinct().ToList();
        }

        var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (rel, _) in files)
        {
            var imports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var used in usings.GetValueOrDefault(rel, new List<string>()))
            {
                foreach (var (ns, nsFiles) in nsToFiles)
                {
                    if (ns == used || ns.StartsWith(used + ".", StringComparison.Ordinal))
                        foreach (var f in nsFiles)
                            if (f != rel) imports.Add(f);
                }
            }
            deps[rel] = imports.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        return deps;
    }

    private static List<(string Rel, string Full)> EnumerateCsFiles(string repoPath, GlobMatcher filter)
    {
        var results = new List<(string, string)>();
        foreach (var full in Directory.EnumerateFiles(repoPath, "*.cs", SearchOption.AllDirectories))
        {
            var rel = PathUtils.ToPosix(Path.GetRelativePath(repoPath, full));
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("bin/")
                || rel.StartsWith("obj/") || rel.StartsWith(".git/")) continue;
            if (!filter.IsMatch(rel)) continue;
            results.Add((rel, full));
        }
        return results;
    }
}
