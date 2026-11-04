using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Common;
using RechercheLogement.Server.Sites;

namespace RechercheLogement.Server.Geo;

/// <summary>
/// Contours des lieux, pour la carte. Départements et régions de métropole : fichiers embarqués.
/// Communes, codes postaux (réunion de leurs communes) et outre-mer : geo.api.gouv.fr, gardé en mémoire.
/// Voir ARCHITECTURE.md, « Contours des lieux ».
/// </summary>
public sealed partial class GeoContours(IHttpClientFactory factory)
{
    private const string Fields = "format=geojson&geometry=contour&fields=nom,code";

    private static readonly Lazy<Dictionary<string, JsonNode>> Departments = new(() => LoadEmbedded("departements.geojson"));
    private static readonly Lazy<Dictionary<string, JsonNode>> Regions = new(() => LoadEmbedded("regions.geojson"));

    private readonly ConcurrentDictionary<string, string> _cache = new();

    /// <summary>Adresse du contour d'un lieu, pour le navigateur.</summary>
    public static string Url(PlaceType type, string code) =>
        $"/geo/contours/{type.ToString().ToLowerInvariant()}/{Uri.EscapeDataString(code)}";

    public static bool ValidCode(string code) => CodeRegex().IsMatch(code);

    /// <summary>GeoJSON (FeatureCollection) du lieu. null : pas de contour pour ce lieu.</summary>
    public async Task<string?> GetAsync(PlaceType type, string code, CancellationToken ct)
    {
        code = code.ToUpperInvariant();
        switch (type)
        {
            case PlaceType.Department when Departments.Value.TryGetValue(code, out var d):
                return Collection(d);
            case PlaceType.Region:
                return Regions.Value.TryGetValue(code, out var r) ? Collection(r) : null;
        }
        var path = type switch
        {
            PlaceType.Commune => $"communes?code={code}&{Fields}",
            PlaceType.PostalCode => $"communes?codePostal={code}&{Fields}",
            PlaceType.Department => $"departements/{code}/communes?{Fields}",
            _ => null,
        };
        if (path is null) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        var http = factory.CreateClient(SiteHttp.ClientName);
        var json = await SiteHttp.GetJsonAsync(http, $"https://geo.api.gouv.fr/{path}", "geo.api.gouv.fr", ct);
        if (json["features"] is not JsonArray { Count: > 0 }) return null;
        return _cache[path] = json.ToJsonString();
    }

    private static string Collection(JsonNode feature) =>
        new JsonObject { ["type"] = "FeatureCollection", ["features"] = new JsonArray(feature.DeepClone()) }.ToJsonString();

    private static Dictionary<string, JsonNode> LoadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Ressource manquante : {name}");
        var features = JsonNode.Parse(stream)?["features"] as JsonArray ?? [];
        return features.Where(f => f is not null).ToDictionary(f => JsonRead.Str(f!["properties"]?["code"])!, f => f!);
    }

    [GeneratedRegex("^[0-9A-Za-z]{1,5}$")]
    private static partial Regex CodeRegex();
}

public static class GeoContourEndpoints
{
    /// <summary>Route lue par la carte du navigateur. Pas de token, comme les pages web du serveur local.</summary>
    public static void MapContours(this WebApplication app) =>
        app.MapGet("/geo/contours/{type}/{code}", async (string type, string code, GeoContours contours, CancellationToken ct) =>
        {
            if (!Enum.TryParse<PlaceType>(type, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || !GeoContours.ValidCode(code))
                return Results.BadRequest();
            try
            {
                return await contours.GetAsync(kind, code, ct) is { } json ? Results.Content(json, "application/geo+json") : Results.NotFound();
            }
            catch (SiteException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
}
