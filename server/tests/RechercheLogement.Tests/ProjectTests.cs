using System.Text.Json.Nodes;
using RechercheLogement.Core.Geo;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Bienici;
using RechercheLogement.Server.Sites.Seloger;

namespace RechercheLogement.Tests;

public class ProjectTests
{
    private long _now = 1_000_000;

    private static SearchProject Project() => new()
    {
        Id = "pr_test",
        Name = "T2 Rodez",
        Transaction = TransactionType.Rent,
        PropertyTypes = ["flat"],
        Locations = [new ProjectLocation { Query = "Rodez" }],
        PriceMax = 700,
        SurfaceMin = 35,
        RoomsMin = 2,
        KeywordsExclude = ["colocation"],
    };

    private static IncomingListing L(string id, decimal price, double surface = 40, int rooms = 2, string? cp = null, string? desc = null) =>
        new(id, new ListingData { Price = price, Surface = surface, Rooms = rooms, PostalCode = cp, DescriptionExcerpt = desc });

    [Fact]
    public void Matcher_AppliesCriteria_UnknownValuesDoNotExclude()
    {
        var p = Project();
        Assert.True(ProjectMatcher.Matches(p, new ListingData { Price = 650, Surface = 40, Rooms = 2 }, out _));
        Assert.True(ProjectMatcher.Matches(p, new ListingData(), out _)); // rien de connu : gardée
        Assert.False(ProjectMatcher.Matches(p, new ListingData { Price = 750 }, out var why));
        Assert.Equal("prix", why);
        Assert.False(ProjectMatcher.Matches(p, new ListingData { PropertyType = "house" }, out _));
        Assert.False(ProjectMatcher.Matches(p, new ListingData { DescriptionExcerpt = "chambre en colocation meublee" }, out why));
        Assert.StartsWith("mot exclu", why);

        p.KeywordsInclude = ["balcon", "terrasse"];
        Assert.False(ProjectMatcher.Matches(p, new ListingData { Title = "Appartement 2 pièces" }, out _));
        Assert.True(ProjectMatcher.Matches(p, new ListingData { Title = "Appartement 2 pièces avec Terrasse" }, out _));
    }

    // ------------------------------------------------------------------ suivi des annonces

    [Fact]
    public void Store_TracksNewChangedRemovedAndReappearedListings()
    {
        var store = new ProjectStore(new InMemoryProjectPersistence(), () => _now++);
        var p = store.Save(Project());

        var run1 = _now;
        var first = store.ApplyResults(p.Id, "bienici", [L("a", 600), L("b", 900), L("c", 500, 36)], complete: true, run1);
        Assert.Equal(new ApplyOutcome(2, 2), first); // "b" trop cher

        var run2 = _now;
        var second = store.ApplyResults(p.Id, "bienici", [L("a", 580), L("d", 650, 50, 3)], complete: true, run2);
        Assert.Equal(new ApplyOutcome(Kept: 2, New: 1, Changed: 1, Removed: 1), second);
        var a = store.Results(p.Id).Single(r => r.SiteId == "a");
        Assert.Contains(a.History, e => e.Kind == ResultEventKind.Changed && e.Text.StartsWith("prix 600 € → 580 €"));
        Assert.True(store.Results(p.Id).Single(r => r.SiteId == "c").Removed);

        // Recherche incomplète (limite atteinte) : pas de retrait.
        var third = store.ApplyResults(p.Id, "bienici", [L("a", 580)], complete: false, _now);
        Assert.Equal(0, third.Removed);

        var fourth = store.ApplyResults(p.Id, "bienici", [L("a", 580), L("c", 500, 36), L("d", 650, 50, 3)], complete: true, _now);
        Assert.Equal(1, fourth.Reappeared);
        var c = store.Results(p.Id).Single(r => r.SiteId == "c");
        Assert.False(c.Removed);
        Assert.Equal(
            new[] { ResultEventKind.Appeared, ResultEventKind.Removed, ResultEventKind.Reappeared },
            c.History.Select(e => e.Kind));
    }

