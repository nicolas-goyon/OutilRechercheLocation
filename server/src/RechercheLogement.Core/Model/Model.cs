namespace RechercheLogement.Core.Model;

/// <summary>Statut d'un bien. Le statut appartient au bien. Toutes les annonces du bien ont donc le même statut.</summary>
public enum PropertyStatus
{
    /// <summary>Aucun statut.</summary>
    None,
    /// <summary>Vu : reste visible, mais atténué.</summary>
    Seen,
    /// <summary>Pas intéressé : masqué dans le plugin.</summary>
    Rejected,
    /// <summary>Me plaît : à contacter. L'étape de suivi est dans <see cref="ContactStage"/>.</summary>
    ToContact,
}

/// <summary>Étape de suivi d'un bien "à contacter".</summary>
public enum ContactStage
{
    Pending,
    Contacted,
    VisitScheduled,
    Visited,
    ApplicationSent,
    Accepted,
    Declined,
}

public enum TransactionType { Rent, Buy }

public enum LinkState
{
    /// <summary>Doublon probable, à confirmer.</summary>
    Suggested,
    /// <summary>Même bien : les deux annonces ont le même <see cref="Property"/>.</summary>
    Confirmed,
    /// <summary>Pas le même bien. Le serveur ne propose plus jamais cette paire.</summary>
    Dismissed,
}

public sealed record GeoPoint(double Lat, double Lon, double? PrecisionM = null);

