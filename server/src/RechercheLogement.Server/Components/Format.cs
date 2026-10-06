using System.Globalization;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Server.Components;

public static class Format
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly TimeZoneInfo Paris = TryZone("Europe/Paris");

    public static string Price(decimal? p) => p is null ? "—" : $"{p.Value.ToString("#,0", Fr)} €";
    public static string Surface(double? s) => s is null ? "—" : $"{s.Value.ToString("0.#", Fr)} m²";
    public static string Date(long ms) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), Paris).ToString("dd/MM/yyyy", Fr);
    public static string DateTime(long ms) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), Paris).ToString("dd/MM/yyyy HH:mm", Fr);

    /// <summary>Miniature d'une photo d'annonce (le CDN du site redimensionne l'image).</summary>
    public static string Thumb(string url, int width, int height)
    {
        if (url.StartsWith("https://file.bienici.com/", StringComparison.Ordinal))
            return $"{url}?width={width}&height={height}&fit=cover";
        if (url.StartsWith("https://cdnihddipa.cloudimg.io/", StringComparison.Ordinal) || url.StartsWith("https://mms.seloger.com/", StringComparison.Ordinal))
            return $"{url}{(url.Contains('?') ? '&' : '?')}w={width}&h={height}";
        return url;
    }

    /// <summary>"CENTURY 21 Foch Immobilier (SIREN 407797521)", ou null si l'agence est inconnue.</summary>
    public static string? Agency(ListingData d)
    {
        if (d.AgencyName is null && d.AgencySiren is null) return null;
        return d.AgencySiren is null ? d.AgencyName : $"{d.AgencyName ?? "Agence"} (SIREN {d.AgencySiren})";
    }

    /// <summary>Référence du champ dédié, puis références trouvées dans le texte : "28251 · mandat 28251-C21".</summary>
    public static string? Refs(ListingData d)
    {
        var parts = new List<string>();
        if (d.AgencyRef is not null) parts.Add(d.AgencyRef);
        foreach (var r in d.OtherRefs ?? []) if (!parts.Contains(r, StringComparer.OrdinalIgnoreCase)) parts.Add(r);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public static string StatusClass(PropertyStatus s) => s switch
    {
        PropertyStatus.Seen => "seen",
        PropertyStatus.Rejected => "rejected",
        PropertyStatus.ToContact => "tocontact",
        _ => "none",
    };

    public static string StatusIcon(PropertyStatus s) => s switch
    {
        PropertyStatus.Seen => "👁",
        PropertyStatus.Rejected => "✕",
        PropertyStatus.ToContact => "📞",
        _ => "•",
    };

    private static TimeZoneInfo TryZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
    }
}
