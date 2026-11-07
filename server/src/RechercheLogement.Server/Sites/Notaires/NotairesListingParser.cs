using System.Globalization;
using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Notaires;

/// <summary>Annonce de la liste (annonceResumeDto).</summary>
/// <param name="Id">"annonceId" : identifiant de la fiche et de l'adresse de l'annonce.</param>
/// <param name="Crpcen">Numéro de l'office notarial (sert à lire son nom et son SIRET).</param>
/// <param name="AuctionClosed">Vente Immo-Interactif terminée, encore listée par le site ("isVniClose").</param>
public sealed record NotairesCard(string Id, ListingData Data, string? Crpcen, bool AuctionClosed);

/// <summary>Nom et SIREN d'un office notarial (/annonces/{id}/office).</summary>
public sealed record NotairesOffice(string? Name, string? Siren);

/// <summary>
/// API JSON de www.immobilier.notaires.fr (pub-services, relevée en novembre 2026) -> format commun.
/// La liste donne l'essentiel (prix, surface, lieu, une photo, description tronquée) ; la fiche complète le reste
/// (description, photos, DPE, époque, équipements...). Valeurs non renseignées : "INCONNU" (ignorées), oui/non :
/// "OUI"/"NON". Pas de position du bien : Geo reste vide. Fonctions sans effet de bord (Fixtures/notaires-*.json).
/// </summary>
public static class NotairesListingParser
{
    public const string Origin = "https://www.immobilier.notaires.fr";

    /// <summary>Page de la liste : annonces, nombre total d'annonces et de pages.</summary>
    public static (List<NotairesCard> Cards, int Total, int Pages) ParseList(JsonNode json)
    {
        var cards = (json["annonceResumeDto"] as JsonArray ?? [])
            .Select(a => a is null ? null : ParseCard(a)).OfType<NotairesCard>().ToList();
        return (cards, JsonRead.Int(json["nbTotalAnnonces"]) ?? cards.Count, JsonRead.Int(json["nbPages"]) ?? 1);
    }

    public static NotairesCard? ParseCard(JsonNode ad)
    {
        var id = JsonRead.Int(ad["annonceId"])?.ToString(CultureInfo.InvariantCulture) ?? Text(ad["annonceId"]);
        if (id is null) return null;
        var transaction = Text(ad["typeTransaction"]);
        var type = TypeOf(Text(ad["typeBien"]));
        var surface = Positive(JsonRead.Dbl(ad["surface"]));
        var city = Text(ad["communeNom"]) ?? Text(ad["localiteNom"]);
        var description = Text(ad["descriptionFr"]);
        var reference = Text(ad["reference"]);
        var photo = Text(ad["urlPhotoPrincipale"]);
        var data = new ListingData
        {
            Url = Text(ad["urlDetailAnnonceFr"]) ?? $"{Origin}/fr/annonce-immo/{id}",
            Title = DefaultTitle(type, surface, city),
            Transaction = TransactionOf(transaction),
            PropertyType = type,
            // Prix payé par l'acquéreur (honoraires de négociation compris), comme sur les portails. Location : loyer
            // charges comprises ; les charges sont à part dans "montantCharges".
            Price = Positive(JsonRead.Dec(ad["prixTotal"])) ?? Positive(JsonRead.Dec(ad["prixAffiche"])),
            Charges = Positive(JsonRead.Dec(ad["montantCharges"])),
            Surface = surface,
            Rooms = Positive(JsonRead.Int(ad["nbPieces"])),
            Bedrooms = Positive(JsonRead.Int(ad["nbChambres"])),
            PostalCode = Text(ad["codePostal"]),
            City = city,
            District = Text(ad["quartierNom"]),
            AgencyRef = reference,
            OtherRefs = OtherRefs(reference, null, description),
            Photos = photo is null ? null : [photo],
            DescriptionExcerpt = JsonRead.Excerpt(description),
            PublishedAt = Text(ad["dateCreation"]),
            LandSurface = Positive(JsonRead.Dbl(ad["surfaceTerrain"])),
            NewBuild = NewBuild(Text(ad["ancienNeuf"])),
            SaleKind = SaleKind(transaction, Text(ad["viager"])),
        };
        return new NotairesCard(id, data, Text(ad["crpcen"]), JsonRead.Bool(ad["isVniClose"]) == true);
    }