    [Fact]
    public void Store_GroupsTheSamePropertyAcrossSites_AndCanDetach()
    {
        var store = new ProjectStore(new InMemoryProjectPersistence(), () => _now++, id => id == "bienici" ? "Bien'ici" : "SeLoger");
        var p = store.Save(Project());
        var data = new ListingData { Price = 600, Surface = 45, Rooms = 2, PostalCode = "12000", AgencyRef = "LA2100-REGOURD12" };
        store.ApplyResults(p.Id, "bienici", [new IncomingListing("x1", data)], true, _now);
        store.ApplyResults(p.Id, "seloger", [new IncomingListing("26AB", data with { Price = 590 })], true, _now);

        var group = Assert.Single(store.Groups(p.Id));
        Assert.Equal(2, group.Listings.Count);
        Assert.Contains(group.Timeline(), x => x.Event.Kind == ResultEventKind.Linked && x.Event.Text.Contains("aussi sur SeLoger"));
        var summary = Assert.Single(store.Summaries());
        Assert.Equal(1, summary.Properties);

        store.Detach(p.Id, "seloger:26AB");
        Assert.Equal(2, store.Groups(p.Id).Count);

        store.SetGroupHidden(p.Id, "bienici:x1", true);
        Assert.Equal(1, Assert.Single(store.Summaries()).Properties);
    }

    [Fact]
    public void Diff_DescribesVisibleChanges()
    {
        var d = ProjectStore.Diff(
            new ListingData { Price = 650, Surface = 40, Rooms = 2, Furnished = false },
            new ListingData { Price = 620, Surface = 40.2, Rooms = 3, Furnished = true });
        Assert.Equal(new[] { "prix 650 € → 620 € (-30 €)", "pièces 2 → 3", "devenue meublée" }, d);
    }

    [Fact]
    public void Project_IsDue_OnlyWhenActiveWithAnInterval()
    {
        var p = Project();
        Assert.False(p.IsDue(_now));
        p.AutoRefreshHours = 6;
        Assert.True(p.IsDue(_now));
        p.LastRunAt = _now;
        Assert.False(p.IsDue(_now + 3_600_000));
        Assert.True(p.IsDue(_now + 6 * 3_600_000));
        p.Active = false;
        Assert.False(p.IsDue(_now + 6 * 3_600_000));
    }

