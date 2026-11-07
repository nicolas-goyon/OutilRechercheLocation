using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Paruvendu;

namespace RechercheLogement.Tests;

/// <summary>Connecteur ParuVendu, avec des extraits de pages réelles (Fixtures/paruvendu-*.html, *.json).</summary>
public class ParuvenduTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static JsonNode Json(string name) => JsonNode.Parse(Fixture(name))!;

    // ------------------------------------------------------------------ lecture de la liste

    [Fact]
    public void List_GivesListingsWithoutSponsoredInserts()
    {
        var page = ParuvenduListingParser.ParseList(Fixture("paruvendu-liste.html"), TransactionType.Buy);

        Assert.Equal(878, page.Total);
        // 4 blocs dans l'extrait, dont un encart constructeur "Maison neuve" (data-cdp="MFC…") ignoré.
        Assert.Equal(["1293825623", "1297132917", "1297126629"], page.Cards.Select(c => c.Id));

        var odos = page.Cards[2];
        var d = odos.Data;
        Assert.Equal("65", odos.Department);
        Assert.Equal("https://www.paruvendu.fr/immobilier/vente/maison/1297126629A1KIVHMN000", d.Url);
        Assert.Equal("Maison 220 m² Odos (65)", d.Title);
        Assert.Equal(TransactionType.Buy, d.Transaction);
        Assert.Equal(PropertyTypes.House, d.PropertyType);
        Assert.Null(d.Subtype);
        Assert.Equal(545000m, d.Price);
        Assert.Equal(220, d.Surface);
        Assert.Equal(6, d.Rooms);
        Assert.Equal(5, d.Bedrooms);
        Assert.Equal(1995, d.LandSurface);
        Assert.Equal("B", d.EnergyClass);
        Assert.Equal("Odos", d.City);
        Assert.Null(d.PostalCode); // absent de la liste : déduit par l'adaptateur
        Assert.Equal("iad France", d.AgencyName);
        Assert.NotNull(d.DescriptionExcerpt);
        Assert.Null(d.SaleKind);
        Assert.InRange(d.Photos!.Count, 1, 6);
        Assert.All(d.Photos, u => Assert.StartsWith("https://img.paruvendu.fr/media_ext/", u, StringComparison.Ordinal));
        Assert.All(d.Photos, u => Assert.Contains("w=1000", u, StringComparison.Ordinal));
    }

    [Fact]
    public void List_ReadsACardWithoutSurface()
    {
        var d = ParuvenduListingParser.ParseList(Fixture("paruvendu-liste.html"), TransactionType.Buy).Cards[0].Data;

        Assert.Equal("Maison Saint-Laurent-de-Neste (65)", d.Title);
        Assert.Equal("Saint-Laurent-de-Neste", d.City);
        Assert.Null(d.Surface);
        Assert.Equal(242000m, d.Price);
        Assert.Equal(5, d.Rooms);
        Assert.Equal(4, d.Bedrooms);
        Assert.Equal(1248, d.LandSurface); // "Terrain 1 248 m²", espace insécable
        Assert.Equal("D", d.EnergyClass);
        Assert.Equal("3G IMMO - RESEAU NATIONAL", d.AgencyName);
        // Première photo posée par un script, les suivantes en <img> : 6 photos.
        Assert.Equal(6, d.Photos!.Count);
        Assert.Contains("L2dldF9waG90by8xMjAxNjU1", d.Photos[0], StringComparison.Ordinal);
    }

    [Fact]
    public void List_ReadsPrivateSellersSubtypesAndLifeAnnuities()
    {
        var cards = ParuvenduListingParser.ParseList(Fixture("paruvendu-liste-particuliers.html"), TransactionType.Buy).Cards;
        var byId = cards.ToDictionary(c => c.Id, c => c.Data);

        Assert.Equal(22, ParuvenduListingParser.ParseList(Fixture("paruvendu-liste-particuliers.html"), TransactionType.Buy).Total);
        var chalet = byId["1296645087"];
        Assert.Equal(PropertyTypes.House, chalet.PropertyType);
        Assert.Equal("Chalet", chalet.Subtype);
        Assert.Equal(45, chalet.Surface);
        Assert.Equal(3, byId["1296398890"].Rooms); // "3/4 pièces" : le minimum
        var particulier = byId["1233088029"];
        Assert.Equal("https://www.paruvendu.fr/immobilier/vente/tres-jolie-maison-lumineuse-en-pierres-de-taille-a-vendre/particulier/1233088029A1KIVHMN000", particulier.Url);
        Assert.Equal(80000m, particulier.Price); // "80 000 € *"
        Assert.Null(particulier.AgencyName);
        Assert.Equal("Lannemezan", particulier.City);
        var viager = Assert.Single(cards, c => c.Data.SaleKind == SaleKinds.LifeAnnuity).Data;
        Assert.Equal(14, viager.Rooms);
    }

    [Fact]
    public void List_OfACommuneDropsTheNearbyMention()
    {
        var page = ParuvenduListingParser.ParseList(Fixture("paruvendu-liste-commune.html"), TransactionType.Buy);

        Assert.Equal(8, page.Total);
        var d = page.Cards[0].Data;
        Assert.Equal("Argelès-Gazost", d.City);
        Assert.Equal("Maison 118 m² Argelès-Gazost (65)", d.Title);
        Assert.Equal(118, d.Surface);
        Assert.Equal(15544, d.LandSurface);
        Assert.Equal("ARROW IMMOBILIER", d.AgencyName);
    }

    [Theory]
    [InlineData("Maison", "house", null)]
    [InlineData("Villa", "house", "Villa")]
    [InlineData("Ferme", "house", "Ferme")]
    [InlineData("Propriété/château", "castle", "Propriété/château")]
    [InlineData("Hôtel particulier", "castle", "Hôtel particulier")]
    [InlineData("Appartement", "flat", null)]
    [InlineData("Duplex/Triplex", "flat", "Duplex/Triplex")]
    [InlineData("Remise/Grange", "other", "Remise/Grange")]
    [InlineData("Parking/Garage", "parking", null)]
    [InlineData("Terrain", "terrain", null)]
    [InlineData("Bateau", null, null)]
    public void Types_FromTheCardLabel(string label, string? type, string? subtype) =>
        Assert.Equal((type, subtype), ParuvenduListingParser.TypeOf(label));

    [Fact]
    public void Detail_GivesThePostalCodeOfTheListingCommune()
    {
        // Titre de la fiche (agence) ; chez un particulier, l'encadré d'alerte. Les annonces similaires citent
        // d'autres communes ("Tarbes (65000)") : écartées.
        Assert.Equal("65310", ParuvenduListingParser.PostalCodeOfDetail(Fixture("paruvendu-fiche.html"), "Odos", "65"));
        Assert.Equal("65140", ParuvenduListingParser.PostalCodeOfDetail(Fixture("paruvendu-fiche-particulier.html"), "Sénac", "65"));
        Assert.Null(ParuvenduListingParser.PostalCodeOfDetail(Fixture("paruvendu-fiche-particulier.html"), "Sénac", "31"));
        Assert.Null(ParuvenduListingParser.PostalCodeOfDetail(Fixture("paruvendu-fiche.html"), "Lourdes", "65"));
    }

    // ------------------------------------------------------------------ requête

    private static Dictionary<string, List<string>> Query(string url)
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&').Select(kv => kv.Split('=', 2));
        return query.GroupBy(kv => Uri.UnescapeDataString(kv[0])).ToDictionary(g => g.Key, g => g.Select(kv => kv.Length > 1 ? kv[1] : "").ToList());
    }

    [Fact]
    public void Url_SendsSimpleCriteriaSortAndPage()
    {
        var p = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            PropertyTypes = [PropertyTypes.House],
            PriceMin = 100000,
            PriceMax = 250000,
            SurfaceMin = 80,
            RoomsMin = 4,
            RoomsMax = 5,
            BedroomsMin = 3,
            LandSurfaceMin = 1000,
            RequiredFeatures = [Features.Pool],
        };

        var q = Query(ParuvenduSearchAdapter.SearchUrl(p, "lo=65", 2));

        Assert.StartsWith("https://www.paruvendu.fr/immobilier/annonceimmofo/liste/listeAnnonces?", ParuvenduSearchAdapter.SearchUrl(p, "lo=65", 2), StringComparison.Ordinal);
        Assert.Equal(["1"], q["tt"]);
        Assert.Equal(["tbMai", "tbVil", "tbCha", "tbMou", "tbFer"], q.Keys.Where(k => k.StartsWith("tb", StringComparison.Ordinal)));
        Assert.Equal(["65"], q["lo"]);
        Assert.Equal(["100000"], q["px0"]);
        Assert.Equal(["250000"], q["px1"]);
        Assert.Equal(["80"], q["sur0"]);
        Assert.Equal(["40"], q["nbp0"]); // pièces en dizaines
        Assert.Equal(["50"], q["nbp1"]);
        Assert.Equal(["dateMiseAJour"], q["ddlTri"]);
        Assert.Equal(["desc"], q["ddlOrd"]);
        Assert.Equal(["2"], q["p"]);
        // Viagers écartés par défaut : ancien et neuf.
        Assert.Equal(["ancien", "neuf"], q["natureProjet[]"]);
        // Critères détaillés non obligatoires : pas envoyés (le site écarterait les annonces sans l'information).
        Assert.False(q.ContainsKey("enrNbChb"));
        Assert.False(q.ContainsKey("surT0"));
        Assert.False(q.ContainsKey("enrPis"));
    }

    [Fact]
    public void Url_SendsStrictCriteria()
    {
        var p = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            PropertyTypes = [PropertyTypes.House],
            RoomsMin = 7,
            RoomsMax = 9,
            BedroomsMin = 8,
            LandSurfaceMin = 1000,
            LandSurfaceMax = 5000,
            BathroomsMin = 2,
            WorkNeeded = true,
            NewBuild = false,
            RequiredFeatures = [Features.Pool, Features.Garage, Features.Fireplace],
            StrictCriteria = [.. Criteria.All],
        };

        var q = Query(ParuvenduSearchAdapter.SearchUrl(p, "codeINSEE=65025:0,", 1));

        Assert.Equal(["65025:0,"], q["codeINSEE"]);
        Assert.Equal(["55"], q["nbp0"]); // plus de 5 pièces
        Assert.False(q.ContainsKey("nbp1"));
        Assert.Equal(["5"], q["enrNbChb"]);
        Assert.Equal(["2"], q["enrNbSdB"]);
        Assert.Equal(["1000"], q["surT0"]);
        Assert.Equal(["5000"], q["surT1"]);
        Assert.Equal(["1"], q["enrPis"]);
        Assert.Equal(["1"], q["enrBox"]);
        Assert.Equal(["1"], q["travaux"]);
        Assert.Equal(["ancien"], q["natureProjet[]"]);
    }

    [Fact]
    public void Url_ForRentals()
    {
        var p = new SearchProject { Id = "p", Transaction = TransactionType.Rent, PropertyTypes = [PropertyTypes.Flat], Furnished = true };

        var q = Query(ParuvenduSearchAdapter.SearchUrl(p, "lo=65400", 1));

        Assert.Equal(["5"], q["tt"]);
        Assert.Equal(["tbApp", "tbDup"], q.Keys.Where(k => k.StartsWith("tb", StringComparison.Ordinal)));
        Assert.Equal(["1"], q["mb"]);
        Assert.False(q.ContainsKey("natureProjet[]"));
    }

    [Fact]
    public void Types_BecomeSiteCheckboxes()
    {
        Assert.Equal(["tbPro", "tbHot", "tbApp", "tbDup"], ParuvenduSearchAdapter.SiteTypes(["castle", "APARTMENT"]));
        Assert.Empty(ParuvenduSearchAdapter.SiteTypes([PropertyTypes.Commercial]));
    }

    [Fact]
    public void Places_AreGroupedByKind()
    {
        var groups = ParuvenduSearchAdapter.Groups(["dep:65", "cp:65400", "dep:64", "com:65025:65400", "com:65440:65000", "cp:65200"]);

        Assert.Equal(["lo=65,64", "lo=65400,65200", "codeINSEE=65025:0,65440:0,"], groups.Select(g => g.Parameter));
        Assert.Empty(groups[0].PostalCodes);
        Assert.Equal(["65400", "65200"], groups[1].PostalCodes);
        Assert.Equal(["65400", "65000"], groups[2].PostalCodes);
    }

    // ------------------------------------------------------------------ lieux

    [Fact]
    public void Places_FromTheSiteSuggestions()
    {
        var cp = ParuvenduSearchAdapter.PickPlace(Json("paruvendu-autocomplete-cp-65400.json"), new PlaceQuery(PlaceKind.PostalCode, "65400"));
        Assert.Equal(["cp:65400"], cp!.Ids);

        var department = ParuvenduSearchAdapter.PickPlace(Json("paruvendu-autocomplete-departement.json"), new PlaceQuery(PlaceKind.Department, "Hautes-Pyrénées"));
        Assert.Equal(["dep:65"], department!.Ids);
        Assert.Equal("Hautes-Pyrénées (département)", department.Label);

        var commune = ParuvenduSearchAdapter.PickPlace(Json("paruvendu-autocomplete-tarbes.json"), new PlaceQuery(PlaceKind.Auto, "Tarbes", "65000"));
        Assert.Equal(["com:65440:65000"], commune!.Ids);
        Assert.Equal("Tarbes (65000)", commune.Label);

        // Texte libre : la commune de ce nom, sinon le département.
        Assert.Equal(["com:65440:65000"], ParuvenduSearchAdapter.PickPlace(Json("paruvendu-autocomplete-tarbes.json"), new PlaceQuery(PlaceKind.Auto, "tarbes"))!.Ids);
        Assert.Equal(["dep:65"], ParuvenduSearchAdapter.PickPlace(Json("paruvendu-autocomplete-departement.json"), new PlaceQuery(PlaceKind.Auto, "Hautes-Pyrénées"))!.Ids);
    }

    /// <summary>L'autocomplétion du site ne trouve rien avec des accents ("Hautes-Pyrénées" -> []).</summary>
    [Theory]
    [InlineData("Hautes-Pyrénées", "Hautes-Pyrenees")]
    [InlineData(" Argelès-Gazost ", "Argeles-Gazost")]
    [InlineData("Saint-Cœur", "Saint-Coeur")]
    [InlineData("65400", "65400")]
    public void Places_AreAskedWithoutAccents(string text, string term) =>
        Assert.Equal(term, ParuvenduSearchAdapter.SuggestTerm(text));

    [Fact]
    public void Places_NeverAHomonymFromAnotherDepartment()
    {
        var json = Json("paruvendu-autocomplete-ville-argeles.json");

        // "Argelès" n'est aucune de ces communes (Argelès-Gazost, Argelès-sur-Mer…) : pas de nom approchant.
        Assert.Null(ParuvenduSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Auto, "Argelès", "65400")));
        Assert.Equal(["com:65025:65400"], ParuvenduSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Auto, "Argelès-Gazost", "65400"))!.Ids);
        // Même nom, autre code postal du même département : gardée ; autre département : refusée.
        Assert.Equal(["com:65025:65400"], ParuvenduSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Auto, "Argelès-Gazost", "65401"))!.Ids);
        Assert.Null(ParuvenduSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Auto, "Argelès-sur-Mer", "65400")));
        Assert.Null(ParuvenduSearchAdapter.PickPlace(Json("paruvendu-autocomplete-tarbes.json"), new PlaceQuery(PlaceKind.Auto, "Tarbes", "31000")));
        Assert.Null(ParuvenduSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.PostalCode, "65000")));
    }

    // ------------------------------------------------------------------ code postal déduit

    private static PlanCommune Commune(string name, params string[] cps) => new("00000", name, [.. cps], "65", new GeoPoint(43, 0), false);

    [Fact]
    public void PostalCode_IsDeducedFromTheCommuneAndTheSearch()
    {
        List<PlanCommune> communes = [Commune("Odos", "65310"), Commune("Saint-Pé-de-Bigorre", "65270"), Commune("Bagnères-de-Bigorre", "65200", "65710")];

        Assert.Equal("65270", ParuvenduSearchAdapter.CommuneOf(communes, "St Pé de Bigorre")!.PostalCodes[0]);
        Assert.Null(ParuvenduSearchAdapter.CommuneOf(communes, "Bagnères"));
        Assert.Equal("65310", ParuvenduSearchAdapter.DeducePostalCode(communes[0], []));
        // Commune à deux codes postaux : celui que la recherche demandait, sinon inconnu.
        Assert.Equal("65710", ParuvenduSearchAdapter.DeducePostalCode(communes[2], ["65710", "65400"]));
        Assert.Null(ParuvenduSearchAdapter.DeducePostalCode(communes[2], []));
        // Commune inconnue : le code demandé s'il était seul.
        Assert.Equal("65400", ParuvenduSearchAdapter.DeducePostalCode(null, ["65400"]));
        Assert.Null(ParuvenduSearchAdapter.DeducePostalCode(null, ["65400", "65200"]));
    }

    [Fact]
    public void StrictCriteriaSentToTheSite_AreKnownOnTheListings()
    {
        var p = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            NewBuild = false,
            BathroomsMin = 2,
            RequiredFeatures = [Features.Pool, Features.Fireplace],
            StrictCriteria = [Criteria.Features, Criteria.Bathrooms],
        };

        var d = ParuvenduSearchAdapter.Deduced(p, new ListingData { Features = Features.Of((Features.Garden, true)) });

        Assert.False(d.NewBuild);
        Assert.Equal(2, d.Bathrooms);
        Assert.True(d.Has(Features.Pool));
        Assert.True(d.Has(Features.Garden));
        Assert.Null(d.Has(Features.Fireplace)); // pas de filtre cheminée sur le site
        Assert.Null(ParuvenduSearchAdapter.Deduced(new SearchProject { Id = "p", Transaction = TransactionType.Buy }, new ListingData()).NewBuild);
    }

    // ------------------------------------------------------------------ recherche (site simulé)

    private sealed class Fake(Func<Uri, (HttpStatusCode, string)?> answer) : DelegatingHandler, IHttpClientFactory
    {
        public List<string> Calls { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.PathAndQuery);
            var (status, body) = answer(request.RequestUri) ?? (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, request.RequestUri.Host.StartsWith("geo", StringComparison.Ordinal) ? "application/json" : "text/html"),
            });
        }
    }

    /// <summary>Page pleine (30 annonces) : la première carte de l'extrait répétée avec d'autres identifiants.</summary>
    private static string FullPage(int first, int total)
    {
        var html = Fixture("paruvendu-liste-commune.html");
        var start = html.IndexOf("<div class=\"blocAnnonce", StringComparison.Ordinal);
        var second = html.IndexOf("<div class=\"blocAnnonce", start + 10, StringComparison.Ordinal);
        var block = html[start..second];
        var cards = string.Concat(Enumerable.Range(first, 30).Select(i => block.Replace("1291779817", $"9{i:D9}", StringComparison.Ordinal)));
        return Regex.Replace(html[..start], @"""offerCount"":\d+", $"\"offerCount\":{total}") + cards + "</body></html>";
    }

    private const string Communes65 = """
        [ { "nom": "Argelès-Gazost", "code": "65025", "codesPostaux": ["65400"], "centre": { "type": "Point", "coordinates": [-0.0997, 43.0082] } },
          { "nom": "Odos", "code": "65339", "codesPostaux": ["65310"], "centre": { "type": "Point", "coordinates": [0.0532, 43.1965] } } ]
        """;

    [Fact]
    public async Task Search_ReadsPagesUntilTheGonePage()
    {
        var fake = new Fake(u => u.AbsolutePath switch
        {
            "/immobilier/annonceimmofo/liste/listeAnnonces" when u.Query.Contains("&p=1", StringComparison.Ordinal) => (HttpStatusCode.OK, FullPage(0, 100)),
            "/immobilier/annonceimmofo/liste/listeAnnonces" => (HttpStatusCode.Gone, "Erreur 410 - La page n'existe plus"),
            _ when u.Host.StartsWith("geo", StringComparison.Ordinal) && u.AbsolutePath == "/departements/65/communes" => (HttpStatusCode.OK, Communes65),
            _ => null,
        });
        var adapter = new ParuvenduSearchAdapter(fake, new GeoServices(fake), delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["dep:65"], 300), default);

        Assert.True(outcome.Complete); // HTTP 410 : fin de la liste, pas une erreur
        Assert.Equal(100, outcome.Total);
        Assert.Equal(30, outcome.Items.Count);
        var d = outcome.Items[0].Data;
        Assert.Equal("65400", d.PostalCode); // commune de l'annonce (une seule), recherche par département
        Assert.Equal(43.0082, d.Geo!.Lat, 4);
        Assert.Equal(2, fake.Calls.Count(c => c.StartsWith("/immobilier/", StringComparison.Ordinal)));
        Assert.Single(fake.Calls, c => c.StartsWith("/departements/65/communes", StringComparison.Ordinal)); // gardées en mémoire
        Assert.DoesNotContain(fake.Calls, c => c.Contains("A1KIVHMN000", StringComparison.Ordinal)); // aucune fiche lue
    }

    [Fact]
    public async Task Search_StopsAtMaxResults()
    {
        var fake = new Fake(u => u.AbsolutePath == "/immobilier/annonceimmofo/liste/listeAnnonces" ? (HttpStatusCode.OK, FullPage(0, 100)) : null);
        var adapter = new ParuvenduSearchAdapter(fake, new GeoServices(fake), delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy };

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["cp:65400"], 10), default);

        Assert.False(outcome.Complete);
        Assert.Equal(10, outcome.Items.Count);
        Assert.Single(fake.Calls, c => c.StartsWith("/immobilier/", StringComparison.Ordinal));
        // geo.api.gouv.fr ne répond pas : le code postal demandé (seul) est gardé.
        Assert.All(outcome.Items, i => Assert.Equal("65400", i.Data.PostalCode));
    }

    [Fact]
    public async Task Search_ReadsTheDetailOnlyWhenThePostalCodeIsUnknown()
    {
        var fake = new Fake(u => u.AbsolutePath switch
        {
            "/immobilier/annonceimmofo/liste/listeAnnonces" => (HttpStatusCode.OK, Fixture("paruvendu-liste-particuliers.html")),
            "/immobilier/vente/maison/1295610467A1KIVHMN000" => (HttpStatusCode.OK, Fixture("paruvendu-fiche-particulier.html")),
            "/departements/65/communes" => (HttpStatusCode.OK, "[]"),
            _ => null,
        });
        var adapter = new ParuvenduSearchAdapter(fake, new GeoServices(fake), delayMs: 0);
        // Seule l'annonce de Sénac (144 m², 279 000 €) respecte le projet : seule sa fiche est lue.
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House], PriceMin = 250000, PriceMax = 300000, SurfaceMin = 140 };

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["dep:65"], 300), default);

        Assert.True(outcome.Complete);
        Assert.Equal("65140", outcome.Items.Single(i => i.SiteId == "1295610467").Data.PostalCode);
        Assert.All(outcome.Items.Where(i => i.SiteId != "1295610467"), i => Assert.Null(i.Data.PostalCode));
        Assert.Single(fake.Calls, c => c.Contains("A1KIVH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_IsEmptyForTypesMissingFromTheSite()
    {
        var fake = new Fake(_ => null);
        var adapter = new ParuvenduSearchAdapter(fake, new GeoServices(fake), delayMs: 0);
        var project = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.Commercial] };

        var outcome = await adapter.SearchAsync(new SearchRequest(project, ["dep:65"], 300), default);

        Assert.Empty(outcome.Items);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Search_RefusalBecomesAReadableError()
    {
        var fake = new Fake(_ => (HttpStatusCode.Forbidden, "<html>captcha</html>"));
        var adapter = new ParuvenduSearchAdapter(fake, new GeoServices(fake), delayMs: 0);

        var e = await Assert.ThrowsAsync<SiteException>(() =>
            adapter.SearchAsync(new SearchRequest(new SearchProject { Id = "p", Transaction = TransactionType.Buy }, ["dep:65"], 300), default));
        Assert.Contains("anti-robot", e.Message, StringComparison.Ordinal);
    }
}
