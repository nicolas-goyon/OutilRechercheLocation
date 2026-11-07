using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Figaro;

/// <summary>
/// Annonce de l'outil MCP "listings" de Figaro Immobilier -> format commun (relevé en novembre 2026) :
/// { area, bedrooms, city "Trie-sur-Baïse (65)", description, estateType "maison" / "maison neuve" / "ferme"…,
///   groundArea, options ["garage", "piscine", "travaux"…], photoUrls, price, rooms, title, updatedAt, url }.
/// Ni code postal, ni GPS, ni agence : le code postal est retrouvé ensuite d'après la commune (<see cref="FigaroSearchAdapter"/>).
/// </summary>
public static partial class FigaroListingParser
{
    /// <summary>Annonces et nombre total de résultats d'une réponse "listings".</summary>
    public static (List<CollectedItem> Items, int Total) ParseListings(JsonNode? content, TransactionType transaction)
    {
        var items = new List<CollectedItem>();
        foreach (var n in content?["listings"] as JsonArray ?? new JsonArray())
            if (n is not null && Parse(n, transaction) is { } item) items.Add(item);
        return (items, JsonRead.Int(content?["totalHits"]) ?? items.Count);
    }

    public static CollectedItem? Parse(JsonNode n, TransactionType transaction)
    {
        var url = JsonRead.Str(n["url"]);
        var id = url is null ? null : AdId().Match(url) is { Success: true } m ? m.Groups[1].Value : null;
        if (id is null) return null;
        var estate = JsonRead.Str(n["estateType"])?.Trim().ToLowerInvariant();
        var (type, subtype) = PropertyType(estate);
        var options = JsonRead.Strings(n["options"]).ToHashSet(StringComparer.Ordinal);
        bool? Opt(params string[] keys) => keys.Any(options.Contains) ? true : null;
        var (city, _) = SplitCity(JsonRead.Str(n["city"]));
        var description = JsonRead.Str(n["description"]);
        var photos = JsonRead.Strings(n["photoUrls"]) is { Count: > 0 } list ? list : JsonRead.Strings(n["photoUrl"]);
        return new CollectedItem(id, new ListingData
        {
            Url = url,
            Title = JsonRead.Str(n["title"]),
            Transaction = transaction,
            PropertyType = type,
            Subtype = subtype,
            Price = JsonRead.Dec(n["price"]) is { } price and > 0 ? price : null,
            Surface = JsonRead.Dbl(n["area"]) is { } area and > 0 ? area : null,
            Rooms = JsonRead.Int(n["rooms"]) is { } rooms and > 0 ? rooms : null,
            Bedrooms = JsonRead.Int(n["bedrooms"]),
            LandSurface = JsonRead.Dbl(n["groundArea"]) is { } land and > 0 ? land : null,
            City = city,
            Photos = photos.Count > 0 ? photos.Take(ListingData.MaxPhotos).ToList() : null,
            DescriptionExcerpt = JsonRead.Excerpt(description),
            OtherRefs = RefExtractor.FromText(description) is { Count: > 0 } refs ? refs : null,
            NewBuild = estate?.Contains("neuf", StringComparison.Ordinal) == true || estate?.Contains("neuve", StringComparison.Ordinal) == true ? true : null,
            WorkNeeded = Opt("travaux"),
            SaleKind = estate?.Contains("viager", StringComparison.Ordinal) == true ? SaleKinds.LifeAnnuity : null,
            Features = Features.Of(
                (Features.Garden, Opt("jardin")),
                (Features.Pool, Opt("piscine")),
                (Features.Terrace, Opt("terrasse")),
                (Features.Balcony, Opt("balcon")),
                (Features.Garage, Opt("garage", "box")),
                (Features.Parking, Opt("parking")),
                (Features.Cellar, Opt("cave")),
                (Features.Elevator, Opt("ascenseur")),
                (Features.Fireplace, Opt("cheminee")),
                (Features.AirConditioning, Opt("climatisation"))),
        });
    }

    /// <summary>"Trie-sur-Baïse (65)" -> ("Trie-sur-Baïse", "65").</summary>
    public static (string? City, string? Department) SplitCity(string? text)
    {
        if (text is null) return (null, null);
        var m = CityDepartment().Match(text);
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.ToUpperInvariant()) : (text.Trim(), null);
    }

    /// <summary>Type du site ("maison", "maison neuve", "ferme", "château"…) -> type commun et précision.</summary>
    public static (string? Type, string? Subtype) PropertyType(string? estate)
    {
        if (estate is null) return (null, null);
        var word = estate.Replace(" neuve", "", StringComparison.Ordinal).Replace(" neuf", "", StringComparison.Ordinal).Trim();
        var type = word switch
        {
            "maison" or "villa" or "propriété" or "chalet" or "moulin" or "ferme" or "manoir" or "hôtel particulier" => PropertyTypes.House,
            "château" => PropertyTypes.Castle,
            "appartement" or "chambre" or "atelier" => PropertyTypes.Flat,
            "loft" => PropertyTypes.Loft,
            "terrain" => PropertyTypes.Terrain,
            "parking" or "box" => PropertyTypes.Parking,
            "immeuble" or "bâtiment" => PropertyTypes.Building,
            "boutique" or "local commercial" or "bureau" or "locaux professionnels" => PropertyTypes.Commercial,
            _ => PropertyTypes.Other,
        };
        var subtype = word is "villa" or "propriété" or "chalet" or "moulin" or "ferme" or "manoir" or "hôtel particulier" or "atelier" or "chambre" or "péniche"
            ? char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..]
            : null;
        return (type, subtype);
    }

    /// <summary>Types communs -> types du site (outil "listings").</summary>
    public static List<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).SelectMany(t => t switch
    {
        PropertyTypes.House or PropertyTypes.TownHouse => ["maison"],
        PropertyTypes.Castle => ["château"],
        PropertyTypes.Flat => ["appartement"],
        PropertyTypes.Loft => ["loft"],
        PropertyTypes.Terrain => ["terrain"],
        PropertyTypes.Parking => ["parking", "box"],
        PropertyTypes.Building => ["immeuble"],
        PropertyTypes.Commercial => ["locaux professionnels"],
        _ => Array.Empty<string>(),
    }).Distinct().ToList();

    [GeneratedRegex(@"annonce-(\d+)\.html")]
    private static partial Regex AdId();

    [GeneratedRegex(@"^(.*)\((\d{2,3}|2[AaBb])\)\s*$")]
    private static partial Regex CityDepartment();
}