/// <summary>Données d'une annonce. Toutes sont optionnelles, car la quantité de données change selon le site d'annonces.</summary>
public sealed record ListingData
{
    public string? Url { get; init; }
    public string? Title { get; init; }
    public TransactionType? Transaction { get; init; }
    public string? PropertyType { get; init; }
    public decimal? Price { get; init; }
    public decimal? Charges { get; init; }
    public double? Surface { get; init; }
    public int? Rooms { get; init; }
    public int? Bedrooms { get; init; }
    public int? Floor { get; init; }
    public bool? Furnished { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public string? District { get; init; }
    public GeoPoint? Geo { get; init; }
    /// <summary>Référence de l'annonce donnée par l'agence (champ dédié du site). Souvent identique d'un site à l'autre.</summary>
    public string? AgencyRef { get; init; }
    /// <summary>Autres références trouvées ("Réf. : …", "Mandat n° …" dans la description ou un encadré de la page).</summary>
    public IReadOnlyList<string>? OtherRefs { get; init; }
    public string? AgencyName { get; init; }
    /// <summary>SIREN de l'agence (9 chiffres, mentions légales RCS / SIRET). Identifie l'agence d'un site à l'autre.</summary>
    public string? AgencySiren { get; init; }
    public IReadOnlyList<string>? Photos { get; init; }
    public IReadOnlyList<string>? PhotoKeys { get; init; }
    public string? DescriptionExcerpt { get; init; }
    public string? PublishedAt { get; init; }

    /// <summary>Surface du terrain, en m².</summary>
    public double? LandSurface { get; init; }
    /// <summary>Classe du DPE, de "A" à "G" (<see cref="EnergyClasses"/>).</summary>
    public string? EnergyClass { get; init; }
    /// <summary>Classe d'émission de gaz à effet de serre (GES), de "A" à "G".</summary>
    public string? GhgClass { get; init; }
    public int? YearBuilt { get; init; }
    /// <summary>Salles de bain et salles d'eau.</summary>
    public int? Bathrooms { get; init; }
    /// <summary>Chauffage, tel que le site l'écrit ("gaz individuel", "Pompe à chaleur"...).</summary>
    public string? Heating { get; init; }
    /// <summary>Exposition ("sud", "sud, est"...).</summary>
    public string? Exposure { get; init; }
    /// <summary>Précision du type, tel que le site l'écrit ("Chalet", "Villa", "Ferme"...).</summary>
    public string? Subtype { get; init; }
    /// <summary>Logement neuf (programme neuf, VEFA). false : ancien.</summary>
    public bool? NewBuild { get; init; }
    /// <summary>Travaux à prévoir. false : bon état annoncé.</summary>
    public bool? WorkNeeded { get; init; }
    /// <summary>Mode de vente particulier (<see cref="SaleKinds"/>) : viager, enchères, appel d'offres. null : vente classique.</summary>
    public string? SaleKind { get; init; }
    /// <summary>Taxe foncière annuelle, en euros.</summary>
    public decimal? PropertyTax { get; init; }
    /// <summary>
    /// Équipements (<see cref="Model.Features"/>) : true présent, false absent (annoncé comme absent par le site).
    /// Un équipement non mentionné n'a pas de clé.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? Features { get; init; }

    /// <summary>
    /// Fusionne <paramref name="incoming"/> dans ces données. Si <paramref name="fillOnly"/> est vrai,
    /// la méthode remplit uniquement les champs vides. (Une carte HTML ne remplace pas les données de l'API.)
    /// </summary>
    public ListingData Merge(ListingData incoming, bool fillOnly) => new()
    {
        Url = Pick(Url, incoming.Url, fillOnly),
        Title = Pick(Title, incoming.Title, fillOnly),
        Transaction = Pick(Transaction, incoming.Transaction, fillOnly),
        PropertyType = Pick(PropertyType, incoming.PropertyType, fillOnly),
        Price = Pick(Price, incoming.Price, fillOnly),
        Charges = Pick(Charges, incoming.Charges, fillOnly),
        Surface = Pick(Surface, incoming.Surface, fillOnly),
        Rooms = Pick(Rooms, incoming.Rooms, fillOnly),
        Bedrooms = Pick(Bedrooms, incoming.Bedrooms, fillOnly),
        Floor = Pick(Floor, incoming.Floor, fillOnly),
        Furnished = Pick(Furnished, incoming.Furnished, fillOnly),
        PostalCode = Pick(PostalCode, incoming.PostalCode, fillOnly),
        City = Pick(City, incoming.City, fillOnly),
        District = Pick(District, incoming.District, fillOnly),
        Geo = Pick(Geo, incoming.Geo, fillOnly),
        AgencyRef = Pick(AgencyRef, incoming.AgencyRef, fillOnly),
        OtherRefs = PickList(OtherRefs, incoming.OtherRefs, fillOnly, 6),
        AgencyName = Pick(AgencyName, incoming.AgencyName, fillOnly),
        AgencySiren = Pick(AgencySiren, incoming.AgencySiren, fillOnly),
        Photos = PickList(Photos, incoming.Photos, fillOnly, MaxPhotos),
        PhotoKeys = PickList(PhotoKeys, incoming.PhotoKeys, fillOnly, 6),
        DescriptionExcerpt = Pick(DescriptionExcerpt, Truncate(incoming.DescriptionExcerpt, 600), fillOnly),
        PublishedAt = Pick(PublishedAt, incoming.PublishedAt, fillOnly),
        LandSurface = Pick(LandSurface, incoming.LandSurface, fillOnly),
        EnergyClass = Pick(EnergyClass, incoming.EnergyClass, fillOnly),
        GhgClass = Pick(GhgClass, incoming.GhgClass, fillOnly),
        YearBuilt = Pick(YearBuilt, incoming.YearBuilt, fillOnly),
        Bathrooms = Pick(Bathrooms, incoming.Bathrooms, fillOnly),
        Heating = Pick(Heating, incoming.Heating, fillOnly),
        Exposure = Pick(Exposure, incoming.Exposure, fillOnly),
        Subtype = Pick(Subtype, incoming.Subtype, fillOnly),
        NewBuild = Pick(NewBuild, incoming.NewBuild, fillOnly),
        WorkNeeded = Pick(WorkNeeded, incoming.WorkNeeded, fillOnly),
        SaleKind = Pick(SaleKind, incoming.SaleKind, fillOnly),
        PropertyTax = Pick(PropertyTax, incoming.PropertyTax, fillOnly),
        Features = MergeFeatures(Features, incoming.Features, fillOnly),
    };

    /// <summary>
    /// Équipements des deux côtés, équipement par équipement : la fiche d'un site dit "Piscine", la liste d'un autre
    /// "pas de cave". Même équipement des deux côtés : la nouvelle valeur l'emporte, sauf si <paramref name="fillOnly"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, bool>? MergeFeatures(IReadOnlyDictionary<string, bool>? current, IReadOnlyDictionary<string, bool>? incoming, bool fillOnly)
    {
        if (incoming is null || incoming.Count == 0) return current;
        if (current is null || current.Count == 0) return new SortedDictionary<string, bool>(incoming.ToDictionary(), StringComparer.Ordinal);
        var merged = new SortedDictionary<string, bool>(current.ToDictionary(), StringComparer.Ordinal);
        foreach (var (key, value) in incoming)
            if (!fillOnly || !merged.ContainsKey(key)) merged[key] = value;
        return merged;
    }

    /// <summary>Égalité de contenu. La méthode compare les listes élément par élément.</summary>
    public bool SameAs(ListingData other) =>
        this with { Photos = null, PhotoKeys = null, OtherRefs = null, Features = null } == other with { Photos = null, PhotoKeys = null, OtherRefs = null, Features = null }
        && SeqEq(Photos, other.Photos) && SeqEq(PhotoKeys, other.PhotoKeys) && SeqEq(OtherRefs, other.OtherRefs)
        && SameFeatures(Features, other.Features);

    /// <summary>Équipement présent (true), absent (false) ou non mentionné (null).</summary>
    public bool? Has(string feature) => Features is not null && Features.TryGetValue(feature, out var v) ? v : null;

    private static bool SameFeatures(IReadOnlyDictionary<string, bool>? a, IReadOnlyDictionary<string, bool>? b) =>
        (a is null || a.Count == 0) ? (b is null || b.Count == 0)
        : b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Photos gardées par annonce. Même limite dans le plugin (MAX_PHOTOS).</summary>
    public const int MaxPhotos = 40;

    private static T? Pick<T>(T? current, T? incoming, bool fillOnly) where T : class =>
        incoming is null || (fillOnly && current is not null) ? current : (incoming is string s && s.Length == 0 ? current : incoming);

    private static T? Pick<T>(T? current, T? incoming, bool fillOnly) where T : struct =>
        incoming is null || (fillOnly && current is not null) ? current : incoming;

    private static IReadOnlyList<string>? PickList(IReadOnlyList<string>? current, IReadOnlyList<string>? incoming, bool fillOnly, int max) =>
        incoming is null || incoming.Count == 0 || (fillOnly && current is { Count: > 0 }) ? current : incoming.Take(max).ToArray();

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static bool SeqEq(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a is null || a.Count == 0) ? (b is null || b.Count == 0) : b is not null && a.SequenceEqual(b);
}

public sealed record PricePoint(long At, decimal Price);

/// <summary>Une annonce publiée sur UN site d'annonces. Clé : "site:siteId".</summary>
public sealed class Listing
{
    public required string Key { get; init; }
    public required string Site { get; init; }
    public required string SiteId { get; init; }
    public required string PropertyId { get; set; }
    public ListingData Data { get; set; } = new();
    public long FirstSeenAt { get; set; }
    public long LastSeenAt { get; set; }
    public long UpdatedAt { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<PricePoint> PriceHistory { get; set; } = [];

    public static string MakeKey(string site, string siteId) => $"{site}:{siteId}";
}

/// <summary>Le bien réel (l'appartement). Il regroupe une ou plusieurs annonces.</summary>
public sealed class Property
{
    public required string Id { get; init; }
    public PropertyStatus Status { get; set; }
    public ContactStage? ContactStage { get; set; }
    public string? Note { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long? StatusChangedAt { get; set; }

    public static string NewId() => $"p_{Guid.NewGuid():N}"[..14];
}

/// <summary>Paire d'annonces candidate au doublon. A et B sont triés. Chaque paire existe donc une seule fois.</summary>
public sealed class DuplicateLink
{
    public required string A { get; init; }
    public required string B { get; init; }
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = [];
    public LinkState State { get; set; }
    /// <summary>"auto" (fusion au-dessus du seuil) ou "user" (décision de l'utilisateur).</summary>
    public string? DecidedBy { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    public static (string A, string B) Order(string x, string y) => string.CompareOrdinal(x, y) < 0 ? (x, y) : (y, x);

    public string Other(string key) => key == A ? B : A;
}

public enum PropertyEventKind { StatusChanged, ContactStageChanged, Note, Merged, Detached, Comment }

/// <summary>Historique d'un bien. Le serveur local l'affiche sous forme de frise.</summary>
public sealed record PropertyEvent(long Id, string PropertyId, long At, PropertyEventKind Kind, string Text);

/// <summary>
/// Recherche favorite : un lien de recherche d'un site d'annonces, avec ses critères dans l'URL.
/// Plusieurs recherches peuvent viser le même site, ou la même URL (par exemple avec une note différente).
/// </summary>
public sealed class SavedSearch
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Site d'annonces, déduit de l'URL ("bienici", "seloger"...). Sert au regroupement.</summary>
    public string Site { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>Projet de la recherche. null : visible dans tous les projets.</summary>
    public string? ProjectId { get; set; }
    /// <summary>Ordre d'affichage (croissant).</summary>
    public int Order { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long? LastOpenedAt { get; set; }

    public static string NewId() => $"s_{Guid.NewGuid():N}"[..14];

    /// <summary>"https://www.seloger.com/classified-search?..." -> "seloger". Hôte inconnu : l'hôte sans "www.".</summary>
    public static string SiteOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var host = u.Host.ToLowerInvariant();
        static bool Is(string h, string domain) => h == domain || h.EndsWith("." + domain, StringComparison.Ordinal);
        if (Is(host, "bienici.com")) return "bienici";
        if (Is(host, "seloger.com")) return "seloger";
        if (Is(host, "leboncoin.fr")) return "leboncoin";
        if (Is(host, "pap.fr")) return "pap";
        if (Is(host, "logic-immo.com")) return "logicimmo";
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>URL http(s) absolue, sans espaces autour. Renvoie null si l'URL n'est pas utilisable.</summary>
    public static string? NormalizeUrl(string? url)
    {
        var t = url?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            && (u.Host.Contains('.') || u.IsLoopback)
            ? u.AbsoluteUri
            : null;
    }
}
