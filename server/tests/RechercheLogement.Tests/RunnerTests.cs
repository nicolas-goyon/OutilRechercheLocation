using Microsoft.Extensions.Logging.Abstractions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Sites;

namespace RechercheLogement.Tests;

/// <summary>Lancement d'un projet : sites en parallèle, avancement, relance d'un seul site.</summary>
public class RunnerTests
{
    private sealed class FakeSite(string id, Func<SearchRequest, Task<SearchOutcome>> search) : ISiteModule, ISearchAdapter
    {
        public SiteInfo Info { get; } = new(id, id.ToUpperInvariant(), SiteCategory.ImmoPortal, "test");
        public IListingParser? Parser => null;
        public ISearchAdapter Search => this;
        public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode;
        public int Runs;

        public Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct) =>
            Task.FromResult<CachedPlace?>(new CachedPlace(query.Text, [query.Text]));

        public Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return search(request);
        }
    }

    private static readonly SearchOutcome Empty = new([], true, 0);

    private static (ProjectRunner Runner, ProjectStore Store, SearchProject Project) Setup(params FakeSite[] sites)
    {
        var store = new ProjectStore(new InMemoryProjectPersistence());
        var project = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            Sites = sites.Select(s => s.Info.Id).ToList(),
            Locations = [new ProjectLocation { Type = PlaceType.PostalCode, Query = "65170", Code = "65170" }],
        };
        store.Save(project);
        var runner = new ProjectRunner(store, new Catalog(new InMemoryPersistence()), new SiteRegistry(sites),
            new AreaPlanner(new GeoServices(new NoHttp())), NullLogger<ProjectRunner>.Instance);
        return (runner, store, project);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("pas de réseau dans ce test");
    }

    [Fact]
    public async Task Sites_RunInParallel_AndShowTheirProgress()
    {
        var slowGate = new TaskCompletionSource<SearchOutcome>();
        var slow = new FakeSite("slow", r =>
        {
            r.Report("page 1");
            return slowGate.Task;
        });
        var fast = new FakeSite("fast", _ => Task.FromResult(Empty));
        var (runner, store, project) = Setup(slow, fast);

        var run = runner.RunAsync(project.Id, default);
        while (store.Runs(project.Id).All(r => r.Site != "fast")) await Task.Delay(10);

        Assert.Equal("page 1", runner.Progress(project.Id)["slow"]);
        Assert.Equal("SLOW", runner.RunningSite(project.Id));

        slowGate.SetResult(Empty);
        await run;
        Assert.Empty(runner.Progress(project.Id));
        Assert.Null(runner.RunningSite(project.Id));
    }

    [Fact]
    public async Task OneSite_CanBeRunAlone_AndNotTwiceAtOnce()
    {
        var gate = new TaskCompletionSource<SearchOutcome>();
        var a = new FakeSite("a", _ => gate.Task);
        var b = new FakeSite("b", _ => Task.FromResult(Empty));
        var (runner, _, project) = Setup(a, b);

        var first = runner.RunAsync(project.Id, default, onlySite: "a");
        while (!runner.IsRunning(project.Id, "a")) await Task.Delay(10);
        await runner.RunAsync(project.Id, default, onlySite: "a"); // déjà en cours : ignoré

        gate.SetResult(Empty);
        await first;
        Assert.Equal(1, a.Runs);
        Assert.Equal(0, b.Runs);
    }
}
