# Detective .NET

A .NET rewrite of [Detective](../README.md) — forensic code analysis at the
architectural level — that **runs on .NET**, **analyzes .NET/C# codebases** (via
Roslyn), and is presented as a **WPF** desktop app whose reactive state is built
on [MemoizR](https://github.com/timonkrebs/MemoizR).

Where the original analyzes TypeScript/Angular workspaces and renders in Angular
with `@ngrx/signals`, this port analyzes C# projects and renders in WPF with
MemoizR signals/memos/reactions.

## What it does

The same four analyses as the original, plus a Roslyn code x-ray:

| Analysis | Question it answers | Source |
| --- | --- | --- |
| **Coupling** | Which modules depend on each other (statically)? | Roslyn `using`/namespace graph |
| **Change coupling** | Which modules keep changing *together*? | `git log --numstat` |
| **Hotspots** | Which files are complex *and* churn a lot? | git churn × Roslyn McCabe |
| **Team alignment** | Do team boundaries match module boundaries? | `git log` authors + teams |
| **Code x-ray** | Per-file complexity, nesting, method/type counts | Roslyn |

For a .NET repository, **modules ("scopes") are auto-detected as the directories
that contain a `.csproj`** — the natural unit of modularity in .NET — mirroring
how the original inferred modules from an Nx/TypeScript workspace.

## Solution layout

```
dotnet/
  src/
    Detective.Core/        net8.0          Analysis engine (git + Roslyn). No UI deps.
    Detective.ViewModels/  net8.0          Reactive store built on MemoizR.
    Detective.Cli/         net8.0          Console runner (great for CI / headless).
    Detective.Wpf/         net8.0-windows  WPF/XAML shell (Windows-only build).
  test/
    Detective.Tests/       net8.0          xUnit tests for Core + ViewModels.
```

`Core`, `ViewModels`, `Cli` and the tests are plain `net8.0` and build/run on any
platform. Only `Detective.Wpf` is Windows-only.

## How MemoizR powers the UI

`DetectiveViewModel` is the state container. It models the UI as a reactive graph
— the .NET counterpart of an `@ngrx/signals` store:

```
 Signals (inputs)              MemoizR (derived)            Reactions (effects)
 ───────────────              ─────────────────            ───────────────────
 repoPath  ─┐
 limits    ─┼──▶ engine ──┬──▶ coupling        ─────▶ CreateReaction ─▶ Coupling
 refresh   ─┘             ├──▶ changeCoupling  ─────▶ CreateReaction ─▶ ChangeCoupling
 minScore  ───────────────┼──▶ hotspots        ─────▶ CreateReaction ─▶ Hotspots
 metric    ───────────────┘
 byUser    ──────────────────▶ team            ─────▶ CreateReaction ─▶ TeamAlignment
```

```csharp
_repoPath = _f.CreateSignal(string.Empty);
_minScore = _f.CreateSignal(0);

_hotspots = _f.CreateMemoizR(async () =>
{
    var engine   = await _engine.Get();
    var minScore = await _minScore.Get();      // dependency tracked automatically
    var metric   = await _metric.Get();
    return await Task.Run(() => engine.Hotspots(new HotspotCriteria { MinScore = minScore, Metric = metric }));
});

_f.BuildReaction("hotspots").CreateReaction(_hotspots, r => Hotspots = r); // -> bindable property
```

Because MemoizR tracks dependencies per memo, moving the **min-score slider**
recomputes *only* the hotspots — coupling and team alignment are untouched.
On the WPF UI thread the factory is wired with
`AddSynchronizationContext(SynchronizationContext.Current)`, so reactions marshal
back onto the dispatcher before they touch bound properties.

## Running it

### CLI (any platform)

```bash
cd dotnet
dotnet run --project src/Detective.Cli -- --path /path/to/repo --analysis all
```

Useful options: `--analysis coupling|change-coupling|hotspots|team-alignment|code|all`,
`--limit-commits N`, `--limit-months N`, `--min-score N`, `--metric McCabe|Length`,
`--by-user`, `--demo`, `--json`, `--fill-cache`. (`--help` for the full list.)

Example — analyzing the MemoizR repository itself:

```
== Hotspots (churn × complexity) ==
   score  commits  cmplx  file
     756       27     28  MemoizR.StructuredAsyncLock/AsyncAsymmetricLock.cs
     646       34     19  MemoizR.Tests/ReactiveTests.cs
     528       24     22  MemoizR/Context.cs
     414       23     18  MemoizR.Reactive/ReactionBase.cs

== Change coupling (temporal) ==
    40×  MemoizR  <->  MemoizR.StructuredConcurrency
    32×  MemoizR  <->  MemoizR.Reactive
    29×  MemoizR.Reactive  <->  MemoizR.StructuredConcurrency
```

### WPF app (Windows)

```powershell
cd dotnet
dotnet run --project src\Detective.Wpf -- C:\path\to\repo
```

Enter a repository path, click **Analyze**, and explore the tabs (Hotspots,
Coupling, Change Coupling, Team Alignment, Code x-ray). The min-score slider and
the metric/by-user toggles update results live through MemoizR.

## Building & testing

```bash
cd dotnet
./build.sh        # builds the cross-platform projects and runs the tests
```

The WPF project builds on Windows (`dotnet build src/Detective.Wpf`), or on other
platforms with the **official** .NET SDK and `EnableWindowsTargeting=true`. Note
that the Ubuntu-packaged `dotnet-sdk` does not include the Windows Desktop SDK
targets, so the WPF project cannot be compiled there.

## Credits

- Concept & original TypeScript implementation: [Detective](../README.md).
- Inspired by Adam Tornhill's *Your Code as a Crime Scene*.
- Reactive state management: [MemoizR](https://github.com/timonkrebs/MemoizR).
