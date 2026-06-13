using Detective.Core;
using Detective.Core.Infrastructure;
using Detective.Core.Model;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// Listen on :3334 by default — the port apps/frontend/proxy.conf.json already targets.
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:3334");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// The repo this API analyzes. Defaults to CWD (like the original Node backend);
// override per-request with ?path= or globally with DETECTIVE_REPO.
var defaultRepo = Environment.GetEnvironmentVariable("DETECTIVE_REPO") ?? Directory.GetCurrentDirectory();
var engines = new ConcurrentDictionary<string, DetectiveEngine>();
DetectiveEngine EngineFor(HttpRequest req)
{
    var path = req.Query["path"].ToString();
    var key = string.IsNullOrWhiteSpace(path) ? defaultRepo : Path.GetFullPath(path);
    return engines.GetOrAdd(key, p => new DetectiveEngine(new AnalysisOptions { Path = p }));
}

static Limits LimitsFrom(HttpRequest req)
{
    int? Pos(string name) => int.TryParse(req.Query[name], out var v) && v > 0 ? v : null;
    return new Limits { LimitCommits = Pos("limitCommits"), LimitMonths = Pos("limitMonths") };
}

static HotspotCriteria CriteriaFrom(HttpRequest req) => new()
{
    MinScore = int.TryParse(req.Query["minScore"], out var s) ? s : -1,
    Module = req.Query["module"].ToString(),
    Metric = Enum.TryParse<ComplexityMetric>(req.Query["metric"], ignoreCase: true, out var m)
        ? m : ComplexityMetric.McCabe,
};

// Wrap a synchronous analysis so a bad repo path / git error becomes a clean 500.
IResult Run<T>(HttpRequest req, Func<DetectiveEngine, T> analysis)
{
    try { return Results.Ok(analysis(EngineFor(req))); }
    catch (Exception e) { return Results.Json(new { error = e.Message }, statusCode: 500); }
}

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

// ---- Config -----------------------------------------------------------------
app.MapGet("/api/config", (HttpRequest req) => Run(req, e => e.Config));
app.MapPost("/api/config", (HttpRequest req, Config config) =>
{
    try
    {
        var engine = EngineFor(req);
        ConfigService.Save(config, Path.Combine(engine.RepoPath, ".detective", "config.json"));
        return Results.Ok(new { });
    }
    catch (Exception e) { return Results.Json(new { error = e.Message }, statusCode: 500); }
});

// ---- Status & cache ---------------------------------------------------------
app.MapGet("/api/status", (HttpRequest req) => Run(req, e => new { commits = e.CommitCount() }));
app.MapGet("/api/cache/log", (HttpRequest req) => Run(req, e => new { isStale = e.IsStale() }));
app.MapMethods("/api/cache/log/update", new[] { "GET", "POST" },
    (HttpRequest req) => Run(req, e => { e.FillCache(); return new { }; }));

// ---- Analyses (1:1 with the engine) -----------------------------------------
app.MapGet("/api/modules", (HttpRequest req) => Run(req, e => e.ModuleInfo()));
app.MapGet("/api/coupling", (HttpRequest req) => Run(req, e => e.Coupling()));
app.MapGet("/api/change-coupling", (HttpRequest req) => Run(req, e => e.ChangeCoupling(LimitsFrom(req))));
app.MapGet("/api/team-alignment", (HttpRequest req) =>
    Run(req, e => e.TeamAlignment(req.Query["byUser"] == "true", LimitsFrom(req))));
app.MapGet("/api/hotspots", (HttpRequest req) => Run(req, e => e.Hotspots(CriteriaFrom(req), LimitsFrom(req))));
app.MapGet("/api/hotspots/aggregated", (HttpRequest req) =>
    Run(req, e => e.AggregatedHotspots(CriteriaFrom(req), LimitsFrom(req))));
app.MapGet("/api/code", (HttpRequest req) => Run(req, e => e.CodeMetrics()));

// ---- SPA hosting (prod): serve the built Angular app from wwwroot -----------
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
