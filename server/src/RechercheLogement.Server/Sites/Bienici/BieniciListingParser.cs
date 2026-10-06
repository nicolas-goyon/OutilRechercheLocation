using System.Globalization;
using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Bienici;

/// <summary>
/// Annonce Bien'ici (objet de realEstateAds.json ou realEstateAd.json) -> format commun.
/// Mêmes règles que le plugin (plugin/src/sites/bienici/parse.ts, parseBieniciApiAd).
/// </summary>
public sealed class BieniciListingParser : IListingParser
{
    public CollectedItem? Parse(JsonNode raw) => ParseAd(raw);

    public static CollectedItem? ParseAd(JsonNode ad)
    {
        var id = JsonRead.Str(ad["id"]);
        if (id is null) return null;
        var pos = ad["blurInfo"]?["position"];
        var lat = JsonRead.Dbl(pos?["lat"]);
        var lon = JsonRead.Dbl(pos?["lon"]);
        var photos = (ad["photos"] as JsonArray)?
            .Select(p => JsonRead.Str(p?["url"]) ?? JsonRead.Str(p?["url_photo"]))
            .Where(u => u is not null).Cast<string>().Take(4).ToList();
        var rooms = JsonRead.Int(ad["roomsQuantity"]);
        var surface = JsonRead.Dbl(ad["surfaceArea"]);
        var type = JsonRead.Str(ad["propertyType"]);
        var description = JsonRead.Str(ad["description"]);
        var reference = JsonRead.Str(ad["reference"]);
        var contact = ad["contactRelativeData"];
        var otherRefs = RefExtractor.FromText(description).Where(r => !string.Equals(r, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        var data = new ListingData
        {
            Url = $"https://www.bienici.com/annonce/{id}",
            Title = JsonRead.Str(ad["title"]) ?? DefaultTitle(type, rooms, surface),
            Transaction = JsonRead.Str(ad["adType"]) switch { "rent" => TransactionType.Rent, "buy" => TransactionType.Buy, _ => null },
            PropertyType = type,
            Price = JsonRead.Dec(ad["price"]),
            Charges = JsonRead.Dec(ad["charges"]),
            Surface = surface,
            Rooms = rooms,
            Bedrooms = JsonRead.Int(ad["bedroomsQuantity"]),
            Floor = JsonRead.Int(ad["floor"]),
            Furnished = JsonRead.Bool(ad["isFurnished"]),
            PostalCode = JsonRead.Str(ad["postalCode"]),
            City = JsonRead.Str(ad["city"]),
            District = JsonRead.Str(ad["district"]?["libelle"]) ?? JsonRead.Str(ad["district"]?["name"]),
            Geo = lat is not null && lon is not null ? new GeoPoint(lat.Value, lon.Value, JsonRead.Dbl(ad["blurInfo"]?["radius"])) : null,
            AgencyRef = reference,
            OtherRefs = otherRefs.Count > 0 ? otherRefs : null,
            AgencyName = JsonRead.Str(contact?["agencyNameToDisplay"]) ?? JsonRead.Str(ad["accountDisplayName"]),
            AgencySiren = RefExtractor.NormalizeSiren(JsonRead.Str(contact?["rcs"])),
            Photos = photos is { Count: > 0 } ? photos : null,
            DescriptionExcerpt = JsonRead.Excerpt(description),
            PublishedAt = JsonRead.Str(ad["publicationDate"]),
        };
        return new CollectedItem(id, data);
    }

    private static string DefaultTitle(string? type, int? rooms, double? surface)
    {
        var label = type switch { "house" => "Maison", "flat" => "Appartement", "loft" => "Loft", _ => "Bien" };
        if (rooms is not null) label += $" {rooms} pièce{(rooms > 1 ? "s" : "")}";
        if (surface is not null) label += $" {surface.Value.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} m²";
        return label;
    }
}
