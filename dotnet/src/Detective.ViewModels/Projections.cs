namespace Detective.ViewModels;

/// <summary>Flat, grid-friendly projections of the analysis results for the UI.</summary>
public sealed record ModuleStat(string Module, int FileCount, int Cohesion);

public sealed record CouplingPair(string ModuleA, string ModuleB, int Count);

public sealed record TeamModuleRow(string Module, string Team, int Changes);
