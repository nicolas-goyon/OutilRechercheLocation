using System.Net;
using System.Text;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Etat;

namespace RechercheLogement.Tests;

/// <summary>Connecteur des cessions immobilières de l'État, avec des extraits de pages réelles (Fixtures/etat-*.html).</summary>
public class EtatTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void List_GivesCards()
    {
        var cards = EtatListingParser.ParseList(Fixture("etat-liste.html"));

        Assert.Equal(2, cards.Count);
        var c = cards[1];
        Assert.Equal("/biens/appartement-avec-deux-greniers-et-deux-caves", c.Path);
        Assert.Equal("Reims", c.City);
        Assert.Equal("51", c.Department);
        Assert.Equal("Logements", c.TypeLabel);
        Assert.Equal(197, c.Surface);
        Assert.Equal("260510002", c.Reference);
        Assert.Equal("Bâtiments techniques et industriels", cards[0].TypeLabel);
        Assert.Equal(11981, EtatListingParser.ParseList(Fixture("etat-liste.html").Replace("197 m²", "11\u00a0981 m²"))[1].Surface);
    }

    [Fact]
    public void Detail_GivesTheListing()
    {
        var card = EtatListingParser.ParseList(Fixture("etat-liste.html"))[1];
        var (d, insee) = EtatListingParser.ParseDetail(Fixture("etat-fiche.html"), card);

        Assert.Equal("Appartement Avec Deux Greniers Et Deux Caves", d.Title);
        Assert.Equal(PropertyTypes.Flat, d.PropertyType);
        Assert.Equal(TransactionType.Buy, d.Transaction);
        Assert.Null(d.Price);
        Assert.Equal(197, d.Surface);
        Assert.Equal(49.256578, d.Geo!.Lat);
        Assert.Equal(4.038987, d.Geo.Lon);
        Assert.Equal("51454", insee);
        Assert.Equal("260510002", d.AgencyRef);
        Assert.Equal("2026-11-06", d.PublishedAt);
        Assert.Equal(3, d.Photos!.Count);
        Assert.StartsWith("https://cessions.immobilier-etat.gouv.fr/sites/default/files/1_", d.Photos[0]);
        Assert.StartsWith("Procédure : Vente par enchères en ligne. Fini dans 31 jours. Appartement traversant", d.DescriptionExcerpt);
        Assert.Equal("https://cessions.immobilier-etat.gouv.fr/biens/appartement-avec-deux-greniers-et-deux-caves", d.Url);
    }

    [Theory]
    [InlineData("Logements", "Vente d'une maison en mauvais état", "house")]
    [InlineData("Logements", "Ensemble de logements", null)]
    [InlineData("Fonciers", "Parcelle", "terrain")]
    [InlineData("Bureaux / Commerces", "Local", "commercial")]
    [InlineData("Autres et atypiques", "Phare", "other")]
    [InlineData("Maisons forestières", "Maison forestière", "house")]
    [InlineData("Logements", "Box en sous-sol", "parking")]
    public void Types_FollowTheSiteThenTheTitle(string label, string title, string? expected) =>
        Assert.Equal(expected, EtatListingParser.TypeOf(label, title));

    [Theory]
    [InlineData(PlaceKind.PostalCode, "65170", null, "dep:65")]
    [InlineData(PlaceKind.Department, "Hautes-Pyrénées", null, "dep:65")]
    [InlineData(PlaceKind.Auto, "Vignec", "65170", "dep:65")]
    [InlineData(PlaceKind.PostalCode, "20000", null, "dep:2A")]
    public void Places_BecomeDepartments(PlaceKind kind, string text, string? cp, string expected) =>
        Assert.Contains(expected, EtatSearchAdapter.ResolvePlace(new PlaceQuery(kind, text, cp))!.Ids);

    private sealed class Fake(Func<string, string?> answer) : DelegatingHandler, IHttpClientFactory
    {
        public List<string> Calls { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.PathAndQuery);
            var body = answer(request.RequestUri.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? "", Encoding.UTF8, request.RequestUri.Host.StartsWith("geo") ? "application/json" : "text/html"),
            });
        }
    }

    [Fact]
    public async Task Search_ReadsOnlyTheDetailsOfTheProjectDepartments()
    {
        var fake = new Fake(p => p switch
        {
            "/recherche-biens?page=0" => Fixture("etat-liste.html"),
            _ when p.StartsWith("/recherche-biens") => "<html></html>",
            "/biens/appartement-avec-deux-greniers-et-deux-caves" => Fixture("etat-fiche.html"),
            _ when p.StartsWith("/communes/51454") => """{ "codesPostaux": ["51100"] }""",
            _ => null,
        });
        var adapter = new EtatSearchAdapter(fake, new GeoServices(fake), delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy };

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["dep:51"], 300), default);

        var item = Assert.Single(outcome.Items);
        Assert.Equal("51100", item.Data.PostalCode);
        Assert.True(outcome.Complete);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("crocq"));
    }

    [Fact]
    public async Task Search_IsEmptyForRentals()
    {
        var fake = new Fake(_ => null);
        var adapter = new EtatSearchAdapter(fake, new GeoServices(fake), delayMs: 0);

        var outcome = await adapter.SearchAsync(new SearchRequest(new SearchProject { Id = "p", Transaction = TransactionType.Rent }, ["dep:51"], 300), default);

        Assert.Empty(outcome.Items);
        Assert.Empty(fake.Calls);
    }
}
