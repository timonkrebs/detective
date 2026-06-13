using Detective.Core;
using Detective.Core.Model;
using MemoizR;
using MemoizR.Reactive;

namespace Detective.ViewModels;

/// <summary>
/// The application's reactive store, built on <b>MemoizR</b> — the .NET analogue
/// of the Angular front end's <c>@ngrx/signals</c> stores.
///
/// <para>The reactive graph:</para>
/// <list type="bullet">
///   <item><b>Signals</b> (inputs): repo path, limits, hotspot min-score, metric,
///   team-by-user, and a refresh token.</item>
///   <item><b>MemoizR</b> (memoized/derived): one per analysis. They depend only on
///   the signals they read, so e.g. moving the min-score slider recomputes
///   <see cref="Hotspots"/> only — coupling and team alignment are untouched.</item>
///   <item><b>Reactions</b>: push each memo's value onto the UI
///   <see cref="SynchronizationContext"/> and into a bindable property.</item>
/// </list>
/// </summary>
public sealed class DetectiveViewModel : ObservableObject
{
    private readonly MemoFactory _f;

    // ---- Signals (inputs) --------------------------------------------------
    private readonly Signal<string> _repoPath;
    private readonly Signal<Limits> _limits;
    private readonly Signal<int> _minScore;
    private readonly Signal<ComplexityMetric> _metric;
    private readonly Signal<bool> _byUser;
    private readonly Signal<int> _refresh;

    // ---- MemoizR (derived analyses) ---------------------------------------
    private readonly MemoizR<DetectiveEngine?> _engine;
    private readonly MemoizR<CouplingResult> _coupling;
    private readonly MemoizR<ChangeCouplingResult> _changeCoupling;
    private readonly MemoizR<HotspotResult> _hotspots;
    private readonly MemoizR<TeamAlignmentResult> _team;
    private readonly MemoizR<List<CodeMetrics>> _code;

