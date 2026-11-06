using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;

namespace RechercheLogement.LiveTests;

/// <summary>API géographiques publiques : geo.api.gouv.fr (communes, contours) et Géoplateforme IGN (adresses, isochrones).</summary>
public class GeoLiveTests
{
    private static readonly GeoPoint SaintLary = new(42.8170, 0.3220);
    private readonly GeoServices _geo = new(LiveHttp.Factory);

    [Fact]
    public async Task Suggestions_FindCommunesAndDepartments()
    {
        var places = await LiveHttp.Retry(() => _geo.SuggestPlacesAsync("Hautes-Pyr", default));
        Assert.Contains(places, p => p.Location?.Type == PlaceType.Department && p.Location.Code == "65");
    }

    [Fact]
    public async Task Levels_UnderAPoint_GoUpToTheRegion()
    {
        var levels = await LiveHttp.Retry(() => _geo.PlaceLevelsAtAsync(SaintLary, default));

        Assert.Equal(new[] { PlaceType.PostalCode, PlaceType.Commune, PlaceType.Department, PlaceType.Region }, levels.Select(l => l.Type));
        Assert.Equal("65170", levels[0].Code);
        Assert.Equal("76", levels[3].Code);
    }

    [Fact]
    public async Task Communes_OfADepartment_HaveCenters()
    {
        var communes = await LiveHttp.Retry(() => _geo.CommunesAsync("65", default));
        Assert.True(communes.Count > 400, $"{communes.Count} communes");
        Assert.All(communes, c => Assert.NotEqual(0, c.Center.Lat));
    }

    [Fact]
    public async Task Contours_OfACommune_AreGeoJson()
    {
        var json = await LiveHttp.Retry(() => new GeoContours(LiveHttp.Factory).GetAsync(PlaceType.Commune, "65388", default));
        Assert.Contains("Polygon\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Addresses_AreGeocoded()
    {
        var place = await LiveHttp.Retry(() => _geo.GeocodeAsync("Mairie, 65170 Saint-Lary-Soulan", default));
        Assert.NotNull(place);
    }

    [Fact]
    public async Task Isochrone_IsAPolygonAroundThePoint()
    {
        var shape = await LiveHttp.Retry(() => _geo.IsochroneAsync(SaintLary, 15, TravelMode.Car, default));
        Assert.True(shape.Contains(SaintLary));
    }
}
