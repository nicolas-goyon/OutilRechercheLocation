using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Projects;

namespace RechercheLogement.Tests;

/// <summary>Périmètre d'un projet dans le catalogue.</summary>
public class ProjectScopeTests
{
    private static SearchProject Chalet(params ProjectLocation[] places) => new()
    {
        Id = "p1",
        Name = "Chalet",
        Transaction = TransactionType.Buy,
        PriceMax = 300000,
        LocationMode = LocationMode.Places,
        Locations = [.. places],
    };

    private static readonly ProjectLocation Hp = new() { Type = PlaceType.Department, Query = "Hautes-Pyrénées", Code = "65" };

    private static ListingData Data(string cp, decimal price = 200000, string? city = null) =>
        new() { PostalCode = cp, Price = price, City = city, Transaction = TransactionType.Buy };

    [Theory]
    [InlineData(PlaceType.PostalCode, "65170", "65170", null, true)]
    [InlineData(PlaceType.PostalCode, "65170", "65200", null, false)]
    [InlineData(PlaceType.Department, "65", "65170", null, true)]
    [InlineData(PlaceType.Department, "2A", "20000", null, true)]
    [InlineData(PlaceType.Department, "971", "97100", null, true)]
    [InlineData(PlaceType.Department, "65", "09000", null, false)]
    public void Zone_FollowsThePlace(PlaceType type, string code, string cp, string? city, bool expected)
    {
        var project = Chalet(new ProjectLocation { Type = type, Query = code, Code = code });
        Assert.Equal(expected, ProjectScope.Contains(project, Data(cp, city: city)));
    }

    [Fact]
    public void Commune_NeedsItsPostalCodeAndName()
    {
        var project = Chalet(new ProjectLocation { Type = PlaceType.Commune, Query = "Vignec", Code = "65471", PostalCodes = ["65170"] });
        Assert.True(ProjectScope.Contains(project, Data("65170", city: "VIGNEC")));
        Assert.False(ProjectScope.Contains(project, Data("65170", city: "Aragnouet")));
    }

    [Fact]
    public void Region_UsesItsDepartments()
    {
        var project = Chalet(new ProjectLocation { Type = PlaceType.Region, Query = "Occitanie", Code = "76", DepartmentCodes = ["09", "65"] });
        Assert.True(ProjectScope.Contains(project, Data("09000")));
        Assert.False(ProjectScope.Contains(project, Data("64000")));
    }

    [Theory]
    [InlineData("65170", false)]
    [InlineData("64490", false)]
    [InlineData("66000", true)]
    [InlineData("65000", true)]
    [InlineData(null, false)]
    public void OutsidePlaces_UsesThePostalCodesOfThePlaces(string? cp, bool outside)
    {
        var project = Chalet(
            new ProjectLocation { Type = PlaceType.Commune, Query = "Vignec", Code = "65471", PostalCodes = ["65170"] },
            new ProjectLocation { Type = PlaceType.PostalCode, Query = "64490", Code = "64490" });
        Assert.Equal(outside, ProjectScope.OutsidePlaces(project, new ListingData { PostalCode = cp }));
    }

    [Fact]
    public void Store_HidesResultsOutsideThePlaces()
    {
        var store = new ProjectStore(new InMemoryProjectPersistence());
        var project = Chalet(new ProjectLocation { Type = PlaceType.PostalCode, Query = "65170", Code = "65170" });
        project.PriceMax = null;
        store.Save(project);
        store.ApplyResults(project.Id, "seloger", [new("a", new ListingData { PostalCode = "65170" }), new("b", new ListingData { PostalCode = "66000" })], false, 0);

        Assert.Equal("seloger:a", Assert.Single(store.Results(project.Id)).Key);
        Assert.Single(store.Groups(project.Id));
    }

    [Fact]
    public void OutsidePlaces_KeepsEverythingWithAFreeTextPlace()
    {
        var project = Chalet(new ProjectLocation { Type = PlaceType.Free, Query = "Pyrénées" });
        Assert.False(ProjectScope.OutsidePlaces(project, new ListingData { PostalCode = "66000" }));
    }

