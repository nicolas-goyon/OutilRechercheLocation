using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Notaires;

namespace RechercheLogement.Tests;

/// <summary>Connecteur Immobilier des Notaires de France, avec des réponses réelles de l'API (Fixtures/notaires-*.json).</summary>
public class NotairesTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static JsonNode Json(string name) => JsonNode.Parse(Fixture(name))!;

    private static NotairesCard Card(string id) =>
        NotairesListingParser.ParseList(Json("notaires-liste.json")).Cards.Single(c => c.Id == id);

    [Fact]
    public void List_GivesTheListings()
    {
        var (cards, total, pages) = NotairesListingParser.ParseList(Json("notaires-liste.json"));

        Assert.Equal(4, cards.Count);
        Assert.Equal(50, total);
        Assert.Equal(1, pages);
        var c = cards[1];
        Assert.Equal("2096588", c.Id);
        Assert.Equal("65002", c.Crpcen);
        Assert.False(c.AuctionClosed);
        var d = c.Data;
        Assert.Equal("https://www.immobilier.notaires.fr/fr/annonce-immo/vente/maison/barbazan-debat-65/2096588", d.Url);
        Assert.Equal("Maison 92 m² à Barbazan-Debat", d.Title);
        Assert.Equal(TransactionType.Buy, d.Transaction);
        Assert.Equal(PropertyTypes.House, d.PropertyType);
        Assert.Equal(229950, d.Price); // prix acquéreur, honoraires compris (prixTotal), pas le net vendeur 219 000
        Assert.Equal(92, d.Surface);
        Assert.Equal(4, d.Rooms);
        Assert.Equal(3, d.Bedrooms);
        Assert.Equal(332, d.LandSurface);
        Assert.Equal("65690", d.PostalCode);
        Assert.Equal("Barbazan-Debat", d.City);
        Assert.Null(d.Geo); // pas de position du bien
        Assert.Equal("65002-1867", d.AgencyRef);
        Assert.Equal(["https://media.immobilier.notaires.fr/inotr/media/29/65002/2096588/dc22e2e_VGA.jpg"], d.Photos);
        Assert.Equal("2026-10-19T11:19:38Z", d.PublishedAt);
        Assert.False(d.NewBuild);
        Assert.Null(d.SaleKind);
        Assert.StartsWith("une maison individuelle", d.DescriptionExcerpt);
    }

    [Fact]
    public void List_ReadsLifeAnnuitiesAndClosedAuctions()
    {
        var viager = Card("2017567").Data;
        Assert.Equal(SaleKinds.LifeAnnuity, viager.SaleKind);
        Assert.Null(viager.Rooms); // "nbPieces": 0 -> inconnu

        var vni = Card("1620160");
        Assert.True(vni.AuctionClosed);
        Assert.Equal(SaleKinds.Tender, vni.Data.SaleKind);
        Assert.StartsWith("https://www.immo-interactif.fr/", vni.Data.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_CompletesTheListing()
    {
        var d = NotairesListingParser.ParseDetail(Json("notaires-fiche.json"), Card("2096588").Data);

        Assert.Equal("BARBAZAN-DEBAT Maison T4, 92 m2 hab env, jardin.", d.Title);
        Assert.Equal(229950, d.Price); // celui de la liste
        Assert.Equal(15, d.Photos!.Count);
        Assert.Equal("https://media.immobilier.notaires.fr/inotr/media/29/65002/2096588/dc22e2e_XGA.jpg", d.Photos[0]);
        Assert.Equal("C", d.EnergyClass);
        Assert.Equal("A", d.GhgClass);
        Assert.Equal(1975, d.YearBuilt); // époque "E" : 1970 à 1980
        Assert.Equal(1145, d.PropertyTax);
        Assert.Null(d.Heating); // "INCONNU"
        Assert.Null(d.Subtype);
        Assert.Null(d.WorkNeeded);
        Assert.Equal("R65002/1867", Assert.Single(d.OtherRefs!));
        Assert.True(d.DescriptionExcerpt!.Length > Card("2096588").Data.DescriptionExcerpt!.Length);
        Assert.Equal(new Dictionary<string, bool> { [Features.Terrace] = true, [Features.Cellar] = false, [Features.Pool] = false }, d.Features);
        Assert.Null(d.Geo); // contact.coordonneesW84 est la position de l'office, pas du bien
    }

    [Fact]
    public void Detail_OfAnAuctionGivesStateExposureAndHeating()
    {
        var d = NotairesListingParser.ParseDetail(Json("notaires-fiche-vni.json"), Card("1620160").Data);

        Assert.Equal(SaleKinds.Tender, d.SaleKind);
        Assert.Equal(150000, d.Price);
        Assert.Equal("F", d.EnergyClass);
        Assert.Equal(1958, d.YearBuilt); // "D" : 1948 à 1969
        Assert.Equal("Maison de ville", d.Subtype);
        Assert.False(d.WorkNeeded); // état "BON"
        Assert.Equal("individuel", d.Heating);
        Assert.Equal("sud", d.Exposure);
        Assert.Equal(1, d.Bathrooms);
        Assert.Equal(5, d.Rooms);
        Assert.True(d.Has(Features.Parking));
        Assert.True(d.Has(Features.Cellar));
    }

    [Fact]
    public void Detail_WithoutDpeKeepsItUnknown()
    {
        var card = Card("1846377").Data;
        var d = NotairesListingParser.ParseDetail(Json("notaires-fiche-peu-renseignee.json"), card);

        Assert.Null(d.EnergyClass); // "Y" : DPE vierge
        Assert.Null(d.GhgClass);
        Assert.Null(d.YearBuilt);
        Assert.Null(d.Features);
        Assert.Equal(300, d.Surface);
        Assert.Equal(7, d.Photos!.Count); // "nbPhoto": 7 dans la liste, qui n'en donne qu'une
    }

    [Fact]
    public void Rental_GivesRentChargesAndFlatDetails()
    {
        var card = NotairesListingParser.ParseList(Json("notaires-liste-location.json")).Cards.Single();
        var d = NotairesListingParser.ParseDetail(Json("notaires-fiche-location.json"), card.Data);

        Assert.Equal(TransactionType.Rent, d.Transaction);
        Assert.Equal(PropertyTypes.Flat, d.PropertyType);
        Assert.Equal(795, d.Price); // loyer charges comprises
        Assert.Equal(25, d.Charges);
        Assert.False(d.Furnished);
        Assert.Equal(2, d.Floor);
        Assert.Equal(114.32, d.Surface);
        Assert.False(d.Has(Features.Elevator));
        Assert.True(d.Has(Features.Balcony));
        Assert.Equal("D", d.EnergyClass);
    }

    [Fact]
    public void Office_GivesNameAndSiren()
    {
        var office = NotairesListingParser.ParseOffice(Json("notaires-office.json"));
        Assert.Equal("SELARL EMILIE BAREILLE", office.Name);
        Assert.Equal("400178802", office.Siren);
    }

    [Theory]
    [InlineData("A", 1800)]
    [InlineData("J", 2022)]
    [InlineData("Z", null)]
    [InlineData(null, null)]
    public void ConstructionPeriod_GivesARepresentativeYear(string? letter, int? year) =>
        Assert.Equal(year, NotairesListingParser.YearOf(letter));

    [Theory]
    [InlineData("MAI", "CHALET", "Chalet")]
    [InlineData("MAI", "MAITRE", "Maison de maître")]
    [InlineData("TER", "BATIR", "Terrain à bâtir")]
    [InlineData("DIV", "GRANGE", "Grange")]
    [InlineData("MAI", null, null)]
    public void Subtypes_AreFrenchLabels(string type, string? subtype, string? label) =>
        Assert.Equal(label, NotairesListingParser.SubtypeLabel(type, subtype));

    [Theory]
    [InlineData("MAI", PropertyTypes.House)]
    [InlineData("APP", PropertyTypes.Flat)]
    [InlineData("TER", PropertyTypes.Terrain)]
    [InlineData("COM", PropertyTypes.Commercial)]
    [InlineData("AGR", PropertyTypes.Other)]
    [InlineData("XYZ", null)]
    public void Types_AreCommonTypes(string code, string? expected) => Assert.Equal(expected, NotairesListingParser.TypeOf(code));

    [Fact]
    public void SiteTypes_AreSiteCodes()
    {
        Assert.Equal(["MAI"], NotairesSearchAdapter.SiteTypes([PropertyTypes.House, PropertyTypes.TownHouse]));
        Assert.Equal(["APP", "LAC", "COM"], NotairesSearchAdapter.SiteTypes([PropertyTypes.Flat, PropertyTypes.Commercial]));
        Assert.Empty(NotairesSearchAdapter.SiteTypes(["inconnu"]));
    }

    [Fact]
    public void Query_SendsWhatTheSiteFilters()
    {
        var p = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            PropertyTypes = [PropertyTypes.House, PropertyTypes.Terrain],
            PriceMin = 100000,
            PriceMax = 250000,
            SurfaceMin = 80,
            RoomsMin = 4,
            BedroomsMin = 3,
            LandSurfaceMin = 1000,
            EnergyClassMax = "D",
            NewBuild = false,
            RequiredFeatures = [Features.Pool],
        };

        var q = NotairesSearchAdapter.BuildQuery(p, "departements=65,64", 2);

        Assert.Equal("page=2&parPage=100&departements=65,64&typeTransactions=VENTE,VNI,VAE&tri=DATE_MODIFICATION_DESC"
            + "&typeBiens=MAI,TER&prixMin=100000&prixMax=250000&surfaceMin=80", q);
    }

    [Fact]
    public void Query_SendsBedroomsOnlyIfStrict_AndNewBuildOnlyIfWanted()
    {
        var p = new SearchProject { Id = "p", Transaction = TransactionType.Rent, PropertyTypes = [], BedroomsMin = 2, NewBuild = true, StrictCriteria = [Criteria.Bedrooms] };

        var q = NotairesSearchAdapter.BuildQuery(p, "localites=28605", 1);

        Assert.Contains("typeTransactions=LOCATION", q);
        Assert.Contains("nbChambresMin=2", q);
        Assert.Contains("neuf=OUI", q);
        Assert.DoesNotContain("typeBiens", q);
        Assert.DoesNotContain("viager", q);
    }

    [Theory]
    [InlineData("Hautes-Pyrénées", "dep:65")]
    [InlineData("2a", "dep:2A")]
    public void Departments_AreDirect(string text, string id) => Assert.Equal([id], NotairesSearchAdapter.Department(text)!.Ids);

    [Fact]
    public void PostalCode_GivesAllItsLocalities()
    {
        var place = NotairesSearchAdapter.PickPostalCode(Json("notaires-ac-codepostal.json"), "65200")!;
        Assert.Equal(25, place.Ids.Count);
        Assert.Contains("loc:38999", place.Ids); // La Mongie, localité de Bagnères-de-Bigorre
        Assert.Null(NotairesSearchAdapter.PickPostalCode(Json("notaires-ac-codepostal.json"), "65000"));
    }

    [Fact]
    public void Commune_IsTheLocalityOfItsPostalCode()
    {
        var place = NotairesSearchAdapter.PickCommune(Json("notaires-ac-codepostal.json"), "Bagnères-de-Bigorre", "65200")!;
        Assert.Equal(["loc:28605"], place.Ids);
        Assert.Equal("Bagnères-de-Bigorre (65200)", place.Label);
        Assert.Equal(["loc:28743"], NotairesSearchAdapter.PickCommune(Json("notaires-ac-codepostal.json"), "Germs-sur-l'Oussouet", "65200")!.Ids);
    }

    [Fact]
    public void Commune_NeverTakesAHomonymOfAnotherDepartment()
    {
        // "Bagnères" : Bagnères-de-Bigorre (65200) et Bagnères-de-Luchon (31110) ; seule celle du code postal compte.
        var luchon = NotairesSearchAdapter.PickCommune(Json("notaires-ac-localites.json"), "Bagnères-de-Luchon", "31110")!;
        Assert.Equal(["loc:13901"], luchon.Ids);
        Assert.Null(NotairesSearchAdapter.PickCommune(Json("notaires-ac-localites.json"), "Bagnères-de-Bigorre", "31110"));
        Assert.Null(NotairesSearchAdapter.PickCommune(Json("notaires-ac-localites.json"), "Bagnères-de-Luchon", "65200"));
    }

    private sealed class Fake(Func<string, string?> answer) : DelegatingHandler, IHttpClientFactory
    {
        public List<string> Calls { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            Calls.Add(path);
            var body = answer(path);
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Annonces = "/pub-services/inotr-www-annonces/v1/annonces";

    [Fact]
    public async Task Search_ReadsTheListThenTheDetails()
    {
        var fake = new Fake(p => p switch
        {
            _ when p.StartsWith(Annonces + "?", StringComparison.Ordinal) => Fixture("notaires-liste.json"),
            Annonces + "/2096588" => Fixture("notaires-fiche.json"),
            Annonces + "/1846377" => Fixture("notaires-fiche-peu-renseignee.json"),
            _ when p.EndsWith("/office", StringComparison.Ordinal) => Fixture("notaires-office.json"),
            _ => null, // 2017567 : retirée entre-temps (HTTP 400)
        });
        using var adapter = new NotairesSearchAdapter(fake, delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };
        var steps = new List<string>();

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["dep:65"], 300, steps.Add), default);

        Assert.True(outcome.Complete);
        Assert.Equal(50, outcome.Total);
        Assert.Equal(["1846377", "2096588", "2017567"], outcome.Items.Select(i => i.SiteId)); // vente Immo-Interactif close écartée
        Assert.DoesNotContain(fake.Calls, c => c.Contains("1620160", StringComparison.Ordinal));
        Assert.Contains(fake.Calls, c => c.Contains("departements=65&", StringComparison.Ordinal) && c.Contains("typeBiens=MAI", StringComparison.Ordinal));
        var barbazan = outcome.Items[1].Data;
        Assert.Equal("C", barbazan.EnergyClass);
        Assert.Equal("SELARL EMILIE BAREILLE", barbazan.AgencyName);
        Assert.Equal("400178802", barbazan.AgencySiren);
        Assert.Equal(SaleKinds.LifeAnnuity, outcome.Items[2].Data.SaleKind); // fiche illisible : la liste reste
        Assert.Contains("page 1, 0 annonce(s) lue(s)", steps);
        Assert.Contains("fiche 3/3", steps);
        // Un office (3 ici) n'est lu qu'une fois : la recherche suivante ne le redemande pas.
        Assert.Equal(3, fake.Calls.Count(c => c.EndsWith("/office", StringComparison.Ordinal)));
        await adapter.SearchAsync(new SearchRequest(project, ["dep:65"], 300), default);
        Assert.Equal(3, fake.Calls.Count(c => c.EndsWith("/office", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Search_StopsAtMaxResults_AndPagesOtherwise()
    {
        var page1 = Json("notaires-liste.json");
        page1["nbPages"] = 2;
        var page2 = Json("notaires-liste-location.json");
        var fake = new Fake(p => p switch
        {
            _ when p.Contains("page=1&", StringComparison.Ordinal) => page1.ToJsonString(),
            _ when p.Contains("page=2&", StringComparison.Ordinal) => page2.ToJsonString(),
            _ => null,
        });
        using var adapter = new NotairesSearchAdapter(fake, delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy };

        var few = await adapter.SearchAsync(new SearchRequest(project, ["dep:65"], 2), default);
        Assert.False(few.Complete);
        Assert.Equal(2, few.Items.Count);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("page=2&", StringComparison.Ordinal));

        var all = await adapter.SearchAsync(new SearchRequest(project, ["dep:65", "loc:28605", "loc:38999"], 300), default);
        Assert.True(all.Complete);
        Assert.Contains(fake.Calls, c => c.Contains("page=2&", StringComparison.Ordinal));
        Assert.Contains(fake.Calls, c => c.Contains("localites=28605,38999&", StringComparison.Ordinal));
        Assert.Contains(all.Items, i => i.SiteId == "2050270");
    }

    [Fact]
    public async Task Places_PostalCodeAndCommuneUseTheAutocomplete()
    {
        var fake = new Fake(p => p.Contains("suggestion=65200&size=99", StringComparison.Ordinal) ? Fixture("notaires-ac-codepostal.json") : null);
        using var adapter = new NotairesSearchAdapter(fake, delayMs: 0);

        var cp = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65200"), TransactionType.Buy, default);
        var commune = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Gerde", "65200"), TransactionType.Buy, default);
        var unknown = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Commune-Nouvelle", "65200"), TransactionType.Buy, default);
        var department = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Department, "Hautes-Pyrénées"), TransactionType.Buy, default);

        Assert.Equal(25, cp!.Ids.Count);
        Assert.Equal(["loc:28741"], commune!.Ids);
        Assert.Equal("Commune-Nouvelle (tout le code postal 65200)", unknown!.Label);
        Assert.Equal(["dep:65"], department!.Ids);
        Assert.Equal(3, fake.Calls.Count); // département : sans requête
    }

    [Fact]
    public async Task Requests_AreSpacedByTheDelay()
    {
        var fake = new Fake(_ => Fixture("notaires-ac-codepostal.json"));
        using var adapter = new NotairesSearchAdapter(fake, delayMs: 300);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65200"), TransactionType.Buy, default);
        await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65200"), TransactionType.Buy, default);
        await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65200"), TransactionType.Buy, default);

        Assert.True(watch.ElapsedMilliseconds >= 550, $"{watch.ElapsedMilliseconds} ms pour 3 requêtes");
    }

    [Fact]
    public void Site_IsRegistrableWithTheDefaultDelay()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<ISiteModule, NotairesSite>();
        using var provider = services.BuildServiceProvider();

        var site = Assert.IsType<NotairesSite>(provider.GetRequiredService<ISiteModule>());
        Assert.Equal("notaires", site.Info.Id);
        Assert.Equal(PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department, site.Search.SupportedPlaces);
    }
}