    public DetectiveViewModel(SynchronizationContext? uiContext = null)
    {
        // Unique context key => each store instance is an isolated reactive graph.
        _f = new MemoFactory("detective-" + Guid.NewGuid().ToString("N"));
        if (uiContext != null) _f.AddSynchronizationContext(uiContext);

        _repoPath = _f.CreateSignal(nameof(_repoPath), string.Empty);
        _limits = _f.CreateSignal(nameof(_limits), Limits.None);
        _minScore = _f.CreateSignal(nameof(_minScore), 0);
        _metric = _f.CreateSignal(nameof(_metric), ComplexityMetric.McCabe);
        _byUser = _f.CreateSignal(nameof(_byUser), false);
        _refresh = _f.CreateSignal(nameof(_refresh), 0);

        // A fresh engine whenever the path changes or a refresh is requested.
        _engine = _f.CreateMemoizR(nameof(_engine), async () =>
        {
            await _refresh.Get();
            var path = await _repoPath.Get();
            if (string.IsNullOrWhiteSpace(path)) return null;
            var engine = new DetectiveEngine(new AnalysisOptions { Path = path, DemoMode = true });
            return engine.IsRepo() ? engine : null;
        });

        _coupling = _f.CreateMemoizR(nameof(_coupling), async () =>
        {
            var e = await _engine.Get();
            return e is null ? new CouplingResult() : await Task.Run(e.Coupling);
        });

        _changeCoupling = _f.CreateMemoizR(nameof(_changeCoupling), async () =>
        {
            var e = await _engine.Get();
            var limits = await _limits.Get();
            return e is null ? new ChangeCouplingResult() : await Task.Run(() => e.ChangeCoupling(limits));
        });

        _hotspots = _f.CreateMemoizR(nameof(_hotspots), async () =>
        {
            var e = await _engine.Get();
            var limits = await _limits.Get();
            var minScore = await _minScore.Get();
            var metric = await _metric.Get();
            if (e is null) return new HotspotResult();
            var criteria = new HotspotCriteria { MinScore = minScore, Metric = metric };
            return await Task.Run(() => e.Hotspots(criteria, limits));
        });

        _team = _f.CreateMemoizR(nameof(_team), async () =>
        {
            var e = await _engine.Get();
            var limits = await _limits.Get();
            var byUser = await _byUser.Get();
            return e is null ? new TeamAlignmentResult() : await Task.Run(() => e.TeamAlignment(byUser, limits));
        });

        _code = _f.CreateMemoizR(nameof(_code), async () =>
        {
            var e = await _engine.Get();
            return e is null ? new List<CodeMetrics>() : await Task.Run(e.CodeMetrics);
        });

        // Reactions: keep the bindable properties in sync, marshaled to the UI thread.
        _f.BuildReaction(nameof(Coupling)).CreateReaction(_coupling, r => Coupling = r);
        _f.BuildReaction(nameof(ChangeCoupling)).CreateReaction(_changeCoupling, r => ChangeCoupling = r);
        _f.BuildReaction(nameof(Hotspots)).CreateReaction(_hotspots, r => Hotspots = r);
        _f.BuildReaction(nameof(TeamAlignment)).CreateReaction(_team, r => TeamAlignment = r);
        _f.BuildReaction(nameof(Code)).CreateReaction(_code, r => Code = r);

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !Busy);
    }

    // ---- Bindable inputs ---------------------------------------------------

    private string _repoPathInput = string.Empty;
    public string RepoPathInput
    {
        get => _repoPathInput;
        set => SetField(ref _repoPathInput, value);
    }

    private int? _limitCommits;
    public int? LimitCommits
    {
        get => _limitCommits;
        set => SetField(ref _limitCommits, value);
    }

    private int? _limitMonths;
    public int? LimitMonths
    {
        get => _limitMonths;
        set => SetField(ref _limitMonths, value);
    }

    private int _minScoreInput;
    /// <summary>Live input: changing it recomputes only the hotspot analysis.</summary>
    public int MinScoreInput
    {
        get => _minScoreInput;
        set { if (SetField(ref _minScoreInput, value)) FireAndForget(_minScore.Set(value)); }
    }

    private bool _useLengthMetric;
    public bool UseLengthMetric
    {
        get => _useLengthMetric;
        set
        {
            if (SetField(ref _useLengthMetric, value))
                FireAndForget(_metric.Set(value ? ComplexityMetric.Length : ComplexityMetric.McCabe));
        }
    }

    private bool _byUserInput;
    /// <summary>Live input: changing it recomputes only the team alignment.</summary>
    public bool ByUserInput
    {
        get => _byUserInput;
        set { if (SetField(ref _byUserInput, value)) FireAndForget(_byUser.Set(value)); }
    }

    // ---- Bindable outputs --------------------------------------------------

    private CouplingResult _couplingResult = new();
    public CouplingResult Coupling
    {
        get => _couplingResult;
        private set { if (SetField(ref _couplingResult, value)) OnPropertyChanged(nameof(CouplingRows)); }
    }

    private ChangeCouplingResult _changeCouplingResult = new();
    public ChangeCouplingResult ChangeCoupling
    {
        get => _changeCouplingResult;
        private set { if (SetField(ref _changeCouplingResult, value)) OnPropertyChanged(nameof(ChangeCouplingPairs)); }
    }

    private HotspotResult _hotspotResult = new();
    public HotspotResult Hotspots { get => _hotspotResult; private set => SetField(ref _hotspotResult, value); }

    private TeamAlignmentResult _teamResult = new();
    public TeamAlignmentResult TeamAlignment
    {
        get => _teamResult;
        private set { if (SetField(ref _teamResult, value)) OnPropertyChanged(nameof(TeamRows)); }
    }

    // ---- Grid-friendly projections (derived from the results above) --------

    public IReadOnlyList<ModuleStat> CouplingRows
    {
        get
        {
            var c = Coupling;
            var rows = new List<ModuleStat>();
            for (var i = 0; i < c.Dimensions.Count; i++)
                rows.Add(new ModuleStat(
                    c.Dimensions[i],
                    i < c.FileCount.Count ? c.FileCount[i] : 0,
                    i < c.Cohesion.Count ? c.Cohesion[i] : 0));
            return rows;
        }
    }

    public IReadOnlyList<CouplingPair> ChangeCouplingPairs
    {
        get
        {
            var r = ChangeCoupling;
            var pairs = new List<CouplingPair>();
            for (var i = 0; i < r.Dimensions.Count; i++)
                for (var j = i + 1; j < r.Dimensions.Count; j++)
                    if (r.Matrix[i][j] > 0)
                        pairs.Add(new CouplingPair(r.Dimensions[i], r.Dimensions[j], r.Matrix[i][j]));
            return pairs.OrderByDescending(p => p.Count).ToList();
        }
    }

    public IReadOnlyList<TeamModuleRow> TeamRows
    {
        get
        {
            var rows = new List<TeamModuleRow>();
            foreach (var (module, details) in TeamAlignment.Modules)
                foreach (var (team, changes) in details.Changes.OrderByDescending(c => c.Value))
                    rows.Add(new TeamModuleRow(module, team, changes));
            return rows;
        }
    }

    private List<CodeMetrics> _codeResult = new();
    public List<CodeMetrics> Code { get => _codeResult; private set => SetField(ref _codeResult, value); }

    private IReadOnlyList<string> _modules = Array.Empty<string>();
    public IReadOnlyList<string> Modules { get => _modules; private set => SetField(ref _modules, value); }

    private string _status = "Enter a repository path and click Analyze.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set { if (SetField(ref _busy, value)) AnalyzeCommand.RaiseCanExecuteChanged(); }
    }

    public AsyncRelayCommand AnalyzeCommand { get; }

    // ---- Driving the graph -------------------------------------------------

    /// <summary>Push the current inputs into the signals and await the results.</summary>
    public async Task AnalyzeAsync()
    {
        Busy = true;
        Status = $"Analyzing {RepoPathInput} ...";
        try
        {
            await _limits.Set(new Limits { LimitCommits = LimitCommits, LimitMonths = LimitMonths });
            await _repoPath.Set(RepoPathInput.Trim());
            await _refresh.Set(await _refresh.Get() + 1);

            var engine = await _engine.Get();
            if (engine is null)
            {
                Modules = Array.Empty<string>();
                Status = $"'{RepoPathInput}' is not a git repository.";
                return;
            }

            // Touch every memo so values are computed (and exceptions surfaced).
            var coupling = await _coupling.Get();
            var hotspots = await _hotspots.Get();
            await _changeCoupling.Get();
            await _team.Get();
            await _code.Get();

            Modules = engine.Config.Scopes;
            Status = $"Done — {coupling.Dimensions.Count} modules, {hotspots.Hotspots.Count} hotspots.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private static void FireAndForget(Task task) =>
        task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
}
