using System.Text.Json.Serialization;

namespace Detective.Core.Model;

/// <summary>
/// Persisted analysis configuration (<c>.detective/config.json</c>). Mirrors the
/// original TypeScript <c>Config</c> type. For .NET projects the <see cref="Scopes"/>
/// are typically the directories that contain a <c>.csproj</c> (i.e. the modules).
/// </summary>
public sealed class Config
{
    /// <summary>Module folders (relative, forward-slash). The analysis dimensions.</summary>
    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>Optional grouping label per scope (parallel to <see cref="Scopes"/>).</summary>
    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = new();

    [JsonPropertyName("filter")]
    public Filter Filter { get; set; } = new();

    /// <summary>Team name -&gt; member display names, used for team alignment.</summary>
    [JsonPropertyName("teams")]
    public Dictionary<string, List<string>> Teams { get; set; } = new();

    /// <summary>Maps alternate git author names onto a canonical display name.</summary>
    [JsonPropertyName("aliases")]
    public Dictionary<string, string> Aliases { get; set; } = new();

    /// <summary>Optional entry-point globs (reserved; auto-detection is used otherwise).</summary>
    [JsonPropertyName("entries")]
    public List<string> Entries { get; set; } = new();
}

public sealed class Filter
{
    /// <summary>Commit-message substrings; matching commits are skipped.</summary>
    [JsonPropertyName("logs")]
    public List<string> Logs { get; set; } = new();

    /// <summary>File globs to include/exclude (e.g. <c>**/*.cs</c>, <c>!**/*.g.cs</c>).</summary>
    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = new();
}
