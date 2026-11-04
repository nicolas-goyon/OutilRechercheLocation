using System.Net;
using System.Text;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Sites;

namespace RechercheLogement.Tests;

/// <summary>Niveaux de lieux sous un point (sélection à vue), avec des réponses de geo.api.gouv.fr simulées.</summary>
public class GeoLevelsTests
{
    private sealed class Fake(Func<string, string?> answer) : DelegatingHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = answer(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Bagneres = """
        { "nom": "Bagnères", "code": "65059", "codesPostaux": ["65200", "65710"], "codeDepartement": "65", "codeRegion": "76",
          "centre": { "type": "Point", "coordinates": [0.15, 43.06] } }
        """;

    private static readonly GeoPoint Point = new(43.06, 0.15);

    private static string? Answer(string path) => path switch
    {
        _ when path.StartsWith("/communes?lat=") => $"[{Bagneres}]",
        _ when path.StartsWith("/communes?codePostal=65710") => $"[{Bagneres}]",
        _ when path.StartsWith("/geocodage/reverse") => """{ "features": [{ "properties": { "postcode": "65710" } }] }""",
        _ when path.StartsWith("/departements/65") => """{ "nom": "Hautes-Pyrénées", "code": "65" }""",
        _ when path.StartsWith("/regions/76/departements") => """[{ "nom": "Ariège", "code": "09" }, { "nom": "Hautes-Pyrénées", "code": "65" }]""",
        _ when path.StartsWith("/regions/76") => """{ "nom": "Occitanie", "code": "76" }""",
        _ => null,
    };

    [Fact]
    public async Task Levels_GoFromPostalCodeToRegion()
    {
        var levels = await new GeoServices(new Fake(Answer)).PlaceLevelsAtAsync(Point, default);

        Assert.Equal(new[] { PlaceType.PostalCode, PlaceType.Commune, PlaceType.Department, PlaceType.Region }, levels.Select(l => l.Type));
        Assert.Equal("65710", levels[0].Code);
        Assert.Equal(new[] { "Ariège", "Hautes-Pyrénées" }, levels[3].Departments);
    }

    [Fact]
    public async Task Levels_FallBackToFirstPostalCode_WhenAddressIsUnavailable()
    {
        var fake = new Fake(p => p.StartsWith("/geocodage") ? null : p.StartsWith("/communes?codePostal=65200") ? $"[{Bagneres}]" : Answer(p));

        var levels = await new GeoServices(fake).PlaceLevelsAtAsync(Point, default);

        Assert.Equal("65200", levels[0].Code);
    }

    [Fact]
    public async Task Levels_SkipWhatIsUnavailable()
    {
        var fake = new Fake(p => p.StartsWith("/regions") ? null : Answer(p));

        var levels = await new GeoServices(fake).PlaceLevelsAtAsync(Point, default);

        Assert.Equal(PlaceType.Department, levels[^1].Type);
    }

    [Fact]
    public async Task Levels_AreEmptyOutsideFrance()
    {
        var levels = await new GeoServices(new Fake(_ => "[]")).PlaceLevelsAtAsync(new GeoPoint(42.5, 1.5), default);
        Assert.Empty(levels);
    }

    [Fact]
    public void Region_IsSearchedDepartmentByDepartment()
    {
        var project = new SearchProject
        {
            Id = "p",
            Locations =
            [
                new ProjectLocation { Type = PlaceType.Region, Query = "Occitanie", Code = "76", Departments = ["Ariège", "Hautes-Pyrénées"] },
                new ProjectLocation { Type = PlaceType.PostalCode, Query = "65170", Code = "65170" },
            ],
        };

        var (queries, _) = ProjectRunner.PlaceQueries(project, PlaceKinds.PostalCode | PlaceKinds.Department);

        Assert.Equal(new[] { "dep:Ariège", "dep:Hautes-Pyrénées", "cp:65170" }, queries.Select(q => q.CacheKey));
    }
}
