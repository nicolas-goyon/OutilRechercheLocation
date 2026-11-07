using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Leboncoin;

/// <summary>
/// Annonce Leboncoin (objet de props.pageProps.searchData.ads, ou props.pageProps.ad sur la fiche) -> format commun.
/// Les caractéristiques sont dans "attributes" : { key, value, values[], value_label } (relevé en novembre 2026).
/// La liste n'a pas la description ("body" vide) : seule la fiche la donne.
/// Mêmes règles que le plugin (plugin/src/sites/leboncoin/parse.ts).
/// </summary>
public sealed partial class LeboncoinListingParser : IListingParser
{
    public const string Origin = "https://www.leboncoin.fr";

    public CollectedItem? Parse(JsonNode raw) => ParseAd(raw);

    /// <summary>Annonces de la page de résultats (JSON __NEXT_DATA__). Page sans ce JSON : null.</summary>
    public static JsonNode? SearchData(string html) => NextData(html)?["props"]?["pageProps"]?["searchData"];

    /// <summary>JSON __NEXT_DATA__ d'une page Leboncoin (page de résultats ou fiche).</summary>
    public static JsonNode? NextData(string html)
    {
        var m = NextDataRegex().Match(html);
        return m.Success ? JsonNode.Parse(m.Groups[1].Value) : null;
    }

