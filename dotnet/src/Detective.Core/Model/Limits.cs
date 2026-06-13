namespace Detective.Core.Model;

/// <summary>
/// Bounds the slice of git history an analysis looks at. Mirrors the original
/// TypeScript <c>Limits</c> type (commit count and/or a time window in months).
/// </summary>
public sealed record Limits
{
    public int? LimitCommits { get; init; }
    public int? LimitMonths { get; init; }

    public static readonly Limits None = new();
}
