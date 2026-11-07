using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Figaro;

namespace RechercheLogement.Tests;

/// <summary>Figaro Immobilier par son serveur MCP : annonces (Fixtures/figaro-listings.json), lieux, arguments, découpage par prix.</summary>
public class FigaroTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Listings_AreRead()
    {
        var (items, total) = FigaroListingParser.ParseListings(JsonNode.Parse(Fixture("figaro-listings.json")), TransactionType.Buy);
        Assert.Equal(1222, total);
        Assert.Equal(new[] { "110646905", "110126943", "110319309" }, items.Select(i => i.SiteId));

        var d = items[0].Data;
        Assert.Equal("https://immobilier.lefigaro.fr/annonces/annonce-110646905.html", d.Url);
        Assert.Equal((PropertyTypes.House, (string?)null), (d.PropertyType, d.Subtype));
        Assert.Equal(88_000m, d.Price);
        Assert.Equal((85.0, 4, 3, 1503.0), (d.Surface!.Value, d.Rooms!.Value, d.Bedrooms!.Value, d.LandSurface!.Value));
        Assert.Equal("Trie-sur-Baïse", d.City);
        Assert.Null(d.PostalCode); // repris ensuite de la commune
        Assert.Equal(3, d.Photos!.Count);
        Assert.Equal(true, d.Has(Features.Fireplace));
        Assert.Equal(true, d.Has(Features.Garage));
        Assert.Null(d.Has(Features.Pool));
        Assert.StartsWith("a 10 minutes de trie sur baise", d.DescriptionExcerpt, StringComparison.Ordinal);

        Assert.Equal((PropertyTypes.House, "Ferme"), (items[1].Data.PropertyType, items[1].Data.Subtype));
        Assert.Equal(true, items[2].Data.Has(Features.Pool));
    }

    [Theory]
    [InlineData("maison neuve", PropertyTypes.House, null)]
    [InlineData("château", PropertyTypes.Castle, null)]
    [InlineData("propriété", PropertyTypes.House, "Propriété")]
    [InlineData("local commercial", PropertyTypes.Commercial, null)]
    [InlineData("péniche", PropertyTypes.Other, "Péniche")]
    public void PropertyType(string estate, string type, string? subtype) =>
        Assert.Equal((type, subtype), FigaroListingParser.PropertyType(estate));

    [Fact]
    public void NewHouse_IsNewBuild()
    {
        var item = FigaroListingParser.Parse(JsonNode.Parse("""{ "url": "https://immobilier.lefigaro.fr/annonces/annonce-1.html", "estateType": "maison neuve", "options": ["travaux"] }""")!, TransactionType.Buy)!;
        Assert.Equal((true, true), (item.Data.NewBuild, item.Data.WorkNeeded));
    }

    private const string Arreau = """
        { "locations": [
          { "cityLabel": "Arreau", "cityName": "Arreau (65)", "departmentCode": "65", "id": "39854", "inseeCode": "65031", "postalCode": "65240", "typeID": 6 },
          { "cityLabel": "Arreau", "cityName": "Arreau (09)", "departmentCode": "09", "id": "1", "inseeCode": "09999", "postalCode": "09000", "typeID": 6 } ] }
        """;

    [Theory]
    [InlineData("Arreau", "65240", "c:6:65031:65240")]
    [InlineData("arreau", "65240", "c:6:65031:65240")]
    [InlineData("Arreau", "65999", "c:6:65031:65999")] // autre code postal du même département
    [InlineData("Arreau", "31000", null)]                // homonyme ailleurs : non
    [InlineData("Sarrancolin", "65410", null)]
    public void Commune_ExactMatchOnly(string name, string cp, string? expected) =>
        Assert.Equal(expected, FigaroSearchAdapter.PickCommune(JsonNode.Parse(Arreau), name, cp)?.Ids.Single());

    [Fact]
    public async Task Department_WithoutCallingTheSite()
    {
        var adapter = new FigaroSite(new Mcp((_, _) => throw new InvalidOperationException("pas d'appel")), new GeoServices(new Mcp((_, _) => null)), 0).Search;
        var place = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Department, "Hautes-Pyrénées"), TransactionType.Buy, default);
        Assert.Equal("d:65", place!.Ids.Single());
        Assert.Null(await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65240"), TransactionType.Buy, default));
    }

    [Fact]
    public void Arguments_SendTheCriteria()
    {
        var p = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            PropertyTypes = [PropertyTypes.House, PropertyTypes.Castle, PropertyTypes.Terrain],
            SurfaceMin = 100,
            RoomsMin = 4,
            BedroomsMin = 3,
            EnergyClassMax = "C",
            RequiredFeatures = [Features.Pool, Features.Balcony],
        };
        var a = FigaroSearchAdapter.Arguments(p, FigaroSearchAdapter.Place("d:65")!, FigaroListingParser.SiteTypes(p.PropertyTypes), null, 300_000);
        Assert.Equal("""{"typeID":3,"transactionType":"vente","legacyID":"65","estateTypes":["maison","ch\u00E2teau","terrain"],"maximumPrice":300000,"minimumSurface":100,"minimumRooms":4}""", a.ToJsonString());

        p.StrictCriteria = [.. Criteria.All];
        a = FigaroSearchAdapter.Arguments(p, FigaroSearchAdapter.Place("c:6:65031:65240")!, [], 100_000, null);
        Assert.Equal("65031", (string?)a["insee"]);
        Assert.Null(a["legacyID"]);
        Assert.Equal(3, (int)a["minimumBedrooms"]!);
        Assert.Equal("""["A","B","C"]""", a["dpeCategories"]!.ToJsonString());
        Assert.Equal("""["piscine"]""", a["options"]!.ToJsonString()); // balcon : pas d'option sur le site
    }

    [Fact]
    public void PriceBands()
    {
        var (low, high) = FigaroSearchAdapter.Split(100_000, 300_000)!.Value;
        Assert.Equal(((decimal?)100_000, (decimal?)200_000, (decimal?)200_001, (decimal?)300_000), (low.Min, low.Max, high.Min, high.Max));
        (low, high) = FigaroSearchAdapter.Split(null, null)!.Value;
        Assert.Equal(((decimal?)null, (decimal?)3_000_000, (decimal?)3_000_001, (decimal?)null), (low.Min, low.Max, high.Min, high.Max));
        Assert.Null(FigaroSearchAdapter.Split(100_000, 101_000));
    }

    [Fact]
    public void ServerSentEvents_AreRead()
    {
        var json = FigaroMcpClient.ReadResponse(HttpStatusCode.OK, "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"structuredContent\":{\"x\":1}}}\n\n");
        Assert.Equal(1, (int)json!["result"]!["structuredContent"]!["x"]!);
        Assert.Throws<SiteException>(() => FigaroMcpClient.ReadResponse(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":2,"error":{"code":-32602,"message":"typeID requis"}}"""));
    }

    /// <summary>
    /// Zone de 30 annonces : le 1er appel en rend 20 ; les deux moitiés de la fourchette de prix en rendent 15 chacune.
    /// Toutes sont lues, avec le code postal de la commune cherchée.
    /// </summary>
    [Fact]
    public async Task Search_SplitsThePriceRange_WhenMoreThan20()
    {
        var calls = new List<JsonNode>();
        var mcp = new Mcp((method, args) =>
        {
            if (method != "tools/call") return null;
            calls.Add(args!.DeepClone());
            var min = (long?)args!["minimumPrice"] ?? 0;
            var max = (long?)args["maximumPrice"] ?? long.MaxValue;
            var all = Enumerable.Range(1, 30).Select(i => (Id: i, Price: 100_000L + i * 5_000)).Where(x => x.Price >= min && x.Price <= max).ToList();
            var listings = new JsonArray([.. all.Take(20).Select(x => (JsonNode)new JsonObject
            {
                ["url"] = $"https://immobilier.lefigaro.fr/annonces/annonce-{x.Id}.html",
                ["price"] = x.Price,
                ["city"] = "Arreau (65)",
                ["estateType"] = "maison",
            })]);
            return new JsonObject { ["listings"] = listings, ["totalHits"] = all.Count };
        });
        var adapter = new FigaroSite(mcp, new GeoServices(mcp), 0).Search;
        var p = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House], PriceMin = 100_000, PriceMax = 300_000 };

        var outcome = await adapter.SearchAsync(new SearchRequest(p, ["c:6:65031:65240"], 300), default);

        Assert.Equal(30, outcome.Items.Count);
        Assert.True(outcome.Complete);
        Assert.Equal(30, outcome.Total);
        Assert.All(outcome.Items, i => Assert.Equal("65240", i.Data.PostalCode));
        Assert.Equal(3, calls.Count);
        Assert.Equal((100_000L, 200_000L), ((long)calls[1]["minimumPrice"]!, (long)calls[1]["maximumPrice"]!));
    }

    /// <summary>Faux serveur MCP : session à l'initialisation, puis réponses en flux SSE ; et faux geo.api.gouv.fr.</summary>
    private sealed class Mcp(Func<string, JsonNode?, JsonNode?> answer) : DelegatingHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "immobilier.lefigaro.fr")
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var method = (string)message["method"]!;
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            var result = method == "initialize" ? new JsonObject() : answer(method, message["params"]?["arguments"]) is { } content
                ? new JsonObject { ["structuredContent"] = content }
                : new JsonObject { ["isError"] = true };
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result };
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: message\ndata: {body.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream"),
            };
            if (method == "initialize") res.Headers.Add("mcp-session-id", "S1");
            return res;
        }
    }
}
