using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Figaro;

namespace RechercheLogement.LiveTests;

/// <summary>Figaro Immobilier : lieux et recherche sur le vrai serveur MCP du site (quelques appels).</summary>
public class FigaroLiveTests(ITestOutputHelper output)
{
    private static readonly FigaroSearchAdapter Adapter = new FigaroSite(LiveHttp.Factory, new GeoServices(LiveHttp.Factory)).Search;

    [Fact]
    public async Task Commune_IsRecognized()
    {
        var place = await LiveHttp.Retry(() => Adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Arreau", "65240"), TransactionType.Buy, default));
        Assert.Equal("c:6:65031:65240", place?.Ids.Single());
    }

    [Fact]
    public async Task Department_HousesHaveTheMainFields()
    {
        var p = new SearchProject { Id = "live", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House], PriceMin = 150_000, PriceMax = 200_000 };
        var outcome = await LiveHttp.Retry(() => Adapter.SearchAsync(new SearchRequest(p, ["d:65"], 30), default));
        output.WriteLine($"{outcome.Items.Count} annonce(s) lue(s) sur {outcome.Total}");
        Assert.NotEmpty(outcome.Items);
        foreach (var i in outcome.Items) output.WriteLine($"{i.SiteId} {i.Data.City} {i.Data.PostalCode} {i.Data.Price} {i.Data.Surface}");
        Assert.All(outcome.Items, i => Assert.InRange(i.Data.Price!.Value, 150_000m, 200_000m));
        Assert.True(outcome.Items.Count(i => i.Data.PostalCode?.StartsWith("65", StringComparison.Ordinal) == true) >= outcome.Items.Count * 0.8);
        Assert.True(outcome.Items.Count(i => i.Data.Surface is not null && i.Data.Photos is { Count: > 0 }) >= outcome.Items.Count * 0.8);
    }
}