    [Fact]
    public void Criteria_AlsoApply()
    {
        Assert.False(ProjectScope.Contains(Chalet(Hp), Data("65170", price: 500000)));
    }

    [Fact]
    public void Catalog_IsLimitedToTheScope()
    {
        var catalog = new Catalog(new InMemoryPersistence());
        catalog.Sync(new SyncRequest("t",
        [
            new Observation("bienici", "in", "api", new ListingData { Title = "Chalet", PostalCode = "65170", Price = 200000, Surface = 80 }, 1),
            new Observation("bienici", "out", "api", new ListingData { Title = "Studio", PostalCode = "75011", Price = 900000, Surface = 20 }, 1),
        ], null, null));
        var project = Chalet(Hp);
        Func<Listing, bool> scope = l => ProjectScope.Contains(project, l.Data);

        Assert.Equal("Chalet", Assert.Single(catalog.QueryProperties(new PropertyQuery { Scope = scope })).Title);
        Assert.Equal(1, catalog.Stats(scope).Properties);
        Assert.Equal(1, catalog.Stats(scope).Listings);
        Assert.Equal(2, catalog.Stats().Properties);
    }

    [Fact]
    public void Selection_KeepsProjectResults_AndForgetsDeletedProjects()
    {
        var store = new ProjectStore(new InMemoryProjectPersistence());
        var project = Chalet(Hp);
        store.Save(project);
        var selection = new ProjectSelection(store);
        var changes = 0;
        selection.Changed += () => changes++;

        selection.Select("inconnu");
        Assert.Null(selection.ProjectId);
        Assert.Null(selection.Scope);

        selection.Select(project.Id);
        Assert.Equal(1, changes);
        var scope = selection.Scope!;
        Assert.True(scope(new Listing { Key = "bienici:a", Site = "bienici", SiteId = "a", PropertyId = "x", Data = Data("65170") }));
        Assert.False(scope(new Listing { Key = "bienici:b", Site = "bienici", SiteId = "b", PropertyId = "x", Data = Data("75011") }));
    }

    [Fact]
    public void Searches_OfOtherProjectsAreHidden()
    {
        var store = new ProjectStore(new InMemoryProjectPersistence());
        var project = Chalet(Hp);
        store.Save(project);
        var selection = new ProjectSelection(store);
        selection.Select(project.Id);
        SavedSearch Search(string id, string? projectId) => new() { Id = id, ProjectId = projectId };

        var visible = selection.Visible([Search("mine", project.Id), Search("all", null), Search("other", "p2")]);

        Assert.Equal(new[] { "mine", "all" }, visible.Select(s => s.Id));
    }
}

public class PropertySummaryGeoTests
{
    [Fact]
    public void Summary_ExposesThePositionOfAListing()
    {
        var catalog = new Catalog(new InMemoryPersistence());
        catalog.Sync(new SyncRequest("t", [new Observation("bienici", "a", "api", new ListingData { Title = "Chalet", Geo = new GeoPoint(42.8, 0.3) }, 1)], null, null));

        Assert.Equal(42.8, Assert.Single(catalog.QueryProperties(new PropertyQuery())).Geo?.Lat);
    }

    [Fact]
    public void Summary_GroupsPhotosOfAllListings_WithoutDuplicates()
    {
        var catalog = new Catalog(new InMemoryPersistence());
        catalog.Sync(new SyncRequest("t", [new Observation("bienici", "a", "api", new ListingData { Title = "Chalet", Photos = ["1.jpg", "2.jpg", "1.jpg"] }, 1)], null, null));

        Assert.Equal(new[] { "1.jpg", "2.jpg" }, Assert.Single(catalog.QueryProperties(new PropertyQuery())).Photos);
    }
}

public class PhotoLimitTests
{
    [Fact]
    public void Merge_KeepsUpToMaxPhotos()
    {
        var many = Enumerable.Range(1, 50).Select(i => $"{i}.jpg").ToArray();
        var merged = new ListingData { Photos = ["a.jpg"] }.Merge(new ListingData { Photos = many }, fillOnly: false);
        Assert.Equal(ListingData.MaxPhotos, merged.Photos!.Count);
        Assert.True(ListingData.MaxPhotos > 4);
    }
}
