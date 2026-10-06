using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Projects;

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

    [Fact]
    public void Store_KeepsMatchingResults_AndCountsNewOnes()
    {
        var store = new ProjectStore(new InMemoryProjectPersistence(), () => _now++);
        var p = store.Save(Project());

        var first = store.ApplyResults(p.Id, "bienici", [
            ("a", new ListingData { Price = 600, Surface = 40, Rooms = 2 }),
            ("b", new ListingData { Price = 900 }),                 // trop cher
            ("c", new ListingData { Price = 500, Surface = 36, Rooms = 2 }),
        ]);
        Assert.Equal(new ApplyOutcome(3, 2, 2), first);

        var second = store.ApplyResults(p.Id, "bienici", [
            ("a", new ListingData { Price = 590, Surface = 40, Rooms = 2 }), // déjà connue, prix mis à jour
            ("d", new ListingData { Price = 650, Surface = 50, Rooms = 3 }),
        ]);
        Assert.Equal(new ApplyOutcome(2, 2, 1), second);
        Assert.Equal(3, store.Results(p.Id).Count);
        Assert.Equal(590, store.Results(p.Id).Single(r => r.SiteId == "a").Data.Price);

        store.SetHidden(p.Id, "bienici:c", true);
        var summary = Assert.Single(store.Summaries());
        Assert.Equal(2, summary.Results); // masquée exclue
        store.AddRun(new ProjectRun(p.Id, "bienici", _now, true, 3, 1, "ok"));
        Assert.NotNull(store.Get(p.Id)!.LastRunAt);

        store.Delete(p.Id);
        Assert.Empty(store.Summaries());
        Assert.Empty(store.Results(p.Id));
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
            p.Locations[0].SiteIds["bienici"] = ["-116558"];
            store.Save(p);
            store.ApplyResults(p.Id, "seloger", [("26AB", new ListingData { Price = 600, Url = "https://www.seloger.com/annonce/x/26AB" })]);
            store.AddRun(new ProjectRun(p.Id, "seloger", _now, false, 0, 0, "refusé"));

            var restarted = new ProjectStore(new SqliteProjectPersistence(path));
            var loaded = restarted.Get(p.Id)!;
            Assert.Equal("T2 Rodez", loaded.Name);
            Assert.Equal(new[] { "-116558" }, loaded.Locations[0].SiteIds["bienici"]);
            Assert.Equal(700, loaded.PriceMax);
            Assert.Equal("seloger:26AB", Assert.Single(restarted.Results(p.Id)).Key);
            Assert.Equal("refusé", Assert.Single(restarted.Runs(p.Id)).Message);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    // ------------------------------------------------------------------ collecteurs (sans réseau)

    [Fact]
    public void Bienici_Filters_UseTheSiteNames()
    {
        var f = BieniciCollector.BuildFilters(Project(), ["-116558"], 1);
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
    public void Bienici_MapsAnAd_AndPicksAPlace()
    {
        var ad = JsonNode.Parse("""
            { "id": "century-21-202_2190_28268", "adType": "rent", "propertyType": "flat", "reference": "28268",
              "price": 496, "charges": 20, "surfaceArea": 37.5, "roomsQuantity": 2, "bedroomsQuantity": 1, "floor": 2,
              "postalCode": "12000", "city": "Rodez", "accountDisplayName": "CENTURY 21 Foch Immobilier",
              "blurInfo": { "radius": 50, "position": { "lat": 44.35, "lon": 2.57 } },
              "photos": [{ "url": "https://file.bienici.com/photo/a.jpg" }],
              "description": "Appartement <br>lumineux", "publicationDate": "2026-10-02T00:16:28.252Z" }
            """)!;
        var item = BieniciCollector.MapAd(ad)!;
        Assert.Equal("century-21-202_2190_28268", item.SiteId);
        Assert.Equal("https://www.bienici.com/annonce/century-21-202_2190_28268", item.Data.Url);
        Assert.Equal("Appartement 2 pièces 37,5 m²", item.Data.Title);
        Assert.Equal(496m, item.Data.Price);
        Assert.Equal(TransactionType.Rent, item.Data.Transaction);
        Assert.Equal("appartement lumineux", item.Data.DescriptionExcerpt);
        Assert.Equal(50d, item.Data.Geo!.PrecisionM);

        var places = JsonNode.Parse("""
            [ { "name": "Onet-le-Château", "postalCodes": ["12850"], "zoneIds": ["-1973455"] },
              { "name": "Anet", "postalCodes": ["28260"], "zoneIds": ["-255528"] } ]
            """)!;
        Assert.Equal(new LocationMatch("Onet-le-Château (12850)", ["-1973455"]), BieniciCollector.PickPlace(places, "onet"), new LocationComparer());
        Assert.Equal("-255528", BieniciCollector.PickPlace(places, "28260")!.Ids[0]);
        Assert.Null(BieniciCollector.PickPlace(places, "75001"));
    }

    [Fact]
    public void Seloger_SearchBody_AndClassifiedMapping()
    {
        var body = SelogerCollector.BuildSearch(Project(), ["AD08FR4330"], 2);
        Assert.Equal("Rent", body["criteria"]!["distributionTypes"]![0]!.GetValue<string>());
        Assert.Equal("Apartment", body["criteria"]!["estateTypes"]![0]!.GetValue<string>());
        Assert.Equal(700m, body["criteria"]!["priceMax"]!.GetValue<decimal>());
        Assert.Equal(2, body["criteria"]!["numberOfRoomsMin"]!.GetValue<int>());
        Assert.Equal("DateDesc", body["paging"]!["order"]!.GetValue<string>());
        Assert.Equal(2, body["paging"]!["page"]!.GetValue<int>());

        var c = JsonNode.Parse("""
            { "id": "268A2ANTU7U6", "url": "https://www.seloger.com/annonce/location/occitanie/aveyron-12/rodez-12000/268A2ANTU7U6",
              "metadata": { "creationDate": "2026-09-29T08:22:00Z" },
              "location": { "address": { "city": "Rodez", "zipCode": "12000", "district": "Centre" } },
              "hardFacts": { "title": "Appartement à louer", "keyfacts": ["2 pièces", "1 chambre", "45,77 m²", "3ème étage", "Meublé"] },
              "gallery": { "images": [{ "url": "https://cdnihddipa.cloudimg.io/a/b.jpg?ci_seal=x" }] },
              "mainDescription": { "description": "Bel appartement" },
              "provider": { "intermediaryCard": { "title": "Foncia Rives de Garonne   Rodez" } },
              "rawData": { "distributionType": "RENT", "propertyType": "APARTMENT", "price": 505, "nbroom": 2, "nbbedroom": 1,
                           "surface": { "main": 45.77 }, "offererMarketingKey": "331712739" } }
            """)!;
        var item = SelogerCollector.MapClassified(c)!;
        Assert.Equal("268A2ANTU7U6", item.SiteId);
        Assert.Equal("Appartement 2 pièces 45,77 m²", item.Data.Title);
        Assert.Equal(505m, item.Data.Price);
        Assert.Equal(3, item.Data.Floor);
        Assert.True(item.Data.Furnished);
        Assert.Equal("flat", item.Data.PropertyType);
        Assert.Equal("331712739", item.Data.AgencyRef);

        var suggestion = JsonNode.Parse("""{ "items": [{ "text": "Onet-le-Château", "criteria": { "location": { "placeIds": ["AD08FR4303"] } } }] }""")!;
        Assert.Equal("AD08FR4303", SelogerCollector.PickPlace(suggestion)!.Ids[0]);
    }

    private sealed class LocationComparer : IEqualityComparer<LocationMatch?>
    {
        public bool Equals(LocationMatch? x, LocationMatch? y) => x?.Label == y?.Label && (x?.Ids ?? []).SequenceEqual(y?.Ids ?? []);
        public int GetHashCode(LocationMatch? obj) => obj?.Label.GetHashCode() ?? 0;
    }
}
