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
            .Where(u => u is not null).Cast<string>().Take(ListingData.MaxPhotos).ToList();
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
            Transaction = JsonRead.Str(ad["adType"]) switch { "rent" => TransactionType.Rent, "buy" or "lifeAnnuitySale" => TransactionType.Buy, _ => null },
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
            LandSurface = Positive(JsonRead.Dbl(ad["landSurfaceArea"])),
            EnergyClass = EnergyClasses.Normalize(JsonRead.Str(ad["energyClassification"])),
            GhgClass = EnergyClasses.Normalize(JsonRead.Str(ad["greenhouseGazClassification"])),
            YearBuilt = JsonRead.Int(ad["yearOfConstruction"]) is { } year and > 1000 ? year : null,
            Bathrooms = Sum(JsonRead.Int(ad["bathroomsQuantity"]), JsonRead.Int(ad["showerRoomsQuantity"])),
            Heating = JsonRead.Str(ad["heating"]) is { } heating && !heating.All(char.IsDigit) ? heating : null,
            Exposure = JsonRead.Str(ad["exposition"]),
            Subtype = Subtype(JsonRead.Str(ad["propertySubtype"])),
            NewBuild = JsonRead.Bool(ad["newProperty"]),
            WorkNeeded = JsonRead.Bool(ad["workToDo"]),
            SaleKind = JsonRead.Str(ad["adType"]) == "lifeAnnuitySale" ? SaleKinds.LifeAnnuity : null,
            Features = ReadFeatures(ad),
        };
        return new CollectedItem(id, data);
    }

    /// <summary>
    /// Équipements : "hasPool", ou un nombre ("balconyQuantity" : 0 = absent). Garage : garage ou parking fermé.
    /// </summary>
    public static IReadOnlyDictionary<string, bool>? ReadFeatures(JsonNode ad)
    {
        bool? Flag(string name) => JsonRead.Bool(ad[name]);
        bool? Count(string name) => JsonRead.Int(ad[name]) is { } n ? n > 0 : null;
        static bool? Any(params bool?[] values) => values.Any(v => v == true) ? true : values.Any(v => v == false) ? false : null;
        return Features.Of(
            (Features.Garden, Flag("hasGarden")),
            (Features.Pool, Flag("hasPool")),
            (Features.Terrace, Any(Flag("hasTerrace"), Count("terracesQuantity"))),
            (Features.Balcony, Any(Flag("hasBalcony"), Count("balconyQuantity"))),
            (Features.Garage, Any(Flag("hasGarage"), Count("garagesQuantity"), Count("enclosedParkingQuantity"))),
            (Features.Parking, Count("parkingPlacesQuantity")),
            (Features.Cellar, Any(Flag("hasCellar"), Count("cellarsOrUndergroundsQuantity"))),
            (Features.Elevator, Flag("hasElevator")),
            (Features.Fireplace, Any(Flag("hasFirePlace"), Count("firePlacesQuantity"))),
            (Features.AirConditioning, Flag("hasAirConditioning")),
            (Features.Accessible, Flag("isDisabledPeopleFriendly")));
    }

    /// <summary>Sous-type de Bien'ici ("barn", seul relevé sur 1 800 annonces en novembre 2026). "unknown" : rien.</summary>
    private static string? Subtype(string? subtype) => subtype switch
    {
        null or "unknown" => null,
        "barn" => "Grange",
        _ => subtype,
    };

    private static double? Positive(double? value) => value > 0 ? value : null;

    private static int? Sum(int? a, int? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static string DefaultTitle(string? type, int? rooms, double? surface)
    {
        var label = PropertyTypes.Normalize(type) is { } common and not PropertyTypes.Other ? PropertyTypes.Label(common).Split(',')[0] : "Bien";
        if (rooms is not null) label += $" {rooms} pièce{(rooms > 1 ? "s" : "")}";
        if (surface is not null) label += $" {surface.Value.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} m²";
        return label;
    }
}