    /// <summary>
    /// Fiche /annonces/{id} : complète l'annonce de la liste. Le bloc de la transaction porte son nom en minuscules
    /// ("vente", "vni", "location", "vae") ; le bien est le premier sous-objet de "bien" ("maison", "appartement"...).
    /// </summary>
    public static ListingData ParseDetail(JsonNode detail, ListingData card)
    {
        var transaction = Text(detail["typeTransaction"]);
        var block = transaction is null ? null : detail[transaction.ToLowerInvariant()];
        var bien = (detail["bien"] as JsonObject)?.Select(kv => kv.Value).OfType<JsonObject>().FirstOrDefault();
        var type = TypeOf(Text(detail["bien"]?["typeBien"]) ?? Text(bien?["typeBien"])) ?? card.PropertyType;
        var description = (block?["descriptions"] as JsonArray)?.FirstOrDefault(d => Text(d?["langue"]) == "fr") ?? (block?["descriptions"] as JsonArray)?.FirstOrDefault();
        var longText = Text(description?["descLongue"]);
        var reference = Text(block?["reference"]) ?? card.AgencyRef;
        var surface = Positive(JsonRead.Dbl(bien?["surfaceHabitable"])) ?? Positive(JsonRead.Dbl(bien?["surfaceCarrez"]));
        var city = Text(bien?["communeNom"]) ?? card.City;
        var photos = Photos(block?["multimedias"]);
        var incoming = new ListingData
        {
            Title = Text(description?["descCourte"]) ?? DefaultTitle(type, surface ?? card.Surface, city),
            Transaction = TransactionOf(transaction),
            PropertyType = type,
            Charges = Positive(JsonRead.Dec(block?["montantCharges"])),
            Surface = surface,
            Rooms = Positive(JsonRead.Int(bien?["nbPieces"])),
            Bedrooms = Positive(JsonRead.Int(bien?["nbChambres"])),
            Floor = JsonRead.Int(bien?["etage"]),
            Furnished = Flag(block?["meuble"]),
            PostalCode = Text(bien?["codePostal"]),
            City = city,
            District = Text(bien?["quartierNom"]),
            AgencyRef = reference,
            OtherRefs = OtherRefs(reference, Text(block?["referencePartenaire"]), longText),
            Photos = photos.Count > 0 ? photos : null,
            DescriptionExcerpt = JsonRead.Excerpt(longText),
            PublishedAt = Text(block?["dateCreation"]),
            LandSurface = Positive(JsonRead.Dbl(bien?["surfaceTerrain"])),
            // "Y" : DPE vierge ou non disponible (EnergyClasses.Normalize ne garde que A à G).
            EnergyClass = EnergyClasses.Normalize(Text(bien?["consommationClasse"])),
            GhgClass = EnergyClasses.Normalize(Text(bien?["emissionGesClasse"])),
            YearBuilt = YearOf(Text(bien?["epoqueConstruction"])),
            Bathrooms = Positive(JsonRead.Int(bien?["nbSdb"])),
            Heating = Heating(Text(bien?["chauffage"]), Text(bien?["energie"])),
            Exposure = Exposure(Text(bien?["exposition"])),
            Subtype = SubtypeLabel(Text(detail["bien"]?["typeBien"]) ?? Text(bien?["typeBien"]), Text(bien?["sousType"])),
            NewBuild = NewBuild(Text(bien?["ancienNeuf"])),
            WorkNeeded = WorkNeeded(Text(bien?["etat"])),
            SaleKind = SaleKind(transaction, Text(block?["viager"]?["viager"])),
            PropertyTax = Positive(JsonRead.Dec(bien?["taxeFonciere"])),
            Features = bien is null ? null : ReadFeatures(bien),
        };
        // La fiche l'emporte (texte complet, photos en grand) ; le prix reste celui de la liste (honoraires compris).
        return card.Merge(incoming, fillOnly: false) with
        {
            Price = card.Price ?? Positive(JsonRead.Dec(block?["prix"])) ?? Positive(JsonRead.Dec(block?["loyer"])),
        };
    }

