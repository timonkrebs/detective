using System.Text.Json;
using Detective.Core.Model;

namespace Detective.Core.Infrastructure;

/// <summary>
/// Loads (and, on first run, scaffolds) <c>.detective/config.json</c>. For a .NET
/// repository the scopes default to the directories that contain a
/// <c>.csproj</c> — i.e. the projects/modules — mirroring how the original tool
/// inferred modules from an Nx/TypeScript workspace.
/// </summary>
public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Config LoadOrCreate(AnalysisOptions options)
    {
        var repoPath = options.ResolvedPath;
        var configPath = Path.Combine(repoPath, options.ConfigPath);

        Config config;
        if (File.Exists(configPath))
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath), JsonOptions)
                     ?? new Config();
        }
        else
        {
            config = CreateDefault(repoPath);
            Save(config, configPath);
        }

        config.Scopes.Sort(StringComparer.Ordinal);
        return config;
    }

    public static void Save(Config config, string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private static Config CreateDefault(string repoPath) => new()
    {
        Scopes = DetectScopes(repoPath),
        Filter = new Filter
        {
            Files = new List<string> { "**/*.cs", "!**/obj/**", "!**/bin/**" },
            Logs = new List<string>(),
        },
        Teams = new Dictionary<string, List<string>>
        {
            ["example-team-a"] = new() { "John Doe", "Jane Doe" },
            ["example-team-b"] = new() { "Max Muster", "Susi Sorglos" },
        },
    };

    /// <summary>Modules = folders containing a .csproj (fallback: top-level folders).</summary>
    public static List<string> DetectScopes(string repoPath)
    {
        var scopes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var csproj in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories))
        {
            var dir = PathUtils.ToPosix(Path.GetRelativePath(repoPath, Path.GetDirectoryName(csproj)!));
            if (dir is "." or "") continue;
            if (dir.Contains("/bin/") || dir.Contains("/obj/")) continue;
            scopes.Add(dir);
        }

        if (scopes.Count == 0)
        {
            foreach (var dir in Directory.EnumerateDirectories(repoPath))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules") continue;
                scopes.Add(name);
            }
        }

        return scopes.ToList();
    }
}
