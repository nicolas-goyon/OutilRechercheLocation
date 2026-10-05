namespace RechercheLogement.Core.Model;

/// <summary>Statut d'un bien (porté par le bien, donc partagé par toutes ses annonces).</summary>
public enum PropertyStatus
{
    /// <summary>Jamais qualifié.</summary>
    None,
    /// <summary>Vu, gardé visible mais atténué.</summary>
    Seen,
    /// <summary>Vu, pas intéressé : masqué dans le plugin.</summary>
    Rejected,
    /// <summary>Me plaît : à contacter, avec suivi <see cref="ContactStage"/>.</summary>
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
    /// <summary>Doublon probable, à valider.</summary>
    Suggested,
    /// <summary>Même bien : les deux annonces partagent le même <see cref="Property"/>.</summary>
    Confirmed,
    /// <summary>Pas le même bien : ne plus jamais proposer.</summary>
    Dismissed,
}

public sealed record GeoPoint(double Lat, double Lon, double? PrecisionM = null);

/// <summary>Données descriptives d'une annonce. Toutes optionnelles : chaque site en expose plus ou moins.</summary>
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
    public string? AgencyRef { get; init; }
    public string? AgencyName { get; init; }
    public IReadOnlyList<string>? Photos { get; init; }
    public IReadOnlyList<string>? PhotoKeys { get; init; }
    public string? DescriptionExcerpt { get; init; }
    public string? PublishedAt { get; init; }

    /// <summary>
    /// Fusionne <paramref name="incoming"/> dans ces données. Si <paramref name="fillOnly"/>,
    /// seuls les champs encore vides sont remplis (une carte HTML n'écrase pas les données API).
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
        AgencyName = Pick(AgencyName, incoming.AgencyName, fillOnly),
        Photos = PickList(Photos, incoming.Photos, fillOnly, 4),
        PhotoKeys = PickList(PhotoKeys, incoming.PhotoKeys, fillOnly, 6),
        DescriptionExcerpt = Pick(DescriptionExcerpt, Truncate(incoming.DescriptionExcerpt, 600), fillOnly),
        PublishedAt = Pick(PublishedAt, incoming.PublishedAt, fillOnly),
    };

    /// <summary>Égalité de contenu (les listes sont comparées élément par élément).</summary>
    public bool SameAs(ListingData other) =>
        this with { Photos = null, PhotoKeys = null } == other with { Photos = null, PhotoKeys = null }
        && SeqEq(Photos, other.Photos) && SeqEq(PhotoKeys, other.PhotoKeys);

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

/// <summary>Une annonce publiée sur UN site. Clé : "site:siteId".</summary>
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

/// <summary>Le bien réel (l'appartement), qui regroupe une ou plusieurs annonces.</summary>
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

/// <summary>Paire d'annonces candidates au doublon. (A, B) triés : une paire n'existe qu'une fois.</summary>
public sealed class DuplicateLink
{
    public required string A { get; init; }
    public required string B { get; init; }
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = [];
    public LinkState State { get; set; }
    /// <summary>"auto" (fusion au-dessus du seuil) ou "user".</summary>
    public string? DecidedBy { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    public static (string A, string B) Order(string x, string y) => string.CompareOrdinal(x, y) < 0 ? (x, y) : (y, x);

    public string Other(string key) => key == A ? B : A;
}

public enum PropertyEventKind { StatusChanged, ContactStageChanged, Note, Merged, Detached, Comment }

/// <summary>Historique d'un bien (affiché en frise sur le site local).</summary>
public sealed record PropertyEvent(long Id, string PropertyId, long At, PropertyEventKind Kind, string Text);
