using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Common;
using RechercheLogement.Server.Sites.Leboncoin;

namespace RechercheLogement.Tests;

/// <summary>Leboncoin : pages de résultats (JSON __NEXT_DATA__), lieux et adresse de recherche (Fixtures/leboncoin-*.html).</summary>
public class LeboncoinTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static List<ListingData> Ads() =>
        LeboncoinListingParser.SearchData(Fixture("leboncoin-recherche.html"))!["ads"]!.AsArray()
            .Select(a => LeboncoinListingParser.ParseAd(a!)!.Data).ToList();

    [Fact]
    public void SearchPage_ReadsTheListings()
    {
        var data = LeboncoinListingParser.SearchData(Fixture("leboncoin-recherche.html"))!;
        Assert.Equal(2050, (int)data["total"]!);
        var item = LeboncoinListingParser.ParseAd(data["ads"]![0]!)!;
        var d = item.Data;

        Assert.Equal("3267473989", item.SiteId);
        Assert.Equal("https://www.leboncoin.fr/ad/ventes_immobilieres/3267473989", d.Url);
        Assert.Equal("Propriété 5 pièces 152 m²", d.Title);
        Assert.Equal(TransactionType.Buy, d.Transaction);
        Assert.Equal(PropertyTypes.House, d.PropertyType);
        Assert.Equal(350_000m, d.Price);
        Assert.Equal((152.0, 5, 4), (d.Surface!.Value, d.Rooms!.Value, d.Bedrooms!.Value));
        Assert.Equal(("65590", "Bordères-Louron"), (d.PostalCode, d.City));
        Assert.Equal(1500, d.Geo!.PrecisionM); // point de la commune
        Assert.Equal(("V30000548", "AGENCE DU LOURON", "909572356"), (d.AgencyRef, d.AgencyName, d.AgencySiren));
        Assert.Equal(2, d.Photos!.Count);
        Assert.EndsWith("rule=ad-large", d.Photos[0], StringComparison.Ordinal);
        Assert.Equal("2026-11-06T01:14:06", d.PublishedAt);
        Assert.Equal(322, d.LandSurface);
        Assert.Equal(("E", "C"), (d.EnergyClass, d.GhgClass));
        Assert.Equal(1900, d.YearBuilt);
        Assert.Equal(2, d.Bathrooms);
        Assert.Equal("Électrique, Individuel", d.Heating);
        Assert.Equal("Sud", d.Exposure);
        Assert.Equal((false, false), (d.NewBuild, d.WorkNeeded));
        Assert.Equal(650m, d.PropertyTax);
        Assert.Equal(true, d.Has(Features.Garden));
        Assert.Equal(true, d.Has(Features.Balcony));
        Assert.Equal(true, d.Has(Features.Cellar));
        Assert.Equal(true, d.Has(Features.Parking));
        Assert.Equal(false, d.Has(Features.Elevator));
        Assert.Null(d.Has(Features.Pool));
    }

    [Fact]
    public void OtherListings_SubtypeViagerAndPrivateSeller()
    {
        var ads = Ads();
        Assert.Equal("Maison mitoyenne", ads[1].Subtype);
        Assert.Equal(150, ads[1].Geo!.PrecisionM); // adresse
        Assert.Equal(0, ads[1].Bathrooms);

        var viager = ads[2];
        Assert.Equal(SaleKinds.LifeAnnuity, viager.SaleKind);
        Assert.Null(viager.NewBuild);
        Assert.True(viager.WorkNeeded);
        Assert.Null(viager.AgencyName); // particulier
        Assert.Equal(true, viager.Has(Features.Pool));
    }

    [Fact]
    public void DetailPage_HasTheDescription()
    {
        var ad = LeboncoinListingParser.NextData(Fixture("leboncoin-fiche.html"))!["props"]!["pageProps"]!["ad"]!;
        var d = LeboncoinListingParser.ParseAd(ad)!.Data;
        Assert.StartsWith("propriete 5 pieces 152 m cette maison ancienne", d.DescriptionExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoNextData_IsNull() => Assert.Null(LeboncoinListingParser.SearchData("<html><body>captcha</body></html>"));

    [Theory]
    [InlineData(PlaceKind.Department, "Hautes-Pyrénées", null, "d_65")]
    [InlineData(PlaceKind.PostalCode, "65240", null, "65240")]
    [InlineData(PlaceKind.Auto, "Arreau", "65240", "Arreau_65240")]
    [InlineData(PlaceKind.Auto, "Arreau", null, null)]
    [InlineData(PlaceKind.PostalCode, "652", null, null)]
    public void Places_WithoutCallingTheSite(PlaceKind kind, string text, string? cp, string? expected) =>
        Assert.Equal(expected, LeboncoinSearchAdapter.ResolvePlace(new PlaceQuery(kind, text, cp))?.Ids.Single());

    [Fact]
    public void SearchUrl_SendsTheCriteria()
    {
        var p = new SearchProject
        {
            Id = "p",
            Transaction = TransactionType.Buy,
            PropertyTypes = [PropertyTypes.House, PropertyTypes.Terrain],
            PriceMax = 300_000,
            SurfaceMin = 100,
            RoomsMin = 4,
            LandSurfaceMin = 1000,
            EnergyClassMax = "D",
            RequiredFeatures = [Features.Pool],
        };
        var url = LeboncoinSearchAdapter.SearchUrl(p, ["d_65", "Arreau_65240"], 2);
        Assert.Equal("https://www.leboncoin.fr/recherche?category=9&locations=d_65,Arreau_65240&real_estate_type=1,3&price=min-300000"
            + "&square=100-max&rooms=4-max&immo_sell_type=old,new&sort=time&order=desc&page=2", url);

        // Obligatoires : terrain, DPE et piscine envoyés au site.
        p.StrictCriteria = [.. Criteria.All];
        url = LeboncoinSearchAdapter.SearchUrl(p, ["d_65"], 1);
        Assert.Contains("&land_plot_surface=1000-max", url, StringComparison.Ordinal);
        Assert.Contains("&energy_rate=a,b,c,d", url, StringComparison.Ordinal);
        Assert.Contains("&outside_access=pool", url, StringComparison.Ordinal);
        Assert.DoesNotContain("page=", url, StringComparison.Ordinal);

        // Deux équipements du même groupe : pas envoyés (l'outil vérifie).
        p.RequiredFeatures = [Features.Pool, Features.Garden];
        Assert.DoesNotContain("outside_access", LeboncoinSearchAdapter.SearchUrl(p, ["d_65"], 1), StringComparison.Ordinal);
    }

    [Fact]
    public void SellTypes()
    {
        var p = new SearchProject { Id = "p", Transaction = TransactionType.Buy };
        Assert.Equal("old,new", LeboncoinSearchAdapter.SellTypes(p));
        p.NewBuild = false;
        Assert.Equal("old", LeboncoinSearchAdapter.SellTypes(p));
        p.ExcludeLifeAnnuity = false;
        Assert.Equal("old,viager", LeboncoinSearchAdapter.SellTypes(p));
        p.NewBuild = null;
        Assert.Null(LeboncoinSearchAdapter.SellTypes(p));
    }

    [Fact]
    public void Captcha_IsRecognized()
    {
        Assert.True(SiteHttp.LooksLikeCaptcha("<html><script src=\"https://ct.captcha-delivery.com/c.js\"></script></html>"));
        Assert.False(SiteHttp.LooksLikeCaptcha(Fixture("leboncoin-recherche.html")));
    }
}
