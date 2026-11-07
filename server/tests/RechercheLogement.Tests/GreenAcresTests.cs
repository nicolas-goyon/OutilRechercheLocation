using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.GreenAcres;

namespace RechercheLogement.Tests;

/// <summary>Connecteur Green-Acres, avec des extraits de pages et de réponses réelles (Fixtures/greenacres-*).</summary>
public class GreenAcresTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static JsonNode Json(string name) => JsonNode.Parse(Fixture(name))!;

    [Fact]
    public void List_GivesCards()
    {
        var page = GreenAcresListingParser.ParseList(Fixture("greenacres-liste.html"));

        Assert.Equal(230, page.Total);
        Assert.Equal("order-date_d-lg-fr-cn-fr-currency-EUR-hab_house-on-city_id-dp_65", page.Conditions);
        Assert.Equal(3, page.Cards.Count);
        var (id, d) = page.Cards[0];
        Assert.Equal("Apu1pbz5axanjk33", id);
        // Lien de la carte décodé depuis data-o (base64).
        Assert.Equal("https://www.green-acres.fr/fr/properties/immobilier/trie-sur-baise/Apu1pbz5axanjk33.htm", d.Url);
        Assert.Equal("Belle opportunité", d.Title);
        Assert.Equal(TransactionType.Buy, d.Transaction);
        Assert.Equal(PropertyTypes.House, d.PropertyType);
        Assert.Equal("Maison de campagne", d.Subtype);
        Assert.Equal(88000m, d.Price);
        Assert.Equal(85, d.Surface);
        Assert.Equal(4, d.Rooms);
        Assert.Equal(3, d.Bedrooms);
        Assert.Equal(1503, d.LandSurface);
        Assert.Equal("Trie-sur-Baïse", d.City);
        Assert.Null(d.PostalCode); // seulement dans la fiche
        Assert.Equal("ABAFIM", d.AgencyName);
        Assert.Equal(20, d.Photos!.Count);
        Assert.Equal("https://lb1.green-acres.com/4052339a/Apu1pbz5axanjk33/Photos/Apu1pbz5axanjk33_1.jpg?639296109320000000", d.Photos[0]);
        Assert.EndsWith("_20.jpg?639296109320000000", d.Photos[^1]);
        Assert.StartsWith("a 10 minutes de trie sur baise", d.DescriptionExcerpt);
        // Terrain en hectares.
        Assert.Equal(17000, page.Cards[1].Data.LandSurface);
        Assert.Equal("Villa", page.Cards[2].Data.Subtype);
        Assert.Equal(PropertyTypes.House, page.Cards[2].Data.PropertyType);
    }

    [Fact]
    public void List_IgnoresTheNearbyListingsAddedBySite()
    {
        var html = Fixture("greenacres-liste-proximite.html");

        var page = GreenAcresListingParser.ParseList(html);

        Assert.Equal(2, page.Total);
        Assert.Equal(["Arjphimsxm4l925h", "Aosjc35kbb9098he"], page.Cards.Select(c => c.Id));
        Assert.Equal("L'AGENCE IMMOBILI RE-FREECADRE IMMO", page.Cards[0].Data.AgencyName);
        // Jamais plus de cartes que le total annoncé, même sans le séparateur.
        var withoutSeparator = html.Replace("class=\"actual-criteria\"", "class=\"autre\"", StringComparison.Ordinal);
        Assert.Equal(2, GreenAcresListingParser.ParseList(withoutSeparator).Cards.Count);
    }

    [Fact]
    public void List_IgnoresPricesAndSurfacesInOtherUnits()
    {
        var html = Fixture("greenacres-liste.html")
            .Replace("88&#xA0;000 &#x20AC;", "$102,000", StringComparison.Ordinal)
            .Replace(">85 m&#xB2;<", ">915 sq ft<", StringComparison.Ordinal);

        var d = GreenAcresListingParser.ParseList(html).Cards[0].Data;

        Assert.Null(d.Price);
        Assert.Null(d.Surface);
    }

    [Fact]
    public void Detail_GivesPostalCodeGeoAndFeatures()
    {
        var card = GreenAcresListingParser.ParseList(Fixture("greenacres-liste.html")).Cards[0];

        var d = GreenAcresListingParser.ParseDetail(Fixture("greenacres-fiche.html"), card);

        Assert.Equal("65220", d.PostalCode);
        Assert.Equal("Trie-sur-Baïse", d.City);
        Assert.Equal(43.322932816327885, d.Geo!.Lat, 6);
        Assert.Equal(0.3697336005760624, d.Geo.Lon, 6);
        Assert.Equal(1000, d.Geo.PrecisionM); // position approximative (precise: false)
        Assert.Equal(88000m, d.Price);
        Assert.Equal(85, d.Surface);
        Assert.Equal(4, d.Rooms);
        Assert.Equal(3, d.Bedrooms);
        Assert.Equal(1503, d.LandSurface);
        Assert.Equal(1, d.Bathrooms);
        Assert.Equal("Chauffage au bois", d.Heating);
        Assert.Equal("est, sud", d.Exposure);
        Assert.Equal("af27414", d.AgencyRef);
        Assert.Equal("ABAFIM", d.AgencyName);
        Assert.Null(d.EnergyClass);
        Assert.Equal("2026-11-07", d.PublishedAt);
        Assert.Equal(26, d.Photos!.Count);
        Assert.Equal("https://lb1.green-acres.com/4052339a/Apu1pbz5axanjk33/Photos/Apu1pbz5axanjk33_26.jpg?639296109320000000", d.Photos[^1]);
        Assert.True(d.Has(Features.Garden));
        Assert.True(d.Has(Features.Fireplace));
        Assert.True(d.Has(Features.Cellar));
        Assert.True(d.Has(Features.Garage));
        Assert.True(d.Has(Features.Parking)); // "1 parking (box)"
        Assert.Null(d.Has(Features.Pool));
        Assert.Equal("Maison de campagne", d.Subtype);
    }

    [Fact]
    public void Detail_GivesEnergyClassesAndMandate()
    {
        var card = GreenAcresListingParser.ParseList(Fixture("greenacres-liste-proximite.html")).Cards[0];

        var d = GreenAcresListingParser.ParseDetail(Fixture("greenacres-fiche-dpe.html"), card);

        Assert.Equal("D", d.EnergyClass);
        Assert.Equal("C", d.GhgClass);
        Assert.Equal("65710", d.PostalCode);
        Assert.Equal("Campan", d.City);
        Assert.Equal("kossel00299-41714", d.AgencyRef);
        Assert.Contains("220256", d.OtherRefs!);
        Assert.Equal(228500m, d.Price);
        Assert.Equal(181, d.Surface);
        Assert.Equal(9, d.Rooms);
        Assert.Equal(5, d.Bedrooms);
        Assert.Equal(828, d.LandSurface);
        Assert.Equal("sud", d.Exposure);
        Assert.True(d.Has(Features.Parking));
        Assert.True(d.Has(Features.Garden));
        Assert.Equal(30, d.Photos!.Count);
        Assert.Equal("2026-08-30", d.PublishedAt);
    }

    [Theory]
    [InlineData("Maison de campagne", "Belle opportunité", "house")]
    [InlineData("Ferme", "Corps de ferme", "house")]
    [InlineData("Chalet", "Chalet", "house")]
    [InlineData("Maison de ville", "Maison", "townhouse")]
    [InlineData("Manoir", "Manoir du XVIIe", "castle")]
    [InlineData("Appartement", "T3", "flat")]
    [InlineData("Immeuble", "Immeuble de rapport", "building")]
    [InlineData("Programme neuf", "Appartements neufs à Tarbes", "flat")]
    [InlineData("Programme neuf", "Résidence Les Pins", null)]
    [InlineData("Inconnu", "Bien atypique", null)]
    public void Types_FollowTheSiteThenTheTitle(string label, string title, string? expected) =>
        Assert.Equal(expected, GreenAcresListingParser.TypeOf(label, title));

    [Fact]
    public void Url_SendsTheSiteFiltersAndOnlyStrictDetailedOnes()
    {
        var project = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            PropertyTypes = [PropertyTypes.House, PropertyTypes.Castle],
            PriceMin = 100000,
            PriceMax = 300000,
            SurfaceMin = 100,
            RoomsMin = 5,
            BedroomsMin = 3,
            LandSurfaceMin = 5000,
            EnergyClassMax = "D",
            RequiredFeatures = [Features.Pool, Features.AirConditioning],
        };

        var loose = GreenAcresSearchAdapter.BuildUrl(project, ["dp_65", "dp_64"], 2);

        Assert.Equal("https://www.green-acres.fr/maison-a-vendre?cn=fr&city_id=dp_65,dp_64&hab_house=true&hab_castle=true"
            + "&mn_p=100000&mx_p=300000&mn_h_s=100&mn_rooms=5&nb_min=0&order=date_d&p_n=2", loose);

        project.StrictCriteria = [.. Criteria.All];
        var strict = GreenAcresSearchAdapter.BuildUrl(project, ["city_33034"], 1);

        Assert.Contains("&mn_beds=3&", strict);
        Assert.Contains("&mn_l_s=5000&", strict);
        Assert.Contains("&dpe=A,B,C,D&", strict);
        Assert.Contains("&pool=true&", strict);
        Assert.DoesNotContain("air", strict);
        Assert.EndsWith("&nb_min=0&order=date_d&p_n=1", strict);
    }

    [Fact]
    public void Types_BecomeSiteParameters()
    {
        Assert.Equal(["hab_appartement", "hab_house", "hab_land"], GreenAcresSearchAdapter.SiteTypes([PropertyTypes.Flat, PropertyTypes.House, PropertyTypes.TownHouse, PropertyTypes.Terrain]));
        Assert.Equal(["hab_business", "hab_parking", "hab_other"], GreenAcresSearchAdapter.SiteTypes([PropertyTypes.Commercial, PropertyTypes.Parking, PropertyTypes.Other]));
        // Maison de ville seule : sous-type des maisons.
        var townOnly = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.TownHouse] };
        Assert.Contains("hab_house=true&hab_subtype=town_house", GreenAcresSearchAdapter.BuildUrl(townOnly, ["dp_65"], 1));
    }

    [Theory]
    [InlineData(PlaceKind.Department, "Hautes-Pyrénées", null, "dp_65")]
    [InlineData(PlaceKind.Department, "2A", null, "dp_2A")]
    [InlineData(PlaceKind.PostalCode, "65240", null, "pc_65240")]
    [InlineData(PlaceKind.Auto, "65240", null, "pc_65240")]
    [InlineData(PlaceKind.Auto, "Aveyron", null, "dp_12")]
    public void Places_WithoutCall(PlaceKind kind, string text, string? cp, string expected) =>
        Assert.Equal([expected], GreenAcresSearchAdapter.ResolveWithoutCall(new PlaceQuery(kind, text, cp))!.Ids);

    [Fact]
    public void Places_CommuneNeedsTheAutocomplete() =>
        Assert.Null(GreenAcresSearchAdapter.ResolveWithoutCall(new PlaceQuery(PlaceKind.Auto, "Campan", "65710")));

    [Fact]
    public void Communes_ArePickedWithTheirPostalCode()
    {
        var campan = GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-campan.json"), "Campan", "65710", false);
        Assert.Equal(["city_33034"], campan!.Ids);
        Assert.Equal("Campan (65710)", campan.Label);
        // Arreau : la commune, pas son quartier "Centre, Arreau".
        Assert.Equal(["city_32793"], GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-arreau.json"), "Arreau", "65240", false)!.Ids);
        // Quartier homonyme ("Campan, Ytrac (15130)") ou commune proche du nom (Campana) : refusés.
        Assert.Null(GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-campan.json"), "Campan", "15130", false));
        Assert.Null(GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-campan.json"), "Campana", "65710", false));
        // Recherche par code postal : communes sans code postal dans la réponse, vérifié par la recherche.
        Assert.Equal(["city_32791"], GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-65240.json"), "Adervielle-Pouchergues", "65240", true)!.Ids);
        Assert.Null(GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-65240.json"), "Adervielle-Pouchergues", "65240", false));
        // Un département n'est pas une commune.
        Assert.Null(GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-pyr.json"), "Pyrénées-Atlantiques", "64000", false));
    }

    [Fact]
    public void Communes_NeverAHomonymFromAnotherDepartment()
    {
        var json = JsonNode.Parse("""
            [{"id":"city_1","areaType":"city","label":"Saint-Martin (64200)","postalCode":"64200"},
             {"id":"city_2","areaType":"city","label":"Saint-Martin (65360)","postalCode":"65360"},
             {"id":"city_3","areaType":"city","label":"Saint-Martin (32300)","postalCode":"32300"}]
            """)!;

        Assert.Equal(["city_2"], GreenAcresSearchAdapter.PickCommune(json, "Saint-Martin", "65360", false)!.Ids);
        // Autre code postal du même département (commune à plusieurs codes postaux) : accepté.
        Assert.Equal(["city_1"], GreenAcresSearchAdapter.PickCommune(json, "St Martin", "64100", false)!.Ids);
        Assert.Null(GreenAcresSearchAdapter.PickCommune(json, "Saint-Martin", "12000", false));
        // Sans code postal : ambigu, refusé ; nom unique : accepté.
        Assert.Null(GreenAcresSearchAdapter.PickCommune(json, "Saint-Martin", null, false));
        Assert.Equal(["city_33034"], GreenAcresSearchAdapter.PickCommune(Json("greenacres-lieux-campan.json"), "Campan", null, false)!.Ids);
    }

    [Theory]
    [InlineData("lg-fr-cn-fr-currency-EUR-hab_house-on-city_id-city_33034-nb_min-0", true)]
    [InlineData("order-date_d-lg-fr-cn-fr-currency-EUR-hab_house-on-city_id-dp_65", true)]
    [InlineData("lg-fr-cn-fr-currency-EUR-hab_house-on-city_id-pc_65240-nb_min-20", false)]
    [InlineData("lg-fr-cn-fr-currency-USD-city_id-dp_65", false)]
    public void Conditions_RefuseAWidenedSearchOrOtherCurrency(string conditions, bool accepted)
    {
        var error = Record.Exception(() => GreenAcresSearchAdapter.CheckConditions(conditions));
        if (accepted) Assert.Null(error);
        else Assert.IsType<SiteException>(error);
    }

    [Fact]
    public void Session_KeepsTheAntiforgeryCookieButNeverTheSearchOne()
    {
        var session = GreenAcresSearchAdapter.ReadSession(
            """<meta name="csrf-token" content="CfDJ8abc_-x" />""",
            [".AspNetCore.Antiforgery.Xyz=CfDJ8tok; path=/; samesite=strict; httponly", "LastPropertySearch=lg-fr-cn-fr; path=/", "VisitorCountry=us; path=/"],
            DateTime.UnixEpoch)!;

        Assert.Equal("CfDJ8abc_-x", session.Token);
        Assert.Equal("VisitorCountry=fr; .AspNetCore.Antiforgery.Xyz=CfDJ8tok", session.Cookies);
        Assert.Null(GreenAcresSearchAdapter.ReadSession("<html></html>", [], DateTime.UnixEpoch));
    }

    /// <summary>Faux site : réponses par chemin, et requêtes reçues (avec leurs cookies).</summary>
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage?> answer) : DelegatingHandler
    {
        public List<(HttpMethod Method, string Path, string? Cookie, string? Token)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("Cookie", out var c) ? string.Join("; ", c) : null,
                request.Headers.TryGetValues("RequestVerificationToken", out var t) ? t.Single() : null));
            return Task.FromResult(answer(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
        }

        public static HttpResponseMessage Ok(string body, string type = "text/html") =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
    }

    [Fact]
    public async Task Search_ReadsTheResultsThenTheirDetails_WithOnlyTheCountryCookie()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath switch
        {
            "/maison-a-vendre" => Fake.Ok(Fixture("greenacres-liste-proximite.html")),
            "/fr/properties/immobilier/campan/Arjphimsxm4l925h.htm" => Fake.Ok(Fixture("greenacres-fiche-dpe.html")),
            _ => null, // seconde fiche retirée : la carte suffit
        });
        using var adapter = new GreenAcresSearchAdapter(GreenAcresSearchAdapter.CreateClient(fake), delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["city_33034"], 300), default);

        Assert.True(outcome.Complete);
        Assert.Equal(2, outcome.Total);
        Assert.Equal(["Arjphimsxm4l925h", "Aosjc35kbb9098he"], outcome.Items.Select(i => i.SiteId));
        Assert.Equal("65710", outcome.Items[0].Data.PostalCode);
        Assert.Equal("D", outcome.Items[0].Data.EnergyClass);
        Assert.Equal(120000m, outcome.Items[1].Data.Price);
        Assert.Equal(3, fake.Calls.Count); // une page, puis les deux fiches (pas les annonces « à proximité »)
        Assert.All(fake.Calls, c => Assert.Equal("VisitorCountry=fr", c.Cookie));
        Assert.Equal("/maison-a-vendre?cn=fr&city_id=city_33034&hab_house=true&nb_min=0&order=date_d&p_n=1", fake.Calls[0].Path);
    }

    [Fact]
    public async Task Search_StopsAtMaxResults()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath == "/maison-a-vendre" ? Fake.Ok(Fixture("greenacres-liste.html")) : null);
        using var adapter = new GreenAcresSearchAdapter(GreenAcresSearchAdapter.CreateClient(fake), delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["dp_65"], 2), default);

        Assert.False(outcome.Complete);
        Assert.Equal(230, outcome.Total);
        Assert.Equal(2, outcome.Items.Count);
        Assert.Equal(1, fake.Calls.Count(c => c.Path.StartsWith("/maison-a-vendre", StringComparison.Ordinal)));
        Assert.Equal(2, fake.Calls.Count(c => c.Path.StartsWith("/fr/properties/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Search_IsEmptyForRentals()
    {
        var fake = new Fake(_ => null);
        using var adapter = new GreenAcresSearchAdapter(GreenAcresSearchAdapter.CreateClient(fake), delayMs: 0);

        var outcome = await adapter.SearchAsync(new SearchRequest(new SearchProject { Id = "p", Transaction = TransactionType.Rent }, ["dp_65"], 300), default);

        Assert.Empty(outcome.Items);
        Assert.True(outcome.Complete);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Commune_UsesASeparateSessionForTheAutocomplete()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath switch
        {
            "/fr" => WithCookie(Fake.Ok("""<meta name="csrf-token" content="jeton" />""")),
            "/fr/LocationActions/SearchLocations" when r.Method == HttpMethod.Post => Fake.Ok(Fixture("greenacres-lieux-campan.json"), "application/json"),
            _ => null,
        });
        using var adapter = new GreenAcresSearchAdapter(GreenAcresSearchAdapter.CreateClient(fake), delayMs: 0);

        var campan = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Campan", "65710"), TransactionType.Buy, default);
        var again = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Campan", "65710"), TransactionType.Buy, default);

        Assert.Equal(["city_33034"], campan!.Ids);
        Assert.Equal(["city_33034"], again!.Ids);
        Assert.Equal(3, fake.Calls.Count); // une seule page d'accueil pour deux recherches de lieux
        Assert.Equal("VisitorCountry=fr", fake.Calls[0].Cookie);
        Assert.All(fake.Calls.Skip(1), c =>
        {
            Assert.Equal("jeton", c.Token);
            Assert.Equal("VisitorCountry=fr; .AspNetCore.Antiforgery.A=x", c.Cookie);
            Assert.StartsWith("/fr/LocationActions/SearchLocations?excludeCountries=False&term=Campan&countryId=fr", c.Path);
        });

        static HttpResponseMessage WithCookie(HttpResponseMessage res)
        {
            res.Headers.Add("Set-Cookie", ".AspNetCore.Antiforgery.A=x; path=/; httponly");
            return res;
        }
    }

    [Fact]
    public async Task Commune_UnknownBySite_FallsBackToItsPostalCode()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath switch
        {
            "/fr" => Fake.Ok("""<input name="__RequestVerificationToken" type="hidden" value="jeton" />"""),
            "/fr/LocationActions/SearchLocations" => Fake.Ok("[]", "application/json"),
            _ => null,
        });
        using var adapter = new GreenAcresSearchAdapter(GreenAcresSearchAdapter.CreateClient(fake), delayMs: 0);

        var place = await adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Val-de-Sos", "09220"), TransactionType.Buy, default);

        Assert.Equal(["pc_09220"], place!.Ids);
        Assert.Contains(fake.Calls, c => c.Path.Contains("term=09220", StringComparison.Ordinal));
    }
}
