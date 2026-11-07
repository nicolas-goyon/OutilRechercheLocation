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
            .Select(i => JsonRead.Str(i?["url"])).Where(u => u is not null).Cast<string>().Take(ListingData.MaxPhotos).ToList();
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
            PropertyType = PropertyTypes.Normalize(JsonRead.Str(raw?["propertyType"])),
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
            LandSurface = JsonRead.Dbl(raw?["surface"]?["plot"]) ?? FactNumber(c["hardFacts"]?["facts"], "plotSpace"),
            EnergyClass = EnergyClasses.Normalize(JsonRead.Str(c["energyClass"]) ?? JsonRead.Str(c["legacyTracking"]?["energy_letter"])),
            YearBuilt = JsonRead.Int(c["legacyTracking"]?["year_of_construction"]) is { } year and > 1000 ? year : null,
            Subtype = Subtype(JsonRead.Str(raw?["propertySubType"])),
            NewBuild = NewBuild(heading, JsonRead.Str(raw?["distributionSubType"]?["buy"])),
            SaleKind = heading is not null && heading.Contains("viager", StringComparison.OrdinalIgnoreCase) ? SaleKinds.LifeAnnuity : null,
        };
        return new CollectedItem(id, data);
    }

    /// <summary>
    /// Neuf : titre "Maison à vendre • neuf" (liste) ou "NEW_BUILD_UNIT" (fiche). Titre sans "neuf" : ancien.
    /// (tags.isNew veut dire "annonce récente", pas "logement neuf".)
    /// </summary>
    public static bool? NewBuild(string? heading, string? subType) => subType switch
    {
        "NEW_BUILD_UNIT" or "NEW_BUILD_PROJECT" => true,
        "RESALE" => false,
        _ when heading is null => null,
        _ => NewWord().IsMatch(heading),
    };

    /// <summary>Valeur numérique d'un fait de hardFacts.facts ("plotSpace" : "750 m² de terrain" -> 750).</summary>
    private static double? FactNumber(JsonNode? facts, string type)
    {
        var fact = (facts as JsonArray)?.FirstOrDefault(f => JsonRead.Str(f?["type"]) == type);
        var text = JsonRead.Str(fact?["splitValue"]) ?? JsonRead.Str(fact?["value"]);
        var m = text is null ? null : Number().Match(text.Replace("\u00a0", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal));
        return m is { Success: true } && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Sous-types de SeLoger (valeurs de son code, novembre 2026) -> libellé.</summary>
    public static string? Subtype(string? subtype) => subtype?.ToUpperInvariant() switch
    {
        null => null,
        "VILLA" => "Villa",
        "CHALET" => "Chalet",
        "BUNGALOW" => "Bungalow",
        "FARMHOUSE" => "Ferme",
        "CASTLE" => "Château",
        "MANOR_HOUSE" => "Manoir",
        "MANSION" => "Demeure",
        "TOWN_HOUSE" => "Maison de ville",
        "TERRACE_HOUSE" or "SEMIDETACHED_HOUSE" => "Maison mitoyenne",
        "SINGLE_FAMILY_HOUSE" => "Maison individuelle",
        "MULTI_FAMILY_HOUSE" => "Maison plurifamiliale",
        "LOFT" => "Loft",
        "PENTHOUSE" => "Dernier étage",
        "STUDIO" => "Studio",
        "MULTI_STOREY" => "Duplex",
        "TERRACE" => "Appartement avec terrasse",
        _ => char.ToUpperInvariant(subtype[0]) + subtype[1..].ToLowerInvariant().Replace('_', ' '),
    };

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

    [GeneratedRegex(@"\bneu(f|ve)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewWord();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex Number();
}