    /// <summary>Office notarial : dénomination ("SELARL EMILIE BAREILLE") et SIREN (9 premiers chiffres du SIRET).</summary>
    public static NotairesOffice ParseOffice(JsonNode office) =>
        new(Text(office["denominationSociale"]) ?? Text(office["intitule"]), RefExtractor.NormalizeSiren(Text(office["abonnement"]?["siret"])));

    /// <summary>Équipements du bien (OUI / NON / INCONNU). Garage : box fermé ; parking : stationnement.</summary>
    public static IReadOnlyDictionary<string, bool>? ReadFeatures(JsonNode bien)
    {
        bool? F(string name) => Flag(bien[name]);
        var spaces = JsonRead.Int(bien["nbStationnements"]);
        return Features.Of(
            (Features.Garden, F("jardin")),
            (Features.Pool, F("piscine")),
            (Features.Terrace, F("terrasse")),
            (Features.Balcony, F("balcon")),
            (Features.Garage, F("boxFerme")),
            (Features.Parking, F("stationnement") ?? (spaces is null ? null : spaces > 0)),
            (Features.Cellar, F("cave")),
            (Features.Elevator, F("ascenseur")),
            (Features.Fireplace, F("cheminee")),
            (Features.AirConditioning, F("climatisation")),
            (Features.Accessible, F("accesHandicapes")));
    }

