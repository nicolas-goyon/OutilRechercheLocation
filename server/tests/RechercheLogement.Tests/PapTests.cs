using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Pap;

namespace RechercheLogement.Tests;

/// <summary>PAP : liste, annonces « à proximité » écartées, fiche, lieux et adresse de recherche (Fixtures/pap-*).</summary>
public class PapTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void List_ReadsTheListings_AndSkipsBanners()
    {
        var cards = PapListingParser.ParseList(Fixture("pap-liste.html"));

        Assert.Equal(new[] { "464301065", "463300632" }, cards.Select(c => c.Id));
        var d = cards[0].Data;
        Assert.Equal("https://www.pap.fr/annonces/maison-ibos-r464301065", d.Url);
        Assert.Equal(PropertyTypes.House, d.PropertyType);
        Assert.Equal(239_000m, d.Price);
        Assert.Equal((6, 4, 155.0), (d.Rooms!.Value, d.Bedrooms!.Value, d.Surface!.Value));
        Assert.Equal("Ibos", d.City);
        Assert.Null(d.PostalCode); // ni dans le texte ni dans l'adresse : la fiche le donne
        Assert.Equal("D", d.EnergyClass);
        Assert.Equal("https://cdn.pap.fr/photos/pap/5d/e1/5de14bee3f3e2da905c7a9b8e87bec41/5-p1.jpg", d.Photos![0]);
        Assert.StartsWith("situee dans un quartier calme", d.DescriptionExcerpt, StringComparison.Ordinal);
        Assert.Equal("Maison 6 pièces 155 m²", d.Title!.Replace(' ', ' '));

        var second = cards[1].Data;
        Assert.Equal(("Poueyferré", "65100"), (second.City, second.PostalCode));
        Assert.Equal(335_000m, second.Price);
    }

    [Fact]
    public void NextPage_FollowsTheSearch_NotTheNearbyPages()
    {
        Assert.Equal("/annonce/ventes-maisons-hautes-pyrenees-65-g429-2", PapListingParser.NextPage(Fixture("pap-liste.html")));
        Assert.Null(PapListingParser.NextPage(Fixture("pap-proximite.html")));
    }

    [Fact]
    public void NearbyListings_AreSkipped() => Assert.Empty(PapListingParser.ParseList(Fixture("pap-proximite.html")));

    [Fact]
    public void Detail_CompletesTheListing()
    {
        var card = PapListingParser.ParseList(Fixture("pap-liste.html"))[0];
        var d = PapListingParser.ParseDetail(Fixture("pap-fiche.html"), card.Data);

        Assert.Equal(("65420", "Ibos"), (d.PostalCode, d.City));
        Assert.Equal(500, d.LandSurface);
        Assert.Equal(("D", "D"), (d.EnergyClass, d.GhgClass));
        Assert.Equal("2026-11-06", d.PublishedAt);
        Assert.Equal("G43/1065", d.AgencyRef);
        Assert.Null(d.AgencyName); // particulier
        Assert.Equal(1600m, d.PropertyTax);
        Assert.Equal(3, d.Photos!.Count);
        Assert.Contains("terrasse en beton", d.DescriptionExcerpt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlaceKind.PostalCode, "65240", null, "g4644")]
    [InlineData(PlaceKind.Auto, "Arreau", "65240", "g33928")]
    [InlineData(PlaceKind.Auto, "arreau", "65240", "g33928")]
    [InlineData(PlaceKind.Department, "Hautes-Pyrénées", null, "g429")]
    [InlineData(PlaceKind.Auto, "Arreau", "65000", null)]
    [InlineData(PlaceKind.Auto, "Germ", null, null)]
    public void Places_ExactMatchOnly(PlaceKind kind, string text, string? cp, string? expected)
    {
        var places = PapListingParser.ParsePlaces(JsonNode.Parse(Fixture("pap-ac-geo.json"))!);
        Assert.Equal(expected, PapSearchAdapter.PickPlace(places, new PlaceQuery(kind, text, cp))?.Ids.Single());
    }

    [Fact]
    public void SearchUrl_OneTypePerSearch_CriteriaInTheAddress()
    {
        var p = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House, PropertyTypes.Castle, PropertyTypes.Terrain], PriceMax = 300_000, SurfaceMin = 100, BedroomsMin = 3 };
        Assert.Equal(new[] { "maisons", "terrains" }, PapSearchAdapter.SiteTypes(p.PropertyTypes));
        Assert.Equal("https://www.pap.fr/annonce/vente-maisons-g429g4644-jusqu-a-300000-euros-a-partir-de-100-m2",
            PapSearchAdapter.SearchUrl(p, "maisons", ["g429", "g4644"]));

        p.StrictCriteria = [Criteria.Bedrooms];
        p.PriceMin = 100_000;
        Assert.Equal("https://www.pap.fr/annonce/vente-maisons-g429-a-partir-de-3-chambres-entre-100000-et-300000-euros-a-partir-de-100-m2",
            PapSearchAdapter.SearchUrl(p, "maisons", ["g429"]));
    }

    [Theory]
    [InlineData("/annonces/maison-ibos-r464301065", PropertyTypes.House)]
    [InlineData("/annonces/appartement-tarbes-65000-r1", PropertyTypes.Flat)]
    [InlineData("/annonces/terrain-arreau-65240-r2", PropertyTypes.Terrain)]
    [InlineData("/annonces/grange-x-r3", PropertyTypes.Other)]
    public void TypeOfPath(string path, string type) => Assert.Equal(type, PapListingParser.TypeOfPath(path));
}
