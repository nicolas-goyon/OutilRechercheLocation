using System.Globalization;
using System.Text;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Dedup;

public sealed record DedupThresholds(double AutoLink = 0.8, double Suggest = 0.5);

public sealed record DedupResult(double Score, IReadOnlyList<string> Reasons, string? RejectedBy = null);

/// <summary>
/// Détection de doublons entre deux annonces (même site d'annonces ou sites différents).
///
/// 1. Rejets durs : différences qui excluent le même bien.
/// 2. Points pour chaque critère commun ou proche. Score = points / 10, avec un maximum de 1.
///    Un critère absent d'une des deux annonces ne compte ni pour ni contre.
///
/// Voir ARCHITECTURE.md pour le barème.
/// </summary>
public static class DedupScorer
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static DedupResult Compare(ListingData a, ListingData b)
    {
        var reasons = new List<string>();
        double points = 0;

        // --- 1. Rejets durs -------------------------------------------------
        if (a.Transaction is not null && b.Transaction is not null && a.Transaction != b.Transaction)
            return Rejected("transaction différente");
        if (a.PropertyType is not null && b.PropertyType is not null && a.PropertyType != b.PropertyType)
            return Rejected("type de bien différent");

        double? dist = a.Geo is not null && b.Geo is not null ? DistanceMeters(a.Geo, b.Geo) : null;
        var cpA = CanonicalPostalCode(a.PostalCode);
        var cpB = CanonicalPostalCode(b.PostalCode);
        if (cpA is not null && cpB is not null && cpA != cpB && !(dist < 300))
            return Rejected("code postal différent");
        if (dist is not null)
        {
            var tolerance = 1500 + (a.Geo!.PrecisionM ?? 0) + (b.Geo!.PrecisionM ?? 0);
            if (dist > tolerance) return Rejected($"trop éloignés ({Math.Round(dist.Value)} m)");
        }
        if (a.Surface is > 0 && b.Surface is > 0 && RelativeDiff(a.Surface.Value, b.Surface.Value) > 0.1)
            return Rejected("surfaces trop différentes");
        if (a.Rooms is not null && b.Rooms is not null && Math.Abs(a.Rooms.Value - b.Rooms.Value) >= 2)
            return Rejected("nombre de pièces différent");

        // --- 2. Points -----------------------------------------------------
        var refA = NormalizeRef(a.AgencyRef);
        var refB = NormalizeRef(b.AgencyRef);
        if (refA is not null && refB is not null)
        {
            if (refA == refB)
            {
                points += 6;
                reasons.Add($"même référence agence ({a.AgencyRef})");
            }
            else if (refA.Length >= 5 && refB.Length >= 5 && (refA.EndsWith(refB, StringComparison.Ordinal) || refB.EndsWith(refA, StringComparison.Ordinal)))
            {
                points += 4;
                reasons.Add($"références agence compatibles ({a.AgencyRef} / {b.AgencyRef})");
            }
        }

        if (a.PhotoKeys is { Count: > 0 } && b.PhotoKeys is { Count: > 0 })
        {
            var common = b.PhotoKeys.Intersect(a.PhotoKeys).Count();
            if (common > 0)
            {
                points += common >= 2 ? 6 : 4;
                reasons.Add($"{common} photo(s) identique(s)");
            }
        }

        var sim = TextSimilarity(a.DescriptionExcerpt, b.DescriptionExcerpt);
        if (sim >= 0.6)
        {
            points += 5;
            reasons.Add($"description quasi identique ({Math.Round(sim.Value * 100)} %)");
        }
        else if (sim >= 0.35)
        {
            points += 3;
            reasons.Add($"description proche ({Math.Round(sim.Value * 100)} %)");
        }

        if (a.Surface is > 0 && b.Surface is > 0)
        {
            var d = RelativeDiff(a.Surface.Value, b.Surface.Value);
            if (Math.Abs(a.Surface.Value - b.Surface.Value) <= 1 || d <= 0.02)
            {
                points += 2;
                reasons.Add($"même surface ({Num(a.Surface)} / {Num(b.Surface)} m²)");
            }
            else if (d <= 0.05)
            {
                points += 1;
                reasons.Add($"surface proche ({Num(a.Surface)} / {Num(b.Surface)} m²)");
            }
        }

        if (a.Price is > 0 && b.Price is > 0)
        {
            var d = RelativeDiff((double)a.Price.Value, (double)b.Price.Value);
            if (d <= 0.02)
            {
                points += 2;
                reasons.Add($"même prix ({Num(a.Price)} / {Num(b.Price)} €)");
            }
            else if (d <= 0.08)
            {
                // Écart typique entre prix charges comprises et hors charges, selon le site d'annonces.
                points += 1;
                reasons.Add($"prix proche ({Num(a.Price)} / {Num(b.Price)} €)");
            }
        }

        if (a.Rooms is not null && a.Rooms == b.Rooms)
        {
            points += 1;
            reasons.Add($"{a.Rooms} pièce(s)");
        }
        if (a.Bedrooms is not null && a.Bedrooms == b.Bedrooms) points += 0.5;
        if (a.Floor is not null && a.Floor == b.Floor)
        {
            points += 1;
            reasons.Add($"même étage ({a.Floor})");
        }
        if (a.Furnished is not null && b.Furnished is not null && a.Furnished != b.Furnished)
        {
            points -= 1;
            reasons.Add("meublé / non meublé diffèrent");
        }

        if (dist is not null)
        {
            var slack = (a.Geo!.PrecisionM ?? 0) + (b.Geo!.PrecisionM ?? 0);
            if (dist <= 150 + slack)
            {
                points += 2;
                reasons.Add($"position très proche ({Math.Round(dist.Value)} m)");
            }
            else if (dist <= 400 + slack)
            {
                points += 1;
                reasons.Add($"position proche ({Math.Round(dist.Value)} m)");
            }
        }
        else if (cpA is not null && cpA == cpB)
        {
            points += 1;
            reasons.Add($"même code postal ({a.PostalCode})");
        }

        var score = Math.Clamp(points / 10, 0, 1);
        return new DedupResult(Math.Round(score, 2), reasons);

        DedupResult Rejected(string why) => new(0, [], why);
    }

    /// <summary>Clé de blocage. Le serveur compare une annonce uniquement aux annonces du même bucket.</summary>
    public static string BlockingKey(ListingData d) =>
        CanonicalPostalCode(d.PostalCode) ?? (d.City is not null ? $"city:{d.City.ToLowerInvariant()}" : "?");

    /// <summary>D'autres sites d'annonces publient souvent 75116 (Paris 16e nord) sous la forme 75016.</summary>
    public static string? CanonicalPostalCode(string? cp)
    {
        if (string.IsNullOrWhiteSpace(cp)) return null;
        var c = cp.Trim();
        return c == "75116" ? "75016" : c;
    }

    public static string? NormalizeRef(string? r)
    {
        if (r is null) return null;
        var s = new string(r.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length >= 3 ? s : null;
    }

    /// <summary>Convertit en minuscules. Retire les accents et la ponctuation. Réduit les espaces multiples à un espace.</summary>
    public static string NormalizeText(string input)
    {
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = true;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Similarité de Jaccard sur les trigrammes de caractères. Renvoie null si un texte est trop court.</summary>
    public static double? TextSimilarity(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return null;
        var ta = Trigrams(a);
        var tb = Trigrams(b);
        if (ta.Count < 20 || tb.Count < 20) return null;
        var inter = ta.Count(tb.Contains);
        return (double)inter / (ta.Count + tb.Count - inter);
    }

    private static HashSet<string> Trigrams(string text)
    {
        var s = $" {NormalizeText(text)} ";
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= s.Length; i++) set.Add(s.Substring(i, 3));
        return set;
    }

    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        const double r = 6371000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(b.Lat - a.Lat);
        var dLon = Rad(b.Lon - a.Lon);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * r * Math.Asin(Math.Sqrt(h));
    }

    private static double RelativeDiff(double a, double b)
    {
        var m = Math.Max(Math.Abs(a), Math.Abs(b));
        return m == 0 ? 0 : Math.Abs(a - b) / m;
    }

    private static string Num(double? v) => v?.ToString("0.##", Fr) ?? "?";
    private static string Num(decimal? v) => v?.ToString("0.##", Fr) ?? "?";
}