    public static CollectedItem? ParseAd(JsonNode ad)
    {
        var id = JsonRead.Str(ad["list_id"]) ?? (JsonRead.Dbl(ad["list_id"]) is { } n ? n.ToString("0", CultureInfo.InvariantCulture) : null);
        if (id is null) return null;
        var a = Attributes(ad);
        string? Value(string key) => a.TryGetValue(key, out var x) ? x.Value : null;
        string? Label(string key) => a.TryGetValue(key, out var x) ? x.Label : null;
        IReadOnlyList<string> Values(string key) => a.TryGetValue(key, out var x) ? x.Values : [];
        int? Int(string key) => int.TryParse(Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        double? Dbl(string key) => double.TryParse(Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

        var location = ad["location"];
        var lat = JsonRead.Dbl(location?["lat"]);
        var lon = JsonRead.Dbl(location?["lng"]);
        var owner = ad["owner"];
        var description = JsonRead.Str(ad["body"]);
        var reference = Value("custom_ref");
        var images = ad["images"];
        var photos = JsonRead.Strings(images?["urls_large"]) is { Count: > 0 } large ? large : JsonRead.Strings(images?["urls"]);
        var outside = Values("outside_access");
        var specific = Values("specificities");
        var sellType = Value("immo_sell_type");
        var bathrooms = Int("nb_bathrooms");
        var showers = Int("nb_shower_room");
        var otherRefs = RefExtractor.FromText(description).Where(r => !string.Equals(r, reference, StringComparison.OrdinalIgnoreCase)).ToList();

        var data = new ListingData
        {
            Url = JsonRead.Str(ad["url"]) ?? $"{Origin}/ad/ventes_immobilieres/{id}",
            Title = JsonRead.Str(ad["subject"]),
            Transaction = JsonRead.Str(ad["category_id"]) switch { "9" => TransactionType.Buy, "10" => TransactionType.Rent, _ => null },
            PropertyType = PropertyType(Value("real_estate_type")),
            Price = JsonRead.Dec(ad["price"]),
            Surface = Dbl("square"),
            Rooms = Int("rooms"),
            Bedrooms = Int("bedrooms"),
            Floor = Int("floor_number"),
            Furnished = Value("furnished") switch { "1" => true, "2" => false, _ => null },
            PostalCode = JsonRead.Str(location?["zipcode"]),
            City = JsonRead.Str(location?["city"]),
            // Point de la commune ("city") ou de l'adresse : la précision change.
            Geo = lat is not null && lon is not null ? new GeoPoint(lat.Value, lon.Value, JsonRead.Str(location?["source"]) == "city" ? 1500 : 150) : null,
            AgencyRef = reference,
            OtherRefs = otherRefs.Count > 0 ? otherRefs : null,
            AgencyName = JsonRead.Str(owner?["type"]) == "pro" ? JsonRead.Str(owner?["name"]) ?? Value("store_name") : null,
            AgencySiren = RefExtractor.NormalizeSiren(JsonRead.Str(owner?["siren"])),
            Photos = photos.Count > 0 ? photos.Take(ListingData.MaxPhotos).ToList() : null,
            DescriptionExcerpt = JsonRead.Excerpt(description),
            PublishedAt = PublishedAt(JsonRead.Str(ad["first_publication_date"])),
            LandSurface = Dbl("land_plot_surface"),
            EnergyClass = EnergyClasses.Normalize(Value("energy_rate")),
            GhgClass = EnergyClasses.Normalize(Value("ges")),
            YearBuilt = Int("building_year") is { } year and > 1000 ? year : null,
            Bathrooms = bathrooms is null && showers is null ? null : (bathrooms ?? 0) + (showers ?? 0),
            Heating = string.Join(", ", new[] { Label("heating_mode"), Label("heating_type") }.OfType<string>()) is { Length: > 0 } heating ? heating : null,
            Exposure = Label("orientation"),
            Subtype = Label("real_estate_type_specificities") is { } sub and not "Autre" ? sub : null,
            NewBuild = sellType switch { "new" => true, "old" => false, _ => null },
            WorkNeeded = Value("global_condition") switch { "4" or "5" => true, "1" or "2" or "3" => false, _ => null },
            SaleKind = sellType == "viager" ? SaleKinds.LifeAnnuity : null,
            PropertyTax = Int("property_tax") is { } tax and > 0 ? tax : null,
            Features = Features.Of(
                (Features.Garden, outside.Contains("garden") ? true : null),
                (Features.Pool, outside.Contains("pool") ? true : null),
                (Features.Terrace, outside.Contains("terrace") ? true : null),
                (Features.Balcony, outside.Contains("balcony") ? true : null),
                (Features.Cellar, specific.Contains("cellar") ? true : null),
                (Features.AirConditioning, specific.Contains("air_conditioned") ? true : null),
                (Features.Parking, specific.Contains("with_garage_or_parking_spot") || Int("nb_parkings") > 0 ? true : null),
                (Features.Elevator, Value("elevator") switch { "1" => true, "2" => false, _ => null })),
        };
        return new CollectedItem(id, data);
    }

    /// <summary>"1" maison, "2" appartement, "3" terrain, "4" parking, "5" autre -> types communs.</summary>
    public static string? PropertyType(string? realEstateType) => realEstateType switch
    {
        "1" => PropertyTypes.House,
        "2" => PropertyTypes.Flat,
        "3" => PropertyTypes.Terrain,
        "4" => PropertyTypes.Parking,
        "5" => PropertyTypes.Other,
        _ => null,
    };

    /// <summary>"2026-11-06 01:14:06" (heure de Paris) -> "2026-11-06T01:14:06".</summary>
    private static string? PublishedAt(string? date) => date?.Replace(' ', 'T');

    private sealed record Attribute(string? Value, IReadOnlyList<string> Values, string? Label);

    private static Dictionary<string, Attribute> Attributes(JsonNode ad)
    {
        var map = new Dictionary<string, Attribute>(StringComparer.Ordinal);
        foreach (var at in ad["attributes"] as JsonArray ?? new JsonArray())
        {
            if (JsonRead.Str(at?["key"]) is not { } key) continue;
            map.TryAdd(key, new Attribute(JsonRead.Str(at?["value"]), JsonRead.Strings(at?["values"]), JsonRead.Str(at?["value_label"])));
        }
        return map;
    }

    [GeneratedRegex("""<script id="__NEXT_DATA__"[^>]*>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();
}