    [Fact]
    public void Store_SurvivesARestart_WithSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rl-projets-{Guid.NewGuid():N}.db");
        try
        {
            var store = new ProjectStore(new SqliteProjectPersistence(path), () => _now++);
            var p = Project();
            p.LocationMode = LocationMode.TravelTime;
            p.CenterQuery = "Rodez";
            p.Area = new AreaPlan
            {
                Fingerprint = p.AreaFingerprint(),
                Shape = GeoShape.Circle(new GeoPoint(44.35, 2.57), 20_000),
                Communes = [new PlanCommune("12202", "Rodez", ["12000"], "12", new GeoPoint(44.36, 2.57), true)],
                Departments = new() { ["12"] = "Aveyron" },
            };
            p.PlaceCache["bienici"] = new() { ["cp:12000"] = new CachedPlace("Rodez (12000)", ["-116558"]), ["cp:99999"] = null };
            store.Save(p);
            store.ApplyResults(p.Id, "seloger", [L("26AB", 600)], true, _now);
            store.AddRun(new ProjectRun(p.Id, "seloger", _now, false, 0, 0, "refusé"));

            var restarted = new ProjectStore(new SqliteProjectPersistence(path));
            var loaded = restarted.Get(p.Id)!;
            Assert.Equal(LocationMode.TravelTime, loaded.LocationMode);
            Assert.Equal(20_000, loaded.Area!.Shape.RadiusM);
            Assert.Equal("Rodez", loaded.Area.Communes[0].Name);
            Assert.Equal(new[] { "-116558" }, loaded.PlaceCache["bienici"]["cp:12000"]!.Ids);
            Assert.Null(loaded.PlaceCache["bienici"]["cp:99999"]);
            var result = Assert.Single(restarted.Results(p.Id));
            Assert.Equal(ResultEventKind.Appeared, Assert.Single(result.History).Kind);
            Assert.Equal("refusé", Assert.Single(restarted.Runs(p.Id)).Message);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    // ------------------------------------------------------------------ zone

    [Fact]
    public void GeoShape_CircleAndPolygon()
    {
        var rodez = new GeoPoint(44.3506, 2.5750);
        var circle = GeoShape.Circle(rodez, 10_000);
        Assert.True(circle.Contains(new GeoPoint(44.3720, 2.5600)));  // Onet-le-Château, ~2,7 km
        Assert.False(circle.Contains(new GeoPoint(44.6000, 2.0300))); // Villefranche-de-Rouergue, ~50 km
        Assert.Equal(24, circle.BoundaryPoints(24).Count());

        var square = GeoShape.Polygons([[new(44.0, 2.0), new(44.0, 3.0), new(45.0, 3.0), new(45.0, 2.0)]]);
        Assert.True(square.Contains(rodez));
        Assert.False(square.Contains(new GeoPoint(43.99, 2.5)));
        Assert.True(square.Contains(new GeoPoint(43.99, 2.5), toleranceM: 2000)); // 1,1 km sous le bord
    }

    [Fact]
    public void AreaFilter_UsesGps_ElseTheCommuneCenter()
    {
        var plan = new AreaPlan
        {
            Shape = GeoShape.Circle(new GeoPoint(44.3506, 2.5750), 10_000),
            Communes =
            [
                new PlanCommune("12176", "Onet-le-Château", ["12850"], "12", new GeoPoint(44.3720, 2.5600), true),
                new PlanCommune("12300", "Villefranche-de-Rouergue", ["12200"], "12", new GeoPoint(44.3520, 2.0350), false),
            ],
        };
        Assert.Equal(AreaFilter.Verdict.Inside, AreaFilter.Check(plan, new ListingData { Geo = new GeoPoint(44.36, 2.58, 50) }, out var dist));
        Assert.InRange(dist!.Value, 500, 1500);
        Assert.Equal(AreaFilter.Verdict.Inside, AreaFilter.Check(plan, new ListingData { PostalCode = "12850", City = "Onet le Chateau" }, out _));
        Assert.Equal(AreaFilter.Verdict.Outside, AreaFilter.Check(plan, new ListingData { PostalCode = "12200", City = "Villefranche-de-Rouergue" }, out _));
        Assert.Equal(AreaFilter.Verdict.Unknown, AreaFilter.Check(plan, new ListingData { PostalCode = "75001" }, out _));
    }

    [Fact]
    public void PlaceQueries_PostalCodes_ThenDepartmentsForLargeAreas()
    {
        var p = Project();
        Assert.Equal(new[] { "rodez" }, ProjectRunner.PlaceQueries(p, PlaceKinds.PostalCode).Queries.Select(q => q.CacheKey));

        p.LocationMode = LocationMode.Radius;
        p.Area = new AreaPlan
        {
            Communes = Enumerable.Range(0, 5).Select(i => new PlanCommune($"c{i}", $"C{i}", [$"1200{i}"], "12", new GeoPoint(44, 2), i < 3)).ToList(),
            Departments = new() { ["12"] = "Aveyron" },
        };
        var (queries, _) = ProjectRunner.PlaceQueries(p, PlaceKinds.PostalCode | PlaceKinds.Department);
        Assert.Equal(new[] { "cp:12000", "cp:12001", "cp:12002" }, queries.Select(q => q.CacheKey));

        p.Area.Communes = Enumerable.Range(0, 200).Select(i => new PlanCommune($"c{i}", $"C{i}", [$"{12000 + i}"], "12", new GeoPoint(44, 2), true)).ToList();
        (queries, _) = ProjectRunner.PlaceQueries(p, PlaceKinds.PostalCode | PlaceKinds.Department);
        Assert.Equal(new[] { "dep:Aveyron" }, queries.Select(q => q.CacheKey));
    }

    // ------------------------------------------------------------------ sites (sans réseau)

    [Fact]
    public void Bienici_Filters_UseTheSiteNames()
    {
        var f = BieniciSearchAdapter.BuildFilters(Project(), ["-116558"], 1);
        Assert.Equal("rent", f["filterType"]!.GetValue<string>());
        Assert.Equal(100, f["from"]!.GetValue<int>());
        Assert.Equal(700m, f["maxPrice"]!.GetValue<decimal>());
        Assert.Equal(35d, f["minArea"]!.GetValue<double>());
        Assert.Equal(2, f["minRooms"]!.GetValue<int>());
        Assert.Equal("-116558", f["zoneIdsByTypes"]!["zoneIds"]![0]!.GetValue<string>());
        Assert.Equal("publicationDate", f["sortBy"]!.GetValue<string>());
        Assert.Null(f["isFurnished"]);
    }

    [Fact]
    public void Bienici_ParsesAnAd_AndPicksAPlace()
    {
        var ad = JsonNode.Parse("""
            { "id": "century-21-202_2190_28268", "adType": "rent", "propertyType": "flat", "reference": "28268",
              "price": 496, "charges": 20, "surfaceArea": 37.5, "roomsQuantity": 2, "bedroomsQuantity": 1, "floor": 2,
              "postalCode": "12000", "city": "Rodez", "accountDisplayName": "CENTURY 21 Foch Immobilier",
              "contactRelativeData": { "rcs": "407797521" },
              "blurInfo": { "radius": 50, "position": { "lat": 44.35, "lon": 2.57 } },
              "photos": [{ "url": "https://file.bienici.com/photo/a.jpg" }],
              "description": "Appartement <br>lumineux. Mandat n° 28268-C21", "publicationDate": "2026-10-02T00:16:28.252Z" }
            """)!;
        var item = new BieniciListingParser().Parse(ad)!;
        Assert.Equal("century-21-202_2190_28268", item.SiteId);
        Assert.Equal("https://www.bienici.com/annonce/century-21-202_2190_28268", item.Data.Url);
        Assert.Equal("Appartement 2 pièces 37,5 m²", item.Data.Title);
        Assert.Equal(496m, item.Data.Price);
        Assert.Equal(TransactionType.Rent, item.Data.Transaction);
        Assert.Equal(50d, item.Data.Geo!.PrecisionM);
        Assert.Equal("407797521", item.Data.AgencySiren);
        Assert.Equal(new[] { "28268-C21" }, item.Data.OtherRefs);

        var places = JsonNode.Parse("""
            [ { "name": "12850", "type": "postalCode", "postalCodes": ["12850"], "zoneIds": ["-1973455", "-116564"] },
              { "name": "Onet-le-Château", "type": "city", "postalCodes": ["12850"], "zoneIds": ["-1973455"] },
              { "name": "Aveyron", "type": "department", "postalCodes": [], "zoneIds": ["-7451"] } ]
            """)!;
        Assert.Equal(new[] { "-1973455", "-116564" }, BieniciSearchAdapter.PickPlace(places, new PlaceQuery(PlaceKind.PostalCode, "12850"))!.Ids);
        Assert.Equal("Aveyron (département)", BieniciSearchAdapter.PickPlace(places, new PlaceQuery(PlaceKind.Department, "Aveyron"))!.Label);
        Assert.Null(BieniciSearchAdapter.PickPlace(places, new PlaceQuery(PlaceKind.PostalCode, "75001")));
    }

    [Fact]
    public void Seloger_SearchBody_AndClassifiedParsing()
    {
        var body = SelogerSearchAdapter.BuildSearch(Project(), ["AD08FR4330"], 2);
        Assert.Equal("Rent", body["criteria"]!["distributionTypes"]![0]!.GetValue<string>());
        Assert.Equal("Apartment", body["criteria"]!["estateTypes"]![0]!.GetValue<string>());
        Assert.Equal(700m, body["criteria"]!["priceMax"]!.GetValue<decimal>());
        Assert.Equal(2, body["criteria"]!["numberOfRoomsMin"]!.GetValue<int>());
        Assert.Equal("DateDesc", body["paging"]!["order"]!.GetValue<string>());

        var c = JsonNode.Parse("""
            { "id": "268A2ANTU7U6", "url": "https://www.seloger.com/annonce/location/occitanie/aveyron-12/rodez-12000/268A2ANTU7U6",
              "metadata": { "creationDate": "2026-09-29T08:22:00Z" },
              "location": { "address": { "city": "Rodez", "zipCode": "12000", "district": "Centre" } },
              "hardFacts": { "title": "Appartement à louer", "keyfacts": ["2 pièces", "1 chambre", "45,77 m²", "3ème étage", "Meublé"] },
              "gallery": { "images": [{ "url": "https://cdnihddipa.cloudimg.io/a/b.jpg?ci_seal=x" }] },
              "mainDescription": { "description": "Bel appartement. Réf. : FON-331712739" },
              "provider": { "intermediaryCard": { "title": "Foncia Rives de Garonne   Rodez" },
                            "agencyLegalInformations": ["Foncia Rives de Garonne", "<b>RCS:</b> 325539286"] },
              "rawData": { "distributionType": "RENT", "propertyType": "APARTMENT", "price": 505, "nbroom": 2, "nbbedroom": 1,
                           "surface": { "main": 45.77 }, "offererMarketingKey": "331712739" } }
            """)!;
        var item = new SelogerListingParser().Parse(c)!;
        Assert.Equal("Appartement 2 pièces 45,77 m²", item.Data.Title);
        Assert.Equal(505m, item.Data.Price);
        Assert.Equal(3, item.Data.Floor);
        Assert.True(item.Data.Furnished);
        Assert.Equal("Foncia Rives de Garonne Rodez", item.Data.AgencyName);
        Assert.Equal("325539286", item.Data.AgencySiren);
        Assert.Equal(new[] { "FON-331712739" }, item.Data.OtherRefs);

        var suggestion = JsonNode.Parse("""{ "items": [{ "text": "12850", "criteria": { "location": { "placeIds": ["POCOFR577"] } } }] }""")!;
        Assert.Equal("POCOFR577", SelogerSearchAdapter.PickPlace(suggestion, new PlaceQuery(PlaceKind.PostalCode, "12850"))!.Ids[0]);
    }

    [Fact]
    public void RefExtractor_MatchesThePluginRules()
    {
        Assert.Equal(new[] { "LA2100-REGOURD12" }, RefExtractor.FromText("Bel appartement. Réf. : LA2100-REGOURD12"));
        Assert.Equal(new[] { "1653L1653", "2024-118" }, RefExtractor.FromText("Référence annonce : 1653L1653 — Mandat n° 2024-118."));
        Assert.Empty(RefExtractor.FromText("Cuisine avec réfrigérateur, refaite en 2024. Référence 2024. Cave n°8."));
        Assert.Equal("407797521", RefExtractor.SirenFromText("SARL au capital de 50 000 € - RCS Rodez 407 797 521"));
        Assert.Equal("325539286", RefExtractor.SirenFromText("<b>SIRET:</b> 32553928600105"));
        Assert.Null(RefExtractor.SirenFromText("RCS 123456789"));
    }

    [Fact]
    public void Geo_Suggestions_FromGeoApiAndGeocoding()
    {
        // Réponse réelle de geo.api.gouv.fr/communes?nom=rode&fields=nom,code,codesPostaux,centre,codeDepartement
        var communes = GeoServices.ParseCommunes(JsonNode.Parse("""
            [{"nom":"Rodez","code":"12202","codesPostaux":["12000"],"centre":{"type":"Point","coordinates":[2.5699,44.3591]},"codeDepartement":"12"},
             {"nom":"Toulouse","code":"31555","codesPostaux":["31000","31100","31200"],"centre":{"type":"Point","coordinates":[1.4317,43.6007]},"codeDepartement":"31"},
             {"nom":"Sans centre","code":"00000","codesPostaux":[]}]
            """));
        Assert.Equal(2, communes.Count);
        Assert.Equal(new GeoPoint(44.3591, 2.5699), communes[0].Center);

        var rodez = GeoServices.CommuneSuggestion(communes[0]);
        Assert.Equal("Rodez (12000)", rodez.Label);
        Assert.Equal(PlaceType.Commune, rodez.Location!.Type);
        Assert.Equal("Rodez", rodez.Location.Query);
        Assert.Equal("12202", rodez.Location.Code);
        Assert.Equal("Toulouse (31000…)", GeoServices.CommuneSuggestion(communes[1]).Label);

        var cp = GeoServices.PostalCodeSuggestion("12000", [communes[0]]);
        Assert.Equal(PlaceType.PostalCode, cp.Location!.Type);
        Assert.Equal("12000", cp.Location.Query);
        Assert.Equal("12000 (Rodez)", cp.Location.Display());

        var deps = GeoServices.ParseDepartments(JsonNode.Parse("""[{"nom":"Aveyron","code":"12"}]"""));
        Assert.Equal("Aveyron (12)", deps.Single().Label);
        Assert.Equal("Aveyron", deps[0].Location!.Query);

        var addresses = GeoServices.ParseAddresses(JsonNode.Parse("""
            { "type": "FeatureCollection", "features": [
              { "geometry": { "type": "Point", "coordinates": [2.5734, 44.3506] },
                "properties": { "label": "8 Boulevard Gally 12000 Rodez", "type": "housenumber", "context": "12, Aveyron, Occitanie" } } ] }
            """));
        Assert.Equal("8 Boulevard Gally 12000 Rodez", addresses.Single().Label);
        Assert.Equal("Adresse · 12, Aveyron, Occitanie", addresses[0].Detail);
        Assert.Equal(new GeoPoint(44.3506, 2.5734), addresses[0].Point);
        Assert.Null(addresses[0].Location);
    }

    [Fact]
    public void Locations_BecomeSiteQueries_WithAPostalCodeHint()
    {
        var commune = new ProjectLocation { Type = PlaceType.Commune, Query = "Onet-le-Château", Code = "12176", PostalCodes = ["12850"] };
        var q = ProjectRunner.ToQuery(commune);
        Assert.Equal(PlaceKind.Auto, q.Kind);
        Assert.Equal("12850", q.PostalCode);
        Assert.Equal("onet-le-château|12850", q.CacheKey);
        Assert.Equal(PlaceKind.PostalCode, ProjectRunner.ToQuery(new ProjectLocation { Type = PlaceType.PostalCode, Query = "12850", Code = "12850" }).Kind);
        Assert.Equal(new PlaceQuery(PlaceKind.Department, "Aveyron"), ProjectRunner.ToQuery(new ProjectLocation { Type = PlaceType.Department, Query = "Aveyron", Code = "12" }));
        // Anciens projets : texte libre.
        Assert.Equal(new PlaceQuery(PlaceKind.Auto, "Rodez"), ProjectRunner.ToQuery(new ProjectLocation { Query = "Rodez" }));
        Assert.Equal("Rodez", new ProjectLocation { Query = "Rodez" }.Display());
        Assert.Equal("commune:12176", commune.Key());

        // Bien'ici : le lieu qui a le code postal de la commune, sinon le premier.
        var places = JsonNode.Parse("""
            [ { "name": "Saint-Martin", "type": "city", "postalCodes": ["05120"], "zoneIds": ["-1"] },
              { "name": "Saint-Martin", "type": "city", "postalCodes": ["32300"], "zoneIds": ["-2"] } ]
            """)!;
        Assert.Equal("-2", BieniciSearchAdapter.PickPlace(places, new PlaceQuery(PlaceKind.Auto, "Saint-Martin", "32300"))!.Ids[0]);
        Assert.Equal("-1", BieniciSearchAdapter.PickPlace(places, new PlaceQuery(PlaceKind.Auto, "Saint-Martin", "99999"))!.Ids[0]);

        // SeLoger : le libellé qui contient le code postal ou le département.
        var suggestion = JsonNode.Parse("""
            { "items": [ { "text": "Saint-Martin (05)", "criteria": { "location": { "placeIds": ["A"] } } },
                         { "text": "Saint-Martin (32)", "criteria": { "location": { "placeIds": ["B"] } } } ] }
            """)!;
        Assert.Equal("B", SelogerSearchAdapter.PickPlace(suggestion, new PlaceQuery(PlaceKind.Auto, "Saint-Martin", "32300"))!.Ids[0]);
        Assert.Equal("A", SelogerSearchAdapter.PickPlace(suggestion, new PlaceQuery(PlaceKind.Auto, "Saint-Martin"))!.Ids[0]);
    }

    [Fact]
    public void AreaFingerprint_FollowsTheChosenPoint()
    {
        var p = Project();
        p.LocationMode = LocationMode.Radius;
        p.CenterQuery = "Rodez";
        var typed = p.AreaFingerprint();
        Assert.Equal("radius|rodez|10", typed);
        p.Center = new GeoPoint(44.35, 2.57);
        Assert.Equal("radius|44.35,2.57|10", p.AreaFingerprint());
        p.Center = new GeoPoint(44.36, 2.57);
        Assert.NotEqual("radius|44.35,2.57|10", p.AreaFingerprint());
    }
}