    /// <summary>Photos de la fiche (images seulement), la photo principale d'abord, dans la meilleure définition.</summary>
    private static List<string> Photos(JsonNode? multimedias) =>
        (multimedias as JsonArray ?? [])
            .Where(m => Text(m?["type"])?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(m => Text(m?["photoPrincipale"]) == "OUI" ? 0 : 1)
            .Select(m => Text(m?["urlHighestResolution"]) ?? Text(m?["xga"]?["url"]) ?? Text(m?["vga"]?["url"]))
            .OfType<string>().Distinct().Take(ListingData.MaxPhotos).ToList();

    /// <summary>Texte renseigné : "INCONNU" (valeur par défaut du site) et vide -> null.</summary>
    public static string? Text(JsonNode? n) => JsonRead.Str(n) is { } s && !string.Equals(s, "INCONNU", StringComparison.Ordinal) ? s.Trim() : null;

    /// <summary>"OUI" -> true, "NON" -> false, autre chose ("INCONNU", absent) -> null.</summary>
    public static bool? Flag(JsonNode? n) => Text(n) switch
    {
        "OUI" => true,
        "NON" => false,
        _ => null,
    };

    public static TransactionType? TransactionOf(string? code) => code switch
    {
        "VENTE" or "VNI" or "VAE" => TransactionType.Buy,
        "LOCATION" => TransactionType.Rent,
        _ => null,
    };

    /// <summary>
    /// Code du type de bien -> type commun. Toutes les maisons restent "house" (même une maison de ville : le
    /// sous-type le précise), sinon un projet "maison" écarterait les maisons de ville que le site lui renvoie.
    /// </summary>
    public static string? TypeOf(string? code) => code switch
    {
        "MAI" => PropertyTypes.House,
        "APP" => PropertyTypes.Flat,
        "TER" => PropertyTypes.Terrain,
        "IMM" => PropertyTypes.Building,
        "GAR" => PropertyTypes.Parking,
        "LAC" or "COM" => PropertyTypes.Commercial,
        "DIV" or "AGR" or "VIG" or "AUT" => PropertyTypes.Other,
        _ => null,
    };

    /// <summary>Viager (toutes transactions), Immo-Interactif (appel d'offres en ligne), enchères.</summary>
    public static string? SaleKind(string? transaction, string? viager) =>
        viager == "OUI" ? SaleKinds.LifeAnnuity : transaction switch
        {
            "VNI" => SaleKinds.Tender,
            "VAE" => SaleKinds.Auction,
            _ => null,
        };

    private static bool? NewBuild(string? value) => value switch
    {
        "NEUF" => true,
        "ANCIEN" => false,
        _ => null,
    };

    /// <summary>État : "BON" -> pas de travaux ; à rénover ou à rafraîchir -> travaux.</summary>
    public static bool? WorkNeeded(string? etat) => etat?.ToUpperInvariant() switch
    {
        null => null,
        "BON" or "TRES_BON" or "EXCELLENT" or "NEUF" => false,
        var e when e.Contains("RENOV", StringComparison.Ordinal) || e.Contains("RAFRAICH", StringComparison.Ordinal) => true,
        _ => null,
    };

    /// <summary>
    /// Époque de construction (lettre, libellés du site : I18n_inotr_www_fr.json) -> année représentative de la
    /// période. "Z" (inconnue) et autre chose -> null.
    /// </summary>
    public static int? YearOf(string? epoque) => epoque switch
    {
        "A" => 1800, // avant 1850
        "B" => 1880, // 1850 à 1913
        "C" => 1930, // 1914 à 1947
        "D" => 1958, // 1948 à 1969
        "E" => 1975, // 1970 à 1980
        "F" => 1986, // 1981 à 1991
        "G" => 1996, // 1992 à 2000
        "H" => 2005, // 2001 à 2010
        "I" => 2015, // 2011 à 2020
        "J" => 2022, // après 2020
        _ => null,
    };

    /// <summary>Chauffage ("INDIVIDUEL") et énergie ("GAZ") -> "individuel, gaz".</summary>
    public static string? Heating(string? chauffage, string? energie)
    {
        var parts = new[] { chauffage, energie }.OfType<string>().Select(s => s.Replace('_', ' ').ToLowerInvariant()).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? Exposure(string? code) => code switch
    {
        "N" => "nord",
        "NE" => "nord-est",
        "E" => "est",
        "SE" => "sud-est",
        "S" => "sud",
        "SW" => "sud-ouest",
        "W" => "ouest",
        "NW" => "nord-ouest",
        _ => null,
    };

    /// <summary>Sous-type du site (libellés de I18n_inotr_www_fr.json, précisés) : MAI + CHALET -> "Chalet".</summary>
    public static string? SubtypeLabel(string? type, string? subtype) => (type, subtype) switch
    {
        (_, null) => null,
        ("MAI", "VILLE") => "Maison de ville",
        ("MAI", "RURALE") => "Maison de campagne",
        ("MAI", "MAITRE") => "Maison de maître",
        ("MAI", "HOTEL") => "Hôtel particulier",
        ("MAI", "PROPRIETE") => "Propriété",
        ("APP", "GARDIEN") => "Logement de gardien",
        ("TER", "TERRAIN") => null,
        ("TER", "BATIR") => "Terrain à bâtir",
        ("TER", "LOISIR") => "Terrain de loisir",
        ("TER", "NON_CONSTRUCTIBLE") => "Terrain non constructible",
        (_, var s) => Capitalize(s.Replace('_', ' ')),
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static List<string>? OtherRefs(string? reference, string? partnerRef, string? description)
    {
        var refs = new[] { partnerRef }.OfType<string>().Concat(RefExtractor.FromText(description))
            .Where(r => !string.Equals(r, reference, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return refs.Count > 0 ? refs : null;
    }

    private static string DefaultTitle(string? type, double? surface, string? city)
    {
        var label = type is null ? "Bien" : PropertyTypes.Label(type).Split(',')[0];
        if (surface is not null) label += $" {surface.Value.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} m²";
        return city is null ? label : $"{label} à {city}";
    }

    private static double? Positive(double? v) => v > 0 ? v : null;
    private static decimal? Positive(decimal? v) => v > 0 ? v : null;
    private static int? Positive(int? v) => v > 0 ? v : null;
}
