using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Seloger;

/// <summary>
/// Annonce SeLoger (objet de classifiedList, ou classifiedsData de la 1re page) -> format commun.
/// Mêmes règles que le plugin (plugin/src/sites/seloger/parse.ts, parseSelogerSerpClassified).
/// Les photos SeLoger sont renommées (UUID) : pas d'empreintes de photos.
/// </summary>
public sealed partial class SelogerListingParser : IListingParser
{
    public CollectedItem? Parse(JsonNode raw) => ParseClassified(raw);

    public static CollectedItem? ParseClassified(JsonNode c)
    {
        var id = JsonRead.Str(c["id"]);
        if (id is null) return null;
        var raw = c["rawData"];
        var addr = c["location"]?["address"];
        var facts = JsonRead.Strings(c["hardFacts"]?["keyfacts"]);
        var rooms = JsonRead.Int(raw?["nbroom"]);
        var surface = JsonRead.Dbl(raw?["surface"]?["main"]);
        var heading = JsonRead.Str(c["hardFacts"]?["title"]);
        var description = JsonRead.Str(c["mainDescription"]?["description"]);
        var reference = JsonRead.Str(raw?["offererMarketingKey"]);
        var legal = string.Join("\n", JsonRead.Strings(c["provider"]?["agencyLegalInformations"]));
        var photos = (c["gallery"]?["images"] as JsonArray)?
            .Select(i => JsonRead.Str(i?["url"])).Where(u => u is not null).Cast<string>().Take(4).ToList();
        var otherRefs = RefExtractor.FromText(description).Where(r => !string.Equals(r, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        var data = new ListingData
        {
            Url = JsonRead.Str(c["url"]) ?? $"https://www.seloger.com/annonces/{id}",
            Title = Title(heading, rooms, surface),
            Transaction = JsonRead.Str(raw?["distributionType"]) switch
            {
                { } d when d.StartsWith("RENT", StringComparison.OrdinalIgnoreCase) => TransactionType.Rent,
                { } d when d.StartsWith("BUY", StringComparison.OrdinalIgnoreCase) || d.StartsWith("SALE", StringComparison.OrdinalIgnoreCase) => TransactionType.Buy,
                _ => null,
            },
            PropertyType = JsonRead.Str(raw?["propertyType"]) switch
            {
                "APARTMENT" or "FLAT" or "STUDIO" => "flat",
                "HOUSE" or "VILLA" => "house",
                "LOFT" => "loft",
                _ => null,
            },
            Price = JsonRead.Dec(raw?["price"]),
            Surface = surface,
            Rooms = rooms,
            Bedrooms = JsonRead.Int(raw?["nbbedroom"]),
            Floor = Floor(facts),
            Furnished = facts.Any(f => Furnished().IsMatch(f)) ? true : facts.Any(f => NotFurnished().IsMatch(f)) ? false : null,
            PostalCode = JsonRead.Str(addr?["zipCode"]),
            City = JsonRead.Str(addr?["city"]),
            District = JsonRead.Str(addr?["district"]),
            AgencyRef = reference,
            OtherRefs = otherRefs.Count > 0 ? otherRefs : null,
            AgencyName = JsonRead.Str(c["provider"]?["intermediaryCard"]?["title"]) is { } n ? Spaces().Replace(n, " ").Trim() : null,
            AgencySiren = RefExtractor.SirenFromText(legal),
            Photos = photos is { Count: > 0 } ? photos : null,
            DescriptionExcerpt = JsonRead.Excerpt(description),
            PublishedAt = JsonRead.Str(c["metadata"]?["creationDate"]),
        };
        return new CollectedItem(id, data);
    }

    /// <summary>"Appartement à louer" + 2 pièces + 45,77 m² -> "Appartement 2 pièces 45,77 m²" (même forme que Bien'ici).</summary>
    private static string? Title(string? heading, int? rooms, double? surface)
    {
        if (heading is null) return null;
        var t = ToRentOrSell().Replace(heading, "").Trim();
        if (rooms is not null) t += $" {rooms} pièce{(rooms > 1 ? "s" : "")}";
        if (surface is not null) t += $" {surface.Value.ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"))} m²";
        return t;
    }

    /// <summary>"7ème étage" -> 7, "Étage 1/1" -> 1, "Rez-de-chaussée" -> 0.</summary>
    public static int? Floor(IEnumerable<string> facts)
    {
        foreach (var f in facts)
        {
            var t = DedupScorer.NormalizeText(f);
            if (t.Contains("rez de chaussee", StringComparison.Ordinal)) return 0;
            var m = FloorBefore().Match(t);
            if (!m.Success) m = FloorAfter().Match(t);
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    [GeneratedRegex(@"\s+à\s+(louer|vendre)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ToRentOrSell();

    [GeneratedRegex(@"(\d+)\s*(?:e|er|eme|ere)?\s*etage")]
    private static partial Regex FloorBefore();

    [GeneratedRegex(@"etage\s*(\d+)")]
    private static partial Regex FloorAfter();

    [GeneratedRegex(@"^\s*meubl", RegexOptions.IgnoreCase)]
    private static partial Regex Furnished();

    [GeneratedRegex(@"non\s+meubl", RegexOptions.IgnoreCase)]
    private static partial Regex NotFurnished();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
