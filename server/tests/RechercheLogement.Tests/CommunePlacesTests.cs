using System.Net;
using System.Text;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Bienici;

namespace RechercheLogement.Tests;

/// <summary>Communes aux noms courants et communes nouvelles : reconnues par Bien'ici, gardées par le filtre du projet.</summary>
public class CommunePlacesTests
{
    private sealed class Fake(Func<string, string?> answer) : DelegatingHandler, IHttpClientFactory
    {
        public List<string> Asked { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            Asked.Add(path);
            var body = answer(path);
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? "[]", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string City(string name, string cp, string zone) =>
        $$"""{ "type": "city", "name": "{{name}}", "postalCodes": ["{{cp}}"], "zoneIds": ["{{zone}}"] }""";

    // 10 Saint-Michel ailleurs en France : celui des Pyrénées-Atlantiques n'apparaît qu'avec son code postal.
    private static string? Suggest(string path) => path switch
    {
        "/suggest.json?q=Saint-Michel" => $"[{City("Saint-Michel", "02830", "-1")}, {City("Saint-Michel", "16470", "-2")}]",
        "/suggest.json?q=Saint-Michel 64220" => $"[{City("Saint-Michel", "64220", "-168596")}, {City("Saint-Michel", "02830", "-1")}]",
        "/suggest.json?q=Val-de-Sos" => $"[{City("Val-de-Reuil", "27100", "-5")}]",
        "/suggest.json?q=Val-de-Sos 09220" => $"[{City("Orus", "09220", "-7")}, {City("Goulier", "09220", "-8")}]",
        "/suggest.json?q=09220" => $$"""[ {{City("Orus", "09220", "-7")}}, { "type": "postalCode", "name": "09220", "postalCodes": ["09220"], "zoneIds": ["-7", "-8", "-9"] } ]""",
        _ => "[]",
    };

    [Fact]
    public async Task CommonName_IsFoundWithItsPostalCode()
    {
        using var http = new Fake(Suggest);
        var place = await new BieniciSearchAdapter(http).ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Saint-Michel", "64220"), TransactionType.Buy, default);
        Assert.Equal("-168596", Assert.Single(place!.Ids));
        Assert.Equal(2, http.Asked.Count);
    }

    [Fact]
    public async Task UnknownCommune_FallsBackToItsWholePostalCode()
    {
        using var http = new Fake(Suggest);
        var place = await new BieniciSearchAdapter(http).ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Val-de-Sos", "09220"), TransactionType.Buy, default);
        Assert.Equal(new[] { "-7", "-8", "-9" }, place!.Ids);
        Assert.Equal("Val-de-Sos (tout le code postal 09220)", place.Label);
    }

    [Fact]
    public async Task FreeText_IsNotWidenedToAPostalCode()
    {
        using var http = new Fake(Suggest);
        Assert.Null(await new BieniciSearchAdapter(http).ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Nulle-Part"), TransactionType.Buy, default));
        Assert.Single(http.Asked);
    }

    [Fact]
    public async Task NewCommune_GetsItsFormerNames_AndKeepsTheirListings()
    {
        using var http = new Fake(path => path.StartsWith("/communes_associees_deleguees?codeDepartement=09", StringComparison.Ordinal)
            ? """
              [ { "nom": "Goulier", "chefLieu": "09334" }, { "nom": "Sem", "chefLieu": "09334" },
                { "nom": "Vicdessos", "chefLieu": "09334" }, { "nom": "Ailleurs", "chefLieu": "09999" } ]
              """
            : null);
        var valDeSos = new ProjectLocation { Type = PlaceType.Commune, Query = "Val-de-Sos", Code = "09334", PostalCodes = ["09220"] };
        var auzat = new ProjectLocation { Type = PlaceType.Commune, Query = "Auzat", Code = "09030", PostalCodes = ["09220"] };
        var project = new SearchProject { Id = "p", Locations = [valDeSos] };
        var planner = new AreaPlanner(new GeoServices(http));

        Assert.True(await planner.AddFormerNamesAsync(new SearchProject { Id = "p", Locations = [valDeSos, auzat] }, default));
        Assert.Equal(new[] { "Goulier", "Sem", "Vicdessos" }, valDeSos.FormerNames);
        Assert.Empty(auzat.FormerNames!);
        Assert.False(await planner.AddFormerNamesAsync(project, default)); // déjà fait

        Assert.False(ProjectScope.OutsidePlaces(project, new ListingData { PostalCode = "09220", City = "Vicdessos" }));
        Assert.False(ProjectScope.OutsidePlaces(project, new ListingData { PostalCode = "09220", City = "Val-de-Sos" }));
        Assert.True(ProjectScope.OutsidePlaces(project, new ListingData { PostalCode = "09220", City = "Auzat" }));
    }

    [Fact]
    public async Task FormerNames_AreRetriedWhenTheServiceIsDown()
    {
        using var http = new Fake(_ => null);
        var commune = new ProjectLocation { Type = PlaceType.Commune, Query = "Val-de-Sos", Code = "09334", PostalCodes = ["09220"] };
        Assert.False(await new AreaPlanner(new GeoServices(http)).AddFormerNamesAsync(new SearchProject { Id = "p", Locations = [commune] }, default));
        Assert.Null(commune.FormerNames);
    }
}
